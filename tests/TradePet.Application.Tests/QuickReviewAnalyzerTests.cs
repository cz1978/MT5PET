using TradePet.Application.Review;
using TradePet.Core.Domain;
using Xunit;

namespace TradePet.Application.Tests;

public sealed class QuickReviewAnalyzerTests
{
    private static readonly DateTimeOffset ClosedAt = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TradeSide.Buy, 110, 95, 110, "止盈")]
    [InlineData(TradeSide.Sell, 90, 105, 90, "止盈")]
    [InlineData(TradeSide.Buy, 94, 95, 110, "止损")]
    [InlineData(TradeSide.Sell, 106, 105, 90, "止损")]
    [InlineData(TradeSide.Buy, 104, 105, 110, "止损")]
    public void NoPlan_UsesRecentPositionLevelsIncludingTrailingStops(
        TradeSide side, int exit, int stop, int target, string label)
    {
        var detail = Detail(side, exit) with { PnlSamples = [Sample(1, stop, target)] };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.Equal($"触及{label}价离场（推测）", result.ExitReason);
        Assert.Contains("平仓前持仓记录", result.Explanation);
        Assert.Contains("尚不能确认", result.Explanation);
        Assert.Contains(label, result.Improvement);
    }

    [Fact]
    public void MissingData_StillProvidesFactsAndAnEditableReviewPrompt()
    {
        var result = QuickReviewAnalyzer.Analyze(Detail());

        Assert.Contains("净盈亏 +5", result.Explanation);
        Assert.Contains("持仓 10 分钟", result.Explanation);
        Assert.Contains("缺少有效盈亏采样", result.Explanation);
        Assert.Contains("无法从盈亏确定实际退出原因", result.Explanation);
        Assert.Contains("补充本次实际退出原因", result.Improvement);
        Assert.DoesNotContain("情绪", result.ExitReason);
    }

    [Fact]
    public void ProfitGiveback_UsesRecordedNetPnlAndLabelsIncompleteCoverage()
    {
        var detail = Detail() with { PnlSamples = [Sample(20, null, null), Sample(-3, null, null, 2)] };
        detail = detail with { Trade = detail.Trade with { NetPnl = -5 } };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.Equal("浮盈回吐后亏损离场（待确认）", result.ExitReason);
        Assert.Contains("回吐 25", result.Explanation);
        Assert.Contains("覆盖不完整", result.Explanation);
        Assert.Contains("盈利高点到退出", result.Improvement);
    }

    [Fact]
    public void ReliableExcursion_IsUsedWhenDetailedSamplesAreAbsent()
    {
        var detail = Detail() with
        {
            Excursion = new TradeExcursion("Broker|1", 1, -10, 25, null, null, null,
                ClosedAt.AddMinutes(-10), ClosedAt, 600_000, 600_000, true, true, 1_000, "position-pnl-v1"),
        };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.Contains("回吐 20", result.Explanation);
        Assert.DoesNotContain("覆盖不完整", result.Explanation);
    }

    [Fact]
    public void RemovedStops_AreNotResurrectedFromOlderSamplesOrPlan()
    {
        var detail = Detail(exit: 94) with
        {
            Plan = Plan(),
            PnlSamples = [Sample(0, 95, 110, 2), Sample(0, null, null)],
        };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.DoesNotContain("触及止损", result.ExitReason);
        Assert.Contains("缺少可用", result.Explanation);
    }

    [Fact]
    public void StaleSamples_AreNotUsedAsCurrentExitLevels()
    {
        var detail = Detail(exit: 94) with { PnlSamples = [Sample(0, 95, 110, 60)] };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.DoesNotContain("触及止损", result.ExitReason);
        Assert.Contains("缺少可用", result.Explanation);
    }

    [Fact]
    public void Plan_ProvidesFallbackPriceEvidenceWhenNoRecentSampleExists()
    {
        var result = QuickReviewAnalyzer.Analyze(Detail(exit: 111) with { Plan = Plan() });

        Assert.Equal("触及止盈价离场（推测）", result.ExitReason);
        Assert.Contains("绑定计划", result.Explanation);
    }

    [Fact]
    public void PartialCloses_UseFinalDealPriceRatherThanAverageExit()
    {
        var detail = Detail(exit: 103) with
        {
            Plan = Plan(),
            Deals = [Exit(2, 111, ClosedAt), Exit(1, 99, ClosedAt.AddSeconds(-10))],
        };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.Equal("触及止盈价离场（推测）", result.ExitReason);
        Assert.Contains("最终平仓价 111", result.Explanation);
        Assert.Contains("共 2 笔退出成交", result.Explanation);
    }

    [Fact]
    public void SamplesFromOtherTradesOrOutsideHoldingPeriod_AreExcluded()
    {
        var detail = Detail(exit: 94) with
        {
            PnlSamples =
            [
                Sample(100, 95, 110) with { TradeKey = new TradeKey("Other|1", 1) },
                Sample(200, 95, 110, -1),
                Sample(300, 95, 110, 601),
                Sample(400, 95, 110) with { Volume = 0 },
            ],
        };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.DoesNotContain("触及止损", result.ExitReason);
        Assert.Contains("缺少有效盈亏采样", result.Explanation);
    }

    [Fact]
    public void ConflictingLevels_DoNotProduceAFalseTriggerConclusion()
    {
        var result = QuickReviewAnalyzer.Analyze(Detail(exit: 100) with { PnlSamples = [Sample(0, 105, 95)] });

        Assert.Contains("存在冲突", result.Explanation);
        Assert.DoesNotContain("触及止", result.ExitReason);
    }

    [Fact]
    public void BehaviorAnalysis_OnlyIncludesSupportedRemindersLinkedToThisTrade()
    {
        var behavior = new BehaviorOccurrence("b1", "Broker|1", new(2026, 9, 24),
            BehaviorRuleKind.ReentryCount, "rule-1", 3, 1, 2, BehaviorRiskLevel.Attention,
            ReviewEvidenceSource.LiveObservation, ClosedAt, ClosedAt, "短时间内重复入场", "", false, "", null,
            [new BehaviorTradeLink(new TradeKey("Broker|1", 1), BehaviorTradeRole.Trigger)]);
        var detail = Detail() with
        {
            Behaviors =
            [
                behavior,
                behavior with { Summary = "证据不足提醒", EvidenceInsufficient = true },
                behavior with { Summary = "无关交易提醒", TradeLinks = [] },
                behavior with { Summary = "数据缺失提醒", MissingData = "缺少数据" },
            ],
        };

        var result = QuickReviewAnalyzer.Analyze(detail);

        Assert.Contains("短时间内重复入场", result.Explanation);
        Assert.Contains("短时间内重复入场", result.Improvement);
        Assert.DoesNotContain("证据不足提醒", result.Explanation);
        Assert.DoesNotContain("无关交易提醒", result.Explanation);
        Assert.DoesNotContain("数据缺失提醒", result.Explanation);
    }

    private static TradeDetailData Detail(TradeSide side = TradeSide.Buy, decimal exit = 101) =>
        new(new TradeRecord("Broker|1", 1, "TEST", side, ClosedAt.AddMinutes(-10), ClosedAt,
            new(2026, 9, 24), new(2026, 9, 24), 100, exit, 1, 1, 0, 5, true),
            [], null, null, null, [], null, null, [], [], [], null,
            new ReviewDataVersion("Broker|1", 1, 1, 1, "rule-1", "time-1", ClosedAt));

    private static PositionPnlSample Sample(decimal pnl, decimal? stop, decimal? target, int secondsBeforeClose = 1) =>
        new(new TradeKey("Broker|1", 1), ClosedAt.AddSeconds(-secondsBeforeClose), 0, pnl, pnl, 1,
            stop, target, 1_000, "position-pnl-v1");

    private static StructuredTradePlan Plan() =>
        new("p1", "Broker|1", new(2026, 9, 24), "TEST", TradeSide.Buy, 100, null, null, 95, 110,
            "", "", [], "", true, ClosedAt.AddHours(-1), ClosedAt.AddHours(-1));

    private static DealRecord Exit(long ticket, decimal price, DateTimeOffset time) =>
        new(ticket, ticket, 1, "TEST", TradeSide.Sell, DealEntryKind.Out, 0.5m, price, 0, 0, 0, 0, time);
}
