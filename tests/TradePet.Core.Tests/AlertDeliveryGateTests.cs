using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class AlertDeliveryGateTests
{
    [Fact]
    public void CommittedAlert_IsDeduplicatedOnlyWithinItsAccountAndDate()
    {
        var gate = new AlertDeliveryGate();
        var date = new DateOnly(2026, 9, 6);

        Assert.True(gate.CanAttempt("Broker|1", date, "same-id", AlertPriority.Important, null));
        Assert.True(gate.Commit("Broker|1", date, "same-id"));

        Assert.False(gate.CanAttempt("Broker|1", date, "same-id", AlertPriority.Important, null));
        Assert.True(gate.CanAttempt("Broker|2", date, "same-id", AlertPriority.Important, null));
        Assert.True(gate.CanAttempt("Broker|1", date.AddDays(1), "same-id", AlertPriority.Important, null));
    }

    [Fact]
    public void HigherActivePriority_RejectsWithoutConsumingDeduplicationKey()
    {
        var gate = new AlertDeliveryGate();
        var date = new DateOnly(2026, 9, 6);

        Assert.False(gate.CanAttempt(
            "Broker|1", date, "alert", AlertPriority.Normal, AlertPriority.Critical));
        Assert.True(gate.CanAttempt(
            "Broker|1", date, "alert", AlertPriority.Critical, AlertPriority.Critical));
        Assert.True(gate.Commit("Broker|1", date, "alert"));
    }

    [Fact]
    public void DisabledDeduplication_AllowsReplayEveryTime()
    {
        var gate = new AlertDeliveryGate();
        var date = new DateOnly(2026, 9, 6);

        Assert.True(gate.Commit("replay", date, "fixed", deduplicate: false));
        Assert.True(gate.Commit("replay", date, "fixed", deduplicate: false));
        Assert.True(gate.CanAttempt(
            "replay", date, "fixed", AlertPriority.Critical, null, deduplicate: false));
    }

    [Fact]
    public void HigherRisk_CanBeDeliveredAgainForSameEvent()
    {
        var gate = new AlertDeliveryGate();
        var date = new DateOnly(2026, 9, 15);
        Assert.True(gate.Commit("Broker|1", date, "trade:42", priority: AlertPriority.Normal));
        Assert.True(gate.CanAttempt("Broker|1", date, "trade:42", AlertPriority.Critical, null));
        Assert.True(gate.Commit("Broker|1", date, "trade:42", priority: AlertPriority.Critical));
        Assert.False(gate.CanAttempt("Broker|1", date, "trade:42", AlertPriority.Important, null));
    }
}
