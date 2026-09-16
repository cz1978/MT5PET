using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class IncrementalTradeProjectionTests
{
    [Fact]
    public void Projection_OnlyRebuildsPositionsTouchedByTheIncomingBatch()
    {
        var start = new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        var untouched = Deal(1, 100, DealEntryKind.In, start);
        var opening = Deal(2, 200, DealEntryKind.In, start.AddMinutes(1));
        var closing = Deal(3, 200, DealEntryKind.Out, start.AddMinutes(2));

        var delta = new IncrementalTradeProjection().ProjectAffected(
            "Broker|1",
            [untouched, opening, closing],
            [closing],
            new Dictionary<long, TradeRecord>(),
            timestamp => DateOnly.FromDateTime(timestamp.UtcDateTime));

        var trade = Assert.Single(delta.Upserts);
        Assert.Equal(200, trade.PositionId);
        Assert.True(trade.IsComplete);
        Assert.DoesNotContain(delta.Upserts, item => item.PositionId == 100);
    }

    private static DealRecord Deal(long ticket, long positionId, DealEntryKind entry, DateTimeOffset occurred) =>
        new(ticket, ticket, positionId, "EURUSD", entry == DealEntryKind.In ? TradeSide.Buy : TradeSide.Sell,
            entry, 0.01m, 1.1m, entry == DealEntryKind.Out ? 1m : 0m, 0m, 0m, 0m, occurred);
}
