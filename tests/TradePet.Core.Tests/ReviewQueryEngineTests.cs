using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class ReviewQueryEngineTests
{
    [Fact]
    public void Select_ExtendsBehaviorRangeToEarliestSelectedTradeOpenDate()
    {
        var filterDate = new DateOnly(2026, 9, 6);
        var engine = new ReviewQueryEngine();
        var selected = Trade(1, "EURUSD", new DateOnly(2026, 9, 5), filterDate);
        var filteredOut = Trade(2, "XAUUSD", new DateOnly(2026, 9, 1), filterDate);

        var result = engine.Select(
            new ReviewFilter("Broker|1", filterDate, filterDate, Symbol: "EURUSD"),
            [selected, filteredOut]);

        Assert.Equal(new DateOnly(2026, 9, 5), result.BehaviorFromServerDate);
        Assert.Equal(1, Assert.Single(result.Snapshot.Trades).PositionId);
    }

    [Fact]
    public void Select_UsesFilterStartWhenNoTradesMatch()
    {
        var from = new DateOnly(2026, 9, 1);
        var result = new ReviewQueryEngine().Select(
            new ReviewFilter("Broker|1", from, new DateOnly(2026, 9, 6)),
            [Trade(1, "EURUSD", from, new DateOnly(2026, 8, 31))]);

        Assert.Empty(result.Snapshot.Trades);
        Assert.Equal(from, result.BehaviorFromServerDate);
    }

    [Fact]
    public void Complete_AttachesBehaviorWithoutChangingSelectedPerformance()
    {
        var date = new DateOnly(2026, 9, 6);
        var trade = Trade(1, "EURUSD", date, date);
        IReadOnlyDictionary<long, TradeReviewMetadata> metadata =
            new Dictionary<long, TradeReviewMetadata>
            {
                [1] = new(
                    "Broker|1", 1, "plan-1", PlanComplianceStatus.OutsidePlan,
                    "突破", "回踩", [], false, DateTimeOffset.UtcNow),
            };
        var engine = new ReviewQueryEngine();
        var selection = engine.Select(
            new ReviewFilter("Broker|1", date, date), [trade], metadata);

        var completed = engine.Complete(
            selection, [], [], metadata, BehaviorPolicy.Balanced,
            observedAtUtc: new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

        Assert.Same(selection.Snapshot.Trades, completed.Trades);
        Assert.Equal(selection.Snapshot.Performance, completed.Performance);
        var deviation = Assert.Single(completed.Behavior.Evaluations,
            item => item.Rule == BehaviorRuleKind.PlanDeviationRate);
        Assert.Equal(100m, deviation.Value);
        Assert.True(deviation.Triggered);
    }

    private static TradeRecord Trade(
        long positionId,
        string symbol,
        DateOnly openDate,
        DateOnly closeDate)
    {
        var opened = new DateTimeOffset(openDate, new TimeOnly(1, 0), TimeSpan.Zero);
        return new TradeRecord(
            "Broker|1", positionId, symbol, TradeSide.Buy,
            opened, opened.AddMinutes(5), openDate, closeDate,
            1.1m, 1.2m, 0.1m, 0.1m, 0m, 10m, true);
    }
}
