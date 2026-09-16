using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class TradeProjectorTests
{
    [Fact]
    public void Projector_AggregatesAddsPartialClosesAndAllCosts()
    {
        var start = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var deals = new[]
        {
            Deal(1, DealEntryKind.In, TradeSide.Buy, 0.02m, 3352m, 0m, -0.20m, start),
            Deal(2, DealEntryKind.In, TradeSide.Buy, 0.01m, 3351m, 0m, -0.10m, start.AddMinutes(1)),
            Deal(3, DealEntryKind.Out, TradeSide.Sell, 0.01m, 3353m, 1m, -0.10m, start.AddMinutes(2)),
            Deal(4, DealEntryKind.Out, TradeSide.Sell, 0.02m, 3348m, -9m, -0.20m, start.AddMinutes(3)),
        };

        var trade = Assert.Single(new TradeProjector().Project("Broker|1", deals, timestamp => DateOnly.FromDateTime(timestamp.UtcDateTime)));

        Assert.Equal(TradeSide.Buy, trade.Side);
        Assert.Equal(0.02m, trade.OpeningVolume);
        Assert.Equal(0.03m, trade.MaximumVolume);
        Assert.Equal(0m, trade.RemainingVolume);
        Assert.Equal((3352m * 0.02m + 3351m * 0.01m) / 0.03m, trade.EntryPrice);
        Assert.Equal((3353m * 0.01m + 3348m * 0.02m) / 0.03m, trade.ExitPrice);
        Assert.Equal(-8.60m, trade.NetPnl);
        Assert.True(trade.IsComplete);
    }

    [Fact]
    public void Projector_RejectsNettingReversalInsteadOfSilentlyClosingIt()
    {
        var start = new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        var deals = new[]
        {
            Deal(1, DealEntryKind.In, TradeSide.Buy, 1m, 100m, 0m, 0m, start),
            Deal(2, DealEntryKind.InOut, TradeSide.Sell, 2m, 99m, -1m, 0m, start.AddMinutes(1)),
        };

        Assert.Throws<NotSupportedException>(() => new TradeProjector().Project(
            "Broker|1", deals, timestamp => DateOnly.FromDateTime(timestamp.UtcDateTime)));
    }

    private static DealRecord Deal(
        long ticket,
        DealEntryKind entryKind,
        TradeSide side,
        decimal volume,
        decimal price,
        decimal profit,
        decimal commission,
        DateTimeOffset occurredAt) =>
        new(ticket, ticket, 9001, "XAUUSD.s", side, entryKind, volume, price, profit, commission, 0m, 0m, occurredAt);
}
