using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class ReviewAnalyticsTests
{
    [Fact]
    public void Summary_ComputesCoreMetricsAndRealizedDrawdown()
    {
        var first = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trades = new[]
        {
            Trade(1, 10m, first, first.AddMinutes(5)),
            Trade(2, -5m, first.AddMinutes(10), first.AddMinutes(20)),
            Trade(3, 8m, first.AddDays(1), first.AddDays(1).AddMinutes(5)),
            Trade(4, -20m, first.AddDays(1).AddMinutes(10), first.AddDays(1).AddMinutes(30)),
        };

        var result = ReviewAnalyticsCalculator.CalculateSummary(trades);

        Assert.Equal(4, result.TradeCount);
        Assert.Equal(50m, result.WinRate);
        Assert.InRange(result.ProfitFactor!.Value, 18m / 25m - 0.0001m, 18m / 25m + 0.0001m);
        Assert.Equal(-1.75m, result.Expectancy);
        Assert.Equal(9m, result.AverageWin);
        Assert.Equal(-12.5m, result.AverageLoss);
        Assert.InRange(result.AverageWinLossRatio!.Value, 0.719m, 0.721m);
        Assert.Equal(20m, result.MaximumDrawdown);
        Assert.InRange(result.MaximumDrawdownPercentage!.Value, 153.8m, 153.9m);
        Assert.Equal(12.5m, result.AverageDrawdown);
        Assert.InRange(result.RecoveryFactor!.Value, -0.351m, -0.349m);
        Assert.Equal(50m, result.DailyWinRate);
        Assert.Equal(TimeSpan.FromMinutes(10), result.AverageHoldingTime);
    }

    [Fact]
    public void Calculator_FiltersAndGroupsByReviewMetadata()
    {
        var now = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trade = Trade(1, 10m, now, now.AddMinutes(5));
        var metadata = new Dictionary<long, TradeReviewMetadata>
        {
            [1] = new("Broker|1", 1, "plan", PlanComplianceStatus.Matched, "突破", "回踩", ["早盘"], false, now),
        };

        var snapshot = new ReviewAnalyticsCalculator().Calculate(
            new ReviewFilter("Broker|1", new DateOnly(2026, 8, 30), new DateOnly(2026, 8, 30), Strategy: "突破"),
            [trade], metadata);

        Assert.Single(snapshot.Trades);
        Assert.Equal("突破", Assert.Single(snapshot.StrategyPerformance).Group);
        Assert.Equal("早盘", Assert.Single(snapshot.TagPerformance).Group);
    }

    [Fact]
    public void GroupMetrics_KeepExactTradeIdentityRawFeesAndIndependentRiskCoverage()
    {
        var now = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var first = Trade(1, 10m, now, now.AddMinutes(5));
        var second = Trade(2, -4m, now.AddMinutes(6), now.AddMinutes(9));
        var metadata = new Dictionary<long, TradeReviewMetadata>
        {
            [1] = new("Broker|1", 1, null, PlanComplianceStatus.Unclassified, "突破", "回踩", ["Early", "early"], true, now),
            [2] = new("Broker|1", 2, null, PlanComplianceStatus.Unclassified, "突破", "回踩", ["EARLY"], true, now),
        };
        var excursions = new Dictionary<long, TradeExcursion>
        {
            [1] = new("Broker|1", 1, -2m, 12m, 5m, null, 2m, now, now.AddMinutes(5), 300_000, 300_000, true, true),
        };
        var deals = new[]
        {
            new DealRecord(1, 1, 1, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out, 1m, 1m, 10m, -1m, 0m, -0.2m, now),
            new DealRecord(2, 2, 2, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out, 1m, 1m, -4m, -0.5m, 0.1m, 0m, now),
        };

        var snapshot = new ReviewAnalyticsCalculator().Calculate(
            new ReviewFilter("Broker|1", new(2026, 8, 30), new(2026, 8, 30)),
            [first, second], metadata, excursions, deals: deals);
        var tag = Assert.Single(snapshot.TagPerformance);

        Assert.Equal(2, tag.TradeCount);
        Assert.Equal([1L, 2L], tag.Trades!.Select(item => item.PositionId).Order().ToArray());
        Assert.Equal(-1.5m, tag.Commission);
        Assert.Equal(0.1m, tag.Swap);
        Assert.Equal(-0.2m, tag.OtherFees);
        Assert.Equal(1, tag.RiskCoveredCount);
        Assert.Equal(50m, tag.RiskCoveragePercentage);
        Assert.True(tag.HasSmallSampleWarning);
    }

    [Fact]
    public void PlanMatcher_PrefersNarrowestMatchingPlanCreatedBeforeTrade()
    {
        var opened = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trade = Trade(9, 1m, opened, opened.AddMinutes(1), 3352m);
        var broad = Plan("broad", 3340m, 3360m, opened.AddMinutes(-10));
        var narrow = Plan("narrow", 3351m, 3353m, opened.AddMinutes(-5));
        var future = Plan("future", 3351m, 3353m, opened.AddMinutes(1));

        Assert.Equal("narrow", new TradePlanMatcher().FindBest(trade, [broad, narrow, future])?.Id);
    }

    [Fact]
    public void PlanMatcher_UsesPlanVersionThatWasActiveWhenTradeOpened()
    {
        var opened = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trade = Trade(9, 1m, opened, opened.AddMinutes(1), 3352m);
        var activeAtOpen = Plan("then-active", 3351m, 3353m, opened.AddMinutes(-10)) with
        {
            IsActive = false,
            UpdatedAtUtc = opened.AddMinutes(5),
        };
        var stoppedBeforeOpen = Plan("already-stopped", 3351m, 3353m, opened.AddMinutes(-10)) with
        {
            IsActive = false,
            UpdatedAtUtc = opened.AddMinutes(-1),
        };

        var matched = new TradePlanMatcher().FindBest(trade, [stoppedBeforeOpen, activeAtOpen]);

        Assert.Equal("then-active", matched?.Id);
    }

    [Fact]
    public void PlanMatcher_DistinguishesOutsidePlanFromNoApplicablePlan()
    {
        var opened = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var outsideTrade = Trade(9, 1m, opened, opened.AddMinutes(1), 3370m);
        var plan = Plan("plan", 3351m, 3353m, opened.AddMinutes(-5));
        var matcher = new TradePlanMatcher();

        var outside = matcher.CreateAutomaticMetadata(outsideTrade, [plan], opened);
        var unclassified = matcher.CreateAutomaticMetadata(
            outsideTrade with { Symbol = "EURUSD" }, [plan], opened);

        Assert.Equal(PlanComplianceStatus.OutsidePlan, outside.ComplianceStatus);
        Assert.Equal("plan", outside.PlanId);
        Assert.Equal(PlanComplianceStatus.Unclassified, unclassified.ComplianceStatus);
        Assert.Null(unclassified.PlanId);
    }

    [Fact]
    public void PlanMatcher_ValidatesReferenceAndDirectionalRiskBounds()
    {
        var created = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        var validBuy = Plan("buy", 100m, 110m, created);
        var invalidBuy = validBuy with { ReferenceEntryPrice = 120m, StopPrice = 105m };
        var validSell = validBuy with
        {
            Id = "sell", Side = TradeSide.Sell, ReferenceEntryPrice = 105m,
            StopPrice = 115m, TargetPrice = 95m,
        };
        var matcher = new TradePlanMatcher();

        Assert.True(matcher.Validate(validBuy).IsValid);
        Assert.False(matcher.Validate(invalidBuy).IsValid);
        Assert.True(matcher.Validate(validSell).IsValid);
    }

    [Fact]
    public void Summary_ReportsActualRiskMultipleOnlyForReliablePricedRisk()
    {
        var opened = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trade = Trade(1, 150m, opened, opened.AddSeconds(10));
        IReadOnlyDictionary<long, TradeExcursion> excursions = new Dictionary<long, TradeExcursion>
        {
            [1] = new(
                "Broker|1", 1, -40m, 180m, 100m, 2m, 1.5m,
                opened, opened.AddSeconds(10), 10_000, 10_000, true, true,
                1_000, "position-pnl-v1"),
        };

        var summary = ReviewAnalyticsCalculator.CalculateSummary([trade], excursions: excursions);

        Assert.Equal(1.5m, summary.AverageActualRiskMultiple);
        Assert.Equal(0.4m, summary.AverageAdverseExcursionRiskMultiple);
        Assert.Equal(1.8m, summary.AverageFavorableExcursionRiskMultiple);
        Assert.Equal(100m, summary.AdvancedDataCoverage);
    }

    private static TradeRecord Trade(long id, decimal pnl, DateTimeOffset opened, DateTimeOffset closed, decimal entry = 3352m) =>
        new("Broker|1", id, "XAUUSD.s", TradeSide.Buy, opened, closed,
            DateOnly.FromDateTime(opened.UtcDateTime), DateOnly.FromDateTime(closed.UtcDateTime),
            entry, entry + 1m, 0.1m, 0.1m, 0m, pnl, true);

    private static StructuredTradePlan Plan(string id, decimal low, decimal high, DateTimeOffset created) =>
        new(id, "Broker|1", new DateOnly(2026, 8, 30), "XAUUSD.s", TradeSide.Buy,
            (low + high) / 2m, low, high, low - 2m, high + 2m, "突破", "回踩", [], "", true, created, created);
}
