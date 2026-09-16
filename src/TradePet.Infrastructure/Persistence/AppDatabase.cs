using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Persistence;

public sealed partial class AppDatabase : IReviewWorkspaceRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    internal Func<string, CancellationToken, Task>? ReviewReadCheckpointAsync { get; set; }
    internal Action<int>? MigrationCheckpoint { get; set; }

    public AppDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _databasePath = fullPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSupportedSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var migrationTransaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.Transaction = (SqliteTransaction)migrationTransaction;
            bootstrap.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL);";
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var migration in SchemaMigrations.All)
        {
            await ApplyMigrationAsync(connection, (SqliteTransaction)migrationTransaction,
                migration, cancellationToken);
        }
        await migrationTransaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureSupportedSchemaAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return;
        }
        if (new FileInfo(_databasePath).Length == 0)
        {
            throw new InvalidDataException("现有数据库是空文件；已停止初始化，避免覆盖未确认的工作区。");
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
        if (Convert.ToInt64(await table.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            await using var anyTable = connection.CreateCommand();
            anyTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            if (Convert.ToInt64(await anyTable.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                return;
            }
            throw new InvalidDataException("现有 SQLite 文件没有 TradePet 迁移记录；已拒绝把未知数据库当作新工作区写入。");
        }
        await using var versions = connection.CreateCommand();
        versions.CommandText = "SELECT COUNT(*), MIN(version), MAX(version) FROM schema_migrations;";
        await using var reader = await versions.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var count = reader.GetInt64(0);
        var minimum = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        var maximum = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
        var supported = SchemaMigrations.All[^1].Version;
        await reader.DisposeAsync();
        if (count == 0)
        {
            await using var otherTables = connection.CreateCommand();
            otherTables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT IN ('schema_migrations', 'sqlite_sequence');";
            if (Convert.ToInt64(await otherTables.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                // A process may have stopped after creating the migration ledger but before migration 1.
                return;
            }
        }
        if (maximum > supported)
        {
            throw new InvalidDataException(
                $"工作区 schema {maximum} 高于本程序支持的 {supported}；已拒绝写入。请使用兼容的新版本或恢复副本。");
        }
        if (count == 0 || minimum != 1 || count != maximum)
        {
            throw new InvalidDataException("工作区迁移记录不连续；已拒绝继续写入并保留现有文件。");
        }
        if (maximum >= 10)
        {
            await using var checksums = connection.CreateCommand();
            checksums.CommandText = "SELECT version, checksum FROM schema_migrations ORDER BY version;";
            await using var rows = await checksums.ExecuteReaderAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
            {
                var migration = SchemaMigrations.All.Single(item => item.Version == rows.GetInt64(0));
                if (!string.Equals(rows.GetString(1), SchemaMigrations.Checksum(migration),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"工作区迁移 {migration.Version} 的校验和不匹配；已拒绝继续写入。");
                }
            }
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task<bool> TryMarkEventProcessedAsync(
        string sourceInstanceId,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO processed_events(source_instance_id, sequence, processed_at_utc)
            VALUES ($source, $sequence, $now);
            """;
        command.Parameters.AddWithValue("$source", sourceInstanceId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpsertAccountAsync(AccountSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts(account_key, server, login, currency, last_seen_utc)
            VALUES ($key, $server, $login, $currency, $seen)
            ON CONFLICT(account_key) DO UPDATE SET
                currency = excluded.currency,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$key", snapshot.Scope.AccountKey);
        command.Parameters.AddWithValue("$server", snapshot.Scope.Server);
        command.Parameters.AddWithValue("$login", snapshot.Scope.Login);
        command.Parameters.AddWithValue("$currency", snapshot.Currency);
        command.Parameters.AddWithValue("$seen", Format(snapshot.CapturedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertTradingDayAsync(
        DailyState state,
        DailyPlanSettings settings,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trading_days(account_key, server_date, settings_json, daily_state_json, updated_at_utc)
            VALUES ($account, $date, $settings, $state, $updated)
            ON CONFLICT(account_key, server_date) DO UPDATE SET
                settings_json = excluded.settings_json,
                daily_state_json = excluded.daily_state_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$account", state.AccountKey);
        command.Parameters.AddWithValue("$date", Format(state.ServerDate));
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings, ProtocolJson.Options));
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, ProtocolJson.Options));
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(DailyState State, DailyPlanSettings Settings)?> LoadTradingDayAsync(
        string accountKey,
        DateOnly serverDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT settings_json, daily_state_json
            FROM trading_days
            WHERE account_key = $account AND server_date = $date;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$date", Format(serverDate));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var settings = JsonSerializer.Deserialize<DailyPlanSettings>(reader.GetString(0), ProtocolJson.Options)
            ?? throw new InvalidDataException("Stored daily settings are invalid.");
        var state = JsonSerializer.Deserialize<DailyState>(reader.GetString(1), ProtocolJson.Options)
            ?? throw new InvalidDataException("Stored daily state is invalid.");
        return (state, settings);
    }

    public async Task UpsertDealAsync(string accountKey, DealRecord deal, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO deals(
                account_key, ticket, order_ticket, position_id, symbol, side, entry_kind,
                volume, price, profit, commission, swap, fee, occurred_at_utc)
            VALUES (
                $account, $ticket, $order, $position, $symbol, $side, $entry,
                $volume, $price, $profit, $commission, $swap, $fee, $occurred)
            ON CONFLICT(account_key, ticket) DO UPDATE SET
                order_ticket = excluded.order_ticket,
                position_id = excluded.position_id,
                symbol = excluded.symbol,
                side = excluded.side,
                entry_kind = excluded.entry_kind,
                volume = excluded.volume,
                price = excluded.price,
                profit = excluded.profit,
                commission = excluded.commission,
                swap = excluded.swap,
                fee = excluded.fee,
                occurred_at_utc = excluded.occurred_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$ticket", deal.Ticket);
        command.Parameters.AddWithValue("$order", deal.OrderTicket);
        command.Parameters.AddWithValue("$position", deal.PositionId);
        command.Parameters.AddWithValue("$symbol", deal.Symbol);
        command.Parameters.AddWithValue("$side", deal.Side.ToString());
        command.Parameters.AddWithValue("$entry", deal.EntryKind.ToString());
        command.Parameters.AddWithValue("$volume", deal.Volume);
        command.Parameters.AddWithValue("$price", deal.Price);
        command.Parameters.AddWithValue("$profit", deal.Profit);
        command.Parameters.AddWithValue("$commission", deal.Commission);
        command.Parameters.AddWithValue("$swap", deal.Swap);
        command.Parameters.AddWithValue("$fee", deal.Fee);
        command.Parameters.AddWithValue("$occurred", Format(deal.OccurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DealRecord>> LoadDealsAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticket, order_ticket, position_id, symbol, side, entry_kind, volume, price,
                   profit, commission, swap, fee, occurred_at_utc
            FROM deals
            WHERE account_key = $account
            ORDER BY occurred_at_utc, ticket;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new List<DealRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DealRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                Enum.Parse<TradeSide>(reader.GetString(4)), Enum.Parse<DealEntryKind>(reader.GetString(5)),
                reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9),
                reader.GetDecimal(10), reader.GetDecimal(11), ParseTimestamp(reader.GetString(12))));
        }

        return result;
    }

    public async Task UpsertTradeAsync(TradeRecord trade, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trades(
                account_key, position_id, symbol, side, opened_at_utc, closed_at_utc,
                open_server_date, close_server_date, entry_price, exit_price, opening_volume,
                maximum_volume, remaining_volume, net_pnl, is_complete)
            VALUES (
                $account, $position, $symbol, $side, $opened, $closed,
                $openDate, $closeDate, $entry, $exit, $openingVolume,
                $maximumVolume, $remainingVolume, $netPnl, $complete)
            ON CONFLICT(account_key, position_id) DO UPDATE SET
                symbol = excluded.symbol,
                side = excluded.side,
                opened_at_utc = excluded.opened_at_utc,
                closed_at_utc = excluded.closed_at_utc,
                open_server_date = excluded.open_server_date,
                close_server_date = excluded.close_server_date,
                entry_price = excluded.entry_price,
                exit_price = excluded.exit_price,
                opening_volume = excluded.opening_volume,
                maximum_volume = excluded.maximum_volume,
                remaining_volume = excluded.remaining_volume,
                net_pnl = excluded.net_pnl,
                is_complete = excluded.is_complete;
            """;
        command.Parameters.AddWithValue("$account", trade.AccountKey);
        command.Parameters.AddWithValue("$position", trade.PositionId);
        command.Parameters.AddWithValue("$symbol", trade.Symbol);
        command.Parameters.AddWithValue("$side", trade.Side.ToString());
        command.Parameters.AddWithValue("$opened", Format(trade.OpenedAtUtc));
        command.Parameters.AddWithValue("$closed", DbValue(trade.ClosedAtUtc is null ? null : Format(trade.ClosedAtUtc.Value)));
        command.Parameters.AddWithValue("$openDate", Format(trade.OpenServerDate));
        command.Parameters.AddWithValue("$closeDate", DbValue(trade.CloseServerDate is null ? null : Format(trade.CloseServerDate.Value)));
        command.Parameters.AddWithValue("$entry", trade.EntryPrice);
        command.Parameters.AddWithValue("$exit", DbValue(trade.ExitPrice));
        command.Parameters.AddWithValue("$openingVolume", trade.OpeningVolume);
        command.Parameters.AddWithValue("$maximumVolume", trade.MaximumVolume);
        command.Parameters.AddWithValue("$remainingVolume", trade.RemainingVolume);
        command.Parameters.AddWithValue("$netPnl", trade.NetPnl);
        command.Parameters.AddWithValue("$complete", trade.IsComplete ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplacePositionsAsync(
        string accountKey,
        IReadOnlyCollection<PositionSnapshot> positions,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM positions WHERE account_key = $account;";
            delete.Parameters.AddWithValue("$account", accountKey);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var position in positions)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO positions(account_key, ticket, position_id, payload_json, captured_at_utc)
                VALUES ($account, $ticket, $position, $payload, $captured);
                """;
            insert.Parameters.AddWithValue("$account", accountKey);
            insert.Parameters.AddWithValue("$ticket", position.Ticket);
            insert.Parameters.AddWithValue("$position", position.PositionId);
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(position, ProtocolJson.Options));
            insert.Parameters.AddWithValue("$captured", Format(position.CapturedAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertChartObjectAsync(
        ChartObjectSnapshot chartObject,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(chartObject, ProtocolJson.Options);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = (SqliteTransaction)transaction;
            upsert.CommandText = """
                INSERT INTO chart_objects(
                    object_key, terminal_id, chart_id, object_name, symbol, timeframe, kind,
                    payload_json, content_hash, captured_at_utc, deleted_at_utc)
                VALUES ($key, $terminal, $chart, $name, $symbol, $timeframe, $kind,
                    $payload, $hash, $captured, $deleted)
                ON CONFLICT(object_key) DO UPDATE SET
                    symbol = excluded.symbol,
                    timeframe = excluded.timeframe,
                    kind = excluded.kind,
                    payload_json = excluded.payload_json,
                    content_hash = excluded.content_hash,
                    captured_at_utc = excluded.captured_at_utc,
                    deleted_at_utc = excluded.deleted_at_utc;
                """;
            upsert.Parameters.AddWithValue("$key", chartObject.ObjectKey);
            upsert.Parameters.AddWithValue("$terminal", chartObject.TerminalId);
            upsert.Parameters.AddWithValue("$chart", chartObject.ChartId);
            upsert.Parameters.AddWithValue("$name", chartObject.ObjectName);
            upsert.Parameters.AddWithValue("$symbol", chartObject.Symbol);
            upsert.Parameters.AddWithValue("$timeframe", chartObject.Timeframe);
            upsert.Parameters.AddWithValue("$kind", chartObject.Kind.ToString());
            upsert.Parameters.AddWithValue("$payload", payload);
            upsert.Parameters.AddWithValue("$hash", contentHash);
            upsert.Parameters.AddWithValue("$captured", Format(chartObject.CapturedAtUtc));
            upsert.Parameters.AddWithValue("$deleted", DbValue(chartObject.IsDeleted ? Format(chartObject.CapturedAtUtc) : null));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var revision = connection.CreateCommand())
        {
            revision.Transaction = (SqliteTransaction)transaction;
            revision.CommandText = """
                INSERT INTO chart_object_revisions(object_key, content_hash, payload_json, recorded_at_utc, is_deleted)
                SELECT $key, $hash, $payload, $recorded, $deleted
                WHERE NOT EXISTS (
                    SELECT 1 FROM chart_object_revisions
                    WHERE object_key = $key AND content_hash = $hash AND is_deleted = $deleted
                );
                """;
            revision.Parameters.AddWithValue("$key", chartObject.ObjectKey);
            revision.Parameters.AddWithValue("$hash", contentHash);
            revision.Parameters.AddWithValue("$payload", payload);
            revision.Parameters.AddWithValue("$recorded", Format(chartObject.CapturedAtUtc));
            revision.Parameters.AddWithValue("$deleted", chartObject.IsDeleted ? 1 : 0);
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertPlanItemAsync(PlanItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plan_items(
                id, account_key, server_date, object_key, category, symbol,
                price_low, price_high, text, is_active, updated_at_utc)
            VALUES ($id, $account, $date, $object, $category, $symbol,
                $low, $high, $text, $active, $updated)
            ON CONFLICT(id) DO UPDATE SET
                category = excluded.category,
                price_low = excluded.price_low,
                price_high = excluded.price_high,
                text = excluded.text,
                is_active = excluded.is_active,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$account", item.AccountKey);
        command.Parameters.AddWithValue("$date", Format(item.ServerDate));
        command.Parameters.AddWithValue("$object", item.ObjectKey);
        command.Parameters.AddWithValue("$category", item.Category.ToString());
        command.Parameters.AddWithValue("$symbol", item.Symbol);
        command.Parameters.AddWithValue("$low", DbValue(item.PriceLow));
        command.Parameters.AddWithValue("$high", DbValue(item.PriceHigh));
        command.Parameters.AddWithValue("$text", DbValue(item.Text));
        command.Parameters.AddWithValue("$active", item.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$updated", Format(item.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertLossZoneAsync(LossZoneState zone, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO loss_zones(
                id, account_key, server_date, symbol, center_price, tolerance,
                attempt_count, loss_count, cumulative_loss, last_attempt_at_utc)
            VALUES ($id, $account, $date, $symbol, $center, $tolerance,
                $attempts, $losses, $cumulative, $last)
            ON CONFLICT(id) DO UPDATE SET
                center_price = excluded.center_price,
                tolerance = excluded.tolerance,
                attempt_count = excluded.attempt_count,
                loss_count = excluded.loss_count,
                cumulative_loss = excluded.cumulative_loss,
                last_attempt_at_utc = excluded.last_attempt_at_utc;
            """;
        command.Parameters.AddWithValue("$id", zone.Id);
        command.Parameters.AddWithValue("$account", zone.AccountKey);
        command.Parameters.AddWithValue("$date", Format(zone.ServerDate));
        command.Parameters.AddWithValue("$symbol", zone.Symbol);
        command.Parameters.AddWithValue("$center", zone.CenterPrice);
        command.Parameters.AddWithValue("$tolerance", zone.Tolerance);
        command.Parameters.AddWithValue("$attempts", zone.AttemptCount);
        command.Parameters.AddWithValue("$losses", zone.LossCount);
        command.Parameters.AddWithValue("$cumulative", zone.CumulativeLoss);
        command.Parameters.AddWithValue("$last", Format(zone.LastAttemptAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertLossZoneAttemptAsync(LossZoneAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO loss_zone_attempts(
                id, zone_id, position_id, side, entry_price, opening_volume,
                net_pnl, opened_at_utc, closed_at_utc)
            VALUES ($id, $zone, $position, $side, $entry, $volume,
                $pnl, $opened, $closed)
            ON CONFLICT(id) DO UPDATE SET
                net_pnl = excluded.net_pnl,
                closed_at_utc = excluded.closed_at_utc;
            """;
        command.Parameters.AddWithValue("$id", attempt.Id);
        command.Parameters.AddWithValue("$zone", attempt.ZoneId);
        command.Parameters.AddWithValue("$position", attempt.PositionId);
        command.Parameters.AddWithValue("$side", attempt.Side.ToString());
        command.Parameters.AddWithValue("$entry", attempt.EntryPrice);
        command.Parameters.AddWithValue("$volume", attempt.OpeningVolume);
        command.Parameters.AddWithValue("$pnl", DbValue(attempt.NetPnl));
        command.Parameters.AddWithValue("$opened", Format(attempt.OpenedAtUtc));
        command.Parameters.AddWithValue("$closed", DbValue(attempt.ClosedAtUtc is null ? null : Format(attempt.ClosedAtUtc.Value)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceLossZoneProjectionAsync(
        string accountKey,
        DateOnly serverDate,
        LossZoneProjection projection,
        CancellationToken cancellationToken = default)
    {
        if (projection.Zones.Any(zone => zone.AccountKey != accountKey || zone.ServerDate != serverDate))
        {
            throw new ArgumentException("Loss-zone projection contains a different account or server date.", nameof(projection));
        }

        var zoneIds = projection.Zones.Select(zone => zone.Id).ToHashSet(StringComparer.Ordinal);
        if (projection.Attempts.Any(attempt => !zoneIds.Contains(attempt.ZoneId)) ||
            projection.Attempts.GroupBy(attempt => attempt.PositionId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Every loss-zone attempt must belong to one projected zone and use a unique position id.", nameof(projection));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteAttempts = connection.CreateCommand())
        {
            deleteAttempts.Transaction = (SqliteTransaction)transaction;
            deleteAttempts.CommandText = """
                DELETE FROM loss_zone_attempts
                WHERE zone_id IN (
                    SELECT id FROM loss_zones WHERE account_key = $account AND server_date = $date
                );
                """;
            deleteAttempts.Parameters.AddWithValue("$account", accountKey);
            deleteAttempts.Parameters.AddWithValue("$date", Format(serverDate));
            await deleteAttempts.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteZones = connection.CreateCommand())
        {
            deleteZones.Transaction = (SqliteTransaction)transaction;
            deleteZones.CommandText = "DELETE FROM loss_zones WHERE account_key = $account AND server_date = $date;";
            deleteZones.Parameters.AddWithValue("$account", accountKey);
            deleteZones.Parameters.AddWithValue("$date", Format(serverDate));
            await deleteZones.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var zone in projection.Zones)
        {
            await using var insertZone = connection.CreateCommand();
            insertZone.Transaction = (SqliteTransaction)transaction;
            insertZone.CommandText = """
                INSERT INTO loss_zones(
                    id, account_key, server_date, symbol, center_price, tolerance,
                    attempt_count, loss_count, cumulative_loss, last_attempt_at_utc)
                VALUES ($id, $account, $date, $symbol, $center, $tolerance,
                    $attempts, $losses, $cumulative, $last);
                """;
            insertZone.Parameters.AddWithValue("$id", zone.Id);
            insertZone.Parameters.AddWithValue("$account", zone.AccountKey);
            insertZone.Parameters.AddWithValue("$date", Format(zone.ServerDate));
            insertZone.Parameters.AddWithValue("$symbol", zone.Symbol);
            insertZone.Parameters.AddWithValue("$center", zone.CenterPrice);
            insertZone.Parameters.AddWithValue("$tolerance", zone.Tolerance);
            insertZone.Parameters.AddWithValue("$attempts", zone.AttemptCount);
            insertZone.Parameters.AddWithValue("$losses", zone.LossCount);
            insertZone.Parameters.AddWithValue("$cumulative", zone.CumulativeLoss);
            insertZone.Parameters.AddWithValue("$last", Format(zone.LastAttemptAtUtc));
            await insertZone.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var attempt in projection.Attempts)
        {
            await using var insertAttempt = connection.CreateCommand();
            insertAttempt.Transaction = (SqliteTransaction)transaction;
            insertAttempt.CommandText = """
                INSERT INTO loss_zone_attempts(
                    id, zone_id, position_id, side, entry_price, opening_volume,
                    net_pnl, opened_at_utc, closed_at_utc)
                VALUES ($id, $zone, $position, $side, $entry, $volume,
                    $pnl, $opened, $closed);
                """;
            insertAttempt.Parameters.AddWithValue("$id", attempt.Id);
            insertAttempt.Parameters.AddWithValue("$zone", attempt.ZoneId);
            insertAttempt.Parameters.AddWithValue("$position", attempt.PositionId);
            insertAttempt.Parameters.AddWithValue("$side", attempt.Side.ToString());
            insertAttempt.Parameters.AddWithValue("$entry", attempt.EntryPrice);
            insertAttempt.Parameters.AddWithValue("$volume", attempt.OpeningVolume);
            insertAttempt.Parameters.AddWithValue("$pnl", DbValue(attempt.NetPnl));
            insertAttempt.Parameters.AddWithValue("$opened", Format(attempt.OpenedAtUtc));
            insertAttempt.Parameters.AddWithValue("$closed", DbValue(
                attempt.ClosedAtUtc is null ? null : Format(attempt.ClosedAtUtc.Value)));
            await insertAttempt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddTimelineEventAsync(TimelineEvent timelineEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO timeline_events(
                id, account_key, server_date, occurred_at_utc, kind, summary, details_json)
            VALUES ($id, $account, $date, $occurred, $kind, $summary, $details);
            """;
        command.Parameters.AddWithValue("$id", timelineEvent.Id);
        command.Parameters.AddWithValue("$account", timelineEvent.AccountKey);
        command.Parameters.AddWithValue("$date", Format(timelineEvent.ServerDate));
        command.Parameters.AddWithValue("$occurred", Format(timelineEvent.OccurredAtUtc));
        command.Parameters.AddWithValue("$kind", timelineEvent.Kind.ToString());
        command.Parameters.AddWithValue("$summary", timelineEvent.Summary);
        command.Parameters.AddWithValue("$details", timelineEvent.DetailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveSettingAsync<T>(
        string scopeKey,
        string settingKey,
        T value,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(scope_key, setting_key, value_json, updated_at_utc)
            VALUES ($scope, $key, $value, $updated)
            ON CONFLICT(scope_key, setting_key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$key", settingKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, ProtocolJson.Options));
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T?> LoadSettingAsync<T>(
        string scopeKey,
        string settingKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM settings WHERE scope_key = $scope AND setting_key = $key;";
        command.Parameters.AddWithValue("$scope", scopeKey);
        command.Parameters.AddWithValue("$key", settingKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string json ? JsonSerializer.Deserialize<T>(json, ProtocolJson.Options) : default;
    }

    public async Task<IReadOnlyList<ChartObjectSnapshot>> LoadChartObjectsAsync(
        string? terminalId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = terminalId is null
            ? "SELECT payload_json FROM chart_objects WHERE deleted_at_utc IS NULL ORDER BY captured_at_utc;"
            : "SELECT payload_json FROM chart_objects WHERE terminal_id = $terminal AND deleted_at_utc IS NULL ORDER BY captured_at_utc;";
        if (terminalId is not null)
        {
            command.Parameters.AddWithValue("$terminal", terminalId);
        }

        var items = new List<ChartObjectSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = JsonSerializer.Deserialize<ChartObjectSnapshot>(reader.GetString(0), ProtocolJson.Options);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    public async Task<IReadOnlyList<PlanItem>> LoadPlanItemsAsync(
        string accountKey,
        DateOnly serverDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, object_key, category, symbol, price_low, price_high, text, is_active, updated_at_utc
            FROM plan_items
            WHERE account_key = $account AND server_date = $date
            ORDER BY updated_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$date", Format(serverDate));
        var items = new List<PlanItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PlanItem(
                reader.GetString(0),
                accountKey,
                serverDate,
                reader.GetString(1),
                Enum.Parse<PlanCategory>(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7) == 1,
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }

        return items;
    }

    public async Task<IReadOnlyList<LossZoneState>> LoadLossZonesAsync(
        string accountKey,
        DateOnly serverDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, symbol, center_price, tolerance, attempt_count, loss_count, cumulative_loss, last_attempt_at_utc
            FROM loss_zones
            WHERE account_key = $account AND server_date = $date
            ORDER BY last_attempt_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$date", Format(serverDate));
        var items = new List<LossZoneState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LossZoneState(
                reader.GetString(0), accountKey, serverDate, reader.GetString(1),
                reader.GetDecimal(2), reader.GetDecimal(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetDecimal(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
        }

        return items;
    }

    public async Task<IReadOnlyList<LossZoneAttempt>> LoadLossZoneAttemptsAsync(
        IReadOnlyCollection<string> zoneIds,
        CancellationToken cancellationToken = default)
    {
        if (zoneIds.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameterNames = zoneIds.Select((_, index) => $"$zone{index}").ToArray();
        command.CommandText = $"""
            SELECT id, zone_id, position_id, side, entry_price, opening_volume, net_pnl, opened_at_utc, closed_at_utc
            FROM loss_zone_attempts
            WHERE zone_id IN ({string.Join(",", parameterNames)})
            ORDER BY opened_at_utc;
            """;
        var index = 0;
        foreach (var zoneId in zoneIds)
        {
            command.Parameters.AddWithValue(parameterNames[index++], zoneId);
        }

        var items = new List<LossZoneAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LossZoneAttempt(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Enum.Parse<TradeSide>(reader.GetString(3)),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }

        return items;
    }

    public async Task<IReadOnlyList<TimelineEvent>> LoadTimelineAsync(
        string accountKey,
        DateOnly serverDate,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, occurred_at_utc, kind, summary, details_json
            FROM timeline_events
            WHERE account_key = $account AND server_date = $date
            ORDER BY occurred_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$date", Format(serverDate));
        command.Parameters.AddWithValue("$limit", limit);
        var items = new List<TimelineEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new TimelineEvent(
                reader.GetString(0), accountKey, serverDate,
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Enum.Parse<TimelineKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4)));
        }

        return items;
    }

    private async Task ApplyMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SchemaMigration migration,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = $version;";
        exists.Parameters.AddWithValue("$version", migration.Version);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = migration.Sql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (migration.Version == 10)
        {
            foreach (var previous in SchemaMigrations.All.Where(item => item.Version < 10))
            {
                await using var stamp = connection.CreateCommand();
                stamp.Transaction = transaction;
                stamp.CommandText = "UPDATE schema_migrations SET checksum=$checksum WHERE version=$version;";
                stamp.Parameters.AddWithValue("$checksum", SchemaMigrations.Checksum(previous));
                stamp.Parameters.AddWithValue("$version", previous.Version);
                await stamp.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = migration.Version < 10
            ? "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, $applied);"
            : "INSERT INTO schema_migrations(version, applied_at_utc, checksum) VALUES ($version, $applied, $checksum);";
        record.Parameters.AddWithValue("$version", migration.Version);
        record.Parameters.AddWithValue("$applied", Format(DateTimeOffset.UtcNow));
        if (migration.Version >= 10)
        {
            record.Parameters.AddWithValue("$checksum", SchemaMigrations.Checksum(migration));
        }
        await record.ExecuteNonQueryAsync(cancellationToken);
        MigrationCheckpoint?.Invoke(migration.Version);
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Format(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
