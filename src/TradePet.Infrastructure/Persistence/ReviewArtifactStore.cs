using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Persistence;

public sealed class ReviewAttachmentStore : IReviewAttachmentStore
{
    private const long MaximumFileBytes = 25 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".md"] = "text/markdown",
            [".csv"] = "text/csv",
        };

    private readonly AppDatabase _database;
    private readonly string _root;
    private readonly TimeProvider _timeProvider;

    public ReviewAttachmentStore(AppDatabase database, string? root = null, TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _root = Path.GetFullPath(root ?? TradePetPaths.GetAttachmentDirectory());
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_root);
    }

    public async Task<ReviewAttachment> ImportAsync(
        string accountKey,
        string ownerKind,
        string ownerId,
        string sourcePath,
        string title,
        ReviewEvidenceStamp evidence,
        CancellationToken cancellationToken = default,
        string eventReference = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists)
        {
            throw new FileNotFoundException("附件文件不存在。", source.FullName);
        }
        if (source.Length == 0 || source.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("附件必须大于 0 字节且不超过 25 MB。");
        }
        var extension = source.Extension.ToLowerInvariant();
        if (!MediaTypes.TryGetValue(extension, out var mediaType))
        {
            throw new InvalidDataException("仅支持图片、PDF、TXT、Markdown 和 CSV 附件。");
        }

        string sha256;
        await using (var input = source.OpenRead())
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        }
        var accountFolder = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountKey))).ToLowerInvariant()[..16];
        var relativePath = Path.Combine(accountFolder, sha256[..2], sha256 + extension);
        var target = ResolveInsideRoot(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var createdFile = false;
        if (!File.Exists(target))
        {
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var input = source.OpenRead())
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                File.Move(temp, target, false);
                createdFile = true;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        var attachment = new ReviewAttachment(
            Guid.NewGuid().ToString("N"), accountKey, sha256, source.Name, mediaType, source.Length,
            relativePath.Replace(Path.DirectorySeparatorChar, '/'), title.Trim(), ownerKind.Trim(), ownerId.Trim(),
            evidence, _timeProvider.GetUtcNow(), eventReference.Trim());
        try
        {
            return await _database.SaveAttachmentAsync(attachment, cancellationToken);
        }
        catch
        {
            if (createdFile && File.Exists(target))
            {
                File.Delete(target);
            }
            throw;
        }
    }

    public async Task DeleteAsync(
        string attachmentId,
        string accountKey,
        string ownerKind,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var relativePath = await _database.DeleteAttachmentLinkAsync(
            attachmentId, accountKey, ownerKind, ownerId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var path = ResolveInsideRoot(relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public string ResolvePath(ReviewAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return ResolveInsideRoot(attachment.RelativePath);
    }

    private string ResolveInsideRoot(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("附件路径超出受控目录。");
        }
        return path;
    }
}

public sealed class ReviewPackageWriter : IReviewPackageWriter
{
    private const long MaximumAttachmentBytes = 25L * 1024 * 1024;
    private const long MaximumGeneratedEntryBytes = 128L * 1024 * 1024;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;

    public async Task<string> WriteAsync(
        ReviewExportPackage package,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var directory = Path.GetFullPath(outputDirectory ?? TradePetPaths.GetExportDirectory());
        Directory.CreateDirectory(directory);
        var safeName = Path.GetFileName(package.FileName);
        if (!safeName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            safeName += ".zip";
        }
        var destination = Path.Combine(directory, safeName);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (package.Entries.Count > 5000)
            {
                throw new InvalidDataException("导出条目数量超过上限。");
            }
            var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalUncompressedBytes = 0;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var item in package.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var normalized = item.Path.Replace('\\', '/');
                    if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':') ||
                        normalized.Any(char.IsControl) ||
                        normalized.Split('/').Any(part => part is ".." or "." or "") ||
                        !writtenPaths.Add(normalized))
                    {
                        throw new InvalidDataException("导出条目路径无效。");
                    }
                    var isFileEntry = item.SourcePath is not null;
                    var entryLimit = isFileEntry ? MaximumAttachmentBytes : MaximumGeneratedEntryBytes;
                    var expectedBytes = isFileEntry ? item.ExpectedSizeBytes : item.Content.LongLength;
                    if (expectedBytes is null || (isFileEntry && expectedBytes <= 0) ||
                        expectedBytes > entryLimit || expectedBytes < 0 ||
                        totalUncompressedBytes > MaximumPackageBytes - expectedBytes)
                    {
                        throw new InvalidDataException("导出文件大小或总量超过上限。");
                    }
                    var verifiedLength = expectedBytes.Value;
                    totalUncompressedBytes += verifiedLength;
                    var entry = archive.CreateEntry(normalized, CompressionLevel.Optimal);
                    await using var output = entry.Open();
                    if (isFileEntry)
                    {
                        if (item.Content.Length != 0 || item.ExpectedSha256 is null ||
                            item.ExpectedSha256.Length != 64 ||
                            !item.ExpectedSha256.All(Uri.IsHexDigit))
                        {
                            throw new InvalidDataException("附件条目缺少可核验的大小或 SHA-256。");
                        }
                        await using var input = new FileStream(item.SourcePath!, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        long copied = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            copied += count;
                            if (copied > verifiedLength)
                            {
                                throw new InvalidDataException("附件读取大小与登记值不一致。");
                            }
                            hash.AppendData(buffer, 0, count);
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        }
                        if (copied != verifiedLength ||
                            !Convert.ToHexString(hash.GetHashAndReset()).Equals(
                                item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("附件缺失、大小或 SHA-256 与登记值不一致，未生成分享包。");
                        }
                    }
                    else
                    {
                        await output.WriteAsync(item.Content, cancellationToken);
                    }
                }
            }
            File.Move(temp, destination, false);
            return destination;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

