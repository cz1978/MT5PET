using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class PriceDistancePolicyTests
{
    [Theory]
    [InlineData("XAUUSD", "0.01", "0.01", "2.00")]
    [InlineData("EURUSD", "0.00001", "0.00001", "0.00200")]
    [InlineData("USDJPY", "0.001", "0.001", "0.200")]
    [InlineData("INDEX", "0.01", "0.25", "50.00")]
    public void PriceDistance_UsesTradableTickSizeAcrossQuoteScales(
        string symbol,
        string pointText,
        string tickText,
        string expectedText)
    {
        var specification = new SymbolSpecification(
            symbol,
            decimal.Parse(pointText, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(tickText, System.Globalization.CultureInfo.InvariantCulture),
            5);

        var distance = PriceDistancePolicy.ToPriceDistance(
            200m,
            specification,
            fallbackDistance: 2m);

        Assert.Equal(
            decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture),
            distance);
    }
}
