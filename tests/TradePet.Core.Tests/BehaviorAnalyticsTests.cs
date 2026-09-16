using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class BehaviorAnalyticsTests
{
    [Fact]
    public void Evaluator_ComputesRevengeAndCooldownAfterConsecutiveLosses()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trades = new[]
        {
            Trade(1, -8m, 0.05m, start, start.AddMinutes(1)),
            Trade(2, -11m, 0.05m, start.AddMinutes(2), start.AddMinutes(3)),
            Trade(3, 0m, 0.1m, start.AddMinutes(3).AddSeconds(20), start.AddMinutes(4)),
        };
        var summary = new BehaviorAnalyticsCalculator().Evaluate(
            "Broker|1", new DateOnly(2026, 8, 30), trades, [], [], null,
            BehaviorPolicy.Balanced, observedAtUtc: start.AddMinutes(5));

        var revenge = Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.RevengeScore);
        var cooldown = Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.CooldownViolation);
        Assert.True(revenge.Value > 60m);
        Assert.True(revenge.Triggered);
        Assert.Equal(1m, cooldown.Value);
        Assert.True(cooldown.Triggered);
        Assert.Equal(BehaviorRiskLevel.Critical, summary.RiskLevel);
    }

    [Fact]
    public void Presets_ExposeBalancedDefaultsAndPlanDeviationUsesClassifiedTrades()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trades = new[]
        {
            Trade(1, 1m, 0.01m, start, start.AddMinutes(1)),
            Trade(2, -1m, 0.01m, start.AddMinutes(2), start.AddMinutes(3)),
        };
        var metadata = new Dictionary<long, TradeReviewMetadata>
        {
            [1] = new("Broker|1", 1, "p", PlanComplianceStatus.Matched, "", "", [], false, start),
            [2] = new("Broker|1", 2, null, PlanComplianceStatus.OutsidePlan, "", "", [], false, start),
        };
        var summary = new BehaviorAnalyticsCalculator().Evaluate(
            "Broker|1", new DateOnly(2026, 8, 30), trades, [], [], metadata,
            BehaviorPolicy.Balanced, observedAtUtc: start.AddMinutes(5));
        var deviation = Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.PlanDeviationRate);

        Assert.Equal(60, BehaviorPolicy.Balanced.RapidReentrySeconds);
        Assert.Equal(2m, BehaviorPolicy.Balanced.LotEscalationMultiplier);
        Assert.Equal(50m, deviation.Value);
        Assert.True(deviation.Triggered);
    }

    [Fact]
    public void RuleThresholds_CanBeTunedIndependently()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var zone = new LossZoneState("z", "Broker|1", new DateOnly(2026, 8, 30), "XAUUSD.s", 3352m, 2m, 3, 2, -19m, start);
        var policy = BehaviorPolicy.Balanced with
        {
            LossZonePersistenceThreshold = 2,
            ProfitGivebackThreshold = 10,
        };
        var attempts = Enumerable.Range(1, 3)
            .Select(index => Attempt("z", index, start.AddMinutes(index)))
            .ToArray();

        var summary = new BehaviorAnalyticsCalculator().Evaluate(
            "Broker|1", new DateOnly(2026, 8, 30), [], [zone], attempts, null, policy,
            new DailyState("Broker|1", new DateOnly(2026, 8, 30), 20m, 0m, 100m, 20m, 0, 0, 0, 0, 0m, false, false, false),
            start.AddMinutes(5));

        Assert.True(Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.LossZonePersistence).Triggered);
        Assert.True(Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.ProfitGiveback).Triggered);
    }

    [Fact]
    public void DisabledBaselineAndHardLimits_DoNotTriggerRules()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var policy = BehaviorPolicy.Balanced with { BaselineEnabled = false, HardLimitEnabled = false };
        var trade = Trade(1, -10m, 0.1m, start, start.AddMinutes(1));

        var summary = new BehaviorAnalyticsCalculator().Evaluate(
            "Broker|1", new DateOnly(2026, 8, 30), [trade, trade with { PositionId = 2, OpenedAtUtc = start.AddMinutes(2), ClosedAtUtc = start.AddMinutes(3) }],
            [], [], null, policy, observedAtUtc: start.AddMinutes(5));

        Assert.DoesNotContain(summary.Evaluations, item => item.Triggered);
    }

    [Fact]
    public void ZonePersistence_IsolatedByAccountAndServerDate()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var matching = new LossZoneState("match", "Broker|1", new DateOnly(2026, 8, 30), "XAUUSD.s", 3352m, 2m, 4, 1, -5m, start);
        var otherAccount = matching with { Id = "other-account", AccountKey = "Broker|2" };
        var otherDate = matching with { Id = "other-date", ServerDate = new DateOnly(2026, 8, 29) };
        var attempts = Enumerable.Range(1, 4)
            .Select(index => Attempt("match", index, start.AddMinutes(index)))
            .Concat([Attempt("other-account", 10, start), Attempt("other-date", 11, start)])
            .ToArray();

        var summary = new BehaviorAnalyticsCalculator().Evaluate(
            "Broker|1", new DateOnly(2026, 8, 30), [], [matching, otherAccount, otherDate], attempts, null,
            BehaviorPolicy.Balanced, observedAtUtc: start);

        Assert.Equal(3m, Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.LossZonePersistence).Value);
    }

    [Fact]
    public void PeriodEvaluation_AggregatesEverySelectedTradingDayInsteadOfUsingTodayOnly()
    {
        var firstDate = new DateOnly(2026, 8, 30);
        var secondDate = firstDate.AddDays(1);
        var first = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trades = Enumerable.Range(1, 4)
            .Select(index => Trade(index, index == 4 ? -1m : 1m, 0.01m,
                first.AddMinutes(index * 2), first.AddMinutes(index * 2 + 1)))
            .Concat(Enumerable.Range(5, 2).Select(index =>
                Trade(index, 1m, 0.01m, first.AddDays(1).AddMinutes(index * 2), first.AddDays(1).AddMinutes(index * 2 + 1)) with
                {
                    OpenServerDate = secondDate,
                    CloseServerDate = secondDate,
                }))
            .ToArray();
        var filter = new ReviewFilter("Broker|1", firstDate, secondDate);

        var summary = new BehaviorAnalyticsCalculator().EvaluatePeriod(
            filter, trades, [], [], null, BehaviorPolicy.Balanced, observedAtUtc: first.AddDays(2));

        var reentry = Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.ReentryCount);
        Assert.Equal(3m, reentry.Value);
        Assert.Contains("周期累计 4", reentry.Summary);
        Assert.Contains("2026-08-30", reentry.Summary);
        Assert.Equal(2, summary.BaselineDayCount);
    }

    [Fact]
    public void LiveOpen_DoesNotReplayHistoricalBehaviorPeaks()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            Trade(1, -8m, 0.01m, start, start.AddMinutes(1)),
            Trade(2, -11m, 0.01m, start.AddMinutes(2), start.AddMinutes(3)),
            Trade(3, 2m, 0.04m, start.AddMinutes(3).AddSeconds(20), start.AddMinutes(4)),
            Trade(4, -2m, 0.02m, start.AddMinutes(5), start.AddMinutes(6)),
        };
        var opening = OpenTrade(5, 0.02m, 3400m, start.AddMinutes(8));

        var summary = new BehaviorAnalyticsCalculator().EvaluateOpen(
            "Broker|1", new DateOnly(2026, 8, 30), opening, history, BehaviorPolicy.Balanced,
            start.AddMinutes(8));

        Assert.DoesNotContain(summary.Evaluations, item => item.Triggered);
        Assert.Equal(0m, Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.RevengeScore).Value);
        Assert.Equal(1m, Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.SizeEscalationAfterLoss).Value);
        Assert.False(Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.CooldownViolation).Triggered);
        Assert.DoesNotContain(summary.Evaluations, item => item.Rule == BehaviorRuleKind.LossZonePersistence);
    }

    [Fact]
    public void LiveOpen_UsesCurrentLossZoneAttemptCountForPersistence()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var opening = OpenTrade(5, 0.02m, 3352m, start.AddMinutes(8));

        var summary = new BehaviorAnalyticsCalculator().EvaluateOpen(
            "Broker|1", new DateOnly(2026, 8, 30), opening, [],
            BehaviorPolicy.Balanced with { LossZonePersistenceThreshold = 3 },
            opening.OpenedAtUtc,
            matchingLossZoneAttemptCount: 4);

        var persistence = Assert.Single(summary.Evaluations,
            item => item.Rule == BehaviorRuleKind.LossZonePersistence);
        Assert.Equal(3m, persistence.Value);
        Assert.True(persistence.Triggered);
        Assert.DoesNotContain(summary.Evaluations, item => item.Rule == BehaviorRuleKind.ReentryCount);
    }

    [Fact]
    public void LiveOpen_CooldownUsesLatestConsecutiveLossesAndReportsRemainingSeconds()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            Trade(1, -8m, 0.05m, start, start.AddMinutes(1)),
            Trade(2, -11m, 0.05m, start.AddMinutes(2), start.AddMinutes(3)),
        };
        var opening = OpenTrade(3, 0.05m, 3400m, start.AddMinutes(3).AddSeconds(20));

        var summary = new BehaviorAnalyticsCalculator().EvaluateOpen(
            "Broker|1", new DateOnly(2026, 8, 30), opening, history, BehaviorPolicy.Balanced,
            opening.OpenedAtUtc);
        var cooldown = Assert.Single(summary.Evaluations, item => item.Rule == BehaviorRuleKind.CooldownViolation);

        Assert.True(cooldown.Triggered);
        Assert.Equal(20m, cooldown.Value);
        Assert.Equal(60m, cooldown.Threshold);
        Assert.Contains("连续亏损 2 笔", cooldown.Summary);
        Assert.Contains("还剩 40 秒", cooldown.Summary);
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void LiveOpen_CooldownHonorsSixtySecondBoundary(int elapsedSeconds, bool expected)
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            Trade(1, -1m, 0.01m, start, start.AddMinutes(1)),
            Trade(2, -1m, 0.01m, start.AddMinutes(2), start.AddMinutes(3)),
        };
        var opening = OpenTrade(3, 0.01m, 3400m, start.AddMinutes(3).AddSeconds(elapsedSeconds));

        var summary = new BehaviorAnalyticsCalculator().EvaluateOpen(
            "Broker|1", new DateOnly(2026, 8, 30), opening, history, BehaviorPolicy.Balanced,
            opening.OpenedAtUtc);

        Assert.Equal(expected, Assert.Single(summary.Evaluations,
            item => item.Rule == BehaviorRuleKind.CooldownViolation).Triggered);
    }

    [Fact]
    public void LiveOpen_ProfitClosedLastBreaksLossStreakByCloseTime()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            Trade(1, -2m, 0.01m, start, start.AddMinutes(2)),
            Trade(2, -3m, 0.01m, start.AddSeconds(30), start.AddMinutes(3)),
            Trade(3, 1m, 0.01m, start.AddMinutes(1), start.AddMinutes(3).AddSeconds(10)),
        };
        var opening = OpenTrade(4, 0.01m, 3400m, start.AddMinutes(3).AddSeconds(20));

        var summary = new BehaviorAnalyticsCalculator().EvaluateOpen(
            "Broker|1", new DateOnly(2026, 8, 30), opening, history, BehaviorPolicy.Balanced,
            opening.OpenedAtUtc);

        Assert.False(Assert.Single(summary.Evaluations,
            item => item.Rule == BehaviorRuleKind.CooldownViolation).Triggered);
    }

    [Fact]
    public void PriceFixation_UsesSymbolPointInsteadOfOneDecimalPriceRounding()
    {
        var date = new DateOnly(2026, 8, 30);
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var trades = new[]
        {
            EurTrade(1, 1.06m, start),
            EurTrade(2, 1.08m, start.AddMinutes(1)),
            EurTrade(3, 1.10m, start.AddMinutes(2)),
        };
        IReadOnlyDictionary<string, SymbolSpecification> specifications =
            new Dictionary<string, SymbolSpecification>(StringComparer.OrdinalIgnoreCase)
            {
                ["EURUSD"] = new("EURUSD", 0.00001m, 0.00001m, 5),
            };

        var summary = new BehaviorAnalyticsCalculator().Evaluate(
            "Broker|1",
            date,
            trades,
            [],
            [],
            null,
            BehaviorPolicy.Balanced,
            observedAtUtc: start.AddMinutes(3),
            symbolSpecifications: specifications);

        var fixation = Assert.Single(summary.Evaluations,
            item => item.Rule == BehaviorRuleKind.PriceFixationScore);
        Assert.Equal(0m, fixation.Value);
        Assert.False(fixation.Triggered);
    }

    private static TradeRecord Trade(long id, decimal pnl, decimal volume, DateTimeOffset opened, DateTimeOffset closed) =>
        new("Broker|1", id, "XAUUSD.s", TradeSide.Buy, opened, closed,
            new DateOnly(2026, 8, 30), new DateOnly(2026, 8, 30), 3352m, 3352m,
            volume, volume, 0m, pnl, true);

    private static TradeRecord OpenTrade(long id, decimal volume, decimal price, DateTimeOffset opened) =>
        new("Broker|1", id, "XAUUSD.s", TradeSide.Buy, opened, null,
            new DateOnly(2026, 8, 30), null, price, null,
            volume, volume, volume, 0m, false);

    private static TradeRecord EurTrade(long id, decimal price, DateTimeOffset opened) =>
        new("Broker|1", id, "EURUSD", TradeSide.Buy, opened, opened.AddSeconds(30),
            new DateOnly(2026, 8, 30), new DateOnly(2026, 8, 30), price, price,
            0.01m, 0.01m, 0m, -1m, true);

    private static LossZoneAttempt Attempt(string zoneId, long positionId, DateTimeOffset opened) =>
        new($"{zoneId}:{positionId}", zoneId, positionId, TradeSide.Buy, 3352m, 0.01m, -1m, opened, opened.AddMinutes(1));
}
