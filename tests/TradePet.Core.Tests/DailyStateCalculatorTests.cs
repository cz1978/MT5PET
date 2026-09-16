using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class DailyStateCalculatorTests
{
    [Fact]
    public void Calculator_FiresGivebackOnceAfterTargetAndNewHigh()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 8, 30);
        var settings = DailyPlanSettings.BalancedDefault with { DailyTarget = 30m };
        var previous = new DailyState(account, date, 50m, 0m, 50m, 0m, 3, 3, 0, 0, 0.03m, true, false, false);
        var trades = new[] { CompleteTrade(account, date, 25m, 1) };

        var result = new DailyStateCalculator().Calculate(account, date, trades, [], settings, previous, 50m);

        Assert.Equal(50m, result.State.HighWaterPnl);
        Assert.Equal(25m, result.State.Giveback);
        Assert.True(result.State.GivebackAlerted);
        Assert.Single(result.NewFacts, fact => fact.Kind == RuleFactKind.ProfitGiveback);
    }

    [Fact]
    public void Calculator_ReplacesPoisonedStoredHighWithReliableObservation()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 9, 1);
        var previous = new DailyState(account, date, 287m, 0m, 388.35m, 101.35m,
            5, 5, 0, 0, 0.06m, false, false, false);
        var trades = new[] { CompleteTrade(account, date, 287m, 1) };

        var result = new DailyStateCalculator().Calculate(
            account, date, trades, [], DailyPlanSettings.BalancedDefault, previous, 296.60m);

        Assert.Equal(296.60m, result.State.HighWaterPnl);
        Assert.Equal(9.60m, result.State.Giveback);
    }

    [Fact]
    public void Calculator_FiresConfiguredDailyLossAndConsecutiveLosses()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 8, 30);
        var settings = DailyPlanSettings.BalancedDefault with { DailyLoss = 15m };
        var trades = new[]
        {
            CompleteTrade(account, date, -8m, 1),
            CompleteTrade(account, date, -11m, 2),
        };

        var result = new DailyStateCalculator().Calculate(account, date, trades, [], settings, null);

        Assert.Equal(-19m, result.State.RealizedPnl);
        Assert.Equal(2, result.State.ConsecutiveLosses);
        Assert.Contains(result.NewFacts, fact => fact.Kind == RuleFactKind.DailyLoss);
        Assert.Contains(result.NewFacts, fact => fact.Kind == RuleFactKind.ConsecutiveLosses);
    }

    [Fact]
    public void Calculator_ProfitBreaksEarlierLossSequenceAndExplainsCurrentStreak()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 8, 30);
        var trades = new[]
        {
            CompleteTrade(account, date, -8m, 1),
            CompleteTrade(account, date, 2m, 2),
            CompleteTrade(account, date, -3m, 3),
            CompleteTrade(account, date, -4m, 4),
        };

        var result = new DailyStateCalculator().Calculate(
            account, date, trades, [], DailyPlanSettings.BalancedDefault, null);

        Assert.Equal(2, result.State.ConsecutiveLosses);
        var fact = Assert.Single(result.NewFacts, item => item.Kind == RuleFactKind.ConsecutiveLosses);
        Assert.Equal("最近连续亏了 2 笔完整交易。", fact.Summary);
        Assert.Contains("这段合计 -7", fact.Detail);
        Assert.Contains("再前一笔为盈利", fact.Detail);
    }

    [Fact]
    public void Calculator_UsesDailyDealCashPnlForPartialCloseAndGiveback()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 9, 6);
        var openTrade = new TradeRecord(
            account, 1, "XAUUSD.s", TradeSide.Buy,
            new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero), null,
            date, null, 3350m, null, 2m, 2m, 1m, 100m, false);

        var result = new DailyStateCalculator().Calculate(
            account,
            date,
            [openTrade],
            [],
            DailyPlanSettings.BalancedDefault,
            null,
            observedHighWaterPnl: 100m,
            realizedPnlOverride: 100m);

        Assert.Equal(100m, result.State.RealizedPnl);
        Assert.Equal(0m, result.State.Giveback);
        Assert.Equal(0, result.State.TradeCount);
    }

    [Fact]
    public void Calculator_FiresConfiguredEntryAndAggregatedExposureLimitsOnce()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 9, 6);
        var settings = DailyPlanSettings.BalancedDefault with { MaximumTrades = 2, MaximumLot = 0.03m };
        var trades = new[]
        {
            OpenTrade(account, date, 1),
            OpenTrade(account, date, 2),
            OpenTrade(account, date.AddDays(-1), 3),
        };
        var captured = new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);
        var positions = new[]
        {
            Position(1, "EURUSD", TradeSide.Buy, 0.01m, captured),
            Position(2, "EURUSD", TradeSide.Buy, 0.02m, captured),
            Position(3, "EURUSD", TradeSide.Sell, 0.02m, captured),
        };

        var first = new DailyStateCalculator().Calculate(account, date, trades, positions, settings, null);
        var second = new DailyStateCalculator().Calculate(account, date, trades, positions, settings, first.State);

        Assert.Contains(first.NewFacts, fact => fact.Kind == RuleFactKind.MaximumTrades);
        Assert.Contains(first.NewFacts, fact => fact.Kind == RuleFactKind.MaximumLot);
        Assert.True(first.State.TradeLimitAlerted);
        Assert.True(first.State.LotLimitAlerted);
        Assert.DoesNotContain(second.NewFacts, fact =>
            fact.Kind is RuleFactKind.MaximumTrades or RuleFactKind.MaximumLot);
    }

    [Fact]
    public void Calculator_RecordsFirstTargetTimeAndRuleVersionOnlyWhenObservedLive()
    {
        const string account = "Broker|1";
        var date = new DateOnly(2026, 9, 6);
        var observedAt = new DateTimeOffset(2026, 9, 6, 10, 15, 0, TimeSpan.Zero);
        var settings = DailyPlanSettings.BalancedDefault with { DailyTarget = 30m };
        var trades = new[] { CompleteTrade(account, date, 35m, 1) };

        var first = new DailyStateCalculator().Calculate(
            account, date, trades, [], settings, null, observedAtUtc: observedAt);
        var second = new DailyStateCalculator().Calculate(
            account, date, trades, [], settings, first.State, observedAtUtc: observedAt.AddMinutes(5));
        var historical = new DailyStateCalculator().Calculate(
            account, date, trades, [], settings,
            first.State with { TargetReachedAtUtc = null, TargetRuleVersion = "", TargetAmountAtReach = null },
            observedAtUtc: observedAt.AddMinutes(10));

        Assert.Equal(observedAt, first.State.TargetReachedAtUtc);
        Assert.Equal("daily-target/v1:30", first.State.TargetRuleVersion);
        Assert.Equal(30m, first.State.TargetAmountAtReach);
        Assert.Equal(observedAt, second.State.TargetReachedAtUtc);
        Assert.Null(historical.State.TargetReachedAtUtc);
        Assert.Empty(historical.State.TargetRuleVersion);
    }

    private static TradeRecord CompleteTrade(string account, DateOnly date, decimal pnl, long id)
    {
        var opened = new DateTimeOffset(date.Year, date.Month, date.Day, 1, (int)id, 0, TimeSpan.Zero);
        return new TradeRecord(account, id, "XAUUSD.s", TradeSide.Buy, opened, opened.AddMinutes(1), date, date,
            3350m, 3351m, 0.01m, 0.01m, 0m, pnl, true);
    }

    private static TradeRecord OpenTrade(string account, DateOnly date, long id)
    {
        var opened = new DateTimeOffset(date.Year, date.Month, date.Day, 1, (int)id, 0, TimeSpan.Zero);
        return new TradeRecord(account, id, "EURUSD", TradeSide.Buy, opened, null, date, null,
            1.1m, null, 0.01m, 0.01m, 0.01m, 0m, false);
    }

    private static PositionSnapshot Position(
        long id,
        string symbol,
        TradeSide side,
        decimal volume,
        DateTimeOffset captured) =>
        new(id, id, symbol, side, volume, 1.1m, 1.1m, 0m, 0m, 0m, captured.AddMinutes(-5), captured);
}
