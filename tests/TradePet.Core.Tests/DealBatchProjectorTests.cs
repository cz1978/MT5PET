using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class DealBatchProjectorTests
{
    private static readonly Func<DateTimeOffset, DateOnly> UtcDate =
        value => DateOnly.FromDateTime(value.UtcDateTime);

    [Fact]
    public void Project_AppliesExactCloseDateAndReportsCompletionTransition()
    {
        var openedAt = new DateTimeOffset(2026, 9, 5, 23, 50, 0, TimeSpan.Zero);
        var opening = Deal(1, 100, DealEntryKind.In, openedAt);
        var closing = Deal(2, 100, DealEntryKind.Out, openedAt.AddMinutes(20));
        var previous = new TradeProjector().Project("Broker|1", [opening], UtcDate).Single();

        var result = new DealBatchProjector().Project(
            "Broker|1",
            [opening, closing],
            [closing],
            new Dictionary<long, TradeRecord> { [100] = previous },
            new Dictionary<long, DateOnly>(),
            UtcDate,
            new DateOnly(2026, 9, 6),
            new DateOnly(2026, 9, 7),
            isHistoricalBatch: true);

        Assert.Empty(result.NewlyOpened);
        var completed = Assert.Single(result.NewlyCompleted);
        Assert.Equal(new DateOnly(2026, 9, 7), completed.CloseServerDate);
        Assert.Equal(new DateOnly(2026, 9, 7), result.ExactCloseDateUpdates[100]);
    }

    [Fact]
    public void Project_ReplayOfExistingCompletedTradeDoesNotRepeatLifecycleEvents()
    {
        var openedAt = new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        var opening = Deal(1, 100, DealEntryKind.In, openedAt);
        var closing = Deal(2, 100, DealEntryKind.Out, openedAt.AddMinutes(5));
        var existing = new TradeProjector().Project("Broker|1", [opening, closing], UtcDate).Single();

        var result = new DealBatchProjector().Project(
            "Broker|1",
            [opening, closing],
            [closing],
            new Dictionary<long, TradeRecord> { [100] = existing },
            new Dictionary<long, DateOnly>(),
            UtcDate,
            new DateOnly(2026, 9, 6),
            batchServerDate: null,
            isHistoricalBatch: false);

        Assert.Single(result.Upserts);
        Assert.Empty(result.NewlyOpened);
        Assert.Empty(result.NewlyCompleted);
    }

    [Fact]
    public void Project_HistoricalBatchAffectsTodayOnlyByBatchOrDealDate()
    {
        var projector = new DealBatchProjector();
        var currentDate = new DateOnly(2026, 9, 6);
        var oldDeal = Deal(1, 100, DealEntryKind.In,
            new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero));
        var currentDeal = Deal(2, 200, DealEntryKind.In,
            new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero));

        bool Affects(DealRecord deal, DateOnly? batchDate, bool historical) =>
            projector.Project(
                "Broker|1", [deal], [deal], new Dictionary<long, TradeRecord>(),
                new Dictionary<long, DateOnly>(), UtcDate, currentDate, batchDate, historical)
                .AffectsCurrentServerDate;

        Assert.True(Affects(oldDeal, null, historical: false));
        Assert.True(Affects(oldDeal, currentDate, historical: true));
        Assert.True(Affects(currentDeal, currentDate.AddDays(-1), historical: true));
        Assert.False(Affects(oldDeal, currentDate.AddDays(-1), historical: true));
    }

    private static DealRecord Deal(
        long ticket,
        long positionId,
        DealEntryKind entry,
        DateTimeOffset occurredAtUtc) =>
        new(
            ticket,
            ticket,
            positionId,
            "EURUSD",
            entry == DealEntryKind.In ? TradeSide.Buy : TradeSide.Sell,
            entry,
            0.1m,
            1.1m,
            entry == DealEntryKind.Out ? 10m : 0m,
            0m,
            0m,
            0m,
            occurredAtUtc);
}
