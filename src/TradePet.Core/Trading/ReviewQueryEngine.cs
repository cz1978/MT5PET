using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class ReviewQueryEngine
{
    private readonly ReviewAnalyticsCalculator _reviewCalculator = new();
    private readonly BehaviorAnalyticsCalculator _behaviorCalculator = new();

    public ReviewQuerySelection Select(
        ReviewFilter filter,
        IEnumerable<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata = null,
        IReadOnlyDictionary<long, TradeExcursion>? excursions = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(trades);

        var snapshot = _reviewCalculator.Calculate(filter, trades, metadata, excursions);
        var behaviorFrom = snapshot.Trades.Count == 0
            ? filter.FromServerDate
            : snapshot.Trades.Min(trade =>
                trade.OpenServerDate < filter.FromServerDate
                    ? trade.OpenServerDate
                    : filter.FromServerDate);
        return new ReviewQuerySelection(snapshot, behaviorFrom);
    }

    public ReviewSnapshot Complete(
        ReviewQuerySelection selection,
        IReadOnlyCollection<LossZoneState> lossZones,
        IReadOnlyCollection<LossZoneAttempt> attempts,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata,
        BehaviorPolicy policy,
        IReadOnlyDictionary<DateOnly, DailyState>? dailyStates = null,
        DateTimeOffset? observedAtUtc = null,
        IReadOnlyDictionary<string, SymbolSpecification>? symbolSpecifications = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(lossZones);
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(policy);

        var behavior = _behaviorCalculator.EvaluatePeriod(
            selection.Snapshot.Filter,
            selection.Snapshot.Trades,
            lossZones,
            attempts,
            metadata,
            policy,
            dailyStates,
            observedAtUtc,
            symbolSpecifications);
        return selection.Snapshot with { Behavior = behavior };
    }
}

public sealed record ReviewQuerySelection(
    ReviewSnapshot Snapshot,
    DateOnly BehaviorFromServerDate);
