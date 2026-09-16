using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;
using TradePet.Core.Trading;

namespace TradePet.Infrastructure.Persistence;

public sealed partial class AppDatabase
{
    public async Task<bool> TryMarkAlertDeliveredAsync(
        string accountKey,
        DateOnly serverDate,
        CombinedAlert alert,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO alert_deliveries(id, account_key, server_date, alert_key, delivered_at_utc, payload_json) VALUES (@id, @account, @date, @key, @delivered, @payload);";
        command.Parameters.AddWithValue("@id", AlertDeliveryIdentity.Create(accountKey, serverDate, alert.Id));
        command.Parameters.AddWithValue("@account", accountKey);
        command.Parameters.AddWithValue("@date", Format(serverDate));
        command.Parameters.AddWithValue("@key", alert.Id);
        command.Parameters.AddWithValue("@delivered", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(alert, ProtocolJson.Options));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpsertStructuredTradePlanAsync(
        StructuredTradePlan plan,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO structured_trade_plans(
                id, account_key, server_date, symbol, side, reference_entry_price,
                entry_low, entry_high, stop_price, target_price, strategy, setup,
                tags_json, notes, is_active, created_at_utc, updated_at_utc)
            VALUES ($id, $account, $date, $symbol, $side, $reference,
                $low, $high, $stop, $target, $strategy, $setup,
                $tags, $notes, $active, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                symbol = excluded.symbol,
                side = excluded.side,
                reference_entry_price = excluded.reference_entry_price,
                entry_low = excluded.entry_low,
                entry_high = excluded.entry_high,
                stop_price = excluded.stop_price,
                target_price = excluded.target_price,
                strategy = excluded.strategy,
                setup = excluded.setup,
                tags_json = excluded.tags_json,
                notes = excluded.notes,
                is_active = excluded.is_active,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", plan.Id);
        command.Parameters.AddWithValue("$account", plan.AccountKey);
        command.Parameters.AddWithValue("$date", Format(plan.ServerDate));
        command.Parameters.AddWithValue("$symbol", plan.Symbol);
        command.Parameters.AddWithValue("$side", plan.Side.ToString());
        command.Parameters.AddWithValue("$reference", DbValue(plan.ReferenceEntryPrice));
        command.Parameters.AddWithValue("$low", DbValue(plan.EntryLow));
        command.Parameters.AddWithValue("$high", DbValue(plan.EntryHigh));
        command.Parameters.AddWithValue("$stop", DbValue(plan.StopPrice));
        command.Parameters.AddWithValue("$target", DbValue(plan.TargetPrice));
        command.Parameters.AddWithValue("$strategy", plan.Strategy.Trim());
        command.Parameters.AddWithValue("$setup", plan.Setup.Trim());
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(NormalizeTags(plan.Tags), ProtocolJson.Options));
        command.Parameters.AddWithValue("$notes", plan.Notes.Trim());
        command.Parameters.AddWithValue("$active", plan.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$created", Format(plan.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", Format(plan.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StructuredTradePlan>> LoadStructuredTradePlansAsync(
        string accountKey,
        DateOnly fromServerDate,
        DateOnly toServerDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, server_date, symbol, side, reference_entry_price, entry_low, entry_high,
                   stop_price, target_price, strategy, setup, tags_json, notes, is_active,
                   created_at_utc, updated_at_utc
            FROM structured_trade_plans
            WHERE account_key = $account AND server_date BETWEEN $from AND $to
            ORDER BY created_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromServerDate));
        command.Parameters.AddWithValue("$to", Format(toServerDate));
        var result = new List<StructuredTradePlan>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StructuredTradePlan(
                reader.GetString(0), accountKey, DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(2), Enum.Parse<TradeSide>(reader.GetString(3)),
                NullableDecimal(reader, 4), NullableDecimal(reader, 5), NullableDecimal(reader, 6),
                NullableDecimal(reader, 7), NullableDecimal(reader, 8), reader.GetString(9), reader.GetString(10),
                DeserializeTags(reader.GetString(11)), reader.GetString(12), reader.GetInt32(13) == 1,
                ParseTimestamp(reader.GetString(14)), ParseTimestamp(reader.GetString(15))));
        }

        return result;
    }

