using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed record TradeProjectionDelta(
    IReadOnlyList<TradeRecord> Upserts,
    IReadOnlyList<TradeRecord> NewlyOpened,
    IReadOnlyList<TradeRecord> NewlyCompleted)
{
    public static TradeProjectionDelta Empty { get; } = new([], [], []);
}

public sealed class IncrementalTradeProjection
{
    private readonly TradeProjector _projector = new();

    public TradeProjectionDelta ProjectAffected(
        string accountKey,
        IReadOnlyCollection<DealRecord> allDeals,
        IReadOnlyCollection<DealRecord> changedDeals,
        IReadOnlyDictionary<long, TradeRecord> currentTrades,
        Func<DateTimeOffset, DateOnly> serverDateResolver)
    {
        var affectedPositionIds = changedDeals
            .Where(deal => deal.PositionId != 0)
            .Select(deal => deal.PositionId)
            .ToHashSet();
        if (affectedPositionIds.Count == 0)
        {
            return TradeProjectionDelta.Empty;
        }

        var upserts = _projector.Project(
                accountKey,
                allDeals.Where(deal => affectedPositionIds.Contains(deal.PositionId)),
                serverDateResolver)
            .ToArray();
        var newlyOpened = upserts
            .Where(trade => !currentTrades.ContainsKey(trade.PositionId))
            .OrderBy(trade => trade.OpenedAtUtc)
            .ToArray();
        var newlyCompleted = upserts
            .Where(trade => trade.IsComplete &&
                            (!currentTrades.TryGetValue(trade.PositionId, out var previous) || !previous.IsComplete))
            .OrderBy(trade => trade.ClosedAtUtc)
            .ToArray();
        return new TradeProjectionDelta(upserts, newlyOpened, newlyCompleted);
    }
}
