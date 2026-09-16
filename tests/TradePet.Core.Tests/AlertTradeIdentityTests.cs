using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class AlertTradeIdentityTests
{
    [Theory]
    [InlineData("add-loss:42", "42")]
    [InlineData("open:Broker|100:73", "73")]
    public void ExplicitTradeAlerts_ReturnPositionIdentity(string alertId, string expected) =>
        Assert.Equal(expected, AlertTradeIdentity.TryExtract(alertId));

    [Theory]
    [InlineData("daily-risk")]
    [InlineData("loss-zone:today")]
    [InlineData("add-loss:not-a-position")]
    public void NonTradeAlerts_DoNotReturnIdentity(string alertId) =>
        Assert.Null(AlertTradeIdentity.TryExtract(alertId));
}
