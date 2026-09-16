using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace TradePet.Infrastructure.Persistence;

public sealed record DatabaseInitializationResult(
    bool Recovered,
    string? ArchiveDirectory,
    IReadOnlyList<string> ArchivedFiles,
    int SchemaVersion)
{
    public static DatabaseInitializationResult Healthy { get; } = new(false, null, [], SchemaMigrations.All[^1].Version);
}

public sealed class DatabaseRecoveryRequiredException(
    string message,
    string? safetySnapshotDirectory,
    DatabaseSalvageReport? salvageReport = null,
    Exception? innerException = null) : IOException(message, innerException)
{
    public string? SafetySnapshotDirectory { get; } = safetySnapshotDirectory;
    public DatabaseSalvageReport? SalvageReport { get; } = salvageReport;
}

public sealed record DatabaseSalvageReport(
    IReadOnlyDictionary<string, long> ReadableRowCounts,
    IReadOnlyList<string> FailedTables,
    IReadOnlyDictionary<string, string> SalvagedRowFiles,
    long MissingAttachmentCount,
    IReadOnlyList<string> MissingAttachmentPaths);

public sealed partial class AppDatabase
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private static readonly HashSet<string> SalvageHumanTables = new(StringComparer.Ordinal)
    {
        "accounts", "settings", "trading_days", "trade_review_documents", "daily_journals",
        "period_reviews", "review_revisions", "attachment_assets", "attachment_links",
        "playbook_versions", "trade_rule_assessments", "trade_campaigns", "trade_campaign_members",
        "behavior_occurrences", "behavior_trade_links", "improvement_goals", "goal_observations",
        "opportunity_records", "review_saved_filters", "structured_trade_plans",
        "structured_trade_plan_links", "trade_review_metadata", "plan_items", "chart_objects",
        "chart_object_revisions", "loss_zones", "loss_zone_attempts", "timeline_events",
        "trading_session_definitions",
    };

    public async Task<DatabaseInitializationResult> InitializeWithRecoveryAsync(
        string? backupRoot = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedBackupRoot = ResolveBackupRoot(backupRoot);
        var safetySnapshot = CreateSafetySnapshot(resolvedBackupRoot);
        try
        {
            if (File.Exists(_databasePath) && !await HasSqliteHeaderAsync(
                    safetySnapshot is null
                        ? _databasePath
                        : Path.Combine(safetySnapshot, Path.GetFileName(_databasePath)),
                    cancellationToken))
            {
                throw new DatabaseRecoveryRequiredException(
                    "数据库文件不是可识别的 SQLite 格式；可能是错误密钥、其他格式或文件损坏。已保留原文件，不能自动重建。",
                    safetySnapshot);
            }
            if (File.Exists(_databasePath) && !await PassesReadOnlyQuickCheckAsync(
                    safetySnapshot is null
                        ? _databasePath
                        : Path.Combine(safetySnapshot, Path.GetFileName(_databasePath)),
                    cancellationToken))
            {
                throw await BuildRecoveryRequiredAsync(
                    "原数据库未通过 SQLite 完整性检查；已保留原文件与副本，必须先选择恢复或抢救，不能自动建立空库。",
                    safetySnapshot, cancellationToken);
            }
            await InitializeAsync(cancellationToken);
            if (await PassesQuickCheckAsync(cancellationToken))
            {
                DeleteSafetySnapshot(safetySnapshot);
                return DatabaseInitializationResult.Healthy;
            }
            throw await BuildRecoveryRequiredAsync(
                "迁移后数据库未通过 SQLite 完整性检查；已保留迁移前副本，不能继续写入。",
                safetySnapshot, cancellationToken);
        }
        catch (SqliteException exception) when (IsDatabaseCorruption(exception))
        {
            throw await BuildRecoveryRequiredAsync(
                "数据库报告损坏；已保留原文件与副本，不能自动重建空库。",
                safetySnapshot, cancellationToken, exception);
        }
    }

    private static async Task<bool> HasSqliteHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = 0;
        while (read < header.Length)
        {
            var next = await stream.ReadAsync(header.AsMemory(read), cancellationToken);
            if (next == 0)
            {
                return false;
            }
            read += next;
        }
        return header.AsSpan().SequenceEqual("SQLite format 3\0"u8);
    }

    private async Task<DatabaseRecoveryRequiredException> BuildRecoveryRequiredAsync(
        string message, string? safetySnapshot, CancellationToken cancellationToken, Exception? cause = null)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var failed = new List<string>();
        var salvageFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingAttachments = new List<string>();
        long missingAttachmentCount = 0;
        var attachmentRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_databasePath)!, "attachments"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (safetySnapshot is not null)
        {
            try
            {
                var databasePath = Directory.EnumerateFiles(safetySnapshot, "*.db").FirstOrDefault();
                if (databasePath is not null)
                {
                    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = databasePath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString());
                    await connection.OpenAsync(cancellationToken);
                    var names = new List<string>();
                    await using (var tables = connection.CreateCommand())
                    {
                        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                        await using var rows = await tables.ExecuteReaderAsync(cancellationToken);
                        while (await rows.ReadAsync(cancellationToken))
                        {
                            names.Add(rows.GetString(0));
                        }
                    }
                    for (var index = 0; index < names.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = names[index];
                        var count = 0L;
                        StreamWriter? writer = null;
                        try
                        {
                            if (SalvageHumanTables.Contains(name))
                            {
                                var fileName = $"salvage-table-{index + 1:D3}.jsonl";
                                writer = new StreamWriter(new FileStream(Path.Combine(safetySnapshot, fileName),
                                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                                    FileOptions.Asynchronous), new UTF8Encoding(false));
                                salvageFiles[name] = fileName;
                            }
                            await using var command = connection.CreateCommand();
                            command.CommandText = $"SELECT rowid,* FROM \"{name.Replace("\"", "\"\"")}\" NOT INDEXED;";
                            await using var rows = await command.ExecuteReaderAsync(cancellationToken);
                            while (await rows.ReadAsync(cancellationToken))
                            {
                                count++;
                                if (writer is not null)
                                {
                                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                                    for (var column = 0; column < rows.FieldCount; column++)
                                    {
                                        values[rows.GetName(column)] = rows.IsDBNull(column)
                                            ? null : rows.GetValue(column);
                                    }
                                    await writer.WriteLineAsync(
                                        JsonSerializer.Serialize(values).AsMemory(), cancellationToken);
                                }
                                if (name == "attachment_assets")
                                {
                                    var relative = rows.GetString(rows.GetOrdinal("relative_path"));
                                    var candidate = Path.GetFullPath(Path.Combine(attachmentRoot,
                                        relative.Replace('/', Path.DirectorySeparatorChar)));
                                    if (!candidate.StartsWith(attachmentRoot, StringComparison.OrdinalIgnoreCase) ||
                                        !File.Exists(candidate))
                                    {
                                        missingAttachmentCount++;
                                        if (missingAttachments.Count < 1000)
                                        {
                                            missingAttachments.Add(relative);
                                        }
                                    }
                                }
                            }
                        }
                        catch (SqliteException)
                        {
                            failed.Add(name);
                        }
                        catch (IOException)
                        {
                            failed.Add(name + ":export");
                        }
                        finally
                        {
                            if (writer is not null)
                            {
                                await writer.DisposeAsync();
                            }
                        }
                        counts[name] = count;
                    }
                }
            }
            catch (SqliteException)
            {
                failed.Add("<database-schema>");
            }
            catch (IOException)
            {
                failed.Add("<snapshot-read>");
            }
        }
        var report = new DatabaseSalvageReport(counts, failed, salvageFiles,
            missingAttachmentCount, missingAttachments);
        if (safetySnapshot is not null)
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(safetySnapshot, "salvage-report.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken);
            }
            catch (IOException)
            {
                // The report remains available in memory; never mask the original recovery failure.
            }
        }
        return new DatabaseRecoveryRequiredException(message, safetySnapshot, report, cause);
    }

    private static async Task<bool> PassesReadOnlyQuickCheckAsync(
        string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var foundResult = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            foundResult = true;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return foundResult;
    }

    private async Task<bool> PassesQuickCheckAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var foundResult = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            foundResult = true;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return foundResult;
    }

    private string ResolveBackupRoot(string? backupRoot)
    {
        var databaseDirectory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        var resolvedBackupRoot = Path.GetFullPath(
            backupRoot ?? Path.Combine(databaseDirectory, "backups"));
        EnsureChildPath(databaseDirectory, resolvedBackupRoot);
        return resolvedBackupRoot;
    }

    private string? CreateSafetySnapshot(string backupRoot)
    {
        var sourceFiles = DatabaseFileSet(_databasePath).Where(File.Exists).ToArray();
        if (sourceFiles.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(backupRoot);
        var snapshotDirectory = Path.Combine(
            backupRoot,
            $".database-check-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(snapshotDirectory);
        foreach (var sourcePath in sourceFiles)
        {
            File.Copy(sourcePath, Path.Combine(snapshotDirectory, Path.GetFileName(sourcePath)), overwrite: false);
        }

        return snapshotDirectory;
    }

    private static void DeleteSafetySnapshot(string? snapshotDirectory)
    {
        if (snapshotDirectory is null || !Directory.Exists(snapshotDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(snapshotDirectory))
        {
            File.Delete(file);
        }
        Directory.Delete(snapshotDirectory, recursive: false);
    }

    private static IEnumerable<string> DatabaseFileSet(string databasePath)
    {
        yield return databasePath;
        yield return databasePath + "-wal";
        yield return databasePath + "-shm";
    }

    private static bool IsDatabaseCorruption(SqliteException exception)
    {
        var primaryExtendedCode = exception.SqliteExtendedErrorCode & 0xFF;
        return exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase ||
               primaryExtendedCode is SqliteCorrupt or SqliteNotADatabase;
    }

    private static void EnsureChildPath(string parentDirectory, string candidateDirectory)
    {
        var parent = Path.GetFullPath(parentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidateDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The database backup directory must remain inside the TradePet data directory.");
        }
    }
}
