using Microsoft.Data.Sqlite;
using TradePet.Core.Domain;
using TradePet.Infrastructure.Persistence;
using Xunit;

namespace TradePet.Infrastructure.Tests;

public sealed class AppDatabaseRecoveryTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), "TradePetRecoveryTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HealthyDatabase_IsNotArchived_AndPersistsAcrossInstances()
    {
        var databasePath = Path.Combine(_testDirectory, "tradepet.db");
        var backupRoot = Path.Combine(_testDirectory, "backups");
        var database = new AppDatabase(databasePath);
        var scope = new AccountScope("Broker", 1001);

        var firstInitialization = await database.InitializeWithRecoveryAsync(backupRoot);
        await database.UpsertAccountAsync(new AccountSnapshot(
            scope, "USD", 1_000m, 1_147m, 0m, 0,
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)));
        await database.UpsertTradingDayAsync(
            CreateDailyState(scope.AccountKey, new DateOnly(2026, 9, 2), 147m),
            DailyPlanSettings.BalancedDefault);

        var reopened = new AppDatabase(databasePath);
        var secondInitialization = await reopened.InitializeWithRecoveryAsync(backupRoot);
        var loaded = await reopened.LoadTradingDayAsync(scope.AccountKey, new DateOnly(2026, 9, 2));

        Assert.False(firstInitialization.Recovered);
        Assert.False(secondInitialization.Recovered);
        Assert.Equal(10, firstInitialization.SchemaVersion);
        Assert.Equal(10, secondInitialization.SchemaVersion);
        Assert.Empty(Directory.GetFileSystemEntries(backupRoot));
        Assert.Equal(147m, loaded?.State.RealizedPnl);
    }

    [Fact]
    public async Task CorruptDatabaseAndSidecars_ArePreservedWithoutAutomaticEmptyRebuild()
    {
        Directory.CreateDirectory(_testDirectory);
        var databasePath = Path.Combine(_testDirectory, "tradepet.db");
        var backupRoot = Path.Combine(_testDirectory, "backups");
        var corruptBytes = "this is not sqlite"u8.ToArray();
        await File.WriteAllBytesAsync(databasePath, corruptBytes);
        await File.WriteAllBytesAsync(databasePath + "-wal", [1, 2, 3]);
        await File.WriteAllBytesAsync(databasePath + "-shm", [4, 5, 6]);

        var database = new AppDatabase(databasePath);
        var exception = await Assert.ThrowsAsync<DatabaseRecoveryRequiredException>(() =>
            database.InitializeWithRecoveryAsync(backupRoot));

        Assert.NotNull(exception.SafetySnapshotDirectory);
        Assert.True(Directory.Exists(exception.SafetySnapshotDirectory));
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(
            Path.Combine(exception.SafetySnapshotDirectory!, "tradepet.db")));
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(databasePath));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(databasePath + "-wal"));
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(databasePath + "-shm"));
    }

    [Fact]
    public async Task FutureSchema_IsRejectedBeforeAnyMigrationOrWrite()
    {
        var databasePath = Path.Combine(_testDirectory, "future.db");
        var database = new AppDatabase(databasePath);
        await database.InitializeAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES (999, 'future');";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var oldBytes = await File.ReadAllBytesAsync(databasePath);

        var reopened = new AppDatabase(databasePath);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            reopened.InitializeWithRecoveryAsync(Path.Combine(_testDirectory, "backups")));

        Assert.Contains("schema 999", exception.Message);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(databasePath));
        Assert.Single(Directory.GetDirectories(Path.Combine(_testDirectory, "backups")));
    }

    [Fact]
    public async Task ForeignSqliteFile_IsNotTurnedIntoTradePetWorkspace()
    {
        Directory.CreateDirectory(_testDirectory);
        var databasePath = Path.Combine(_testDirectory, "foreign.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE alien_data(id INTEGER PRIMARY KEY); INSERT INTO alien_data VALUES (7);";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AppDatabase(databasePath).InitializeWithRecoveryAsync(Path.Combine(_testDirectory, "backups")));
        await using var checkConnection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await checkConnection.OpenAsync();
        await using var check = checkConnection.CreateCommand();
        check.CommandText = "SELECT id FROM alien_data;";
        Assert.Equal(7L, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EmptyExistingFile_IsNotSilentlyRecreated()
    {
        Directory.CreateDirectory(_testDirectory);
        var databasePath = Path.Combine(_testDirectory, "empty.db");
        await File.WriteAllBytesAsync(databasePath, []);

        await Assert.ThrowsAsync<DatabaseRecoveryRequiredException>(() =>
            new AppDatabase(databasePath).InitializeWithRecoveryAsync(Path.Combine(_testDirectory, "backups")));
        Assert.Equal(0, new FileInfo(databasePath).Length);
    }

    [Fact]
    public async Task InterruptedBeforeFirstMigration_ResumesFromEmptyLedger()
    {
        Directory.CreateDirectory(_testDirectory);
        var databasePath = Path.Combine(_testDirectory, "bootstrap.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        var result = await new AppDatabase(databasePath).InitializeWithRecoveryAsync(
            Path.Combine(_testDirectory, "backups"));
        Assert.False(result.Recovered);
        Assert.Equal(10, result.SchemaVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task HistoricalSchema_UpgradesOnceAndRetainsAccount(int historicalVersion)
    {
        Directory.CreateDirectory(_testDirectory);
        var databasePath = Path.Combine(_testDirectory, $"schema-{historicalVersion}.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            foreach (var migration in SchemaMigrations.All.Where(item => item.Version <= historicalVersion))
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync();
                await using var record = connection.CreateCommand();
                record.Transaction = (SqliteTransaction)transaction;
                record.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, 'historical');";
                record.Parameters.AddWithValue("$version", migration.Version);
                await record.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            await using var account = connection.CreateCommand();
            account.CommandText = "INSERT INTO accounts(account_key,server,login,currency,last_seen_utc) VALUES ('Legacy|1','Legacy',1,'USD','2026-09-01');";
            await account.ExecuteNonQueryAsync();
        }

        var database = new AppDatabase(databasePath);
        var first = await database.InitializeWithRecoveryAsync(Path.Combine(_testDirectory, "backups"));
        var repeated = await new AppDatabase(databasePath).InitializeWithRecoveryAsync(
            Path.Combine(_testDirectory, "backups"));

        Assert.Equal(10, first.SchemaVersion);
        Assert.Equal(10, repeated.SchemaVersion);
        await using var verified = await database.OpenConnectionAsync();
        await using var query = verified.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM accounts WHERE account_key='Legacy|1';";
        Assert.Equal(1L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE checksum='';";
        Assert.Equal(0L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MigrationChecksumMismatch_RejectsDatabaseBeforeWrite()
    {
        var databasePath = Path.Combine(_testDirectory, "checksum.db");
        var database = new AppDatabase(databasePath);
        await database.InitializeAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_migrations SET checksum='bad' WHERE version=4;";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(databasePath);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AppDatabase(databasePath).InitializeWithRecoveryAsync(Path.Combine(_testDirectory, "backups")));

        Assert.Contains("校验和不匹配", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath));
    }

    [Fact]
    public async Task DamagedTablePage_ReportsReadableRowsAndFailedTableWithoutRebuilding()
    {
        var databasePath = Path.Combine(_testDirectory, "partial-corruption.db");
        var database = new AppDatabase(databasePath);
        await database.InitializeAsync();
        await database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("Salvage", 12), "USD", 1m, 1m, 0m, 0,
            new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero)));
        long rootPage;
        long pageSize;
        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO trade_review_documents(
                    account_key,position_id,status,revision,source_version,rule_version,updated_at_utc,payload_json)
                VALUES ('Salvage|12',1,'Draft',1,'s','r','2026-09-09','{}');
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                INSERT INTO attachment_assets(id,account_key,sha256,file_name,media_type,size_bytes,relative_path,created_at_utc)
                VALUES ('asset-1','Salvage|12','abc','missing.png','image/png',1,'missing.png','2026-09-09');
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT rootpage FROM sqlite_master WHERE name='trade_review_documents';";
            rootPage = (long)(await command.ExecuteScalarAsync())!;
            command.CommandText = "PRAGMA page_size;";
            pageSize = (long)(await command.ExecuteScalarAsync())!;
        }
        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(databasePath);
        var offset = checked((int)((rootPage - 1) * pageSize));
        Assert.True(offset > 100 && offset < bytes.Length);
        bytes[offset] = 0;
        await File.WriteAllBytesAsync(databasePath, bytes);

        var error = await Assert.ThrowsAsync<DatabaseRecoveryRequiredException>(() =>
            new AppDatabase(databasePath).InitializeWithRecoveryAsync(Path.Combine(_testDirectory, "backups")));

        Assert.NotNull(error.SalvageReport);
        Assert.Equal(1, error.SalvageReport.ReadableRowCounts["accounts"]);
        Assert.Contains("trade_review_documents", error.SalvageReport.FailedTables);
        Assert.Equal(1, error.SalvageReport.MissingAttachmentCount);
        Assert.Contains("missing.png", error.SalvageReport.MissingAttachmentPaths);
        var accountsJsonl = Path.Combine(error.SafetySnapshotDirectory!,
            error.SalvageReport.SalvagedRowFiles["accounts"]);
        Assert.Contains("Salvage|12", await File.ReadAllTextAsync(accountsJsonl));
        Assert.True(File.Exists(Path.Combine(error.SafetySnapshotDirectory!, "salvage-report.json")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(databasePath));
    }

    [Fact]
    public async Task MigrationFailure_RollsBackWholeUpgradeAndCanResume()
    {
        Directory.CreateDirectory(_testDirectory);
        var databasePath = Path.Combine(_testDirectory, "interrupted-upgrade.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = SchemaMigrations.All[0].Sql;
            await command.ExecuteNonQueryAsync();
            command.CommandText = "INSERT INTO schema_migrations(version,applied_at_utc) VALUES (1,'legacy');";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "INSERT INTO accounts(account_key,server,login,currency,last_seen_utc) VALUES ('Legacy|12','Legacy',12,'USD','2026-09-01');";
            await command.ExecuteNonQueryAsync();
        }

        var database = new AppDatabase(databasePath)
        {
            MigrationCheckpoint = version =>
            {
                if (version == 4)
                {
                    throw new IOException("injected migration interruption");
                }
            },
        };
        await Assert.ThrowsAsync<IOException>(() => database.InitializeWithRecoveryAsync(
            Path.Combine(_testDirectory, "backups")));
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM accounts WHERE account_key='Legacy|12';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        var repeated = await new AppDatabase(databasePath).InitializeWithRecoveryAsync(
            Path.Combine(_testDirectory, "backups"));
        Assert.Equal(10, repeated.SchemaVersion);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static DailyState CreateDailyState(string accountKey, DateOnly date, decimal realizedPnl) =>
        new(accountKey, date, realizedPnl, 0m, Math.Max(realizedPnl, 0m), 0m, 1,
            realizedPnl > 0 ? 1 : 0, realizedPnl < 0 ? 1 : 0, realizedPnl < 0 ? 1 : 0,
            0.01m, false, false, false);
}