public sealed class ReviewBackupService : IReviewBackupService
{
    private const int CurrentFormatVersion = 1;
    private const string ManifestEntry = "manifest.json";
    private const string DatabaseEntry = "tradepet.db";
    private const long MaximumManifestBytes = 1024 * 1024;
    private const long MaximumEntryBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumTotalBytes = 64L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(ProtocolJson.Options) { WriteIndented = true };

    private readonly AppDatabase _database;
    private readonly string _attachmentRoot;
    private readonly string _databasePath;
    private readonly TimeProvider _timeProvider;
    internal Action<string>? RestoreCheckpoint { get; set; }

    public ReviewBackupService(
        AppDatabase database,
        string? attachmentRoot = null,
        string? databasePath = null,
        TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _attachmentRoot = Path.GetFullPath(attachmentRoot ?? TradePetPaths.GetAttachmentDirectory());
        _databasePath = Path.GetFullPath(databasePath ?? TradePetPaths.GetDatabasePath());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> CreateAsync(string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(outputDirectory ?? TradePetPaths.GetDatabaseBackupDirectory());
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory,
            $"TradePet-backup-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}"[..55] + ".zip");
        var snapshot = Path.Combine(directory, $".{Guid.NewGuid():N}.db");
        var tempArchive = destination + ".tmp";
        try
        {
            await using (var source = await _database.OpenConnectionAsync(cancellationToken))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = snapshot,
                             Pooling = false,
                         }.ToString()))
            {
                await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
            }

            var sourceFiles = new List<(string EntryPath, string SourcePath)> { (DatabaseEntry, snapshot) };
            if (Directory.Exists(_attachmentRoot))
            {
                foreach (var file in Directory.EnumerateFiles(_attachmentRoot, "*", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(_attachmentRoot, file).Replace('\\', '/');
                    sourceFiles.Add((NormalizeArchivePath("attachments/" + relative), file));
                }
            }

            var files = new List<ReviewBackupFile>(sourceFiles.Count);
            foreach (var sourceFile in sourceFiles)
            {
                var info = new FileInfo(sourceFile.SourcePath);
                files.Add(new ReviewBackupFile(sourceFile.EntryPath,
                    await HashFileAsync(sourceFile.SourcePath, cancellationToken), info.Length));
            }
            var manifest = new ReviewBackupManifest(CurrentFormatVersion, _timeProvider.GetUtcNow(), DatabaseEntry,
                files, await LoadAccountKeysAsync(snapshot, cancellationToken));

            await using (var output = new FileStream(tempArchive, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var sourceFile in sourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(sourceFile.EntryPath, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var input = new FileStream(sourceFile.SourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(entryStream, cancellationToken);
                }
                var manifestZipEntry = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var manifestStream = manifestZipEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
            }
            File.Move(tempArchive, destination, overwrite: false);
            return destination;
        }
        catch
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            throw;
        }
        finally
        {
            if (File.Exists(snapshot))
            {
                File.Delete(snapshot);
            }
            if (File.Exists(tempArchive))
            {
                File.Delete(tempArchive);
            }
        }
    }

    public async Task<ReviewBackupManifest> ValidateAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var validated = await ValidateAndExtractAsync(packagePath, cancellationToken);
        try
        {
            return validated.Manifest;
        }
        finally
        {
            DeleteDirectory(validated.Root);
        }
    }

    public async Task<ReviewBackupRestoreResult> RestoreAsync(
        string packagePath,
        string? safetyBackupDirectory = null,
        CancellationToken cancellationToken = default,
        bool preserveUnreadableCurrent = false)
    {
        await RecoverPendingAsync(cancellationToken);
        var validated = await ValidateAndExtractAsync(packagePath, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safetyBackup = preserveUnreadableCurrent
                ? await CreateRawSafetyCopyAsync(safetyBackupDirectory, cancellationToken)
                : await CreateAsync(safetyBackupDirectory, cancellationToken);
            SqliteConnection.ClearAllPools();
            var journal = new RestoreJournal(Guid.NewGuid().ToString("N"), "Prepared", safetyBackup,
                DatabaseFiles.Select(File.Exists).ToArray(), Directory.Exists(_attachmentRoot), validated.Manifest);
            WriteJournal(journal);
            var committed = false;
            try
            {
                Checkpoint("Prepared");
                File.Copy(validated.DatabasePath, StagedDatabase(journal), overwrite: false);
                Directory.CreateDirectory(StagedAttachments(journal));
                if (Directory.Exists(validated.AttachmentRoot))
                {
                    CopyDirectory(validated.AttachmentRoot, StagedAttachments(journal));
                }
                WriteJournal(journal with { Phase = "Staged" });
                Checkpoint("Staged");

                for (var index = 0; index < DatabaseFiles.Length; index++)
                {
                    if (!journal.OriginalDatabaseFiles[index])
                    {
                        continue;
                    }
                    WriteJournal(journal with { Phase = $"BeforeOldDatabase{index}" });
                    Checkpoint($"BeforeOldDatabase{index}");
                    File.Move(DatabaseFiles[index], PreviousDatabase(journal, index), overwrite: false);
                    WriteJournal(journal with { Phase = $"AfterOldDatabase{index}" });
                    Checkpoint($"AfterOldDatabase{index}");
                }
                WriteJournal(journal with { Phase = "BeforeNewDatabase" });
                Checkpoint("BeforeNewDatabase");
                File.Move(StagedDatabase(journal), _databasePath, overwrite: false);
                WriteJournal(journal with { Phase = "AfterNewDatabase" });
                Checkpoint("AfterNewDatabase");

                if (journal.OriginalAttachments)
                {
                    WriteJournal(journal with { Phase = "BeforeOldAttachments" });
                    Checkpoint("BeforeOldAttachments");
                    Directory.Move(_attachmentRoot, PreviousAttachments(journal));
                    WriteJournal(journal with { Phase = "AfterOldAttachments" });
                    Checkpoint("AfterOldAttachments");
                }
                WriteJournal(journal with { Phase = "BeforeNewAttachments" });
                Checkpoint("BeforeNewAttachments");
                Directory.Move(StagedAttachments(journal), _attachmentRoot);
                WriteJournal(journal with { Phase = "AfterNewAttachments" });
                Checkpoint("AfterNewAttachments");

                await _database.InitializeAsync(cancellationToken);
                await VerifyDatabaseAsync(_databasePath, validated.Manifest, cancellationToken);
                await VerifyInstalledAttachmentsAsync(validated.Manifest, cancellationToken);
                WriteJournal(journal with { Phase = "Committed" });
                committed = true;
                Checkpoint("Committed");
                CleanupCommitted(journal);
                return new ReviewBackupRestoreResult(safetyBackup, validated.Manifest.AccountKeys,
                    validated.Manifest.Files.Count(item => item.Path.StartsWith("attachments/", StringComparison.Ordinal)));
            }
            catch (SimulatedRestoreCrashException)
            {
                throw;
            }
            catch
            {
                await RecoverPendingAsync(CancellationToken.None);
                if (committed)
                {
                    return new ReviewBackupRestoreResult(safetyBackup, validated.Manifest.AccountKeys,
                        validated.Manifest.Files.Count(item => item.Path.StartsWith("attachments/", StringComparison.Ordinal)));
                }
                throw;
            }
        }
        finally
        {
            DeleteDirectory(validated.Root);
        }
    }

    public async Task<bool> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        var path = JournalPath;
        if (!File.Exists(path))
        {
            return false;
        }
        RestoreJournal journal;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            journal = await JsonSerializer.DeserializeAsync<RestoreJournal>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("恢复操作记录为空；为保护原数据，已停止自动切换。");
        }
        if (journal.Id is not { Length: 32 } || !journal.Id.All(Uri.IsHexDigit) ||
            journal.OriginalDatabaseFiles is not { Length: 3 } ||
            string.IsNullOrWhiteSpace(journal.SafetyBackupPath) ||
            !(File.Exists(journal.SafetyBackupPath) || Directory.Exists(journal.SafetyBackupPath)) ||
            journal.Manifest is null)
        {
            throw new InvalidDataException("恢复操作记录或安全备份无效；已保留现有文件等待人工处理。");
        }

        SqliteConnection.ClearAllPools();
        if (journal.Phase == "Committed")
        {
            await VerifyDatabaseAsync(_databasePath, journal.Manifest, cancellationToken);
            await VerifyInstalledAttachmentsAsync(journal.Manifest, cancellationToken);
            CleanupCommitted(journal);
            return true;
        }

        // Moves are inferred from both locations. A crash may occur between a move and its journal update.
        foreach (var index in new[] { 1, 2, 0 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = DatabaseFiles[index];
            var previous = PreviousDatabase(journal, index);
            if (File.Exists(previous))
            {
                DeleteIfExists(current);
                File.Move(previous, current, overwrite: false);
            }
            else if (index == 0 && journal.OriginalDatabaseFiles[index] && !File.Exists(current))
            {
                throw new InvalidDataException($"旧数据库文件 {index} 不在原位或保留位；已停止清理。");
            }
            else if (!journal.OriginalDatabaseFiles[index])
            {
                DeleteIfExists(current);
            }
        }

        var previousAttachments = PreviousAttachments(journal);
        if (Directory.Exists(previousAttachments))
        {
            DeleteDirectory(_attachmentRoot);
            Directory.Move(previousAttachments, _attachmentRoot);
        }
        else if (journal.OriginalAttachments && !Directory.Exists(_attachmentRoot))
        {
            throw new InvalidDataException("旧附件目录不在原位或保留位；已停止清理。");
        }
        else if (!journal.OriginalAttachments)
        {
            DeleteDirectory(_attachmentRoot);
        }

        await VerifyReadableDatabaseAsync(_databasePath, cancellationToken);
        CleanupStaged(journal);
        File.Delete(path);
        return true;
    }

    private async Task VerifyInstalledAttachmentsAsync(
        ReviewBackupManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files.Where(item =>
                     item.Path.StartsWith("attachments/", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveInside(_attachmentRoot, file.Path["attachments/".Length..]);
            if (!File.Exists(path) || new FileInfo(path).Length != file.SizeBytes ||
                !string.Equals(await HashFileAsync(path, cancellationToken), file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"切换后的附件未通过哈希复核：{file.Path}");
            }
        }
    }

    private string JournalPath => _databasePath + ".restore-state.json";
    private string[] DatabaseFiles => [_databasePath, _databasePath + "-wal", _databasePath + "-shm"];
    private string StagedDatabase(RestoreJournal journal) => _databasePath + ".restore-" + journal.Id;
    private string PreviousDatabase(RestoreJournal journal, int index) =>
        DatabaseFiles[index] + ".before-restore-" + journal.Id;
    private string StagedAttachments(RestoreJournal journal) =>
        Path.Combine(Path.GetDirectoryName(_attachmentRoot)!, ".attachments-restore-" + journal.Id);
    private string PreviousAttachments(RestoreJournal journal) =>
        Path.Combine(Path.GetDirectoryName(_attachmentRoot)!, ".attachments-before-restore-" + journal.Id);

    private void WriteJournal(RestoreJournal journal)
    {
        var temporary = JournalPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, JournalPath, overwrite: true);
    }

    private void CleanupCommitted(RestoreJournal journal)
    {
        for (var index = 0; index < DatabaseFiles.Length; index++)
        {
            DeleteIfExists(PreviousDatabase(journal, index));
        }
        DeleteDirectory(PreviousAttachments(journal));
        CleanupStaged(journal);
        File.Delete(JournalPath);
    }

    private void CleanupStaged(RestoreJournal journal)
    {
        DeleteIfExists(StagedDatabase(journal));
        DeleteDirectory(StagedAttachments(journal));
        DeleteIfExists(JournalPath + ".tmp");
    }

    private async Task<string> CreateRawSafetyCopyAsync(
        string? outputDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(outputDirectory ?? TradePetPaths.GetDatabaseBackupDirectory());
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root,
            $"TradePet-unreadable-before-restore-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        var files = new List<ReviewBackupFile>();
        foreach (var source in DatabaseFiles.Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(source);
            var target = Path.Combine(destination, name);
            File.Copy(source, target, overwrite: false);
            files.Add(new ReviewBackupFile(name, await HashFileAsync(target, cancellationToken),
                new FileInfo(target).Length));
        }
        if (Directory.Exists(_attachmentRoot))
        {
            var targetRoot = Path.Combine(destination, "attachments");
            Directory.CreateDirectory(targetRoot);
            CopyDirectory(_attachmentRoot, targetRoot);
            foreach (var target in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = "attachments/" + Path.GetRelativePath(targetRoot, target).Replace('\\', '/');
                files.Add(new ReviewBackupFile(relative,
                    await HashFileAsync(target, cancellationToken), new FileInfo(target).Length));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(destination, "raw-preservation.json"),
            JsonSerializer.Serialize(files, JsonOptions), cancellationToken);
        return destination;
    }

    private static async Task VerifyReadableDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException("旧数据库文件不存在；已保留恢复记录。 ");
        }
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)), "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("旧数据库未通过完整性检查；已保留恢复记录。 ");
        }
    }

    private void Checkpoint(string step) => RestoreCheckpoint?.Invoke(step);

    internal sealed class SimulatedRestoreCrashException : Exception;

    private sealed record RestoreJournal(string Id, string Phase, string SafetyBackupPath,
        bool[] OriginalDatabaseFiles, bool OriginalAttachments, ReviewBackupManifest Manifest);

    private async Task<ValidatedBackup> ValidateAndExtractAsync(string packagePath, CancellationToken cancellationToken)
    {
        var package = new FileInfo(Path.GetFullPath(packagePath));
        if (!package.Exists)
        {
            throw new FileNotFoundException("备份文件不存在。", package.FullName);
        }
        var root = Path.Combine(Path.GetTempPath(), "TradePetRestore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var archive = ZipFile.OpenRead(package.FullName);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                var normalized = NormalizeArchivePath(entry.FullName);
                if (!entries.TryAdd(normalized, entry))
                {
                    throw new InvalidDataException($"备份包含重复条目：{normalized}");
                }
                if (entry.Length < 0 || entry.Length > MaximumEntryBytes || totalBytes > MaximumTotalBytes - entry.Length)
                {
                    throw new InvalidDataException("备份解压大小超过限制。");
                }
                totalBytes += entry.Length;
            }
            if (!entries.TryGetValue(ManifestEntry, out var manifestEntry) || manifestEntry.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("备份缺少有效清单。");
            }
            ReviewBackupManifest manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<ReviewBackupManifest>(stream, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("备份清单无效。");
            }
            if (manifest.FormatVersion != CurrentFormatVersion ||
                !string.Equals(NormalizeArchivePath(manifest.DatabaseEntry), DatabaseEntry, StringComparison.Ordinal))
            {
                throw new InvalidDataException("不支持的备份格式。");
            }
            var declaredFiles = manifest.Files.ToDictionary(
                item => NormalizeArchivePath(item.Path), item => item, StringComparer.Ordinal);
            if (declaredFiles.Count != manifest.Files.Count || declaredFiles.ContainsKey(ManifestEntry) ||
                !declaredFiles.ContainsKey(DatabaseEntry) ||
                entries.Keys.Any(path => path != ManifestEntry && !declaredFiles.ContainsKey(path)) ||
                declaredFiles.Keys.Any(path => !entries.ContainsKey(path)))
            {
                throw new InvalidDataException("备份清单与 ZIP 条目不一致。");
            }

            foreach (var declared in declaredFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[declared.Key];
                if (entry.Length != declared.Value.SizeBytes)
                {
                    throw new InvalidDataException($"备份条目大小不匹配：{declared.Key}");
                }
                var target = ResolveInside(root, declared.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                }
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actualHash, declared.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"备份条目哈希不匹配：{declared.Key}");
                }
            }

            var databasePath = ResolveInside(root, DatabaseEntry);
            await VerifyDatabaseAsync(databasePath, manifest, cancellationToken);
            return new ValidatedBackup(root, databasePath, Path.Combine(root, "attachments"), manifest);
        }
        catch
        {
            DeleteDirectory(root);
            throw;
        }
    }

    private static async Task VerifyDatabaseAsync(
        string databasePath,
        ReviewBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("备份数据库未通过 SQLite 完整性检查。");
            }
        }
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_check;";
            if (Convert.ToInt64(await foreignKeys.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                throw new InvalidDataException("备份数据库包含无效外键。");
            }
        }

        var accounts = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT account_key FROM accounts ORDER BY account_key;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(reader.GetString(0));
            }
        }
        if (!accounts.SequenceEqual(manifest.AccountKeys.OrderBy(item => item, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("备份清单中的账户身份与数据库不一致。");
        }

        var files = manifest.Files.ToDictionary(item => NormalizeArchivePath(item.Path), StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sha256, size_bytes, relative_path FROM attachment_assets ORDER BY relative_path;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var relative = NormalizeArchivePath("attachments/" + reader.GetString(2));
                if (!files.TryGetValue(relative, out var file) ||
                    !string.Equals(file.Sha256, reader.GetString(0), StringComparison.OrdinalIgnoreCase) ||
                    file.SizeBytes != reader.GetInt64(1))
                {
                    throw new InvalidDataException($"附件清单与数据库引用不一致：{relative}");
                }
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM attachment_links l
                LEFT JOIN attachment_assets a ON a.id=l.attachment_id AND a.account_key=l.account_key
                WHERE a.id IS NULL;
                """;
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                throw new InvalidDataException("附件链接的账户身份无效。");
            }
        }
    }

    private static async Task<IReadOnlyList<string>> LoadAccountKeysAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_key FROM accounts ORDER BY account_key;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.EndsWith('/') ||
            normalized.Split('/').Any(part => part is ".." or "." or ""))
        {
            throw new InvalidDataException("备份条目路径无效。");
        }
        return normalized;
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(fullRoot,
            NormalizeArchivePath(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("备份条目路径超出临时目录。");
        }
        return target;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record ValidatedBackup(
        string Root,
        string DatabasePath,
        string AttachmentRoot,
        ReviewBackupManifest Manifest);
}
