using System.IO.Compression;
using Microsoft.Data.Sqlite;
using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Infrastructure.Persistence;
using Xunit;

namespace TradePet.Infrastructure.Tests;

public sealed class AppDatabaseTests : IAsyncLifetime
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "TradePetTests", Guid.NewGuid().ToString("N"));
    private AppDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new AppDatabase(Path.Combine(_testDirectory, "test.db"));
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessedEvents_AreIdempotent()
    {
        Assert.True(await _database.TryMarkEventProcessedAsync("source-a", 7));
        Assert.False(await _database.TryMarkEventProcessedAsync("source-a", 7));
        Assert.True(await _database.TryMarkEventProcessedAsync("source-b", 7));
    }

    [Fact]
    public async Task TradeExcursion_MarksMigratedExtremaLegacyAndRoundTripsCurrentSamplingIdentity()
    {
        const string accountKey = "ExcursionBroker|91";
        const long positionId = 9001;
        await using (var connection = await _database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO trade_excursions(
                    account_key, position_id, minimum_pnl, maximum_pnl, initial_risk_amount,
                    planned_risk_multiple, actual_risk_multiple, first_sample_at_utc, last_sample_at_utc,
                    covered_milliseconds, holding_milliseconds, started_at_open, is_complete)
                VALUES ($account, $position, -25, 80, 50, 2, 1.5,
                    '2026-09-09T01:00:00.0000000+00:00', '2026-09-09T01:05:00.0000000+00:00',
                    300000, 300000, 1, 1);
                """;
            command.Parameters.AddWithValue("$account", accountKey);
            command.Parameters.AddWithValue("$position", positionId);
            await command.ExecuteNonQueryAsync();
        }

        var legacy = (await _database.LoadTradeExcursionsAsync(accountKey))[positionId];
        Assert.Equal("legacy-extrema-v1", legacy.AlgorithmVersion);
        Assert.Equal(long.MaxValue, legacy.MaximumGapMilliseconds);
        Assert.True(legacy.HasReliableInitialRisk);
        Assert.False(legacy.IsReliable);

        await _database.UpsertTradeExcursionAsync(legacy with
        {
            MaximumGapMilliseconds = 4_500,
            AlgorithmVersion = "position-pnl-v1",
        });
        var reopened = new AppDatabase(Path.Combine(_testDirectory, "test.db"));
        await reopened.InitializeAsync();
        var current = (await reopened.LoadTradeExcursionsAsync(accountKey))[positionId];

        Assert.Equal("position-pnl-v1", current.AlgorithmVersion);
        Assert.Equal(4_500, current.MaximumGapMilliseconds);
        Assert.True(current.IsReliable);
        Assert.Equal(1.5m, current.ActualRiskMultiple);
    }

    [Fact]
    public async Task LargePositionSelection_DoesNotExceedSqliteParameterLimitAndStillFilters()
    {
        const string accountKey = "LargeSelection|92";
        var now = new DateTimeOffset(2026, 9, 9, 2, 0, 0, TimeSpan.Zero);
        await _database.UpsertTradeReviewMetadataAsync(new TradeReviewMetadata(
            accountKey, 1_000, null, PlanComplianceStatus.Unclassified, "selected", "", [], true, now));
        await _database.UpsertTradeReviewMetadataAsync(new TradeReviewMetadata(
            accountKey, 2_000, null, PlanComplianceStatus.Unclassified, "not-selected", "", [], true, now));
        await _database.UpsertTradeExcursionAsync(new TradeExcursion(
            accountKey, 1_000, -1, 2, 10, 1, 0.5m, now, now.AddMinutes(1),
            60_000, 60_000, true, true, 1_000, "position-pnl-v1"));
        await _database.UpsertTradeExcursionAsync(new TradeExcursion(
            accountKey, 2_000, -1, 2, 10, 1, 0.5m, now, now.AddMinutes(1),
            60_000, 60_000, true, true, 1_000, "position-pnl-v1"));
        var requested = Enumerable.Range(1, 1_000).Select(item => (long)item).ToArray();

        var metadata = await _database.LoadTradeReviewMetadataAsync(accountKey, requested);
        var excursions = await _database.LoadTradeExcursionsAsync(accountKey, requested);

        Assert.Equal([1_000L], metadata.Keys);
        Assert.Equal([1_000L], excursions.Keys);
    }

    [Fact]
    public async Task ProcessedEvents_AcceptConcurrentWorkerAndBridgeWrites()
    {
        var writes = Enumerable.Range(1, 100)
            .SelectMany(sequence => new[]
            {
                _database.TryMarkEventProcessedAsync("python-live", sequence),
                _database.TryMarkEventProcessedAsync("bridge-live", sequence),
            });

        var results = await Task.WhenAll(writes);

        Assert.All(results, Assert.True);
    }

    [Fact]
    public async Task SymbolSpecifications_AreUpsertedPerAccountAndCaseInsensitiveSymbol()
    {
        await _database.UpsertSymbolSpecificationsAsync("Broker|1",
            [new SymbolSpecification("EURUSD", 0.00001m, 0.00001m, 5)]);
        await _database.UpsertSymbolSpecificationsAsync("Broker|1",
            [new SymbolSpecification("eurusd", 0.00001m, 0.00002m, 5)]);
        await _database.UpsertSymbolSpecificationsAsync("Broker|2",
            [new SymbolSpecification("EURUSD", 0.0001m, 0.0001m, 4)]);

        var first = await _database.LoadSymbolSpecificationsAsync("Broker|1");
        var second = await _database.LoadSymbolSpecificationsAsync("Broker|2");

        Assert.Equal(0.00002m, Assert.Single(first).Value.PriceStep);
        Assert.Equal(0.0001m, Assert.Single(second).Value.PriceStep);
    }

    [Fact]
    public async Task TradeProjectionBatch_UpsertsEveryAffectedTradeInOneOperation()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 9, 6);
        var opened = new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        var trades = new[]
        {
            new TradeRecord(account, 1, "EURUSD", TradeSide.Buy, opened, opened.AddMinutes(1),
                date, date, 1.1m, 1.2m, 0.01m, 0.01m, 0m, 1m, true),
            new TradeRecord(account, 2, "USDJPY", TradeSide.Sell, opened, null,
                date, null, 150m, null, 0.02m, 0.02m, 0.02m, 0m, false),
        };

        await _database.SaveTradesAsync(trades);

        var loaded = await _database.LoadTradesAsync(account, date, date);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, trade => trade.PositionId == 1 && trade.IsComplete);
        Assert.Contains(loaded, trade => trade.PositionId == 2 && !trade.IsComplete);
    }

    [Fact]
    public async Task TradingDays_AreIsolatedByAccountAndServerDate()
    {
        var capturedAt = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var firstScope = new AccountScope("WeTrade-Live", 1001);
        var secondScope = new AccountScope("WeTrade-Live", 1002);
        await _database.UpsertAccountAsync(new AccountSnapshot(firstScope, "USD", 1000m, 990m, -10m, 2, capturedAt));
        await _database.UpsertAccountAsync(new AccountSnapshot(secondScope, "USD", 2000m, 2020m, 20m, 2, capturedAt));

        var targetAt = new DateTimeOffset(2026, 8, 30, 2, 3, 0, TimeSpan.Zero);
        var first = CreateDailyState(firstScope.AccountKey, new DateOnly(2026, 8, 30), 10m) with
        {
            TargetAlerted = true,
            TargetReachedAtUtc = targetAt,
            TargetRuleVersion = "daily-target/v1:10",
            TargetAmountAtReach = 10m,
            ConsecutiveLossThresholdAtObservation = 2,
        };
        var nextDay = CreateDailyState(firstScope.AccountKey, new DateOnly(2026, 8, 31), -5m);
        var second = CreateDailyState(secondScope.AccountKey, new DateOnly(2026, 8, 30), 20m);
        await _database.UpsertTradingDayAsync(first, DailyPlanSettings.BalancedDefault);
        await _database.UpsertTradingDayAsync(nextDay, DailyPlanSettings.BalancedDefault);
        await _database.UpsertTradingDayAsync(second, DailyPlanSettings.BalancedDefault);

        var loadedFirst = await _database.LoadTradingDayAsync(first.AccountKey, first.ServerDate);
        var loadedNextDay = await _database.LoadTradingDayAsync(nextDay.AccountKey, nextDay.ServerDate);
        var loadedSecond = await _database.LoadTradingDayAsync(second.AccountKey, second.ServerDate);

        Assert.Equal(10m, loadedFirst?.State.RealizedPnl);
        Assert.Equal(targetAt, loadedFirst?.State.TargetReachedAtUtc);
        Assert.Equal("daily-target/v1:10", loadedFirst?.State.TargetRuleVersion);
        Assert.Equal(-5m, loadedNextDay?.State.RealizedPnl);
        Assert.Equal(20m, loadedSecond?.State.RealizedPnl);
    }

    [Fact]
    public async Task DealAndTradeUpserts_ReplacePreviouslyIncorrectProjectionFields()
    {
        const string accountKey = "WeTrade-Live|1001";
        var wrongTime = new DateTimeOffset(2026, 8, 31, 4, 35, 0, TimeSpan.Zero);
        var correctedTime = wrongTime.AddHours(-3);
        var wrongDate = new DateOnly(2026, 9, 1);
        var correctedDate = new DateOnly(2026, 8, 31);

        await _database.UpsertDealAsync(accountKey, new DealRecord(
            7001, 8001, 9001, "XAUUSD", TradeSide.Sell, DealEntryKind.Out,
            0.01m, 3500m, 1m, -0.01m, 0m, 0m, wrongTime));
        await _database.UpsertDealAsync(accountKey, new DealRecord(
            7001, 8002, 9002, "XAUUSD.s", TradeSide.Buy, DealEntryKind.In,
            0.02m, 3501m, 2m, -0.02m, -0.03m, -0.04m, correctedTime));

        await _database.UpsertTradeAsync(new TradeRecord(
            accountKey, 9002, "XAUUSD", TradeSide.Sell, wrongTime, wrongTime.AddMinutes(5),
            wrongDate, wrongDate, 3500m, 3499m, 0.01m, 0.01m, 0m, 1m, true));
        await _database.UpsertTradeAsync(new TradeRecord(
            accountKey, 9002, "XAUUSD.s", TradeSide.Buy, correctedTime, null,
            correctedDate, null, 3501m, null, 0.02m, 0.03m, 0.02m, -0.09m, false));

        await using var connection = await _database.OpenConnectionAsync();
        await using (var dealCommand = connection.CreateCommand())
        {
            dealCommand.CommandText = """
                SELECT order_ticket, position_id, symbol, side, entry_kind, volume, price,
                       profit, commission, swap, fee, occurred_at_utc
                FROM deals WHERE account_key = $account AND ticket = 7001;
                """;
            dealCommand.Parameters.AddWithValue("$account", accountKey);
            await using var reader = await dealCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(8002, reader.GetInt64(0));
            Assert.Equal(9002, reader.GetInt64(1));
            Assert.Equal("XAUUSD.s", reader.GetString(2));
            Assert.Equal("Buy", reader.GetString(3));
            Assert.Equal("In", reader.GetString(4));
            Assert.Equal(0.02m, reader.GetDecimal(5));
            Assert.Equal(3501m, reader.GetDecimal(6));
            Assert.Equal(2m, reader.GetDecimal(7));
            Assert.Equal(-0.02m, reader.GetDecimal(8));
            Assert.Equal(-0.03m, reader.GetDecimal(9));
            Assert.Equal(-0.04m, reader.GetDecimal(10));
            Assert.Equal(correctedTime, DateTimeOffset.Parse(reader.GetString(11)));
        }

        await using var tradeCommand = connection.CreateCommand();
        tradeCommand.CommandText = """
            SELECT symbol, side, opened_at_utc, closed_at_utc, open_server_date, close_server_date,
                   entry_price, exit_price, opening_volume, maximum_volume, remaining_volume, net_pnl, is_complete
            FROM trades WHERE account_key = $account AND position_id = 9002;
            """;
        tradeCommand.Parameters.AddWithValue("$account", accountKey);
        await using var tradeReader = await tradeCommand.ExecuteReaderAsync();
        Assert.True(await tradeReader.ReadAsync());
        Assert.Equal("XAUUSD.s", tradeReader.GetString(0));
        Assert.Equal("Buy", tradeReader.GetString(1));
        Assert.Equal(correctedTime, DateTimeOffset.Parse(tradeReader.GetString(2)));
        Assert.True(tradeReader.IsDBNull(3));
        Assert.Equal("2026-08-31", tradeReader.GetString(4));
        Assert.True(tradeReader.IsDBNull(5));
        Assert.Equal(3501m, tradeReader.GetDecimal(6));
        Assert.True(tradeReader.IsDBNull(7));
        Assert.Equal(0.02m, tradeReader.GetDecimal(8));
        Assert.Equal(0.03m, tradeReader.GetDecimal(9));
        Assert.Equal(0.02m, tradeReader.GetDecimal(10));
        Assert.Equal(-0.09m, tradeReader.GetDecimal(11));
        Assert.Equal(0, tradeReader.GetInt32(12));
    }

    [Fact]
    public async Task ChartObjectRevision_IsNotDuplicatedForSameContent()
    {
        var chartObject = new ChartObjectSnapshot(
            "terminal-a",
            99,
            "no-trade-zone",
            "XAUUSD.s",
            "M5",
            ChartObjectKind.Rectangle,
            [new PriceAnchor(null, 3349m), new PriceAnchor(null, 3355m)],
            "这里不追",
            0,
            DateTimeOffset.UtcNow);

        await _database.UpsertChartObjectAsync(chartObject, "hash-a");
        await _database.UpsertChartObjectAsync(chartObject with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1) }, "hash-a");

        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chart_object_revisions WHERE object_key = $key;";
        command.Parameters.AddWithValue("$key", chartObject.ObjectKey);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReviewSchema_PersistsPlansMetadataAndSyncStatePerAccount()
    {
        var date = new DateOnly(2026, 8, 30);
        var now = new DateTimeOffset(2026, 8, 30, 2, 0, 0, TimeSpan.Zero);
        var firstAccount = "WeTrade-Live|1001";
        var secondAccount = "WeTrade-Live|1002";
        var plan = new StructuredTradePlan(
            "plan-a", firstAccount, date, "XAUUSD.s", TradeSide.Buy,
            3352m, 3351m, 3353m, 3348m, 3360m,
            "顺势", "回踩", ["早盘", "重点"], "等待确认", true, now, now);

        await _database.UpsertStructuredTradePlanAsync(plan);
        await _database.UpsertTradeReviewMetadataAsync(new TradeReviewMetadata(
            firstAccount, 99, plan.Id, PlanComplianceStatus.Matched,
            plan.Strategy, plan.Setup, plan.Tags, false, now));
        await _database.UpsertHistorySyncStateAsync(new HistorySyncState(firstAccount, 2026, true, 8, now));
        await _database.UpsertHistorySyncStateAsync(new HistorySyncState(secondAccount, 2026, true, 3, now));

        var plans = await _database.LoadStructuredTradePlansAsync(firstAccount, date, date);
        var metadata = await _database.LoadTradeReviewMetadataAsync(firstAccount, [99]);
        var firstSync = await _database.LoadHistorySyncStatesAsync(firstAccount);
        var secondSync = await _database.LoadHistorySyncStatesAsync(secondAccount);

        Assert.Equal(2, plans[0].Tags.Count);
        Assert.Equal(2m, plans[0].PlannedRiskMultiple);
        Assert.Equal(PlanComplianceStatus.Matched, metadata[99].ComplianceStatus);
        Assert.Equal(8, firstSync[0].DealCount);
        Assert.Equal(3, secondSync[0].DealCount);

        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 2;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task EquitySamplesAndCashFlows_LoadOnlyRequestedAccountAndServerDay()
    {
        const string firstAccount = "Broker|1";
        const string secondAccount = "Broker|2";
        var date = new DateOnly(2026, 9, 1);
        var startUtc = new DateTimeOffset(2026, 8, 31, 16, 0, 0, TimeSpan.Zero);
        await _database.AddEquitySampleAsync(new EquitySample(
            firstAccount, date, startUtc.AddHours(1), 1_000m, 1_020m, 20m, true));
        await _database.AddEquitySampleAsync(new EquitySample(
            secondAccount, date, startUtc.AddHours(1), 2_000m, 2_030m, 30m, true));
        await _database.AddEquitySampleAsync(new EquitySample(
            firstAccount, date.AddDays(1), startUtc.AddDays(1), 1_100m, 1_100m, 0m, false));
        await _database.UpsertAccountCashFlowAsync(new AccountCashFlow(
            firstAccount, 11, "balance", 100m, startUtc.AddHours(2)));
        await _database.UpsertAccountCashFlowAsync(new AccountCashFlow(
            firstAccount, 12, "balance", 200m, startUtc.AddDays(1)));
        await _database.UpsertAccountCashFlowAsync(new AccountCashFlow(
            secondAccount, 13, "balance", 300m, startUtc.AddHours(2)));

        var samples = await _database.LoadEquitySamplesAsync(firstAccount, date);
        var cashFlows = await _database.LoadAccountCashFlowsAsync(firstAccount, startUtc, startUtc.AddDays(1));

        Assert.Single(samples);
        Assert.Equal(20m, samples[0].FloatingPnl);
        Assert.Single(cashFlows);
        Assert.Equal(11, cashFlows[0].Ticket);
    }

    [Fact]
    public async Task ReviewWorkspace_LoadsEquityCashFlowsAndTradingSessionsAcrossRestart()
    {
        const string accountKey = "EquityBroker|4101";
        var date = new DateOnly(2026, 9, 8);
        var now = new DateTimeOffset(2026, 9, 8, 4, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("EquityBroker", 4101), "USD", 10_000m, 10_000m, 0m, 0, now));
        await _database.AddEquitySampleAsync(new EquitySample(
            accountKey, date, now, 10_000m, 10_000m, 0m, false));
        await _database.AddEquitySampleAsync(new EquitySample(
            accountKey, date.AddDays(1), now.AddDays(1), 11_000m, 11_000m, 0m, false));
        await _database.UpsertAccountCashFlowAsync(new AccountCashFlow(
            accountKey, 91, "balance", 1_000m, now.AddMinutes(5)));
        var session = new TradingSessionDefinition(
            "session-1", accountKey, "伦敦盘", "UTC", new TimeOnly(7, 0), new TimeOnly(16, 0),
            [DayOfWeek.Monday, DayOfWeek.Tuesday], 1, true, 0, now, now);
        var saved = await _database.SaveTradingSessionAsync(session, 0);

        var reopened = new AppDatabase(Path.Combine(_testDirectory, "test.db"));
        await reopened.InitializeAsync();
        var workspace = await reopened.LoadWorkspaceAsync(accountKey, date, date);
        var stale = await reopened.SaveTradingSessionAsync(
            saved.Value! with { Name = "过期修改", UpdatedAtUtc = now.AddMinutes(1) }, 0);

        Assert.Single(workspace.EquitySamples!);
        Assert.Equal(now, workspace.EquitySamples![0].CapturedAtUtc);
        Assert.Equal(91, Assert.Single(workspace.CashFlows!).Ticket);
        var storedSession = Assert.Single(workspace.TradingSessions!);
        Assert.Equal("伦敦盘", storedSession.Name);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday], storedSession.StartDays);
        Assert.Equal(1, storedSession.Revision);
        Assert.Equal(ReviewSaveStatus.Conflict, stale.Status);
    }

    [Fact]
    public async Task ReviewSchema_PersistsDrawdownEpisodesIdempotently()
    {
        var now = new DateTimeOffset(2026, 8, 30, 2, 0, 0, TimeSpan.Zero);
        var episode = new DrawdownEpisode(
            "dd-1", "Broker|1", "realized", new DateOnly(2026, 8, 30), new DateOnly(2026, 8, 31),
            now, now.AddHours(1), now.AddHours(2), 100m, 80m, 20m, 20m);

        await _database.UpsertDrawdownEpisodeAsync(episode);
        await _database.UpsertDrawdownEpisodeAsync(episode with { RecoveredAtUtc = null, EndServerDate = null });

        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), recovered_at_utc FROM drawdown_episodes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", episode.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task AlertDeliveries_AreDeduplicatedWithinAccountAndDate()
    {
        var date = new DateOnly(2026, 8, 30);
        var alert = new CombinedAlert(
            "open:Broker|1:42", AlertPriority.Important, "注意",
            [new RuleFact(RuleFactKind.RapidReentry, AlertPriority.Important, 20, "重进", "刚刚结束")],
            DateTimeOffset.UtcNow);

        Assert.True(await _database.TryMarkAlertDeliveredAsync("Broker|1", date, alert));
        Assert.False(await _database.TryMarkAlertDeliveredAsync("Broker|1", date, alert));
        Assert.True(await _database.TryMarkAlertDeliveredAsync("Broker|2", date, alert));
        Assert.True(await _database.TryMarkAlertDeliveredAsync("Broker|1", date.AddDays(1), alert));
    }

    [Fact]
    public async Task LossZoneProjection_ReplacesScopeAtomicallyAndRejectsDuplicatePositions()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 8, 30);
        var now = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var staleZone = new LossZoneState("stale", account, date, "XAUUSD.s", 3340m, 2m, 9, 9, -99m, now);
        await _database.UpsertLossZoneAsync(staleZone);
        await _database.UpsertLossZoneAttemptAsync(new LossZoneAttempt(
            "stale:1", "stale", 1, TradeSide.Buy, 3340m, 0.01m, -99m, now, now.AddMinutes(1)));

        var zone = new LossZoneState("zone", account, date, "XAUUSD.s", 3352m, 2m, 2, 1, -3m, now.AddMinutes(4));
        var attempts = new[]
        {
            new LossZoneAttempt("zone:1", "zone", 1, TradeSide.Buy, 3352m, 0.01m, -3m, now, now.AddMinutes(1)),
            new LossZoneAttempt("zone:2", "zone", 2, TradeSide.Sell, 3351m, 0.01m, 2m, now.AddMinutes(2), now.AddMinutes(4)),
        };
        var projection = new LossZoneProjection([zone], attempts);

        await _database.ReplaceLossZoneProjectionAsync(account, date, projection);
        await _database.ReplaceLossZoneProjectionAsync(account, date, projection);

        var loadedZones = await _database.LoadLossZonesAsync(account, date);
        var loadedAttempts = await _database.LoadLossZoneAttemptsAsync(loadedZones.Select(item => item.Id).ToArray());
        Assert.Equal("zone", Assert.Single(loadedZones).Id);
        Assert.Equal(2, loadedAttempts.Count);
        Assert.Equal(2, loadedAttempts.Select(item => item.PositionId).Distinct().Count());

        var duplicateProjection = projection with
        {
            Attempts = attempts.Append(attempts[0] with { Id = "zone:duplicate" }).ToArray(),
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _database.ReplaceLossZoneProjectionAsync(account, date, duplicateProjection));
    }

    [Fact]
    public async Task LossZoneHistoryQueries_IncludeBothSidesOfCrossDayTrades()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 8, 30);
        var previousDate = date.AddDays(-1);
        var nextDate = date.AddDays(1);
        var now = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        await _database.UpsertTradeAsync(new TradeRecord(
            account, 1, "XAUUSD.s", TradeSide.Buy, now.AddDays(-1), now,
            previousDate, date, 3352m, 3350m, 0.01m, 0.01m, 0m, -3m, true));
        await _database.UpsertTradeAsync(new TradeRecord(
            account, 2, "XAUUSD.s", TradeSide.Sell, now.AddHours(1), now.AddDays(1),
            date, nextDate, 3351m, 3349m, 0.01m, 0.01m, 0m, 2m, true));

        var dates = await _database.LoadLossZoneServerDatesAsync(account);
        var trades = await _database.LoadTradesForLossZoneDateAsync(account, date);

        Assert.Contains(date, dates);
        Assert.Equal([1L, 2L], trades.Select(trade => trade.PositionId).Order().ToArray());
    }

    [Fact]
    public async Task HistoricalReviewQueries_ReadPersistedRangeAndKeepAccountsIsolated()
    {
        const string account = "Broker|1";
        const string otherAccount = "Broker|2";
        var firstDate = new DateOnly(2026, 1, 5);
        var lastDate = new DateOnly(2026, 8, 30);
        var firstTime = new DateTimeOffset(2026, 1, 5, 1, 0, 0, TimeSpan.Zero);
        var lastTime = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("Broker", 1), "USD", 1_000m, 1_000m, 0m, 2, firstTime));
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("Broker", 2), "USD", 1_000m, 1_000m, 0m, 2, firstTime));
        await _database.UpsertDealAsync(account, new DealRecord(
            1, 1, 1, "XAUUSD.s", TradeSide.Buy, DealEntryKind.In, 0.01m, 3_000m,
            0m, 0m, 0m, 0m, firstTime));
        await _database.UpsertDealAsync(otherAccount, new DealRecord(
            2, 2, 2, "EURUSD", TradeSide.Sell, DealEntryKind.In, 0.01m, 1m,
            0m, 0m, 0m, 0m, firstTime));
        await _database.UpsertTradeAsync(new TradeRecord(
            account, 1, "XAUUSD.s", TradeSide.Buy, firstTime, firstTime.AddMinutes(1),
            firstDate, firstDate, 3_000m, 3_001m, 0.01m, 0.01m, 0m, 1m, true));
        await _database.UpsertTradeAsync(new TradeRecord(
            account, 2, "XAUUSD.s", TradeSide.Sell, lastTime, lastTime.AddMinutes(1),
            lastDate, lastDate, 3_500m, 3_499m, 0.01m, 0.01m, 0m, 1m, true));
        await _database.UpsertTradingDayAsync(CreateDailyState(account, firstDate, 1m), DailyPlanSettings.BalancedDefault);

        var deals = await _database.LoadDealsAsync(account);
        var range = await _database.LoadTradeDateRangeAsync(account);
        var trades = await _database.LoadTradesAsync(account, firstDate, lastDate);
        var states = await _database.LoadDailyStatesAsync(account, firstDate, lastDate);

        Assert.Single(deals);
        Assert.Equal(firstDate, range.FromServerDate);
        Assert.Equal(lastDate, range.ToServerDate);
        Assert.Equal(2, trades.Count);
        Assert.Single(states);
        Assert.DoesNotContain(deals, deal => deal.Symbol == "EURUSD");
    }

    [Fact]
    public async Task DealBatch_CommitsSourceDataAndHistoryCheckpointTogether()
    {
        const string account = "Broker|1";
        var at = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var deal = new DealRecord(
            901, 902, 903, "XAUUSD.s", TradeSide.Buy, DealEntryKind.In,
            0.01m, 3352m, 0m, -0.1m, 0m, 0m, at);
        var invalidCashFlow = new AccountCashFlow("Broker|2", 904, "balance", 100m, at);

        await Assert.ThrowsAsync<ArgumentException>(() => _database.SaveDealBatchAsync(
            account,
            [deal],
            [invalidCashFlow],
            new HistorySyncState(account, 2026, true, 1, at)));
        Assert.DoesNotContain(await _database.LoadDealsAsync(account), item => item.Ticket == deal.Ticket);
        Assert.DoesNotContain(await _database.LoadHistorySyncStatesAsync(account), item => item.RangeYear == 2026);

        var cashFlow = invalidCashFlow with { AccountKey = account };
        await _database.SaveDealBatchAsync(
            account,
            [deal],
            [cashFlow],
            new HistorySyncState(account, 2026, true, 1, at));

        Assert.Contains(await _database.LoadDealsAsync(account), item => item.Ticket == deal.Ticket);
        Assert.Contains(await _database.LoadAccountCashFlowsAsync(
            account, at.AddMinutes(-1), at.AddMinutes(1)), item => item.Ticket == cashFlow.Ticket);
        Assert.True(Assert.Single(await _database.LoadHistorySyncStatesAsync(account)).IsComplete);
    }

    [Fact]
    public async Task ReviewWorkspaceMigration_UsesAccountScopedOptimisticRevisions()
    {
        var now = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var first = ReviewDocument("Broker|1", 77, now);
        var second = ReviewDocument("Broker|2", 77, now);
        await SeedCompletedTradeAsync("Broker|1", 77, now);
        await SeedCompletedTradeAsync("Broker|2", 77, now);

        var savedFirst = await _database.SaveTradeReviewDocumentAsync(first, 0);
        var conflict = await _database.SaveTradeReviewDocumentAsync(first with { Summary = "过期编辑" }, 0);
        var savedSecond = await _database.SaveTradeReviewDocumentAsync(second, 0);

        Assert.True(savedFirst.IsSaved);
        Assert.Equal(1, savedFirst.Value!.Revision);
        Assert.Equal(ReviewSaveStatus.Conflict, conflict.Status);
        Assert.True(savedSecond.IsSaved);
        Assert.Equal("交易复盘", (await _database.LoadTradeReviewDocumentAsync(new TradeKey("Broker|1", 77)))!.Summary);
        Assert.Equal("交易复盘", (await _database.LoadTradeReviewDocumentAsync(new TradeKey("Broker|2", 77)))!.Summary);

        await using var connection = await _database.OpenConnectionAsync();
        await using var migration = connection.CreateCommand();
        migration.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=4;";
        Assert.Equal(1L, (long)(await migration.ExecuteScalarAsync())!);
        await using var history = connection.CreateCommand();
        history.CommandText = "SELECT COUNT(*) FROM review_revisions WHERE entity_kind='trade' AND entity_id='77';";
        Assert.Equal(2L, (long)(await history.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task TradeReviewDocument_RejectsOrphanAndIncompleteTrade()
    {
        var now = new DateTimeOffset(2026, 9, 7, 2, 0, 0, TimeSpan.Zero);
        var orphan = await _database.SaveTradeReviewDocumentAsync(
            ReviewDocument("Broker|orphan", 901, now), 0);

        await _database.UpsertTradeAsync(new TradeRecord(
            "Broker|open", 902, "XAUUSD.s", TradeSide.Buy,
            now, null, new DateOnly(2026, 9, 7), null,
            3500m, null, 0.01m, 0.01m, 0.01m, 0m, false));
        var incomplete = await _database.SaveTradeReviewDocumentAsync(
            ReviewDocument("Broker|open", 902, now), 0);

        Assert.Equal(ReviewSaveStatus.NotFound, orphan.Status);
        Assert.Equal(ReviewSaveStatus.ValidationFailed, incomplete.Status);
        Assert.Null(await _database.LoadTradeReviewDocumentAsync(new TradeKey("Broker|orphan", 901)));
        Assert.Null(await _database.LoadTradeReviewDocumentAsync(new TradeKey("Broker|open", 902)));
    }

    [Fact]
    public async Task MarketHistory_IsStoredPerAccountTerminalAndSymbol()
    {
        var from = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var range = new MarketDataRange("request-1", "terminal-a", "Broker|1", "XAUUSD.s", "M5",
            from, from.AddMinutes(5), from, from.AddMinutes(5), MarketDataPrecision.Bars,
            MarketCoverageStatus.Complete, "worker-v1", "", from.AddMinutes(6));
        var bar = new MarketBar("terminal-a", "Broker|1", "XAUUSD.s", "M5", from,
            3500m, 3502m, 3499m, 3501m, 50, 12, 0);
        var tickRange = range with
        {
            RequestId = "request-ticks",
            Precision = MarketDataPrecision.Ticks,
            SourceVersion = "worker-ticks-v1",
        };
        var tick = new MarketTick("terminal-a", "Broker|1", "XAUUSD.s", from.AddSeconds(1),
            from.ToUnixTimeMilliseconds() + 1000, 3500m, 3500.5m, 3500.25m, 1m, 1, "tick-a");

        await _database.SaveMarketDataAsync(range, [bar], []);
        await _database.SaveMarketDataAsync(tickRange, [], [tick]);

        var loaded = await _database.LoadMarketDataAsync("Broker|1", "terminal-a", "XAUUSD.s", "M5", MarketDataPrecision.Bars, from, from.AddMinutes(5));
        var loadedTicks = await _database.LoadMarketDataAsync("Broker|1", "terminal-a", "XAUUSD.s", "M5", MarketDataPrecision.Ticks, from, from.AddMinutes(5));
        var otherAccount = await _database.LoadMarketDataAsync("Broker|2", "terminal-a", "XAUUSD.s", "M5", MarketDataPrecision.Bars, from, from.AddMinutes(5));
        Assert.Equal(MarketCoverageStatus.Complete, loaded.Range?.Coverage);
        Assert.Single(loaded.Bars);
        Assert.Empty(loaded.Ticks);
        Assert.Equal(MarketDataPrecision.Ticks, loadedTicks.Range?.Precision);
        Assert.Single(loadedTicks.Ticks);
        Assert.Empty(loadedTicks.Bars);
        Assert.Null(otherAccount.Range);
        Assert.Empty(otherAccount.Bars);
    }

    [Fact]
    public async Task MarketCacheCleanup_RemovesOnlyRebuildableRowsAndKeepsHumanReview()
    {
        const string accountKey = "Broker|cache";
        var from = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var range = new MarketDataRange("cache-request", "terminal-a", accountKey, "XAUUSD.s", "M1",
            from, from.AddMinutes(1), from, from.AddMinutes(1), MarketDataPrecision.Bars,
            MarketCoverageStatus.Complete, "worker-v1", "", from.AddMinutes(2));
        var bar = new MarketBar("terminal-a", accountKey, "XAUUSD.s", "M1", from,
            3500m, 3502m, 3499m, 3501m, 50, 12, 0);
        var document = ReviewDocument(accountKey, 88, from);
        await SeedCompletedTradeAsync(accountKey, 88, from);
        Assert.True((await _database.SaveTradeReviewDocumentAsync(document, 0)).IsSaved);
        await _database.SaveMarketDataAsync(range, [bar], []);

        var cleanup = await _database.ClearMarketDataCacheAsync(accountKey);
        var market = await _database.LoadMarketDataAsync(
            accountKey, "terminal-a", "XAUUSD.s", "M1", MarketDataPrecision.Bars,
            from, from.AddMinutes(1));
        var persistedReview = await _database.LoadTradeReviewDocumentAsync(new TradeKey(accountKey, 88));

        Assert.Equal(1, cleanup.RangeCount);
        Assert.Equal(1, cleanup.BarCount);
        Assert.Equal(0, cleanup.TickCount);
        Assert.Null(market.Range);
        Assert.Empty(market.Bars);
        Assert.Equal("交易复盘", persistedReview!.Summary);
        Assert.Equal(1, persistedReview.Revision);
    }

    [Fact]
    public async Task AttachmentStore_CopiesIntoControlledContentAddressedPathAndDeduplicates()
    {
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        await _database.SaveTradesAsync([
            new TradeRecord("Broker|1", 11, "XAUUSD.s", TradeSide.Buy, now.AddMinutes(-2), now,
                date, date, 1m, 2m, 1m, 1m, 0m, 1m, true),
            new TradeRecord("Broker|1", 12, "XAUUSD.s", TradeSide.Buy, now.AddMinutes(-2), now,
                date, date, 1m, 2m, 1m, 1m, 0m, 1m, true),
        ]);
        var source = Path.Combine(_testDirectory, "截图.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var attachmentRoot = Path.Combine(_testDirectory, "attachments");
        var store = new ReviewAttachmentStore(_database, attachmentRoot);
        var stamp = new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, now,
            now, ReviewTimeBasis.Local, "manual-v1");

        var first = await store.ImportAsync("Broker|1", "trade", "11", source, "入场", stamp, eventReference: "首次入场");
        var second = await store.ImportAsync("Broker|1", "trade", "12", source, "出场", stamp, eventReference: "最终退出");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(Directory.EnumerateFiles(attachmentRoot, "*.png", SearchOption.AllDirectories));
        var detailPath = Path.GetFullPath(Path.Combine(attachmentRoot, first.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(File.Exists(detailPath));

        await store.DeleteAsync(first.Id, "Broker|1", "trade", "11");
        Assert.True(File.Exists(detailPath));
        var reopened = new AppDatabase(Path.Combine(_testDirectory, "test.db"));
        await reopened.InitializeAsync();
        var remaining = Assert.Single((await reopened.LoadTradeDetailAsync(new TradeKey("Broker|1", 12)))!.Attachments);
        Assert.Equal("最终退出", remaining.EventReference);
        await store.DeleteAsync(second.Id, "Broker|2", "trade", "12");
        Assert.True(File.Exists(detailPath));
        await store.DeleteAsync(second.Id, "Broker|1", "trade", "12");
        Assert.False(File.Exists(detailPath));
        Assert.Throws<InvalidDataException>(() => store.ResolvePath(second with { RelativePath = "../outside.png" }));
    }

    [Fact]
    public async Task Opportunity_PersistsRevisionConditionsAndAccountScopedAttachment()
    {
        const string accountKey = "Broker|opportunity";
        var now = new DateTimeOffset(2026, 9, 8, 5, 0, 0, TimeSpan.Zero);
        var opportunity = new OpportunityRecord(
            "opportunity-1", accountKey, OpportunityRecordKind.ObservedBeforeMove, now.AddMinutes(-2), now,
            new DateOnly(2026, 9, 8), "XAUUSD.s", TradeSide.Buy, null, 3500m, 3495m, 3510m,
            "风险额度不足", "保持观察", null, 0, "M5 突破并回踩");
        var created = await _database.SaveOpportunityAsync(opportunity, 0);
        var updated = await _database.SaveOpportunityAsync(created.Value! with { Notes = "走势完成" }, 1);
        var conflict = await _database.SaveOpportunityAsync(updated.Value! with { Notes = "过期修改" }, 1);
        var outsideOpportunity = opportunity with
        {
            Id = "opportunity-outside",
            ServerDate = opportunity.ServerDate.AddDays(1),
            ObservedAtUtc = opportunity.ObservedAtUtc.AddDays(1),
            RecordedAtUtc = opportunity.RecordedAtUtc.AddDays(1),
        };
        Assert.True((await _database.SaveOpportunityAsync(outsideOpportunity, 0)).IsSaved);
        var source = Path.Combine(_testDirectory, "opportunity.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var attachmentStore = new ReviewAttachmentStore(_database, Path.Combine(_testDirectory, "opportunity-attachments"));
        await attachmentStore.ImportAsync(
            accountKey, "opportunity", opportunity.Id, source, "观察截图",
            new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, now, now, ReviewTimeBasis.Local, "manual-v1"),
            eventReference: "突破确认");
        await attachmentStore.ImportAsync(
            accountKey, "opportunity", outsideOpportunity.Id, source, "范围外截图",
            new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, now, now, ReviewTimeBasis.Local, "manual-v1"),
            eventReference: "范围外");

        var workspace = await _database.LoadWorkspaceAsync(accountKey, new(2026, 9, 8), new(2026, 9, 8));
        var otherAccount = await _database.LoadWorkspaceAsync("Broker|other", new(2026, 9, 8), new(2026, 9, 8));

        Assert.True(created.IsSaved);
        Assert.Equal(2, updated.Value!.Revision);
        Assert.Equal(ReviewSaveStatus.Conflict, conflict.Status);
        Assert.Equal("M5 突破并回踩", Assert.Single(workspace.Opportunities).Conditions);
        var attachment = Assert.Single(workspace.OpportunityAttachments!);
        Assert.Equal(opportunity.Id, attachment.OwnerId);
        Assert.Equal("突破确认", attachment.EventReference);
        Assert.Equal(opportunity.Id, attachment.OwnerId);
        Assert.Empty(otherAccount.OpportunityAttachments!);
    }

    [Fact]
    public async Task TradeDetailBatch_UsesExactCompositeIdentityAndOneFrozenVersionPerAccount()
    {
        const string firstAccount = "BrokerA|77";
        const string secondAccount = "BrokerB|77";
        const long sharedPosition = 7001;
        var now = new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero);
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var firstTrade = new TradeRecord(firstAccount, sharedPosition, "XAUUSD.a", TradeSide.Buy,
            now.AddMinutes(-10), now, date, date, 3500m, 3501m, 0.1m, 0.1m, 0m, 10m, true);
        var siblingTrade = firstTrade with { PositionId = sharedPosition + 1, Symbol = "EURUSD.a" };
        var secondTrade = firstTrade with { AccountKey = secondAccount, Symbol = "XAUUSD.b", NetPnl = -4m };
        await _database.SaveTradesAsync([firstTrade, siblingTrade, secondTrade]);
        await _database.UpsertDealAsync(firstAccount, new DealRecord(
            7101, 7101, sharedPosition, firstTrade.Symbol, firstTrade.Side, DealEntryKind.Out,
            0.1m, 3501m, 10m, 0m, 0m, 0m, now));
        await _database.UpsertDealAsync(firstAccount, new DealRecord(
            7102, 7102, siblingTrade.PositionId, siblingTrade.Symbol, siblingTrade.Side, DealEntryKind.Out,
            0.1m, 1.1m, 1m, 0m, 0m, 0m, now));
        await _database.UpsertDealAsync(secondAccount, new DealRecord(
            7201, 7201, sharedPosition, secondTrade.Symbol, secondTrade.Side, DealEntryKind.Out,
            0.1m, 3499m, -4m, 0m, 0m, 0m, now));
        Assert.True((await _database.SaveTradeReviewDocumentAsync(
            ReviewDocument(firstAccount, sharedPosition, now) with { Summary = "A review" }, 0)).IsSaved);
        Assert.True((await _database.SaveTradeReviewDocumentAsync(
            ReviewDocument(secondAccount, sharedPosition, now) with { Summary = "B review" }, 0)).IsSaved);

        var details = await _database.LoadTradeDetailsAsync([
            new TradeKey(firstAccount, sharedPosition),
            new TradeKey(firstAccount, siblingTrade.PositionId),
            new TradeKey(secondAccount, sharedPosition),
        ]);

        Assert.Equal(3, details.Count);
        var first = details.Single(item => item.Trade.AccountKey == firstAccount && item.Trade.PositionId == sharedPosition);
        var sibling = details.Single(item => item.Trade.AccountKey == firstAccount && item.Trade.PositionId == siblingTrade.PositionId);
        var second = details.Single(item => item.Trade.AccountKey == secondAccount && item.Trade.PositionId == sharedPosition);
        Assert.Equal(7101, Assert.Single(first.Deals).Ticket);
        Assert.Equal(7102, Assert.Single(sibling.Deals).Ticket);
        Assert.Equal(7201, Assert.Single(second.Deals).Ticket);
        Assert.Equal("A review", first.Document!.Summary);
        Assert.Equal("B review", second.Document!.Summary);
        Assert.Equal(first.Version.Token, sibling.Version.Token);
        Assert.NotEqual(first.Trade.Symbol, second.Trade.Symbol);
    }

    [Fact]
    public async Task ReviewWorkspace_ReadsBusinessRowsAndVersionFromTheSameConcurrentSnapshot()
    {
        const string accountKey = "Broker|snapshot";
        var now = new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var original = new TradeRecord(accountKey, 8301, "XAUUSD.s", TradeSide.Buy,
            now.AddMinutes(-5), now, date, date, 3500m, 3501m, 0.01m, 0.01m, 0m, 1m, true);
        await _database.UpsertTradeAsync(original);
        var versionBefore = await _database.LoadReviewDataVersionAsync(accountKey);
        var enteredCheckpoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.ReviewReadCheckpointAsync = async (checkpoint, cancellationToken) =>
        {
            if (checkpoint == "workspace-trades-loaded")
            {
                enteredCheckpoint.TrySetResult(true);
                await continueRead.Task.WaitAsync(cancellationToken);
            }
        };

        var pendingSnapshot = _database.LoadWorkspaceAsync(accountKey, date, date);
        await enteredCheckpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await _database.UpsertTradeAsync(original with { NetPnl = 99m });
            Assert.True((await _database.SaveTradeReviewDocumentAsync(
                ReviewDocument(accountKey, original.PositionId, now) with { Summary = "concurrent review" }, 0)).IsSaved);
        }
        finally
        {
            _database.ReviewReadCheckpointAsync = null;
            continueRead.TrySetResult(true);
        }

        var frozen = await pendingSnapshot.WaitAsync(TimeSpan.FromSeconds(5));
        var directlyLoadedDocument = await _database.LoadTradeReviewDocumentAsync(
            new TradeKey(accountKey, original.PositionId));
        var current = await _database.LoadWorkspaceAsync(accountKey, date, date);

        Assert.Equal(1m, Assert.Single(frozen.Trades).NetPnl);
        Assert.Empty(frozen.Documents);
        Assert.Equal(versionBefore.Token, frozen.Version.Token);
        Assert.Equal("concurrent review", directlyLoadedDocument!.Summary);
        Assert.Equal(original.PositionId, directlyLoadedDocument.TradeKey.PositionId);
        Assert.Equal(99m, Assert.Single(current.Trades).NetPnl);
        Assert.Equal("concurrent review", Assert.Single(current.Documents).Value.Summary);
        Assert.NotEqual(frozen.Version.Token, current.Version.Token);
    }

    [Fact]
    public async Task ReviewReadQueries_UseCompositeIdentityAndRangeIndexes()
    {
        const string accountKey = "Broker|query-plan";
        var now = new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero);
        await SeedCompletedTradeAsync(accountKey, 8101, now);
        await _database.UpsertDealAsync(accountKey, new DealRecord(
            8201, 8201, 8101, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out,
            0.01m, 3501m, 1m, 0m, 0m, 0m, now));
        await using var connection = await _database.OpenConnectionAsync();

        var tradePlan = await ReadQueryPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT * FROM trades WHERE account_key=$account AND position_id=$position;",
            ("$account", accountKey), ("$position", 8101L));
        var rangePlan = await ReadQueryPlanAsync(connection,
            """
            EXPLAIN QUERY PLAN
            SELECT position_id, opened_at_utc FROM trades
            WHERE account_key=$account AND is_complete=1 AND open_server_date <= $to AND close_server_date >= $from
            UNION ALL
            SELECT position_id, opened_at_utc FROM trades
            WHERE account_key=$account AND is_complete=0 AND open_server_date <= $to
            ORDER BY opened_at_utc;
            """,
            ("$account", accountKey), ("$from", "2026-09-01"), ("$to", "2026-09-30"));
        var closeRangePlan = await ReadQueryPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT position_id FROM trades WHERE account_key=$account AND is_complete=1 AND close_server_date >= $from AND close_server_date <= $to;",
            ("$account", accountKey), ("$from", "2026-09-01"), ("$to", "2026-09-30"));
        var dealPlan = await ReadQueryPlanAsync(connection,
            "EXPLAIN QUERY PLAN SELECT * FROM deals WHERE account_key=$account AND position_id=$position ORDER BY occurred_at_utc, ticket;",
            ("$account", accountKey), ("$position", 8101L));

        Assert.Contains("sqlite_autoindex_trades_1", tradePlan, StringComparison.OrdinalIgnoreCase);
        Assert.True(rangePlan.Contains("ix_trades_review_open", StringComparison.OrdinalIgnoreCase), rangePlan);
        Assert.DoesNotContain("SCAN trades", rangePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ix_trades_review_close", closeRangePlan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ix_deals_position", dealPlan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewWorkspace_DailyScopeIncludesSpanningTradeDealsAndTargetState()
    {
        const string accountKey = "Broker|9010";
        var date = new DateOnly(2026, 9, 8);
        var targetAt = new DateTimeOffset(2026, 9, 8, 4, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("Broker", 9010), "USD", 1_000m, 1_004m, 4m, 2, targetAt));
        var trade = new TradeRecord(accountKey, 901, "XAUUSD.s", TradeSide.Buy,
            targetAt.AddDays(-1), targetAt.AddDays(1), date.AddDays(-1), date.AddDays(1),
            3500m, 3510m, 1m, 1m, 0m, 10m, true);
        var deal = new DealRecord(902, 902, trade.PositionId, trade.Symbol, trade.Side, DealEntryKind.Out,
            0.5m, 3505m, 5m, -1m, 0m, 0m, targetAt.AddHours(1));
        var state = CreateDailyState(accountKey, date, 4m) with
        {
            TargetAlerted = true,
            TargetReachedAtUtc = targetAt,
            TargetRuleVersion = "daily-target/v1:4",
            TargetAmountAtReach = 4m,
        };
        await _database.UpsertTradeAsync(trade);
        await _database.UpsertDealAsync(accountKey, deal);
        await _database.UpsertTradingDayAsync(state, DailyPlanSettings.BalancedDefault with { DailyTarget = 4m });

        var workspace = await _database.LoadWorkspaceAsync(accountKey, date, date);

        Assert.Equal(trade, Assert.Single(workspace.Trades));
        Assert.Equal(deal, Assert.Single(workspace.Deals));
        Assert.Equal(targetAt, workspace.DailyStates![date].TargetReachedAtUtc);
    }

    [Fact]
    public async Task BulkReviewEdit_RollsBackEveryMetadataChangeOnConcurrencyConflict()
    {
        const string accountKey = "Broker|bulk";
        var date = new DateOnly(2026, 9, 8);
        var now = new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero);
        var firstTrade = new TradeRecord(accountKey, 101, "XAUUSD.s", TradeSide.Buy, now.AddMinutes(-10), now,
            date, date, 3500m, 3501m, 1m, 1m, 0m, 10m, true);
        var secondTrade = firstTrade with { PositionId = 102, NetPnl = -5m };
        await _database.SaveTradesAsync([firstTrade, secondTrade]);
        var firstMetadata = new TradeReviewMetadata(
            accountKey, 101, null, PlanComplianceStatus.Unclassified, "旧一", "", ["旧"], true, now.AddMinutes(-2));
        var secondMetadata = new TradeReviewMetadata(
            accountKey, 102, null, PlanComplianceStatus.Unclassified, "旧二", "", ["旧"], true, now.AddMinutes(-1));
        await _database.UpsertTradeReviewMetadataAsync(firstMetadata);
        await _database.UpsertTradeReviewMetadataAsync(secondMetadata);
        var versionBefore = (await _database.LoadWorkspaceAsync(accountKey, date, date)).Version.MetadataVersion;
        var changedAt = now.AddMinutes(1);
        var firstWrite = new ReviewBulkWriteItem(new TradeKey(accountKey, 101),
            firstMetadata with { Strategy = "新策略", UpdatedAtUtc = changedAt }, firstMetadata.UpdatedAtUtc, null, 0);
        var secondWrite = new ReviewBulkWriteItem(new TradeKey(accountKey, 102),
            secondMetadata with { Strategy = "新策略", UpdatedAtUtc = changedAt }, now.AddDays(-1), null, 0);

        var conflict = await _database.SaveBulkReviewAsync([firstWrite, secondWrite]);
        var afterConflict = await _database.LoadTradeReviewMetadataAsync(accountKey, [101, 102]);
        var versionAfterConflict = (await _database.LoadWorkspaceAsync(accountKey, date, date)).Version.MetadataVersion;
        var saved = await _database.SaveBulkReviewAsync([
            firstWrite,
            secondWrite with { ExpectedMetadataUpdatedAtUtc = secondMetadata.UpdatedAtUtc },
        ]);
        var afterSave = await _database.LoadTradeReviewMetadataAsync(accountKey, [101, 102]);
        var versionAfterSave = (await _database.LoadWorkspaceAsync(accountKey, date, date)).Version.MetadataVersion;

        Assert.Equal(ReviewSaveStatus.Conflict, conflict.Status);
        Assert.Equal("旧一", afterConflict[101].Strategy);
        Assert.Equal("旧二", afterConflict[102].Strategy);
        Assert.Equal(versionBefore, versionAfterConflict);
        Assert.True(saved.IsSaved);
        Assert.Equal(2, saved.Value!.AffectedCount);
        Assert.All(afterSave.Values, item => Assert.Equal("新策略", item.Strategy));
        Assert.True(versionAfterSave > versionAfterConflict);
    }

    [Fact]
    public async Task SavedReviewFilter_RoundTripsAccountAssessmentAndCampaignAfterReopen()
    {
        const string accountKey = "Broker|filter";
        var now = new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero);
        var filter = new ReviewWorkspaceFilter(
            accountKey, new(2026, 8, 1), new(2026, 9, 8), Strategy: "突破",
            Tags: ["早盘", "重点"], TagMatchMode: ReviewTagMatchMode.All,
            Assessment: RuleAssessmentStatus.Failed, CampaignId: "campaign-1", Search: "90001");
        var saved = await _database.SaveFilterAsync(new ReviewSavedFilter(
            "filter-1", accountKey, "违规突破", System.Text.Json.JsonSerializer.Serialize(filter), 0, now, now), 0);

        var reopened = new AppDatabase(Path.Combine(_testDirectory, "test.db"));
        await reopened.InitializeAsync();
        var loaded = Assert.Single(await reopened.LoadFiltersAsync(accountKey));
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ReviewWorkspaceFilter>(loaded.FilterJson)!;

        Assert.True(saved.IsSaved);
        Assert.Equal(accountKey, roundTripped.AccountKey);
        Assert.Equal(ReviewTagMatchMode.All, roundTripped.TagMatchMode);
        Assert.Equal(RuleAssessmentStatus.Failed, roundTripped.Assessment);
        Assert.Equal("campaign-1", roundTripped.CampaignId);
        Assert.Equal("90001", roundTripped.Search);
    }

    [Fact]
    public async Task ReviewPackageWriter_RejectsTraversalEntries()
    {
        var writer = new ReviewPackageWriter();
        var package = new ReviewExportPackage("review.zip", "1:1", [new ReviewExportEntry("../secret.txt", "text/plain", [1])]);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(package, _testDirectory));
    }

    [Fact]
    public async Task ReviewPackageWriter_StreamsVerifiedFileAndRejectsMissingOrChangedAttachment()
    {
        var source = Path.Combine(_testDirectory, "export-source.png");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(source, bytes);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var writer = new ReviewPackageWriter();
        var entry = new ReviewExportEntry(
            "attachments/A000001/asset.png", "image/png", [], source, hash, bytes.Length);
        var validPackage = new ReviewExportPackage("valid.zip", "v1", [entry]);

        var destination = await writer.WriteAsync(validPackage, _testDirectory);
        using (var archive = ZipFile.OpenRead(destination))
        await using (var content = archive.GetEntry(entry.Path)!.Open())
        {
            using var output = new MemoryStream();
            await content.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
        }

        await File.WriteAllBytesAsync(source, [9, 9, 9, 9, 9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(
            validPackage with { FileName = "changed.zip" }, _testDirectory));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "changed.zip")));

        File.Delete(source);
        await Assert.ThrowsAsync<FileNotFoundException>(() => writer.WriteAsync(
            validPackage with { FileName = "missing.zip" }, _testDirectory));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "missing.zip")));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ReviewPackageWriter_RejectsDuplicatePathsAndOversizedContent()
    {
        var writer = new ReviewPackageWriter();
        var duplicate = new ReviewExportPackage("duplicate.zip", "v1", [
            new ReviewExportEntry("trades.csv", "text/csv", [1]),
            new ReviewExportEntry("TRADES.csv", "text/csv", [2]),
        ]);
        var oversized = new ReviewExportPackage("oversized.zip", "v1", [
            new ReviewExportEntry("attachments/A000001/asset.png", "image/png", [],
                Path.Combine(_testDirectory, "unused.png"), new string('a', 64), 25L * 1024 * 1024 + 1),
        ]);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(duplicate, _testDirectory));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(oversized, _testDirectory));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "duplicate.zip")));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "oversized.zip")));
    }

    [Fact]
    public async Task ReviewPackageWriter_CancelledWriteLeavesNoPartialPackageAndNeverOverwritesExisting()
    {
        var writer = new ReviewPackageWriter();
        var original = new ReviewExportPackage("same.zip", "v1", [
            new ReviewExportEntry("trades.csv", "text/csv", [1, 2, 3]),
        ]);
        var destination = await writer.WriteAsync(original, _testDirectory);
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(destination)));

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(
            original with { Entries = [new ReviewExportEntry("trades.csv", "text/csv", [9])] },
            _testDirectory));
        Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(destination))));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(
            original with { FileName = "cancelled.zip" }, _testDirectory, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "cancelled.zip")));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ReviewBackup_RestoresRevisionedNotesAttachmentsAndVerifiedHashes()
    {
        const string accountKey = "BackupBroker|7001";
        var now = new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("BackupBroker", 7001), "USD", 10_000m, 10_000m, 0m, 0, now));
        var originalDocument = ReviewDocument(accountKey, 701, now) with { Summary = "备份前结论" };
        await SeedCompletedTradeAsync(accountKey, 701, now);
        Assert.True((await _database.SaveTradeReviewDocumentAsync(originalDocument, 0)).IsSaved);

        var attachmentRoot = Path.Combine(_testDirectory, "backup-attachments");
        var source = Path.Combine(_testDirectory, "backup-note.txt");
        var originalBytes = "原始复盘附件"u8.ToArray();
        await File.WriteAllBytesAsync(source, originalBytes);
        var attachment = await new ReviewAttachmentStore(_database, attachmentRoot).ImportAsync(
            accountKey, "trade", "701", source, "证据",
            new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, now, now, ReviewTimeBasis.Local, "manual-v1"));
        var backupDirectory = Path.Combine(_testDirectory, "review-backups");
        var service = new ReviewBackupService(_database, attachmentRoot, Path.Combine(_testDirectory, "test.db"));

        var package = await service.CreateAsync(backupDirectory);
        var manifest = await service.ValidateAsync(package);
        Assert.Contains(manifest.Files, item => item.Path == "tradepet.db" && item.Sha256.Length == 64);
        Assert.Contains(accountKey, manifest.AccountKeys);

        Assert.True((await _database.SaveTradeReviewDocumentAsync(
            originalDocument with { Summary = "备份后修改" }, 1)).IsSaved);
        var storedAttachment = Path.Combine(attachmentRoot,
            attachment.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(storedAttachment, "已被修改"u8.ToArray());

        var restored = await service.RestoreAsync(package, backupDirectory);

        Assert.True(File.Exists(restored.SafetyBackupPath));
        Assert.Equal("备份前结论",
            (await _database.LoadTradeReviewDocumentAsync(new TradeKey(accountKey, 701)))?.Summary);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(storedAttachment));
    }

    [Fact]
    public async Task ReviewBackup_RejectsTamperedEntryBeforeReplacingCurrentData()
    {
        const string accountKey = "BackupBroker|7002";
        var now = new DateTimeOffset(2026, 9, 8, 3, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("BackupBroker", 7002), "USD", 10_000m, 10_000m, 0m, 0, now));
        var document = ReviewDocument(accountKey, 702, now) with { Summary = "当前健康数据" };
        await SeedCompletedTradeAsync(accountKey, 702, now);
        Assert.True((await _database.SaveTradeReviewDocumentAsync(document, 0)).IsSaved);
        var attachmentRoot = Path.Combine(_testDirectory, "tamper-attachments");
        var source = Path.Combine(_testDirectory, "tamper-note.txt");
        await File.WriteAllTextAsync(source, "不能篡改");
        await new ReviewAttachmentStore(_database, attachmentRoot).ImportAsync(
            accountKey, "trade", "702", source, "证据",
            new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, now, now, ReviewTimeBasis.Local, "manual-v1"));
        var service = new ReviewBackupService(_database, attachmentRoot, Path.Combine(_testDirectory, "test.db"));
        var backupDirectory = Path.Combine(_testDirectory, "tamper-backups");
        var package = await service.CreateAsync(backupDirectory);
        var tampered = Path.Combine(backupDirectory, "tampered.zip");
        File.Copy(package, tampered);
        using (var archive = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            var entry = archive.Entries.Single(item => item.FullName.StartsWith("attachments/", StringComparison.Ordinal));
            var path = entry.FullName;
            entry.Delete();
            var replacement = archive.CreateEntry(path);
            await using var output = replacement.Open();
            await output.WriteAsync("篡改"u8.ToArray());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(tampered, backupDirectory));

        Assert.Equal("当前健康数据",
            (await _database.LoadTradeReviewDocumentAsync(new TradeKey(accountKey, 702)))?.Summary);
    }

    [Theory]
    [InlineData("Prepared", false)]
    [InlineData("Staged", false)]
    [InlineData("BeforeOldDatabase0", false)]
    [InlineData("AfterOldDatabase0", false)]
    [InlineData("BeforeNewDatabase", false)]
    [InlineData("AfterNewDatabase", false)]
    [InlineData("BeforeOldAttachments", false)]
    [InlineData("AfterOldAttachments", false)]
    [InlineData("BeforeNewAttachments", false)]
    [InlineData("AfterNewAttachments", false)]
    [InlineData("Committed", true)]
    public async Task ReviewBackup_InterruptedProcess_RestartsToOneVerifiedFileSet(
        string step, bool newSetCommitted)
    {
        var databasePath = Path.Combine(_testDirectory, "test.db");
        var attachmentRoot = Path.Combine(_testDirectory, "restart-attachments");
        Directory.CreateDirectory(attachmentRoot);
        var attachmentPath = Path.Combine(attachmentRoot, "proof.txt");
        await File.WriteAllTextAsync(attachmentPath, "package-file");
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("Package", 1), "USD", 1m, 1m, 0m, 0,
            new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero)));
        var backupDirectory = Path.Combine(_testDirectory, "restart-backups");
        var service = new ReviewBackupService(_database, attachmentRoot, databasePath);
        var package = await service.CreateAsync(backupDirectory);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("Current", 2), "USD", 2m, 2m, 0m, 0,
            new DateTimeOffset(2026, 9, 9, 1, 0, 0, TimeSpan.Zero)));
        await File.WriteAllTextAsync(attachmentPath, "current-file");
        service.RestoreCheckpoint = observed =>
        {
            if (observed == step)
            {
                throw new ReviewBackupService.SimulatedRestoreCrashException();
            }
        };

        await Assert.ThrowsAsync<ReviewBackupService.SimulatedRestoreCrashException>(() =>
            service.RestoreAsync(package, backupDirectory));
        var restarted = new ReviewBackupService(new AppDatabase(databasePath), attachmentRoot, databasePath);
        Assert.True(await restarted.RecoverPendingAsync());
        Assert.False(await restarted.RecoverPendingAsync());
        Assert.False(File.Exists(databasePath + ".restore-state.json"));
        Assert.Equal(newSetCommitted ? "package-file" : "current-file",
            await File.ReadAllTextAsync(attachmentPath));
        var accounts = await restarted.ValidateAsync(await restarted.CreateAsync(backupDirectory));
        Assert.Contains("Package|1", accounts.AccountKeys);
        if (newSetCommitted)
        {
            Assert.DoesNotContain("Current|2", accounts.AccountKeys);
        }
        else
        {
            Assert.Contains("Current|2", accounts.AccountKeys);
        }
    }

    [Theory]
    [InlineData("BeforeOldDatabase0")]
    [InlineData("AfterOldDatabase0")]
    [InlineData("BeforeNewDatabase")]
    [InlineData("AfterNewDatabase")]
    [InlineData("BeforeOldAttachments")]
    [InlineData("AfterOldAttachments")]
    [InlineData("BeforeNewAttachments")]
    [InlineData("AfterNewAttachments")]
    public async Task ReviewBackup_MoveFailure_RollsBackBeforeCleaningOldSet(string step)
    {
        var databasePath = Path.Combine(_testDirectory, "test.db");
        var attachmentRoot = Path.Combine(_testDirectory, "failure-attachments");
        Directory.CreateDirectory(attachmentRoot);
        var attachmentPath = Path.Combine(attachmentRoot, "proof.txt");
        await File.WriteAllTextAsync(attachmentPath, "backup");
        var service = new ReviewBackupService(_database, attachmentRoot, databasePath);
        var backupDirectory = Path.Combine(_testDirectory, "failure-backups");
        var package = await service.CreateAsync(backupDirectory);
        await File.WriteAllTextAsync(attachmentPath, "current");
        service.RestoreCheckpoint = observed =>
        {
            if (observed == step)
            {
                throw new IOException("injected move failure");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(package, backupDirectory));
        Assert.Equal("current", await File.ReadAllTextAsync(attachmentPath));
        Assert.False(File.Exists(databasePath + ".restore-state.json"));
        Assert.EndsWith(".zip", await service.CreateAsync(backupDirectory));
    }

    [Fact]
    public async Task ReviewBackup_CancellationBeforeInstall_RestoresCurrentAttachments()
    {
        var databasePath = Path.Combine(_testDirectory, "test.db");
        var attachmentRoot = Path.Combine(_testDirectory, "cancel-attachments");
        Directory.CreateDirectory(attachmentRoot);
        var attachmentPath = Path.Combine(attachmentRoot, "proof.txt");
        await File.WriteAllTextAsync(attachmentPath, "backup");
        var service = new ReviewBackupService(_database, attachmentRoot, databasePath);
        var backupDirectory = Path.Combine(_testDirectory, "cancel-backups");
        var package = await service.CreateAsync(backupDirectory);
        await File.WriteAllTextAsync(attachmentPath, "current");
        service.RestoreCheckpoint = step =>
        {
            if (step == "BeforeNewDatabase")
            {
                throw new OperationCanceledException();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RestoreAsync(package, backupDirectory));
        Assert.Equal("current", await File.ReadAllTextAsync(attachmentPath));
        Assert.False(File.Exists(databasePath + ".restore-state.json"));
    }

    [Fact]
    public async Task ReviewBackup_InstalledAttachmentHashFailure_RestoresCurrentSet()
    {
        var databasePath = Path.Combine(_testDirectory, "test.db");
        var attachmentRoot = Path.Combine(_testDirectory, "hash-attachments");
        Directory.CreateDirectory(attachmentRoot);
        var attachmentPath = Path.Combine(attachmentRoot, "proof.txt");
        await File.WriteAllTextAsync(attachmentPath, "backup");
        var service = new ReviewBackupService(_database, attachmentRoot, databasePath);
        var backupDirectory = Path.Combine(_testDirectory, "hash-backups");
        var package = await service.CreateAsync(backupDirectory);
        await File.WriteAllTextAsync(attachmentPath, "current");
        service.RestoreCheckpoint = step =>
        {
            if (step == "AfterNewAttachments")
            {
                File.WriteAllText(attachmentPath, "tampered");
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreAsync(package, backupDirectory));
        Assert.Equal("current", await File.ReadAllTextAsync(attachmentPath));
        Assert.False(File.Exists(databasePath + ".restore-state.json"));
    }

    [Fact]
    public async Task ReviewBackup_ExplicitUnreadableRestore_PreservesRawOldSetAndInstallsVerifiedPackage()
    {
        var databasePath = Path.Combine(_testDirectory, "test.db");
        var attachmentRoot = Path.Combine(_testDirectory, "unreadable-attachments");
        Directory.CreateDirectory(attachmentRoot);
        var attachmentPath = Path.Combine(attachmentRoot, "proof.txt");
        await File.WriteAllTextAsync(attachmentPath, "package");
        var backupDirectory = Path.Combine(_testDirectory, "unreadable-backups");
        var service = new ReviewBackupService(_database, attachmentRoot, databasePath);
        var package = await service.CreateAsync(backupDirectory);
        await File.WriteAllTextAsync(attachmentPath, "current");
        SqliteConnection.ClearAllPools();
        var unreadableBytes = "not a sqlite database"u8.ToArray();
        await File.WriteAllBytesAsync(databasePath, unreadableBytes);

        var result = await service.RestoreAsync(package, backupDirectory,
            preserveUnreadableCurrent: true);

        Assert.True(Directory.Exists(result.SafetyBackupPath));
        Assert.Equal(unreadableBytes, await File.ReadAllBytesAsync(
            Path.Combine(result.SafetyBackupPath, "test.db")));
        Assert.Equal("current", await File.ReadAllTextAsync(Path.Combine(
            result.SafetyBackupPath, "attachments", "proof.txt")));
        Assert.True(File.Exists(Path.Combine(result.SafetyBackupPath, "raw-preservation.json")));
        Assert.Equal("package", await File.ReadAllTextAsync(attachmentPath));
        Assert.True(await new AppDatabase(databasePath).InitializeWithRecoveryAsync(
            Path.Combine(_testDirectory, "backups-after-restore")) is { Recovered: false });
    }

    [Theory]
    [InlineData("AfterOldDatabase0", false)]
    [InlineData("AfterNewDatabase", false)]
    [InlineData("AfterOldAttachments", false)]
    [InlineData("AfterNewAttachments", false)]
    [InlineData("Committed", true)]
    public async Task ReviewBackup_HardProcessExit_RecoversOnNextInstance(string step, bool committed)
    {
        var databasePath = Path.Combine(_testDirectory, "test.db");
        var attachmentRoot = Path.Combine(_testDirectory, "hard-exit-attachments");
        Directory.CreateDirectory(attachmentRoot);
        var attachmentPath = Path.Combine(attachmentRoot, "proof.txt");
        await File.WriteAllTextAsync(attachmentPath, "package");
        var backupDirectory = Path.Combine(_testDirectory, "hard-exit-backups");
        var service = new ReviewBackupService(_database, attachmentRoot, databasePath);
        var package = await service.CreateAsync(backupDirectory);
        await File.WriteAllTextAsync(attachmentPath, "current");
        SqliteConnection.ClearAllPools();

        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "TradePet.Infrastructure.Tests.csproj"));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "test", project, "-c", "Release", "--no-build", "--no-restore",
                     "--filter", "FullyQualifiedName~ReviewRestoreProcessTests.ExitAtCheckpoint" })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["TRADEPET_RESTORE_FAULT_DATABASE"] = databasePath;
        start.Environment["TRADEPET_RESTORE_FAULT_ATTACHMENTS"] = attachmentRoot;
        start.Environment["TRADEPET_RESTORE_FAULT_PACKAGE"] = package;
        start.Environment["TRADEPET_RESTORE_FAULT_BACKUPS"] = backupDirectory;
        start.Environment["TRADEPET_RESTORE_FAULT_STEP"] = step;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("无法启动隔离恢复测试进程。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode != 0, $"注入进程没有被硬结束：{output}\n{error}");
        Assert.True(File.Exists(databasePath + ".restore-state.json"),
            $"子进程未进入恢复切换：{output}\n{error}");

        var restarted = new ReviewBackupService(new AppDatabase(databasePath), attachmentRoot, databasePath);
        Assert.True(await restarted.RecoverPendingAsync());
        Assert.Equal(committed ? "package" : "current", await File.ReadAllTextAsync(attachmentPath));
        Assert.False(File.Exists(databasePath + ".restore-state.json"));
    }

    [Fact]
    public async Task PlaybookVersion_ActivatesOnlyLatestVersionAndKeepsHistoricalPayload()
    {
        const string accountKey = "PlaybookBroker|8001";
        var now = new DateTimeOffset(2026, 9, 8, 4, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("PlaybookBroker", 8001), "USD", 10_000m, 10_000m, 0m, 0, now));
        var rule = new PlaybookRule("rule-1", PlaybookRuleSection.Entry, "等待确认", "", true, 0);
        var first = new PlaybookVersion("pb:v1", "pb", accountKey, 1, "趋势策略", "XAUUSD.s", "趋势", "破位",
            [rule], now, now, true);
        var second = first with { Id = "pb:v2", Version = 2, Rules = [rule with { Description = "第二版" }], EffectiveFromUtc = now.AddDays(1), CreatedAtUtc = now.AddDays(1) };

        Assert.True((await _database.SavePlaybookVersionAsync(first)).IsSaved);
        Assert.True((await _database.SavePlaybookVersionAsync(second)).IsSaved);
        var workspace = await _database.LoadWorkspaceAsync(accountKey, DateOnly.MinValue, DateOnly.MaxValue);

        Assert.Equal(2, workspace.Playbooks.Count);
        Assert.False(workspace.Playbooks.Single(item => item.Version == 1).IsActive);
        Assert.True(workspace.Playbooks.Single(item => item.Version == 2).IsActive);
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(is_active) FROM playbook_versions WHERE account_key=$account AND playbook_id='pb';";
        command.Parameters.AddWithValue("$account", accountKey);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ImprovementGoal_ArchiveKeepsObservationsAndRejectsStaleRevision()
    {
        const string accountKey = "GoalBroker|8101";
        var now = new DateTimeOffset(2026, 9, 8, 5, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("GoalBroker", 8101), "USD", 10_000m, 10_000m, 0m, 0, now));
        var goal = new ImprovementGoal("goal-1", accountKey, "减少追单", BehaviorRuleKind.ReentryCount,
            "rule-1", new DateOnly(2026, 9, 1), null, 1m, 3m, "每日追单次数", "", true,
            ImprovementGoalStatus.Active, 0, now, now);
        var saved = await _database.SaveGoalAsync(goal, 0);
        Assert.True(saved.IsSaved);
        var observation = new GoalObservation("goal-1:2026-09-08", "goal-1", accountKey,
            new DateOnly(2026, 9, 8), 2, 1, 1, GoalObservationStatus.Failed, "命中一笔追单", now);
        await _database.SaveGoalObservationAsync(observation);
        var archived = goal with
        {
            Status = ImprovementGoalStatus.Archived,
            EndServerDate = new DateOnly(2026, 9, 8),
            NotificationEnabled = false,
            Revision = 1,
            UpdatedAtUtc = now.AddMinutes(1),
        };

        var archiveResult = await _database.SaveGoalAsync(archived, 1);
        var stale = await _database.SaveGoalAsync(goal with { UpdatedAtUtc = now.AddMinutes(2) }, 1);
        var workspace = await _database.LoadWorkspaceAsync(
            accountKey, new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8));

        Assert.True(archiveResult.IsSaved);
        Assert.Equal(ReviewSaveStatus.Conflict, stale.Status);
        Assert.Equal(ImprovementGoalStatus.Archived, Assert.Single(workspace.Goals).Status);
        Assert.Equal(observation, Assert.Single(workspace.GoalObservations));
    }

    [Fact]
    public async Task ImprovementGoal_NewVersionAtomicallyArchivesOldAndPersistsFrozenBaseline()
    {
        const string accountKey = "GoalVersionBroker|8102";
        var now = new DateTimeOffset(2026, 9, 8, 5, 30, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("GoalVersionBroker", 8102), "USD", 10_000m, 10_000m, 0m, 0, now));
        var first = new ImprovementGoal(
            "goal:v1", accountKey, "减少追单", BehaviorRuleKind.ReentryCount, "rule-1",
            new DateOnly(2026, 9, 1), null, 80m, null, "无追单执行率", "XAUUSD.s", true,
            ImprovementGoalStatus.Active, 0, now, now, GoalKey: "goal", Version: 1,
            ObservationWindowDays: 7);
        var storedFirst = (await _database.SaveGoalAsync(first, 0)).Value!;
        var archived = storedFirst with
        {
            Status = ImprovementGoalStatus.Archived,
            EndServerDate = new DateOnly(2026, 9, 7),
            NotificationEnabled = false,
            UpdatedAtUtc = now.AddMinutes(1),
        };
        var second = first with
        {
            Id = "goal:v2",
            Version = 2,
            PreviousVersionId = first.Id,
            StartServerDate = new DateOnly(2026, 9, 8),
            TargetValue = 90m,
            BaselineFromServerDate = new DateOnly(2026, 9, 1),
            BaselineToServerDate = new DateOnly(2026, 9, 7),
            BaselineOpportunityCount = 3,
            BaselinePassCount = 2,
            BaselineFailCount = 1,
            BaselineValue = 2m * 100m / 3m,
            Revision = 0,
            CreatedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(1),
        };

        var saved = await _database.SaveGoalVersionAsync(archived, storedFirst.Revision, second);
        var reopened = new AppDatabase(Path.Combine(_testDirectory, "test.db"));
        await reopened.InitializeAsync();
        var goals = (await reopened.LoadWorkspaceAsync(accountKey, DateOnly.MinValue, DateOnly.MaxValue)).Goals;

        Assert.True(saved.IsSaved);
        Assert.Equal(2, goals.Count);
        Assert.Equal(ImprovementGoalStatus.Archived, goals.Single(item => item.Id == "goal:v1").Status);
        var active = goals.Single(item => item.Id == "goal:v2");
        Assert.Equal(ImprovementGoalStatus.Active, active.Status);
        Assert.Equal(3, active.BaselineOpportunityCount);
        Assert.Equal(first.Id, active.PreviousVersionId);
    }

    [Fact]
    public async Task ServerTimeSegment_ClosesPreviousSourceAndBumpsVersionOnlyOnChange()
    {
        const string accountKey = "TimeBroker|8201";
        var now = new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("TimeBroker", 8201), "USD", 10_000m, 10_000m, 0m, 0, now));
        var inferred = new ServerTimeSegment(accountKey, "terminal-1", now, null, 3 * 60 * 60,
            ReviewTimeBasis.EstimatedBrokerServer, "worker-v1");
        await _database.SaveServerTimeSegmentAsync(inferred);
        var first = await _database.LoadWorkspaceAsync(accountKey, DateOnly.MinValue, DateOnly.MaxValue);
        await _database.SaveServerTimeSegmentAsync(inferred with { FromUtc = now.AddSeconds(1) });
        var unchanged = await _database.LoadWorkspaceAsync(accountKey, DateOnly.MinValue, DateOnly.MaxValue);
        var observed = inferred with
        {
            FromUtc = now.AddMinutes(1),
            TimeBasis = ReviewTimeBasis.BrokerServer,
            SourceVersion = "bridge-v1",
        };
        await _database.SaveServerTimeSegmentAsync(observed);
        var changed = await _database.LoadWorkspaceAsync(accountKey, DateOnly.MinValue, DateOnly.MaxValue);

        Assert.Equal(first.Version.TimeVersion, unchanged.Version.TimeVersion);
        Assert.Single(unchanged.ServerTimeSegments!);
        Assert.NotEqual(first.Version.TimeVersion, changed.Version.TimeVersion);
        Assert.Equal(2, changed.ServerTimeSegments!.Count);
        Assert.Equal(observed.FromUtc, changed.ServerTimeSegments[0].ToUtc);
        Assert.Null(changed.ServerTimeSegments[1].ToUtc);
        Assert.Equal(ReviewTimeBasis.BrokerServer, changed.ServerTimeSegments[1].TimeBasis);
    }

    [Fact]
    public async Task BehaviorReview_PreservesOriginalEvidenceAndHumanConclusionAcrossRestart()
    {
        const string accountKey = "BehaviorBroker|8301";
        var now = new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero);
        var date = new DateOnly(2026, 9, 8);
        await _database.UpsertAccountAsync(new AccountSnapshot(
            new AccountScope("BehaviorBroker", 8301), "USD", 10_000m, 10_000m, 0m, 0, now));
        var occurrence = new BehaviorOccurrence(
            "behavior-1", accountKey, date, BehaviorRuleKind.SizeEscalationAfterLoss,
            "rule-v1", 2m, 1m, 1.5m, BehaviorRiskLevel.Critical,
            ReviewEvidenceSource.LiveObservation, now.AddMinutes(-1), now, "仓位放大到基线两倍", "",
            true, "Delivered", null,
            [new BehaviorTradeLink(new TradeKey(accountKey, 8301), BehaviorTradeRole.Trigger)]);
        await _database.SaveBehaviorOccurrenceAsync(occurrence);

        var saved = await _database.SaveBehaviorReviewAsync(
            accountKey, occurrence.Id, "试图快速回本", true, 0, now.AddMinutes(2));
        var stale = await _database.SaveBehaviorReviewAsync(
            accountKey, occurrence.Id, "覆盖", false, 0, now.AddMinutes(3));
        await _database.SaveBehaviorOccurrenceAsync(occurrence with { ObservedAtUtc = now.AddMinutes(4) });

        var reopened = new AppDatabase(Path.Combine(_testDirectory, "test.db"));
        await reopened.InitializeAsync();
        var stored = Assert.Single((await reopened.LoadWorkspaceAsync(accountKey, date, date)).Behaviors);

        Assert.True(saved.IsSaved);
        Assert.Equal(ReviewSaveStatus.Conflict, stale.Status);
        Assert.Equal("试图快速回本", stored.UserExplanation);
        Assert.True(stored.EvidenceInsufficient);
        Assert.Equal(1, stored.Revision);
        Assert.Equal("rule-v1", stored.RuleVersion);
        Assert.Equal(now.AddMinutes(-1), stored.EventAtUtc);
        Assert.Equal("Delivered", stored.NotificationDisposition);
        Assert.Equal(now.AddMinutes(4), stored.ObservedAtUtc);
        Assert.Equal(BehaviorTradeRole.Trigger, Assert.Single(stored.TradeLinks).Role);
    }

    private Task SeedCompletedTradeAsync(string accountKey, long positionId, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        return _database.UpsertTradeAsync(new TradeRecord(
            accountKey, positionId, "XAUUSD.s", TradeSide.Buy,
            now.AddMinutes(-5), now, date, date,
            3500m, 3501m, 0.01m, 0.01m, 0m, 1m, true));
    }

    private static async Task<string> ReadQueryPlanAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(3));
        }
        return string.Join(Environment.NewLine, rows);
    }

    private static TradeReviewDocument ReviewDocument(string accountKey, long positionId, DateTimeOffset now) =>
        new(new TradeKey(accountKey, positionId), ReviewCompletionStatus.Draft,
            "理由", "退出", "纪律", "改进", "下次动作", "交易复盘", "平静", "趋势",
            0, "source-1", "rule-1", null, null, now, now);

    private static DailyState CreateDailyState(string accountKey, DateOnly date, decimal realizedPnl) =>
        new(accountKey, date, realizedPnl, 0m, Math.Max(realizedPnl, 0m), 0m, 1, realizedPnl > 0 ? 1 : 0,
            realizedPnl < 0 ? 1 : 0, realizedPnl < 0 ? 1 : 0, 0.01m, false, false, false);
}

public sealed class ReviewRestoreProcessTests
{
    [Fact]
    public async Task ExitAtCheckpoint()
    {
        var databasePath = Environment.GetEnvironmentVariable("TRADEPET_RESTORE_FAULT_DATABASE");
        if (databasePath is null)
        {
            return;
        }
        var attachmentRoot = Environment.GetEnvironmentVariable("TRADEPET_RESTORE_FAULT_ATTACHMENTS")!;
        var package = Environment.GetEnvironmentVariable("TRADEPET_RESTORE_FAULT_PACKAGE")!;
        var backupDirectory = Environment.GetEnvironmentVariable("TRADEPET_RESTORE_FAULT_BACKUPS")!;
        var step = Environment.GetEnvironmentVariable("TRADEPET_RESTORE_FAULT_STEP")!;
        var service = new ReviewBackupService(new AppDatabase(databasePath), attachmentRoot, databasePath);
        service.RestoreCheckpoint = observed =>
        {
            if (observed == step)
            {
                Environment.Exit(91);
            }
        };
        await service.RestoreAsync(package, backupDirectory);
        throw new InvalidOperationException($"未到达注入检查点 {step}。");
    }
}
