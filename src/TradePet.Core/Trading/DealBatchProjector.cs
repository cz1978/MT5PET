using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed record DealBatchProjectionResult(
    IReadOnlyList<TradeRecord> Upserts,
    IReadOnlyList<TradeRecord> NewlyOpened,
    IReadOnlyList<TradeRecord> NewlyCompleted,
    IReadOnlyDictionary<long, DateOnly> ExactCloseDateUpdates,
    bool AffectsCurrentServerDate);

public sealed class DealBatchProjector
{
    private readonly IncrementalTradeProjection _incrementalProjection = new();

    public DealBatchProjectionResult Project(
        string accountKey,
        IReadOnlyCollection<DealRecord> allDeals,
        IReadOnlyCollection<DealRecord> incomingDeals,
        IReadOnlyDictionary<long, TradeRecord> currentTrades,
        IReadOnlyDictionary<long, DateOnly> existingExactCloseDates,
        Func<DateTimeOffset, DateOnly> serverDateResolver,
        DateOnly currentServerDate,
        DateOnly? batchServerDate,
        bool isHistoricalBatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentNullException.ThrowIfNull(allDeals);
        ArgumentNullException.ThrowIfNull(incomingDeals);
        ArgumentNullException.ThrowIfNull(currentTrades);
        ArgumentNullException.ThrowIfNull(existingExactCloseDates);
        ArgumentNullException.ThrowIfNull(serverDateResolver);

        var exactCloseDateUpdates = batchServerDate is null
            ? new Dictionary<long, DateOnly>()
            : incomingDeals
                .Where(deal =>
                    deal.PositionId != 0 &&
                    deal.EntryKind is DealEntryKind.Out or DealEntryKind.OutBy or DealEntryKind.InOut)
                .GroupBy(deal => deal.PositionId)
                .ToDictionary(group => group.Key, _ => batchServerDate.Value);
        var effectiveCloseDates = new Dictionary<long, DateOnly>(existingExactCloseDates);
        foreach (var pair in exactCloseDateUpdates)
        {
            effectiveCloseDates[pair.Key] = pair.Value;
        }

        var delta = _incrementalProjection.ProjectAffected(
            accountKey,
            allDeals,
            incomingDeals,
            currentTrades,
            serverDateResolver);
        var upserts = delta.Upserts
            .Select(trade => ApplyExactCloseDate(trade, effectiveCloseDates))
            .ToArray();
        var upsertsByPosition = upserts.ToDictionary(trade => trade.PositionId);
        var newlyOpened = delta.NewlyOpened
            .Select(trade => upsertsByPosition[trade.PositionId])
            .OrderBy(trade => trade.OpenedAtUtc)
            .ToArray();
        var newlyCompleted = delta.NewlyCompleted
            .Select(trade => upsertsByPosition[trade.PositionId])
            .OrderBy(trade => trade.ClosedAtUtc)
            .ToArray();
        var affectsCurrentServerDate = !isHistoricalBatch ||
                                       batchServerDate == currentServerDate ||
                                       incomingDeals.Any(deal =>
                                           serverDateResolver(deal.OccurredAtUtc) == currentServerDate);
        return new DealBatchProjectionResult(
            upserts,
            newlyOpened,
            newlyCompleted,
            exactCloseDateUpdates,
            affectsCurrentServerDate);
    }

    private static TradeRecord ApplyExactCloseDate(
        TradeRecord trade,
        IReadOnlyDictionary<long, DateOnly> exactCloseDates) =>
        trade.IsComplete && exactCloseDates.TryGetValue(trade.PositionId, out var serverDate)
            ? trade with { CloseServerDate = serverDate }
            : trade;
}
