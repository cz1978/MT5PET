using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class PositionSnapshotDifferTests
{
    [Fact]
    public void Differ_DetectsIncreaseProtectionRemovalAndAddingToLoss()
    {
        var now = DateTimeOffset.UtcNow;
        var before = Position(1, 0.02m, -8m, 3340m, 3370m, now.AddSeconds(-1));
        var after = Position(1, 0.05m, -10m, 0m, 3370m, now);

        var events = new PositionSnapshotDiffer().Diff([before], [after], now);

        Assert.Contains(events, item => item.Kind == TradeDomainEventKind.Increased && item.VolumeDelta == 0.03m);
        Assert.Contains(events, item => item.Kind == TradeDomainEventKind.StopLossRemoved);
        Assert.Contains(events, item => item.Kind == TradeDomainEventKind.AddingToLoss && item.VolumeDelta == 0.03m);
    }

    [Fact]
    public void Differ_DetectsSeparateHedgingTicketAsAdditionalExposure()
    {
        var now = DateTimeOffset.UtcNow;
        var before = Position(1, 0.02m, -8m, 0m, 0m, now.AddSeconds(-1));
        var added = Position(2, 0.03m, -1m, 0m, 0m, now) with { PositionId = 2 };

        var events = new PositionSnapshotDiffer().Diff([before], [before with { CapturedAtUtc = now }, added], now);

        Assert.Contains(events, item => item.Kind == TradeDomainEventKind.Opened && item.PositionId == 2);
        Assert.Contains(events, item => item.Kind == TradeDomainEventKind.AddingToLoss && item.VolumeDelta == 0.03m);
    }

    private static PositionSnapshot Position(
        long ticket,
        decimal volume,
        decimal profit,
        decimal stopLoss,
        decimal takeProfit,
        DateTimeOffset capturedAt) =>
        new(ticket, ticket, "XAUUSD.s", TradeSide.Buy, volume, 3352m, 3350m, profit, stopLoss, takeProfit,
            capturedAt.AddMinutes(-5), capturedAt);
}
