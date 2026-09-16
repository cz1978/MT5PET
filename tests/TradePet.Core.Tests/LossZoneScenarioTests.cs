using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class LossZoneScenarioTests
{
    [Fact]
    public void CoreScenario_ProducesOneFourFactAlertAndKeepsDirectionFactInTimelineInput()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var engine = new LossZoneEngine();
        var zones = new List<LossZoneState>();
        var attempts = new List<LossZoneAttempt>();
        var first = Trade(account, 1, TradeSide.Buy, 3352m, 0.02m, -8m, At(14, 31), At(14, 35), date);
        var firstClose = engine.RegisterClose(first, date, 2m, zones, attempts);
        zones.Add(Assert.IsType<LossZoneState>(firstClose.Zone));
        attempts.Add(Assert.IsType<LossZoneAttempt>(firstClose.Attempt));
        Assert.False(LossZoneEngine.ShouldNotifyLossClose(first, firstClose.Zone));

        var secondOpen = Trade(account, 2, TradeSide.Buy, 3351m, 0.02m, 0m, At(14, 36), null, date);
        var secondOpening = engine.EvaluateOpen(secondOpen, date, 2m, zones, attempts, [], first, DailyPlanSettings.BalancedDefault);
        zones[0] = Assert.IsType<LossZoneState>(secondOpening.Zone);
        attempts.Add(Assert.IsType<LossZoneAttempt>(secondOpening.Attempt));
        var second = secondOpen with { ClosedAtUtc = At(14, 40), CloseServerDate = date, NetPnl = -11m, IsComplete = true, RemainingVolume = 0m };
        var secondClose = engine.RegisterClose(second, date, 2m, zones, attempts);
        zones[0] = Assert.IsType<LossZoneState>(secondClose.Zone);
        attempts[1] = Assert.IsType<LossZoneAttempt>(secondClose.Attempt);
        Assert.True(LossZoneEngine.ShouldNotifyLossClose(second, secondClose.Zone));

        var plan = new PlanItem(
            "plan-1", account, date, "terminal/chart/zone", PlanCategory.NoTradeZone, "XAUUSD.s",
            3349m, 3355m, "不交易区", true, At(8, 0));
        var third = Trade(account, 3, TradeSide.Sell, 3352m, 0.05m, 0m, At(14, 40).AddSeconds(40), null, date);

        var result = engine.EvaluateOpen(third, date, 2m, zones, attempts, [plan], second, DailyPlanSettings.BalancedDefault);

        Assert.NotNull(result.Alert);
        Assert.Equal("又是这里。", result.Alert.Headline);
        Assert.Equal(4, result.Alert.Facts.Count);
        Assert.Equal(
            [RuleFactKind.NoTradePlan, RuleFactKind.LossZoneHistory, RuleFactKind.RapidReentry, RuleFactKind.LotEscalation],
            result.Alert.Facts.Select(fact => fact.Kind));
        Assert.Contains(result.AllFacts, fact => fact.Kind == RuleFactKind.DirectionFlip);
        Assert.Contains(result.Alert.Facts, fact => fact.Detail.Contains("2 次") && fact.Detail.Contains("-19"));
        Assert.Equal(3351.5m, zones[0].CenterPrice);
        Assert.Equal(3349.5m, zones[0].CenterPrice - zones[0].Tolerance);
        Assert.Equal(3353.5m, zones[0].CenterPrice + zones[0].Tolerance);
    }

    [Fact]
    public void WinningAttemptDoesNotClearCandidateButTwoLossesAreRequiredBeforeEntryAlert()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var engine = new LossZoneEngine();
        var zones = new List<LossZoneState>();
        var attempts = new List<LossZoneAttempt>();
        var firstLoss = Trade(account, 1, TradeSide.Buy, 3352m, 0.02m, -8m, At(10, 0), At(10, 1), date);
        var firstClose = engine.RegisterClose(firstLoss, date, 2m, zones, attempts);
        zones.Add(Assert.IsType<LossZoneState>(firstClose.Zone));
        attempts.Add(Assert.IsType<LossZoneAttempt>(firstClose.Attempt));

        var winningOpen = Trade(account, 2, TradeSide.Buy, 3351m, 0.02m, 0m, At(10, 5), null, date);
        var winningEntry = engine.EvaluateOpen(
            winningOpen, date, 2m, zones, attempts, [], firstLoss, DailyPlanSettings.BalancedDefault);
        Assert.Null(winningEntry.Alert);
        Assert.DoesNotContain(winningEntry.AllFacts, fact => fact.Kind == RuleFactKind.LossZoneHistory);
        zones[0] = Assert.IsType<LossZoneState>(winningEntry.Zone);
        attempts.Add(Assert.IsType<LossZoneAttempt>(winningEntry.Attempt));
        var winner = winningOpen with
        {
            ClosedAtUtc = At(10, 6),
            CloseServerDate = date,
            NetPnl = 3m,
            IsComplete = true,
            RemainingVolume = 0m,
        };
        var winningClose = engine.RegisterClose(winner, date, 2m, zones, attempts);
        zones[0] = Assert.IsType<LossZoneState>(winningClose.Zone);
        attempts[1] = Assert.IsType<LossZoneAttempt>(winningClose.Attempt);

        var secondLossOpen = Trade(account, 3, TradeSide.Sell, 3353m, 0.02m, 0m, At(10, 10), null, date);
        var secondEntry = engine.EvaluateOpen(
            secondLossOpen, date, 2m, zones, attempts, [], winner, DailyPlanSettings.BalancedDefault);
        Assert.DoesNotContain(secondEntry.AllFacts, fact => fact.Kind == RuleFactKind.LossZoneHistory);
        zones[0] = Assert.IsType<LossZoneState>(secondEntry.Zone);
        attempts.Add(Assert.IsType<LossZoneAttempt>(secondEntry.Attempt));
        var secondLoss = secondLossOpen with
        {
            ClosedAtUtc = At(10, 11),
            CloseServerDate = date,
            NetPnl = -5m,
            IsComplete = true,
            RemainingVolume = 0m,
        };
        var secondClose = engine.RegisterClose(secondLoss, date, 2m, zones, attempts);
        zones[0] = Assert.IsType<LossZoneState>(secondClose.Zone);
        attempts[2] = Assert.IsType<LossZoneAttempt>(secondClose.Attempt);

        var nextOpen = Trade(account, 4, TradeSide.Buy, 3352m, 0.02m, 0m, At(10, 15), null, date);
        var result = engine.EvaluateOpen(
            nextOpen, date, 2m, zones, attempts, [], secondLoss, DailyPlanSettings.BalancedDefault);

        var zoneFact = Assert.Single(result.AllFacts, fact => fact.Kind == RuleFactKind.LossZoneHistory);
        Assert.NotNull(result.Alert);
        Assert.Equal("又是这里。", result.Alert.Headline);
        Assert.Contains("XAUUSD.s 3350.5—3354.5", zoneFact.Detail);
        Assert.Contains("第 4 次进入", zoneFact.Detail);
        Assert.Contains("此前亏 2 次", zoneFact.Detail);
        Assert.Equal(2, result.Zone?.LossCount);
    }

    [Fact]
    public void WinningAttemptBetweenLossesDoesNotResetRepeatedLossNotification()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var engine = new LossZoneEngine();
        var firstLoss = Trade(account, 1, TradeSide.Buy, 3352m, 0.02m, -8m, At(10, 0), At(10, 1), date);
        var firstClose = engine.RegisterClose(firstLoss, date, 2m, [], []);
        var zone = Assert.IsType<LossZoneState>(firstClose.Zone);
        var attempts = new List<LossZoneAttempt> { Assert.IsType<LossZoneAttempt>(firstClose.Attempt) };

        var winner = Trade(account, 2, TradeSide.Buy, 3351m, 0.02m, 3m, At(10, 2), At(10, 3), date);
        var winnerOpen = engine.EvaluateOpen(winner, date, 2m, [zone], attempts, [], firstLoss, DailyPlanSettings.BalancedDefault);
        attempts.Add(Assert.IsType<LossZoneAttempt>(winnerOpen.Attempt));
        var winnerClose = engine.RegisterClose(winner, date, 2m, [zone], attempts);
        zone = Assert.IsType<LossZoneState>(winnerClose.Zone);
        attempts[1] = Assert.IsType<LossZoneAttempt>(winnerClose.Attempt);

        var secondLoss = Trade(account, 3, TradeSide.Sell, 3353m, 0.02m, -5m, At(10, 4), At(10, 5), date);
        var secondOpen = engine.EvaluateOpen(secondLoss, date, 2m, [zone], attempts, [], winner, DailyPlanSettings.BalancedDefault);
        attempts.Add(Assert.IsType<LossZoneAttempt>(secondOpen.Attempt));
        var secondClose = engine.RegisterClose(secondLoss, date, 2m, [zone], attempts);

        Assert.Equal(2, secondClose.Zone?.LossCount);
        Assert.True(LossZoneEngine.ShouldNotifyLossClose(secondLoss, secondClose.Zone));
    }

    [Fact]
    public void Reconcile_RebuildsZoneSummaryFromWinningLosingAndOpenAttempts()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var zone = new LossZoneState(
            "zone", account, date, "XAUUSD.s", 4431m, 2m,
            3, 1, -1m, At(9, 0));
        var attempts = new[]
        {
            Attempt(1, TradeSide.Buy, 4431.5m, 0.02m, -3.24m, At(5, 20), At(5, 21)),
            Attempt(2, TradeSide.Sell, 4433.5m, 0.01m, -1.65m, At(6, 4), At(6, 17)),
            Attempt(3, TradeSide.Sell, 4431.38m, 0.01m, 4.52m, At(7, 30), At(8, 0)),
            Attempt(4, TradeSide.Buy, 4431.6m, 0.02m, 2.92m, At(8, 2), At(8, 37)),
            Attempt(5, TradeSide.Sell, 4431.37m, 0.01m, null, At(8, 39), null),
        };

        var reconciled = new LossZoneEngine().Reconcile(zone, attempts);

        Assert.Equal(5, reconciled.AttemptCount);
        Assert.Equal(2, reconciled.LossCount);
        Assert.Equal(-4.89m, reconciled.CumulativeLoss);
        Assert.Equal(4432.5m, reconciled.CenterPrice);
        Assert.Equal(At(8, 39), reconciled.LastAttemptAtUtc);
    }

    [Fact]
    public void Rebuild_DoesNotRetroactivelyCountATradeOpenedBeforeTheZoneExisted()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var trades = new[]
        {
            Trade(account, 1, TradeSide.Buy, 3352m, 0.01m, -3m, At(10, 0), At(10, 10), date),
            Trade(account, 2, TradeSide.Buy, 3351m, 0.01m, -4m, At(10, 5), At(10, 15), date),
        };

        var projection = new LossZoneEngine().Rebuild(account, date, 2m, trades);

        Assert.Equal(2, projection.Zones.Count);
        Assert.Equal(2, projection.Attempts.Count);
        Assert.All(projection.Zones, zone => Assert.Equal(1, zone.AttemptCount));
        Assert.Equal(2, projection.Attempts.Select(attempt => attempt.PositionId).Distinct().Count());
    }

    [Fact]
    public void Rebuild_AssignsEveryPositionOnceAndKeepsWinsBetweenLosses()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var trades = new[]
        {
            Trade(account, 1, TradeSide.Buy, 3352m, 0.01m, -3m, At(10, 0), At(10, 1), date),
            Trade(account, 2, TradeSide.Sell, 3351m, 0.01m, 5m, At(10, 2), At(10, 3), date),
            Trade(account, 3, TradeSide.Buy, 3353m, 0.02m, -7m, At(10, 4), At(10, 5), date),
            Trade(account, 4, TradeSide.Sell, 3351.5m, 0.01m, 0m, At(10, 6), null, date),
        };

        var projection = new LossZoneEngine().Rebuild(account, date, 2m, trades);

        var zone = Assert.Single(projection.Zones);
        Assert.Equal(4, zone.AttemptCount);
        Assert.Equal(2, zone.LossCount);
        Assert.Equal(-10m, zone.CumulativeLoss);
        Assert.Equal(3352.5m, zone.CenterPrice);
        Assert.Equal(4, projection.Attempts.Count);
        Assert.Equal(4, projection.Attempts.Select(attempt => attempt.PositionId).Distinct().Count());
        Assert.Equal(5m, projection.Attempts.Single(attempt => attempt.PositionId == 2).NetPnl);
        Assert.Null(projection.Attempts.Single(attempt => attempt.PositionId == 4).NetPnl);
    }

    [Fact]
    public void Rebuild_UsesStableTieBreakWhenTwoZonesAreEquallyNear()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var trades = new[]
        {
            Trade(account, 1, TradeSide.Buy, 3350m, 0.01m, -3m, At(10, 0), At(10, 1), date),
            Trade(account, 2, TradeSide.Buy, 3354m, 0.01m, -4m, At(10, 0), At(10, 2), date),
            Trade(account, 3, TradeSide.Sell, 3352m, 0.01m, 2m, At(10, 3), At(10, 4), date),
        };

        var projection = new LossZoneEngine().Rebuild(account, date, 2m, trades);

        var assigned = projection.Attempts.Single(attempt => attempt.PositionId == 3);
        var mostRecentZone = projection.Zones.Single(zone => zone.CenterPrice == 3354m);
        Assert.Equal(mostRecentZone.Id, assigned.ZoneId);
    }

    [Fact]
    public void EvaluateOpen_KeepsAnExistingPositionInItsOriginalZone()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var firstZone = new LossZoneState("first", account, date, "XAUUSD.s", 3350m, 2m, 2, 1, -3m, At(10, 0));
        var nearerZone = new LossZoneState("nearer", account, date, "XAUUSD.s", 3351m, 2m, 1, 1, -2m, At(10, 1));
        var existing = new LossZoneAttempt("first:7", "first", 7, TradeSide.Buy, 3351m, 0.01m, null, At(10, 2), null);
        var trade = Trade(account, 7, TradeSide.Buy, 3351m, 0.01m, 0m, At(10, 2), null, date);

        var result = new LossZoneEngine().EvaluateOpen(
            trade, date, 2m, [firstZone, nearerZone], [existing], [], null, DailyPlanSettings.BalancedDefault);

        Assert.Equal("first", result.Zone?.Id);
        Assert.Equal("first", result.Attempt?.ZoneId);
        Assert.Equal(2, result.Zone?.AttemptCount);
    }

    [Fact]
    public void RegisterClose_DoesNotAttachAnUntrackedTradeToAZoneAfterTheFact()
    {
        const string account = "WeTrade|1";
        var date = new DateOnly(2026, 8, 30);
        var existingZone = new LossZoneState("existing", account, date, "XAUUSD.s", 3352m, 2m, 1, 1, -3m, At(10, 10));
        var trade = Trade(account, 8, TradeSide.Buy, 3351m, 0.01m, -4m, At(10, 5), At(10, 15), date);

        var result = new LossZoneEngine().RegisterClose(trade, date, 2m, [existingZone], []);

        Assert.NotNull(result.Zone);
        Assert.NotEqual("existing", result.Zone!.Id);
        Assert.Equal(result.Zone.Id, result.Attempt?.ZoneId);
        Assert.Equal(1, result.Zone.AttemptCount);
    }

    private static TradeRecord Trade(
        string account,
        long positionId,
        TradeSide side,
        decimal entry,
        decimal volume,
        decimal pnl,
        DateTimeOffset opened,
        DateTimeOffset? closed,
        DateOnly date) =>
        new(account, positionId, "XAUUSD.s", side, opened, closed, date, closed is null ? null : date,
            entry, closed is null ? null : entry, volume, volume, closed is null ? volume : 0m, pnl, closed is not null);

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 8, 30, hour, minute, 0, TimeSpan.Zero);

    private static LossZoneAttempt Attempt(
        long positionId,
        TradeSide side,
        decimal entry,
        decimal volume,
        decimal? pnl,
        DateTimeOffset opened,
        DateTimeOffset? closed) =>
        new($"zone:{positionId}", "zone", positionId, side, entry, volume, pnl, opened, closed);
}
