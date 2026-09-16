using TradePet.Core.Pet;
using TradePet.Core.Domain;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class PetAdviceCatalogTests
{
    [Theory]
    [InlineData(PetAdviceContext.Flat)]
    [InlineData(PetAdviceContext.Holding)]
    [InlineData(PetAdviceContext.CoolingDown)]
    [InlineData(PetAdviceContext.ProtectingProfit)]
    public void Select_ReturnsActionableChineseAdvice(PetAdviceContext context)
    {
        var advice = new PetAdviceCatalog().Select(context, 0);

        Assert.False(string.IsNullOrWhiteSpace(advice.Headline));
        Assert.False(string.IsNullOrWhiteSpace(advice.Details));
        Assert.Matches("[\u4e00-\u9fff]", advice.Headline);
        Assert.Matches("[\u4e00-\u9fff]", advice.Details);
    }

    [Fact]
    public void Select_DoesNotRepeatPreviousAdvice()
    {
        var catalog = new PetAdviceCatalog();
        var first = catalog.Select(PetAdviceContext.Flat, 0);

        var next = catalog.Select(PetAdviceContext.Flat, 0, first.Key);

        Assert.NotEqual(first.Key, next.Key);
    }

    [Fact]
    public void Select_SkipsAllRecentlyShownAdvice()
    {
        var catalog = new PetAdviceCatalog();
        var recent = Enumerable.Range(0, 8)
            .Select(index => catalog.Select(PetAdviceContext.Flat, index).Key)
            .ToArray();

        var next = catalog.Select(PetAdviceContext.Flat, 0, recent);

        Assert.DoesNotContain(next.Key, recent);
    }

    [Fact]
    public void Select_UsesTheRequestedTradingContext()
    {
        var catalog = new PetAdviceCatalog();

        Assert.Contains("顺势", catalog.Select(PetAdviceContext.Flat, 0).Headline);
        Assert.Contains("止损", catalog.Select(PetAdviceContext.Holding, 0).Headline);
        Assert.Contains("停手", catalog.Select(PetAdviceContext.CoolingDown, 0).Headline);
        Assert.Contains("盈利", catalog.Select(PetAdviceContext.ProtectingProfit, 0).Headline);
    }

    [Fact]
    public void Catalog_HasEnoughUniqueBubbleSizedAdviceInEveryContext()
    {
        var catalog = new PetAdviceCatalog();
        var all = Enum.GetValues<PetAdviceContext>()
            .SelectMany(context => Enumerable.Range(0, catalog.Count(context))
                .Select(index => catalog.Select(context, index)))
            .ToArray();

        Assert.All(Enum.GetValues<PetAdviceContext>(), context => Assert.True(catalog.Count(context) >= 32));
        Assert.Equal(all.Length, all.Select(advice => advice.Key).Distinct().Count());
        Assert.Equal(all.Length, all.Select(advice => advice.Headline).Distinct().Count());
        Assert.All(all, advice => Assert.InRange(advice.Headline.Length, 4, 20));
        Assert.All(all, advice => Assert.InRange(advice.Details.Length, 12, 48));
    }

    [Fact]
    public void Select_DoesNotUseFactSpecificAdviceWithoutMatchingSituation()
    {
        var catalog = new PetAdviceCatalog();
        var protecting = Enumerable.Range(0, catalog.Count(PetAdviceContext.ProtectingProfit))
            .Select(index => catalog.Select(
                PetAdviceContext.ProtectingProfit,
                index,
                [],
                PetAdviceSituation.None))
            .Select(advice => advice.Key)
            .Distinct()
            .ToArray();
        var holding = Enumerable.Range(0, catalog.Count(PetAdviceContext.Holding))
            .Select(index => catalog.Select(
                PetAdviceContext.Holding,
                index,
                [],
                PetAdviceSituation.None))
            .Select(advice => advice.Key)
            .Distinct()
            .ToArray();

        Assert.DoesNotContain("profit-confidence", protecting);
        Assert.DoesNotContain("profit-target", protecting);
        Assert.DoesNotContain("profit-drawback", protecting);
        Assert.DoesNotContain("holding-add", holding);
        Assert.DoesNotContain("holding-profit", holding);
    }

    [Theory]
    [InlineData(PetAdviceContext.ProtectingProfit, PetAdviceSituation.WinningStreak, "profit-confidence")]
    [InlineData(PetAdviceContext.ProtectingProfit, PetAdviceSituation.DailyTargetReached, "profit-target")]
    [InlineData(PetAdviceContext.ProtectingProfit, PetAdviceSituation.ProfitGiveback, "profit-drawback")]
    [InlineData(PetAdviceContext.Holding, PetAdviceSituation.HoldingLoss, "holding-add")]
    [InlineData(PetAdviceContext.Holding, PetAdviceSituation.HoldingProfit, "holding-profit")]
    public void Select_UnlocksAdviceWhenItsSituationIsTrue(
        PetAdviceContext context,
        PetAdviceSituation situation,
        string expectedKey)
    {
        var catalog = new PetAdviceCatalog();
        var selected = Enumerable.Range(0, catalog.Count(context))
            .Select(index => catalog.Select(context, index, [], situation))
            .Select(advice => advice.Key)
            .Distinct()
            .ToArray();

        Assert.Contains(expectedKey, selected);
    }

    [Fact]
    public void CountConsecutiveWins_UsesCurrentServerDateAndBreakevenBreaksTheStreak()
    {
        var date = new DateOnly(2026, 9, 2);
        var trades = new[]
        {
            Trade(1, date, 2m, 9),
            Trade(2, date, 0m, 10),
            Trade(3, date, 3m, 11),
            Trade(4, date, 4m, 12),
            Trade(5, date.AddDays(-1), 5m, 13),
        };

        var count = PetAdviceCatalog.CountConsecutiveWins(trades, date);

        Assert.Equal(2, count);
    }

    private static TradeRecord Trade(long positionId, DateOnly date, decimal pnl, int hour)
    {
        var opened = new DateTimeOffset(date.Year, date.Month, date.Day, hour, 0, 0, TimeSpan.Zero);
        return new TradeRecord(
            "WeTrade|1",
            positionId,
            "XAUUSD.s",
            TradeSide.Buy,
            opened,
            opened.AddMinutes(1),
            date,
            date,
            4400m,
            4401m,
            0.01m,
            0.01m,
            0m,
            pnl,
            true);
    }
}
