using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class PositionSnapshotDiffer
{
    public IReadOnlyList<TradeDomainEvent> Diff(
        IReadOnlyCollection<PositionSnapshot> previous,
        IReadOnlyCollection<PositionSnapshot> current,
        DateTimeOffset observedAtUtc)
    {
        var events = new List<TradeDomainEvent>();
        var previousByTicket = previous.ToDictionary(position => position.Ticket);
        var currentByTicket = current.ToDictionary(position => position.Ticket);

        foreach (var position in current)
        {
            if (!previousByTicket.TryGetValue(position.Ticket, out var oldPosition))
            {
                events.Add(Create(TradeDomainEventKind.Opened, null, position, position.Volume, observedAtUtc));
                continue;
            }

            if (position.Volume > oldPosition.Volume)
            {
                events.Add(Create(TradeDomainEventKind.Increased, oldPosition, position, position.Volume - oldPosition.Volume, observedAtUtc));
            }
            else if (position.Volume < oldPosition.Volume)
            {
                events.Add(Create(TradeDomainEventKind.Reduced, oldPosition, position, oldPosition.Volume - position.Volume, observedAtUtc));
            }

            AddProtectionChange(events, oldPosition, position, observedAtUtc, stopLoss: true);
            AddProtectionChange(events, oldPosition, position, observedAtUtc, stopLoss: false);
        }

        foreach (var oldPosition in previous)
        {
            if (!currentByTicket.ContainsKey(oldPosition.Ticket))
            {
                events.Add(Create(TradeDomainEventKind.Closed, oldPosition, null, oldPosition.Volume, observedAtUtc));
            }
        }

        var previousGroups = previous
            .GroupBy(position => (position.Symbol, position.Side))
            .ToDictionary(group => group.Key, group => new
            {
                Volume = group.Sum(position => position.Volume),
                FloatingPnl = group.Sum(position => position.Profit + position.Swap),
            });

        foreach (var group in current.GroupBy(position => (position.Symbol, position.Side)))
        {
            if (!previousGroups.TryGetValue(group.Key, out var before) || before.FloatingPnl >= 0m)
            {
                continue;
            }

            var currentVolume = group.Sum(position => position.Volume);
            if (currentVolume <= before.Volume)
            {
                continue;
            }

            var representative = group.OrderByDescending(position => position.CapturedAtUtc).First();
            events.Add(Create(
                TradeDomainEventKind.AddingToLoss,
                previous.First(position => position.Symbol == group.Key.Symbol && position.Side == group.Key.Side),
                representative,
                currentVolume - before.Volume,
                observedAtUtc));
        }

        return events;
    }

    private static void AddProtectionChange(
        ICollection<TradeDomainEvent> events,
        PositionSnapshot previous,
        PositionSnapshot current,
        DateTimeOffset observedAtUtc,
        bool stopLoss)
    {
        var oldValue = stopLoss ? previous.StopLoss : previous.TakeProfit;
        var newValue = stopLoss ? current.StopLoss : current.TakeProfit;
        if (oldValue == newValue)
        {
            return;
        }

        var kind = (oldValue, newValue, stopLoss) switch
        {
            (0m, > 0m, true) => TradeDomainEventKind.StopLossAdded,
            (> 0m, 0m, true) => TradeDomainEventKind.StopLossRemoved,
            (_, _, true) => TradeDomainEventKind.StopLossModified,
            (0m, > 0m, false) => TradeDomainEventKind.TakeProfitAdded,
            (> 0m, 0m, false) => TradeDomainEventKind.TakeProfitRemoved,
            _ => TradeDomainEventKind.TakeProfitModified,
        };
        events.Add(Create(kind, previous, current, 0m, observedAtUtc));
    }

    private static TradeDomainEvent Create(
        TradeDomainEventKind kind,
        PositionSnapshot? previous,
        PositionSnapshot? current,
        decimal volumeDelta,
        DateTimeOffset observedAtUtc)
    {
        var source = current ?? previous ?? throw new InvalidOperationException("A position change requires a position snapshot.");
        return new TradeDomainEvent(
            kind,
            source.PositionId,
            source.Symbol,
            source.Side,
            volumeDelta,
            previous,
            current,
            observedAtUtc);
    }
}
