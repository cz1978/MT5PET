using TradePet.Core.Domain;
using TradePet.Core.Review;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class ReviewWorkspaceCalculatorTests
{
    private readonly ReviewWorkspaceCalculator _calculator = new();
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FilterAndSort_SupportsTagBooleanSearchAndReviewStatus()
    {
        var trades = new[] { Trade(1, "XAUUSD.s", 10m), Trade(2, "EURUSD", -5m) };
        var metadata = new Dictionary<long, TradeReviewMetadata>
        {
            [1] = Meta(1, "突破", "回踩", ["早盘", "顺势"]),
            [2] = Meta(2, "突破", "假突破", ["早盘"]),
        };
        var documents = new Dictionary<long, TradeReviewDocument>
        {
            [1] = Document(1, ReviewCompletionStatus.Reviewed, "等待回踩确认"),
        };
        var filter = Filter() with
        {
            Strategy = "突破", Tags = ["早盘", "顺势"], TagMatchMode = ReviewTagMatchMode.All,
            Status = ReviewCompletionStatus.Reviewed, Assessment = RuleAssessmentStatus.Passed, Search = "回踩确认",
        };

        var result = _calculator.FilterAndSort(filter, trades, metadata, documents,
            [Assessment("entry", RuleAssessmentStatus.Passed)], [], []);

        Assert.Equal(1, Assert.Single(result).PositionId);
    }

    [Fact]
    public void FilterAndSort_NormalizesDuplicateTagsSearchesTicketAndSortsByMeasuredGiveback()
    {
        var trades = new[] { Trade(1, "XAUUSD.s", 20m), Trade(2, "XAUUSD.s", -5m) };
        var metadata = new Dictionary<long, TradeReviewMetadata>
        {
            [1] = Meta(1, "突破", "回踩", ["Early", "early"]),
            [2] = Meta(2, "突破", "回踩", ["EARLY"]),
        };
        var deals = new[]
        {
            Deal(800001, 1, DealEntryKind.Out, 20m, Start),
            Deal(900002, 2, DealEntryKind.Out, -5m, Start),
        };
        var excursions = new Dictionary<long, TradeExcursion>
        {
            [1] = new("Broker|1", 1, -1m, 25m, 10m, null, 2m, Start, Start, 1, 1, true, true),
            [2] = new("Broker|1", 2, -8m, 40m, 10m, null, -0.5m, Start, Start, 1, 1, true, true),
        };
        var filter = Filter() with
        {
            Tags = [" early ", "EARLY"], TagMatchMode = ReviewTagMatchMode.All,
            Search = "900002", Sort = ReviewSortOrder.LargestGiveback,
        };

        var ticketResult = _calculator.FilterAndSort(filter, trades, metadata, new Dictionary<long, TradeReviewDocument>(),
            [], [], [], deals, excursions);
        var sorted = _calculator.FilterAndSort(filter with { Search = null }, trades, metadata,
            new Dictionary<long, TradeReviewDocument>(), [], [], [], deals, excursions);

        Assert.Equal(2, Assert.Single(ticketResult).PositionId);
        Assert.Equal([2L, 1L], sorted.Select(item => item.PositionId).ToArray());
    }

    [Fact]
    public void Calendar_UsesDealCashDateWhilePerformanceUsesFinalCloseDate()
    {
        var trade = Trade(1, "XAUUSD.s", 100m, Start.AddDays(1));
        var deals = new[]
        {
            Deal(10, 1, DealEntryKind.Out, 40m, Start.AddHours(2)),
            Deal(11, 1, DealEntryKind.Out, 60m, Start.AddDays(1).AddHours(2)),
        };

        var days = _calculator.BuildCalendar("Broker|1", new(2026, 9, 1), new(2026, 9, 2),
            [trade], deals, new Dictionary<long, TradeReviewDocument>(),
            new Dictionary<DateOnly, DailyJournal>(), [], 0);

        Assert.Equal(40m, days[0].RealizedCashPnl);
        Assert.Equal(60m, days[1].RealizedCashPnl);
        Assert.Equal(0, days[0].CompleteTradeCount);
        Assert.Equal(1, days[1].CompleteTradeCount);
    }

    [Fact]
    public void DailyFacts_SeparatesPostTargetNewTradesFromPostTargetCashDeals()
    {
        var date = new DateOnly(2026, 9, 6);
        var targetAt = new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
        var beforeTarget = new TradeRecord("Broker|1", 1, "XAUUSD.s", TradeSide.Buy,
            targetAt.AddHours(-2), targetAt.AddHours(1), date, date,
            100m, 101m, 1m, 1m, 0m, 50m, true);
        var afterTargetComplete = new TradeRecord("Broker|1", 2, "XAUUSD.s", TradeSide.Buy,
            targetAt.AddMinutes(30), targetAt.AddDays(1), date, date.AddDays(1),
            101m, 100m, 1m, 1m, 0m, -20m, true);
        var afterTargetOpen = new TradeRecord("Broker|1", 3, "EURUSD", TradeSide.Sell,
            targetAt.AddHours(1), null, date, null,
            1.1m, null, 1m, 1m, 1m, 0m, false);
        var deals = new[]
        {
            new DealRecord(10, 10, 1, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out,
                0.5m, 101m, 10m, 0m, 0m, 0m, targetAt.AddMinutes(-1)),
            new DealRecord(11, 11, 1, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out,
                0.5m, 101m, 40m, -2m, 0m, 0m, targetAt.AddMinutes(15)),
            new DealRecord(12, 12, 2, "XAUUSD.s", TradeSide.Buy, DealEntryKind.In,
                1m, 101m, 0m, -1m, 0m, 0m, targetAt.AddMinutes(30)),
        };
        var reviewed = Document(1, ReviewCompletionStatus.Reviewed, "已复盘");
        var state = new DailyState("Broker|1", date, 47m, 0m, 50m, 3m,
            1, 1, 0, 0, 1m, true, false, false,
            TargetReachedAtUtc: targetAt, TargetRuleVersion: "daily-target/v1:30", TargetAmountAtReach: 30m,
            ConsecutiveLossThresholdAtObservation: 2);

        var result = _calculator.BuildDailyFacts("Broker|1", date, date,
            [beforeTarget, afterTargetComplete, afterTargetOpen], deals,
            new Dictionary<long, TradeReviewDocument> { [1] = reviewed }, [],
            new Dictionary<DateOnly, DailyState> { [date] = state }, 0)[date];

        Assert.Equal(47m, result.RealizedCashPnl);
        Assert.Equal(-3m, result.Fees);
        Assert.Equal(50m, result.CompleteTradeNetPnl);
        Assert.Equal(-20m, result.AfterTargetNewTradeNetPnl);
        Assert.Equal(1, result.AfterTargetNewCompleteTradeCount);
        Assert.Equal(1, result.AfterTargetNewOpenTradeCount);
        Assert.Equal(37m, result.AfterTargetDealCashPnl);
        Assert.Equal([11L, 12L], result.AfterTargetDealTickets);
        Assert.Equal([2L, 3L], result.AfterTargetTradeKeys.Select(item => item.PositionId).ToArray());
        Assert.Equal(100m, result.ReviewCompletionPercentage);
    }

    [Fact]
    public void DailyFacts_DoesNotInferTargetTimeFromFinalAlertState()
    {
        var date = new DateOnly(2026, 9, 6);
        var state = new DailyState("Broker|1", date, 50m, 0m, 50m, 0m,
            1, 1, 0, 0, 1m, true, false, false);

        var result = _calculator.BuildDailyFacts("Broker|1", date, date,
            [Trade(1, "XAUUSD.s", 50m)], [], new Dictionary<long, TradeReviewDocument>(), [],
            new Dictionary<DateOnly, DailyState> { [date] = state }, 0)[date];

        Assert.False(result.HasReliableTargetMilestone);
        Assert.Null(result.TargetReachedAtUtc);
        Assert.Null(result.AfterTargetNewTradeNetPnl);
        Assert.Null(result.AfterTargetDealCashPnl);
    }

    [Fact]
    public void Analysis_SeparatesRawFeesUnallocatedCostsDailyCashAndMissingRiskPoints()
    {
        var date = new DateOnly(2026, 9, 1);
        var selected = Trade(1, "XAUUSD.s", 10m);
        var deals = new[]
        {
            new DealRecord(1, 1, 1, "XAUUSD.s", TradeSide.Buy, DealEntryKind.Out,
                1m, 100m, 10m, -1m, 0.5m, -0.25m, Start),
            new DealRecord(2, 2, 999, "", TradeSide.Buy, DealEntryKind.Out,
                0m, 0m, 0m, -2m, 0m, 0m, Start),
        };

        var fees = _calculator.CalculateFees("Broker|1", date, date, [selected], [selected], deals, 0);
        var cash = _calculator.BuildDailyCashSeries("Broker|1", date, date, [selected], deals, 0);
        var risks = _calculator.BuildRiskSamples([selected], new Dictionary<long, TradeExcursion>());

        Assert.Equal(-1m, fees.Commission);
        Assert.Equal(0.5m, fees.Swap);
        Assert.Equal(-0.25m, fees.OtherFees);
        Assert.Equal(-2m, fees.UnallocatedFees);
        Assert.Equal(-2.75m, fees.GrandTotal);
        Assert.Equal(9.25m, Assert.Single(cash).CashPnl);
        var risk = Assert.Single(risks);
        Assert.False(risk.HasReliableExcursion);
        Assert.Null(risk.Mae);
        Assert.Null(risk.Mfe);
        Assert.Null(risk.ActualRiskMultiple);
    }

    [Fact]
    public void Comparison_ReturnsExactIdentitiesRiskDistributionsAndThirtyTradeWarning()
    {
        var left = new[] { Trade(1, "XAUUSD.s", 10m) };
        var right = Enumerable.Range(2, 30).Select(id => Trade(id, "XAUUSD.s", 1m)).ToArray();
        var excursions = new Dictionary<long, TradeExcursion>
        {
            [1] = new("Broker|1", 1, -2m, 12m, 5m, null, 2m, Start, Start.AddMinutes(1), 1, 1, true, true),
        };

        var result = _calculator.Compare("左", left, "右", right,
            new Dictionary<long, TradeReviewMetadata>(), excursions);

        Assert.True(result.HasSmallSampleWarning);
        Assert.Equal(1, Assert.Single(result.LeftTrades!).PositionId);
        Assert.Equal(30, result.RightTrades!.Count);
        Assert.Equal(5m, result.LeftAverageInitialRisk);
        Assert.Equal(100m, result.LeftRiskCoveragePercentage);
        Assert.Equal(0m, result.RightRiskCoveragePercentage);
    }

    [Fact]
    public void RuleSummary_SeparatesUnknownAndNotApplicableFromAdherence()
    {
        var assessments = new[]
        {
            Assessment("a", RuleAssessmentStatus.Passed),
            Assessment("b", RuleAssessmentStatus.Failed),
            Assessment("c", RuleAssessmentStatus.Unknown),
            Assessment("d", RuleAssessmentStatus.NotApplicable),
        };

        var result = _calculator.SummarizeRules(assessments);

        Assert.Equal(50m, result.AdherencePercentage);
        Assert.InRange(result.CoveragePercentage, 66.66m, 66.67m);
        Assert.True(result.HasViolation);
        Assert.False(result.IsCompliant);
    }

    [Fact]
    public void Campaign_DeduplicatesMembersAndKeepsDirectionChangeVisible()
    {
        var campaign = new TradeCampaign("c", "Broker|1", "XAUUSD.s", "午后反转", "", [1, 1, 2], 1, Start, Start);
        var trades = new[] { Trade(1, "XAUUSD.s", -10m), Trade(2, "XAUUSD.s", 30m, side: TradeSide.Sell) };
        var deals = new[]
        {
            Deal(1, 1, DealEntryKind.In, 0m, Start, 1m),
            Deal(2, 1, DealEntryKind.Out, -10m, Start.AddMinutes(2), 1m),
            Deal(3, 2, DealEntryKind.In, 0m, Start.AddMinutes(3), 2m),
            Deal(4, 2, DealEntryKind.Out, 30m, Start.AddMinutes(5), 2m),
        };

        var result = _calculator.BuildCampaignSummary(campaign, trades, deals);

        Assert.Equal(2, result.MemberCount);
        Assert.Equal(20m, result.NetPnl);
        Assert.True(result.ContainsDirectionChange);
        Assert.Equal(2m, result.MaximumConcurrentExposure);
    }

    [Fact]
    public void BehaviorImpact_DeduplicatesTradeAcrossRules()
    {
        var key = new TradeKey("Broker|1", 1);
        var trade = Trade(1, "XAUUSD.s", -100m);
        var occurrences = new[]
        {
            Occurrence("a", BehaviorRuleKind.CooldownViolation, key),
            Occurrence("b", BehaviorRuleKind.SizeEscalationAfterLoss, key),
        };

        var result = _calculator.CalculateBehaviorImpact(occurrences, new Dictionary<TradeKey, TradeRecord> { [key] = trade });

        Assert.Equal(2, result.OccurrenceCount);
        Assert.Equal(1, result.UniqueTradeCount);
        Assert.Equal(-100m, result.RelatedNetPnl);
    }

    [Fact]
    public void BehaviorEvidence_SeparatesLiveFromRecalculationAndBuildsSameFilterUnhitSample()
    {
        var hit = new TradeKey("Broker|1", 1);
        var context = new TradeKey("Broker|1", 2);
        var unhit = new TradeKey("Broker|1", 3);
        var live = Occurrence("live", BehaviorRuleKind.CooldownViolation, hit) with
        {
            TradeLinks =
            [
                new BehaviorTradeLink(hit, BehaviorTradeRole.Trigger),
                new BehaviorTradeLink(context, BehaviorTradeRole.PreviousContext),
            ],
        };
        var recalculated = Occurrence("recalc", BehaviorRuleKind.SizeEscalationAfterLoss, hit) with
        {
            Source = ReviewEvidenceSource.RuleRecalculation,
            NotificationDelivered = false,
            NotificationDisposition = "NotApplicable",
        };
        var trades = new Dictionary<TradeKey, TradeRecord>
        {
            [hit] = Trade(1, "XAUUSD.s", -100m),
            [context] = Trade(2, "XAUUSD.s", 25m),
            [unhit] = Trade(3, "EURUSD", 40m),
        };

        var result = _calculator.CompareBehaviorSamples([live, recalculated], trades);

        Assert.Equal(1, result.LiveOccurrenceCount);
        Assert.Equal(1, result.RecalculatedOccurrenceCount);
        Assert.Equal([1L], result.HitSample.Trades.Select(item => item.PositionId).ToArray());
        Assert.Equal(-100m, result.HitSample.NetPnl);
        Assert.Equal([2L, 3L], result.UnhitSample.Trades.Select(item => item.PositionId).Order().ToArray());
        Assert.Equal(65m, result.UnhitSample.NetPnl);
    }

    [Fact]
    public void PeriodFacts_UsesKnownRuleDenominatorAndKeepsBehaviorEvidenceIdentities()
    {
        var trade = Trade(1, "XAUUSD.s", -100m);
        var key = new TradeKey(trade.AccountKey, trade.PositionId);
        var quality = _calculator.CalculateDataQuality([trade], new Dictionary<long, TradeExcursion>(),
            new Dictionary<long, TradeReviewDocument>(), new Dictionary<long, TradeReviewMetadata>(), []);
        var assessments = new[]
        {
            Assessment("pass", RuleAssessmentStatus.Passed),
            Assessment("fail", RuleAssessmentStatus.Failed),
            Assessment("unknown", RuleAssessmentStatus.Unknown),
            Assessment("na", RuleAssessmentStatus.NotApplicable),
        };

        var result = _calculator.BuildPeriodFacts(
            [trade], new Dictionary<long, TradeReviewDocument>(), assessments,
            [Occurrence("event-1", BehaviorRuleKind.CooldownViolation, key)], quality);

        Assert.Equal(50m, result.RuleAdherencePercentage);
        Assert.Equal(1, result.UnknownRuleCount);
        Assert.Equal(1, result.NotApplicableRuleCount);
        Assert.Equal(["event-1"], result.BehaviorOccurrenceIds);
        Assert.Equal([1L], Assert.Single(result.RepeatedBehaviors).Trades.Select(item => item.PositionId));
    }

    [Fact]
    public void GoalProgress_LeavesZeroOpportunityRangeWithoutSyntheticSuccess()
    {
        var goal = new ImprovementGoal(
            "g:v1", "Broker|1", "减少追单", BehaviorRuleKind.ReentryCount, "rule-v1",
            new DateOnly(2026, 9, 1), null, 10m, null, "违规率", "", true,
            ImprovementGoalStatus.Active, 1, Start, Start, GoalKey: "g", ObservationWindowDays: 7);
        var noOpportunity = new GoalObservation(
            "o1", goal.Id, goal.AccountKey, new DateOnly(2026, 9, 2), 0, 0, 0,
            GoalObservationStatus.NotApplicable, "", Start);

        var result = Assert.Single(_calculator.BuildGoalProgress(
            [goal], [noOpportunity], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3)));

        Assert.Equal(3, result.ApplicableDayCount);
        Assert.Equal(2, result.NoOpportunityDayCount);
        Assert.Equal(0, result.OpportunityCount);
        Assert.Null(result.AdherencePercentage);
    }

    [Fact]
    public void DataQuality_KeepsInitialRiskSeparateFromExcursionCoverage()
    {
        var trade = Trade(1, "XAUUSD.s", 150m);
        var excursion = new TradeExcursion("Broker|1", 1, -40m, 180m, 100m, 2m, 1.5m,
            Start, Start.AddMinutes(5), 60_000, 300_000, true, true);

        var result = _calculator.CalculateDataQuality([trade], new Dictionary<long, TradeExcursion> { [1] = excursion },
            new Dictionary<long, TradeReviewDocument>(), new Dictionary<long, TradeReviewMetadata>(), []);

        Assert.Equal(100m, result.InitialRiskCoveragePercentage);
        Assert.Equal(0m, result.ExcursionCoveragePercentage);
    }

    [Fact]
    public void DataQuality_ExplainsExactLegacyGapAndMarketFailureSamples()
    {
        var first = Trade(1, "XAUUSD.s", 10m);
        var second = Trade(2, "EURUSD", -5m);
        var legacy = new TradeExcursion("Broker|1", 1, -10m, 20m, 50m, 2m, 0.2m,
            Start, Start.AddMinutes(1), 60_000, 60_000, true, true);
        var gap = new TradeExcursion("Broker|1", 2, -10m, 20m, 50m, 2m, -0.1m,
            Start, Start.AddMinutes(2), 120_000, 120_000, true, true,
            6_000, "position-pnl-v1");
        var failed = new MarketDataRange("failed-request", "terminal", "Broker|1", "XAUUSD.s", "M1",
            Start, Start.AddMinutes(1), Start, Start, MarketDataPrecision.Bars,
            MarketCoverageStatus.Failed, "1", "terminal offline", Start);
        var partial = new MarketDataRange("partial-request", "terminal", "Broker|1", "EURUSD", "M1",
            Start, Start.AddMinutes(2), Start, Start.AddSeconds(30), MarketDataPrecision.Bars,
            MarketCoverageStatus.Partial, "1", "", Start);

        var result = _calculator.CalculateDataQuality(
            [first, second], new Dictionary<long, TradeExcursion> { [1] = legacy, [2] = gap },
            new Dictionary<long, TradeReviewDocument>(), new Dictionary<long, TradeReviewMetadata>(),
            [failed, partial]);

        var issues = Assert.IsAssignableFrom<IReadOnlyList<ReviewDataQualityIssue>>(result.Issues);
        Assert.Equal([1L], issues.Single(item => item.Code == "excursion-legacy").Trades.Select(item => item.PositionId));
        Assert.Equal([2L], issues.Single(item => item.Code == "excursion-gap").Trades.Select(item => item.PositionId));
        Assert.Equal(["failed-request"], issues.Single(item => item.Code == "market-failed").EvidenceIds);
        Assert.Equal(["partial-request"], issues.Single(item => item.Code == "market-partial").EvidenceIds);
    }

    [Fact]
    public void RiskSamples_KeepReliableRWhenProcessGapDowngradesMaeMfeAndLegacyExtrema()
    {
        var trade = Trade(1, "XAUUSD.s", 150m);
        var gap = new TradeExcursion("Broker|1", 1, -40m, 180m, 100m, 2m, 1.5m,
            Start, Start.AddMinutes(5), 300_000, 300_000, true, true,
            6_000, "position-pnl-v1");
        var legacy = gap with { AlgorithmVersion = "legacy-extrema-v1", MaximumGapMilliseconds = 0 };

        var gapSample = Assert.Single(_calculator.BuildRiskSamples(
            [trade], new Dictionary<long, TradeExcursion> { [1] = gap }));
        var legacySample = Assert.Single(_calculator.BuildRiskSamples(
            [trade], new Dictionary<long, TradeExcursion> { [1] = legacy }));
        var summary = ReviewAnalyticsCalculator.CalculateSummary(
            [trade], excursions: new Dictionary<long, TradeExcursion> { [1] = gap });

        Assert.True(gapSample.HasReliableInitialRisk);
        Assert.False(gapSample.HasReliableExcursion);
        Assert.Equal(100m, gapSample.InitialRiskAmount);
        Assert.Equal(1.5m, gapSample.ActualRiskMultiple);
        Assert.Null(gapSample.Mae);
        Assert.Null(gapSample.Mfe);
        Assert.Equal("legacy-extrema-v1", legacySample.ExcursionAlgorithmVersion);
        Assert.Null(legacySample.Mae);
        Assert.Equal(1.5m, summary.AverageActualRiskMultiple);
        Assert.Null(summary.AverageAdverseExcursion);
        Assert.Null(summary.AverageFavorableExcursion);
    }

    [Fact]
    public void ReviewBasis_MarksOnlyChangedTradeFactsOrAssessmentsAsNeedsReview()
    {
        var trade = Trade(1, "XAUUSD.s", 10m);
        var originalDeal = Deal(1, 1, DealEntryKind.Out, 10m, Start);
        var assessment = Assessment("entry", RuleAssessmentStatus.Passed);
        var legacyVersion = new ReviewDataVersion("Broker|1", 10, 2, 1, "global-rule-v2", "time", Start);
        var basis = _calculator.BuildTradeReviewBasis(trade, [originalDeal], [assessment], null);
        var document = Document(1, ReviewCompletionStatus.Reviewed, "保留旧结论") with
        {
            SourceVersion = basis.SourceVersion,
            RuleVersion = basis.RuleVersion,
            ReviewedSourceVersion = basis.SourceVersion,
            ReviewedRuleVersion = basis.RuleVersion,
        };

        var unrelated = _calculator.ProjectReviewStatuses(
            [trade], [originalDeal, Deal(2, 2, DealEntryKind.Out, 99m, Start)],
            new Dictionary<long, TradeReviewDocument> { [1] = document }, [assessment],
            new Dictionary<long, TradeExcursion>(), legacyVersion);
        var feeChanged = _calculator.ProjectReviewStatuses(
            [trade], [originalDeal with { Commission = -1m }],
            new Dictionary<long, TradeReviewDocument> { [1] = document }, [assessment],
            new Dictionary<long, TradeExcursion>(), legacyVersion);
        var assessmentChanged = _calculator.ProjectReviewStatuses(
            [trade], [originalDeal], new Dictionary<long, TradeReviewDocument> { [1] = document },
            [assessment with { Status = RuleAssessmentStatus.Failed, Revision = 2 }],
            new Dictionary<long, TradeExcursion>(), legacyVersion);

        Assert.Equal(ReviewCompletionStatus.Reviewed, unrelated[1].Status);
        Assert.Equal(ReviewCompletionStatus.NeedsReview, feeChanged[1].Status);
        Assert.Equal("保留旧结论", feeChanged[1].Summary);
        Assert.Equal(ReviewCompletionStatus.NeedsReview, assessmentChanged[1].Status);
    }

    [Fact]
    public void RealizedCurve_LabelsProfitPeakGivebackWithoutPretendingItIsEquityDrawdown()
    {
        var trades = new[]
        {
            Trade(1, "XAUUSD.s", 10m, Start),
            Trade(2, "XAUUSD.s", -5m, Start.AddMinutes(1)),
            Trade(3, "XAUUSD.s", 8m, Start.AddMinutes(2)),
            Trade(4, "XAUUSD.s", -20m, Start.AddMinutes(3)),
        };

        var result = _calculator.BuildRealizedCurve(trades);

        Assert.Equal(-7m, result[^1].Value);
        Assert.Equal(20m, result[^1].Drawdown);
        Assert.InRange(result[^1].DrawdownPercentage!.Value, 153.84m, 153.85m);
    }

    [Fact]
    public void TagSuggestions_UseActionableTriggerEvidenceAndKeepAcceptedState()
    {
        var key = new TradeKey("Broker|1", 1);
        var reentry = Occurrence("reentry-1", BehaviorRuleKind.ReentryCount, key);
        var criticalReentry = Occurrence("reentry-2", BehaviorRuleKind.ReentryCount, key) with
        {
            Level = BehaviorRiskLevel.Critical,
            EventAtUtc = Start.AddMinutes(1),
            Summary = "第三次重复入场",
        };
        var acceptedPlanDeviation = Occurrence("plan-1", BehaviorRuleKind.PlanDeviationRate, key);
        var normal = Occurrence("normal", BehaviorRuleKind.RevengeScore, key) with
        {
            Level = BehaviorRiskLevel.Normal,
        };
        var insufficient = Occurrence("missing", BehaviorRuleKind.CooldownViolation, key) with
        {
            EvidenceInsufficient = true,
        };
        var contextOnly = Occurrence("context", BehaviorRuleKind.OvertradeBurst, key) with
        {
            TradeLinks = [new BehaviorTradeLink(key, BehaviorTradeRole.PreviousContext)],
        };

        var result = _calculator.BuildTagSuggestions(
            key, [reentry, criticalReentry, acceptedPlanDeviation, normal, insufficient, contextOnly], ["计划偏离"]);

        Assert.Equal(2, result.Count);
        var repeated = result.Single(item => item.Tag == "重复入场");
        Assert.Equal(BehaviorRiskLevel.Critical, repeated.Level);
        Assert.Equal(["reentry-2", "reentry-1"], repeated.BehaviorOccurrenceIds);
        Assert.Equal("第三次重复入场", repeated.Reason);
        Assert.False(repeated.IsAccepted);
        Assert.True(result.Single(item => item.Tag == "计划偏离").IsAccepted);
    }

    [Fact]
    public void EquityAnalysis_UnitizesVerifiedDepositWithoutTreatingItAsRecoveryProfit()
    {
        var date = new DateOnly(2026, 9, 1);
        var samples = new[]
        {
            new EquitySample("Broker|1", date, Start, 1_000m, 1_000m, 0m, false),
            new EquitySample("Broker|1", date, Start.AddMinutes(9), 900m, 900m, 0m, false),
            new EquitySample("Broker|1", date, Start.AddMinutes(11), 1_900m, 1_900m, 0m, false),
        };
        var deposit = new AccountCashFlow("Broker|1", 1, "balance", 1_000m, Start.AddMinutes(10));

        var result = _calculator.BuildEquityAnalysis("Broker|1", date, date, samples, [deposit], 0);

        Assert.True(result.HasCompleteCashFlowCoverage);
        Assert.Equal(1, result.VerifiedCashFlowCount);
        Assert.Equal(10m, result.CashFlowAdjustedMaximumDrawdownPercentage);
        Assert.Equal(90m, result.Points[^1].UnitizedValue);
    }

    [Fact]
    public void EquityAnalysis_SplitsAtCashFlowWithoutAdjacentSamplesAndWithholdsCompleteRate()
    {
        var date = new DateOnly(2026, 9, 1);
        var samples = new[]
        {
            new EquitySample("Broker|1", date, Start, 1_000m, 1_000m, 0m, false),
            new EquitySample("Broker|1", date, Start.AddHours(1), 2_000m, 2_000m, 0m, false),
        };
        var deposit = new AccountCashFlow("Broker|1", 1, "balance", 1_000m, Start.AddMinutes(30));

        var result = _calculator.BuildEquityAnalysis("Broker|1", date, date, samples, [deposit], 0);

        Assert.False(result.HasCompleteCashFlowCoverage);
        Assert.Null(result.CashFlowAdjustedMaximumDrawdownPercentage);
        Assert.Equal(2, result.SegmentCount);
        Assert.True(result.Points[^1].StartsAfterUnverifiedCashFlow);
    }

    [Fact]
    public void TradingSessions_UseNamedTimeZoneDaylightRulesForEachTradeDate()
    {
        var timeZoneId = OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York";
        var session = new TradingSessionDefinition(
            "ny-open", "Broker|1", "纽约早盘", timeZoneId, new TimeOnly(9, 30), new TimeOnly(10, 30),
            [], 0, true, 1, Start, Start);
        var winter = Trade(1, "XAUUSD.s", 10m) with
        {
            OpenedAtUtc = new DateTimeOffset(2026, 1, 15, 14, 45, 0, TimeSpan.Zero),
        };
        var summer = Trade(2, "XAUUSD.s", 20m) with
        {
            OpenedAtUtc = new DateTimeOffset(2026, 7, 15, 13, 45, 0, TimeSpan.Zero),
        };

        var result = _calculator.BuildTradingSessionPerformance(
            [winter, summer], [session], new Dictionary<long, TradeExcursion>(), []);

        var group = Assert.Single(result);
        Assert.Equal("纽约早盘", group.Group);
        Assert.Equal(2, group.TradeCount);
        Assert.Equal([1L, 2L], group.Trades!.Select(item => item.PositionId).Order().ToArray());
    }

    [Fact]
    public void TradingSessions_AssignAfterMidnightTradeToSessionStartDay()
    {
        var session = new TradingSessionDefinition(
            "night", "Broker|1", "周四夜盘", "UTC", new TimeOnly(22, 0), new TimeOnly(2, 0),
            [DayOfWeek.Thursday], 0, true, 1, Start, Start);
        var fridayAfterMidnight = Trade(1, "XAUUSD.s", 10m) with
        {
            OpenedAtUtc = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero),
        };

        var result = _calculator.BuildTradingSessionPerformance(
            [fridayAfterMidnight], [session], new Dictionary<long, TradeExcursion>(), []);

        Assert.Equal("周四夜盘", Assert.Single(result).Group);
    }

    [Fact]
    public void Replay_HidesBackfilledNotesButShowsOriginalMarketFacts()
    {
        var cursor = Start.AddMinutes(3);
        var range = new MarketDataRange("r", "t", "Broker|1", "XAUUSD.s", "M1", Start, Start.AddMinutes(10),
            Start, Start.AddMinutes(10), MarketDataPrecision.Bars, MarketCoverageStatus.Complete, "1", "", Start);
        var contemporaneous = new ReviewEvidenceStamp(ReviewEvidenceSource.UserContemporaneous, Start.AddMinutes(1), Start.AddMinutes(1), ReviewTimeBasis.Utc, "1");
        var backfill = new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, Start.AddMinutes(1), Start.AddMinutes(8), ReviewTimeBasis.Utc, "1");

        var result = _calculator.BuildReplayFrame(cursor, range, [], [], [Deal(1, 1, DealEntryKind.In, 0m, Start.AddMinutes(2))],
            [], [], [contemporaneous, backfill]);

        Assert.Single(result.VisibleDeals);
        Assert.Equal(contemporaneous, Assert.Single(result.VisibleNotes));
    }

    [Fact]
    public void Replay_ShowsOnlyCompletedBarsAndHidesFutureResultUntilCloseOrExplicitReveal()
    {
        var range = new MarketDataRange("r", "t", "Broker|1", "XAUUSD.s", "M5", Start, Start.AddMinutes(10),
            Start, Start.AddMinutes(5), MarketDataPrecision.Bars, MarketCoverageStatus.Partial, "1", "", Start);
        var bar = new MarketBar("t", "Broker|1", "XAUUSD.s", "M5", Start, 100m, 102m, 99m, 101m, 10, 1, 0);
        var trade = Trade(1, "XAUUSD.s", 25m, Start.AddMinutes(7));
        var document = Document(1, ReviewCompletionStatus.Reviewed, "盘后结论") with
        {
            UpdatedAtUtc = Start.AddMinutes(8),
        };
        var note = new ReviewEvidenceStamp(
            ReviewEvidenceSource.UserBackfill, Start.AddMinutes(1), Start.AddMinutes(8), ReviewTimeBasis.Local, "1");

        var beforeBarClose = _calculator.BuildReplayFrame(
            Start.AddMinutes(3), range, [bar], [], [], [], [], [note], trade, null, document);
        var afterTradeClose = _calculator.BuildReplayFrame(
            Start.AddMinutes(7), range, [bar], [], [], [], [], [note], trade, null, document);
        var revealed = _calculator.BuildReplayFrame(
            Start.AddMinutes(3), range, [bar], [], [], [], [], [note], trade, null, document, true);

        Assert.Empty(beforeBarClose.VisibleBars);
        Assert.Null(beforeBarClose.VisibleFinalNetPnl);
        Assert.Empty(beforeBarClose.VisibleNotes);
        Assert.Null(beforeBarClose.VisibleReview);
        Assert.Single(afterTradeClose.VisibleBars);
        Assert.Equal(25m, afterTradeClose.VisibleFinalNetPnl);
        Assert.Equal(25m, revealed.VisibleFinalNetPnl);
        Assert.Single(revealed.VisibleNotes);
        Assert.Equal(document, revealed.VisibleReview);
        Assert.Contains(beforeBarClose.NavigationEvents!, item => item.Kind == "K线完成" && item.AtUtc == Start.AddMinutes(5));
        Assert.Contains(beforeBarClose.NavigationEvents!, item => item.Kind == "笔记" && item.AtUtc == Start.AddMinutes(8));
    }

    private static ReviewWorkspaceFilter Filter() => new("Broker|1", new(2026, 9, 1), new(2026, 9, 30));

    private static TradeRecord Trade(long id, string symbol, decimal pnl, DateTimeOffset? closed = null, TradeSide side = TradeSide.Buy) =>
        new("Broker|1", id, symbol, side, Start, closed ?? Start.AddMinutes(id), new(2026, 9, 1),
            DateOnly.FromDateTime((closed ?? Start.AddMinutes(id)).UtcDateTime), 100m, 101m, 1m, 1m, 0m, pnl, true);

    private static DealRecord Deal(long ticket, long positionId, DealEntryKind kind, decimal pnl, DateTimeOffset at, decimal volume = 1m) =>
        new(ticket, ticket, positionId, "XAUUSD.s", TradeSide.Buy, kind, volume, 100m, pnl, 0m, 0m, 0m, at);

    private static TradeReviewMetadata Meta(long id, string strategy, string setup, string[] tags) =>
        new("Broker|1", id, null, PlanComplianceStatus.Unclassified, strategy, setup, tags, true, Start);

    private static TradeReviewDocument Document(long id, ReviewCompletionStatus status, string summary) =>
        new(new TradeKey("Broker|1", id), status, "", "", "", "", "执行", summary, "", "", 1,
            "source", "rule", status == ReviewCompletionStatus.Reviewed ? "source" : null,
            status == ReviewCompletionStatus.Reviewed ? "rule" : null, Start, Start, status == ReviewCompletionStatus.Reviewed ? Start : null);

    private static TradeRuleAssessment Assessment(string id, RuleAssessmentStatus status) =>
        new(new TradeKey("Broker|1", 1), "p", id, status, ReviewEvidenceSource.UserBackfill, "", "", 1, Start);

    private static BehaviorOccurrence Occurrence(string id, BehaviorRuleKind rule, TradeKey key) =>
        new(id, key.AccountKey, new(2026, 9, 1), rule, "1", 1m, null, 1m, BehaviorRiskLevel.Attention,
            ReviewEvidenceSource.LiveObservation, Start, Start, rule.ToString(), "", true, "delivered", null,
            [new BehaviorTradeLink(key, BehaviorTradeRole.Trigger)]);
}