    public async Task UpsertTradeReviewMetadataAsync(
        TradeReviewMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trade_review_metadata(
                account_key, position_id, plan_id, compliance_status, strategy, setup,
                tags_json, user_edited, updated_at_utc)
            VALUES ($account, $position, $plan, $status, $strategy, $setup, $tags, $edited, $updated)
            ON CONFLICT(account_key, position_id) DO UPDATE SET
                plan_id = excluded.plan_id,
                compliance_status = excluded.compliance_status,
                strategy = excluded.strategy,
                setup = excluded.setup,
                tags_json = excluded.tags_json,
                user_edited = excluded.user_edited,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$account", metadata.AccountKey);
        command.Parameters.AddWithValue("$position", metadata.PositionId);
        command.Parameters.AddWithValue("$plan", DbValue(metadata.PlanId));
        command.Parameters.AddWithValue("$status", metadata.ComplianceStatus.ToString());
        command.Parameters.AddWithValue("$strategy", metadata.Strategy.Trim());
        command.Parameters.AddWithValue("$setup", metadata.Setup.Trim());
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(NormalizeTags(metadata.Tags), ProtocolJson.Options));
        command.Parameters.AddWithValue("$edited", metadata.UserEdited ? 1 : 0);
        command.Parameters.AddWithValue("$updated", Format(metadata.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, TradeReviewMetadata>> LoadTradeReviewMetadataAsync(
        string accountKey,
        IReadOnlyCollection<long>? positionIds = null,
        CancellationToken cancellationToken = default)
    {
        if (positionIds is { Count: 0 })
        {
            return new Dictionary<long, TradeReviewMetadata>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = "account_key = $account";
        var largeSelection = positionIds is { Count: > 900 } ? positionIds.ToHashSet() : null;
        if (positionIds is { Count: <= 900 })
        {
            var names = positionIds.Select((_, index) => $"$position{index}").ToArray();
            where += $" AND position_id IN ({string.Join(",", names)})";
            var index = 0;
            foreach (var positionId in positionIds)
            {
                command.Parameters.AddWithValue(names[index++], positionId);
            }
        }

        command.CommandText = $"""
            SELECT position_id, plan_id, compliance_status, strategy, setup, tags_json, user_edited, updated_at_utc
            FROM trade_review_metadata WHERE {where};
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new Dictionary<long, TradeReviewMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var positionId = reader.GetInt64(0);
            if (largeSelection is not null && !largeSelection.Contains(positionId))
            {
                continue;
            }
            result[positionId] = new TradeReviewMetadata(
                accountKey, positionId, reader.IsDBNull(1) ? null : reader.GetString(1),
                Enum.Parse<PlanComplianceStatus>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                DeserializeTags(reader.GetString(5)), reader.GetInt32(6) == 1, ParseTimestamp(reader.GetString(7)));
        }

        return result;
    }

    public async Task UpsertTradeExcursionAsync(TradeExcursion excursion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trade_excursions(
                account_key, position_id, minimum_pnl, maximum_pnl, initial_risk_amount,
                planned_risk_multiple, actual_risk_multiple, first_sample_at_utc, last_sample_at_utc,
                covered_milliseconds, holding_milliseconds, started_at_open, is_complete,
                maximum_gap_milliseconds, algorithm_version)
            VALUES ($account, $position, $minimum, $maximum, $risk, $planned, $actual, $first, $last,
                $covered, $holding, $started, $complete, $maximumGap, $algorithm)
            ON CONFLICT(account_key, position_id) DO UPDATE SET
                minimum_pnl = excluded.minimum_pnl,
                maximum_pnl = excluded.maximum_pnl,
                initial_risk_amount = excluded.initial_risk_amount,
                planned_risk_multiple = excluded.planned_risk_multiple,
                actual_risk_multiple = excluded.actual_risk_multiple,
                first_sample_at_utc = excluded.first_sample_at_utc,
                last_sample_at_utc = excluded.last_sample_at_utc,
                covered_milliseconds = excluded.covered_milliseconds,
                holding_milliseconds = excluded.holding_milliseconds,
                started_at_open = excluded.started_at_open,
                is_complete = excluded.is_complete,
                maximum_gap_milliseconds = excluded.maximum_gap_milliseconds,
                algorithm_version = excluded.algorithm_version;
            """;
        command.Parameters.AddWithValue("$account", excursion.AccountKey);
        command.Parameters.AddWithValue("$position", excursion.PositionId);
        command.Parameters.AddWithValue("$minimum", excursion.MinimumPnl);
        command.Parameters.AddWithValue("$maximum", excursion.MaximumPnl);
        command.Parameters.AddWithValue("$risk", DbValue(excursion.InitialRiskAmount));
        command.Parameters.AddWithValue("$planned", DbValue(excursion.PlannedRiskMultiple));
        command.Parameters.AddWithValue("$actual", DbValue(excursion.ActualRiskMultiple));
        command.Parameters.AddWithValue("$first", Format(excursion.FirstSampleAtUtc));
        command.Parameters.AddWithValue("$last", Format(excursion.LastSampleAtUtc));
        command.Parameters.AddWithValue("$covered", excursion.CoveredMilliseconds);
        command.Parameters.AddWithValue("$holding", excursion.HoldingMilliseconds);
        command.Parameters.AddWithValue("$started", excursion.StartedAtOpen ? 1 : 0);
        command.Parameters.AddWithValue("$complete", excursion.IsComplete ? 1 : 0);
        command.Parameters.AddWithValue("$maximumGap", excursion.MaximumGapMilliseconds);
        command.Parameters.AddWithValue("$algorithm", excursion.AlgorithmVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, TradeExcursion>> LoadTradeExcursionsAsync(
        string accountKey,
        IReadOnlyCollection<long>? positionIds = null,
        CancellationToken cancellationToken = default)
    {
        if (positionIds is { Count: 0 })
        {
            return new Dictionary<long, TradeExcursion>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = "account_key = $account";
        var largeSelection = positionIds is { Count: > 900 } ? positionIds.ToHashSet() : null;
        if (positionIds is { Count: <= 900 })
        {
            var names = positionIds.Select((_, index) => $"$position{index}").ToArray();
            where += $" AND position_id IN ({string.Join(",", names)})";
            var index = 0;
            foreach (var positionId in positionIds)
            {
                command.Parameters.AddWithValue(names[index++], positionId);
            }
        }

        command.CommandText = $"""
            SELECT position_id, minimum_pnl, maximum_pnl, initial_risk_amount, planned_risk_multiple,
                   actual_risk_multiple, first_sample_at_utc, last_sample_at_utc,
                   covered_milliseconds, holding_milliseconds, started_at_open, is_complete,
                   maximum_gap_milliseconds, algorithm_version
            FROM trade_excursions WHERE {where};
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new Dictionary<long, TradeExcursion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var positionId = reader.GetInt64(0);
            if (largeSelection is not null && !largeSelection.Contains(positionId))
            {
                continue;
            }
            result[positionId] = new TradeExcursion(
                accountKey, positionId, reader.GetDecimal(1), reader.GetDecimal(2), NullableDecimal(reader, 3),
                NullableDecimal(reader, 4), NullableDecimal(reader, 5), ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7)), reader.GetInt64(8), reader.GetInt64(9),
                reader.GetInt32(10) == 1, reader.GetInt32(11) == 1,
                reader.GetInt64(12), reader.GetString(13));
        }

        return result;
    }

    public async Task UpsertAccountCashFlowAsync(AccountCashFlow cashFlow, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO account_cash_flows(account_key, ticket, type, amount, occurred_at_utc)
            VALUES ($account, $ticket, $type, $amount, $occurred)
            ON CONFLICT(account_key, ticket) DO UPDATE SET
                type = excluded.type, amount = excluded.amount, occurred_at_utc = excluded.occurred_at_utc;
            """;
        command.Parameters.AddWithValue("$account", cashFlow.AccountKey);
        command.Parameters.AddWithValue("$ticket", cashFlow.Ticket);
        command.Parameters.AddWithValue("$type", cashFlow.Type);
        command.Parameters.AddWithValue("$amount", cashFlow.Amount);
        command.Parameters.AddWithValue("$occurred", Format(cashFlow.OccurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEquitySampleAsync(EquitySample sample, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO equity_samples(
                account_key, captured_at_utc, server_date, balance, equity, floating_pnl, has_open_position)
            VALUES ($account, $captured, $date, $balance, $equity, $floating, $holding);
            """;
        command.Parameters.AddWithValue("$account", sample.AccountKey);
        command.Parameters.AddWithValue("$captured", Format(sample.CapturedAtUtc));
        command.Parameters.AddWithValue("$date", Format(sample.ServerDate));
        command.Parameters.AddWithValue("$balance", sample.Balance);
        command.Parameters.AddWithValue("$equity", sample.Equity);
        command.Parameters.AddWithValue("$floating", sample.FloatingPnl);
        command.Parameters.AddWithValue("$holding", sample.HasOpenPosition ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EquitySample>> LoadEquitySamplesAsync(
        string accountKey,
        DateOnly serverDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at_utc, balance, equity, floating_pnl, has_open_position
            FROM equity_samples
            WHERE account_key = $account AND server_date = $date
            ORDER BY captured_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$date", Format(serverDate));
        var result = new List<EquitySample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EquitySample(
                accountKey,
                serverDate,
                ParseTimestamp(reader.GetString(0)),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetInt32(4) == 1));
        }

        return result;
    }

    public async Task<IReadOnlyList<EquitySample>> LoadEquitySamplesAsync(
        string accountKey,
        DateOnly fromServerDate,
        DateOnly toServerDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_date, captured_at_utc, balance, equity, floating_pnl, has_open_position
            FROM equity_samples
            WHERE account_key = $account AND server_date >= $from AND server_date <= $to
            ORDER BY captured_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromServerDate));
        command.Parameters.AddWithValue("$to", Format(toServerDate));
        var result = new List<EquitySample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EquitySample(
                accountKey,
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ParseTimestamp(reader.GetString(1)),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetInt32(5) == 1));
        }
        return result;
    }

    public async Task<IReadOnlyList<AccountCashFlow>> LoadAccountCashFlowsAsync(
        string accountKey,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticket, type, amount, occurred_at_utc
            FROM account_cash_flows
            WHERE account_key = $account
              AND occurred_at_utc >= $from
              AND occurred_at_utc < $to
            ORDER BY occurred_at_utc, ticket;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromUtc));
        command.Parameters.AddWithValue("$to", Format(toUtc));
        var result = new List<AccountCashFlow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AccountCashFlow(
                accountKey,
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                ParseTimestamp(reader.GetString(3))));
        }

        return result;
    }

    public async Task<int> DeleteEquitySamplesBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM equity_samples WHERE captured_at_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", Format(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertHistorySyncStateAsync(HistorySyncState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO history_sync_state(account_key, range_year, is_complete, deal_count, updated_at_utc)
            VALUES ($account, $year, $complete, $count, $updated)
            ON CONFLICT(account_key, range_year) DO UPDATE SET
                is_complete = excluded.is_complete,
                deal_count = excluded.deal_count,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$account", state.AccountKey);
        command.Parameters.AddWithValue("$year", state.RangeYear);
        command.Parameters.AddWithValue("$complete", state.IsComplete ? 1 : 0);
        command.Parameters.AddWithValue("$count", state.DealCount);
        command.Parameters.AddWithValue("$updated", Format(state.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HistorySyncState>> LoadHistorySyncStatesAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT range_year, is_complete, deal_count, updated_at_utc
            FROM history_sync_state WHERE account_key = $account ORDER BY range_year DESC;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new List<HistorySyncState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HistorySyncState(
                accountKey, reader.GetInt32(0), reader.GetInt32(1) == 1, reader.GetInt32(2),
                ParseTimestamp(reader.GetString(3))));
        }

        return result;
    }

    public async Task AddBehaviorEvaluationAsync(
        BehaviorEvaluation evaluation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO behavior_evaluations(
                id, account_key, server_date, position_id, rule, value, baseline,
                threshold, level, triggered, summary, observed_at_utc)
            VALUES ($id, $account, $date, $position, $rule, $value, $baseline,
                $threshold, $level, $triggered, $summary, $observed);
            """;
        command.Parameters.AddWithValue("$id", evaluation.Id);
        command.Parameters.AddWithValue("$account", evaluation.AccountKey);
        command.Parameters.AddWithValue("$date", Format(evaluation.ServerDate));
        command.Parameters.AddWithValue("$position", DbValue(evaluation.PositionId));
        command.Parameters.AddWithValue("$rule", evaluation.Rule.ToString());
        command.Parameters.AddWithValue("$value", evaluation.Value);
        command.Parameters.AddWithValue("$baseline", DbValue(evaluation.Baseline));
        command.Parameters.AddWithValue("$threshold", evaluation.Threshold);
        command.Parameters.AddWithValue("$level", evaluation.Level.ToString());
        command.Parameters.AddWithValue("$triggered", evaluation.Triggered ? 1 : 0);
        command.Parameters.AddWithValue("$summary", evaluation.Summary);
        command.Parameters.AddWithValue("$observed", Format(evaluation.ObservedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertDrawdownEpisodeAsync(
        DrawdownEpisode episode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO drawdown_episodes(
                id, account_key, curve_kind, start_server_date, end_server_date,
                peak_at_utc, trough_at_utc, recovered_at_utc, peak_value, trough_value,
                drawdown_amount, drawdown_percentage)
            VALUES ($id, $account, $kind, $start, $end, $peak, $trough, $recovered,
                $peakValue, $troughValue, $amount, $percentage)
            ON CONFLICT(id) DO UPDATE SET
                end_server_date = excluded.end_server_date,
                recovered_at_utc = excluded.recovered_at_utc,
                peak_value = excluded.peak_value,
                trough_value = excluded.trough_value,
                drawdown_amount = excluded.drawdown_amount,
                drawdown_percentage = excluded.drawdown_percentage;
            """;
        command.Parameters.AddWithValue("$id", episode.Id);
        command.Parameters.AddWithValue("$account", episode.AccountKey);
        command.Parameters.AddWithValue("$kind", episode.CurveKind);
        command.Parameters.AddWithValue("$start", Format(episode.StartServerDate));
        command.Parameters.AddWithValue("$end", episode.EndServerDate is null ? DBNull.Value : Format(episode.EndServerDate.Value));
        command.Parameters.AddWithValue("$peak", Format(episode.PeakAtUtc));
        command.Parameters.AddWithValue("$trough", Format(episode.TroughAtUtc));
        command.Parameters.AddWithValue("$recovered", episode.RecoveredAtUtc is null ? DBNull.Value : Format(episode.RecoveredAtUtc.Value));
        command.Parameters.AddWithValue("$peakValue", episode.PeakValue);
        command.Parameters.AddWithValue("$troughValue", episode.TroughValue);
        command.Parameters.AddWithValue("$amount", episode.DrawdownAmount);
        command.Parameters.AddWithValue("$percentage", DbValue(episode.DrawdownPercentage));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TradeRecord>> LoadTradesAsync(
        string accountKey,
        DateOnly fromServerDate,
        DateOnly toServerDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position_id, symbol, side, opened_at_utc, closed_at_utc, open_server_date,
                   close_server_date, entry_price, exit_price, opening_volume, maximum_volume,
                   remaining_volume, net_pnl, is_complete
            FROM trades
            WHERE account_key = $account
              AND ((is_complete = 1 AND open_server_date <= $to AND close_server_date >= $from)
                   OR (is_complete = 0 AND open_server_date <= $to))
            ORDER BY opened_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromServerDate));
        command.Parameters.AddWithValue("$to", Format(toServerDate));
        var result = new List<TradeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TradeRecord(
                accountKey, reader.GetInt64(0), reader.GetString(1), Enum.Parse<TradeSide>(reader.GetString(2)),
                ParseTimestamp(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetDecimal(7), NullableDecimal(reader, 8), reader.GetDecimal(9), reader.GetDecimal(10),
                reader.GetDecimal(11), reader.GetDecimal(12), reader.GetInt32(13) == 1));
        }

        return result;
    }

    public async Task<(DateOnly? FromServerDate, DateOnly? ToServerDate)> LoadTradeDateRangeAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(close_server_date), MAX(close_server_date)
            FROM trades
            WHERE account_key = $account AND is_complete = 1 AND close_server_date IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return (null, null);
        }

        return (
            DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<LossZoneState>> LoadLossZonesAsync(
        string accountKey,
        DateOnly fromServerDate,
        DateOnly toServerDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, server_date, symbol, center_price, tolerance, attempt_count, loss_count,
                   cumulative_loss, last_attempt_at_utc
            FROM loss_zones
            WHERE account_key = $account AND server_date BETWEEN $from AND $to
            ORDER BY server_date, last_attempt_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromServerDate));
        command.Parameters.AddWithValue("$to", Format(toServerDate));
        var result = new List<LossZoneState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LossZoneState(
                reader.GetString(0), accountKey,
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetDecimal(7), ParseTimestamp(reader.GetString(8))));
        }

        return result;
    }

    public async Task<IReadOnlyList<LossZoneAttempt>> LoadLossZoneAttemptsAsync(
        string accountKey,
        DateOnly fromServerDate,
        DateOnly toServerDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.zone_id, a.position_id, a.side, a.entry_price, a.opening_volume,
                   a.net_pnl, a.opened_at_utc, a.closed_at_utc
            FROM loss_zone_attempts a
            INNER JOIN loss_zones z ON z.id = a.zone_id
            WHERE z.account_key = $account AND z.server_date BETWEEN $from AND $to
            ORDER BY a.opened_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromServerDate));
        command.Parameters.AddWithValue("$to", Format(toServerDate));
        var result = new List<LossZoneAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LossZoneAttempt(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                Enum.Parse<TradeSide>(reader.GetString(3)), reader.GetDecimal(4), reader.GetDecimal(5),
                NullableDecimal(reader, 6), ParseTimestamp(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8))));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<DateOnly, DailyState>> LoadDailyStatesAsync(
        string accountKey,
        DateOnly fromServerDate,
        DateOnly toServerDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_date, daily_state_json
            FROM trading_days
            WHERE account_key = $account AND server_date BETWEEN $from AND $to
            ORDER BY server_date;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$from", Format(fromServerDate));
        command.Parameters.AddWithValue("$to", Format(toServerDate));
        var result = new Dictionary<DateOnly, DailyState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var state = JsonSerializer.Deserialize<DailyState>(reader.GetString(1), ProtocolJson.Options);
            if (state is not null)
            {
                result[date] = state;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<DateOnly>> LoadLossZoneServerDatesAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_date
            FROM loss_zones
            WHERE account_key = $account
            UNION
            SELECT close_server_date
            FROM trades
            WHERE account_key = $account
              AND is_complete = 1
              AND net_pnl < -0.01
              AND close_server_date IS NOT NULL
            ORDER BY server_date;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var dates = new List<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return dates;
    }

    public async Task<IReadOnlyList<TradeRecord>> LoadTradesForLossZoneDateAsync(
        string accountKey,
        DateOnly serverDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position_id, symbol, side, opened_at_utc, closed_at_utc, open_server_date,
                   close_server_date, entry_price, exit_price, opening_volume, maximum_volume,
                   remaining_volume, net_pnl, is_complete
            FROM trades
            WHERE account_key = $account
              AND (open_server_date = $date OR close_server_date = $date)
            ORDER BY opened_at_utc;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$date", Format(serverDate));
        var result = new List<TradeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TradeRecord(
                accountKey, reader.GetInt64(0), reader.GetString(1), Enum.Parse<TradeSide>(reader.GetString(2)),
                ParseTimestamp(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetDecimal(7), NullableDecimal(reader, 8), reader.GetDecimal(9), reader.GetDecimal(10),
                reader.GetDecimal(11), reader.GetDecimal(12), reader.GetInt32(13) == 1));
        }

        return result;
    }

    private static decimal? NullableDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> DeserializeTags(string json) =>
        JsonSerializer.Deserialize<string[]>(json, ProtocolJson.Options) ?? [];

    private static string[] NormalizeTags(IEnumerable<string> tags) =>
        tags.Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
