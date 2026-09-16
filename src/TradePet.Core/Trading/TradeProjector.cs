using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class TradeProjector
{
    private const decimal VolumeEpsilon = 0.00000001m;

    public IReadOnlyList<TradeRecord> Project(
        string accountKey,
        IEnumerable<DealRecord> deals,
        Func<DateTimeOffset, DateOnly> serverDateResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentNullException.ThrowIfNull(deals);
        ArgumentNullException.ThrowIfNull(serverDateResolver);

        var projected = new List<TradeRecord>();
        foreach (var group in deals.Where(deal => deal.PositionId != 0).GroupBy(deal => deal.PositionId))
        {
            var ordered = group.OrderBy(deal => deal.OccurredAtUtc).ThenBy(deal => deal.Ticket).ToArray();
            var entryDeals = new List<DealRecord>();
            var exitDeals = new List<DealRecord>();
            decimal currentVolume = 0m;
            decimal maximumVolume = 0m;
            decimal openingVolume = 0m;

            foreach (var deal in ordered)
            {
                switch (deal.EntryKind)
                {
                    case DealEntryKind.In:
                        AddEntry(deal);
                        break;
                    case DealEntryKind.Out:
                    case DealEntryKind.OutBy:
                        AddExit(deal);
                        break;
                    case DealEntryKind.InOut:
                        if (currentVolume > VolumeEpsilon)
                        {
                            if (deal.Volume > currentVolume + VolumeEpsilon)
                            {
                                throw new NotSupportedException(
                                    "Netting position reversals require segmented trade projection.");
                            }

                            AddExit(deal);
                        }
                        else
                        {
                            AddEntry(deal);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(deal.EntryKind));
                }
            }

            if (entryDeals.Count == 0)
            {
                continue;
            }

            var firstEntry = entryDeals[0];
            var complete = currentVolume <= VolumeEpsilon && exitDeals.Count > 0;
            DateTimeOffset? closedAt = complete ? exitDeals[^1].OccurredAtUtc : null;
            projected.Add(new TradeRecord(
                accountKey,
                group.Key,
                firstEntry.Symbol,
                firstEntry.Side,
                firstEntry.OccurredAtUtc,
                closedAt,
                serverDateResolver(firstEntry.OccurredAtUtc),
                closedAt is null ? null : serverDateResolver(closedAt.Value),
                WeightedPrice(entryDeals),
                exitDeals.Count == 0 ? null : WeightedPrice(exitDeals),
                openingVolume,
                maximumVolume,
                Math.Max(0m, currentVolume),
                ordered.Sum(deal => deal.NetPnl),
                complete));

            void AddEntry(DealRecord deal)
            {
                if (entryDeals.Count == 0)
                {
                    openingVolume = deal.Volume;
                }

                entryDeals.Add(deal);
                currentVolume += deal.Volume;
                maximumVolume = Math.Max(maximumVolume, currentVolume);
            }

            void AddExit(DealRecord deal)
            {
                exitDeals.Add(deal);
                currentVolume = Math.Max(0m, currentVolume - deal.Volume);
            }
        }

        return projected.OrderBy(trade => trade.OpenedAtUtc).ToArray();
    }

    private static decimal WeightedPrice(IReadOnlyCollection<DealRecord> deals)
    {
        var totalVolume = deals.Sum(deal => deal.Volume);
        return totalVolume == 0m
            ? 0m
            : deals.Sum(deal => deal.Price * deal.Volume) / totalVolume;
    }
}
