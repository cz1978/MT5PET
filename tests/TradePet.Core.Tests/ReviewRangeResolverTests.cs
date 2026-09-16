using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class ReviewRangeResolverTests
{
    private static readonly DateOnly Current = new(2026, 9, 6);
    private readonly ReviewRangeResolver _resolver = new();

    [Fact]
    public void Presets_UseServerCalendarAndInclusiveRollingWindows()
    {
        Assert.Equal(new ReviewDateRange(Current, Current),
            _resolver.Resolve(ReviewPeriodPreset.Today, Current));
        Assert.Equal(new ReviewDateRange(new DateOnly(2026, 8, 31), Current),
            _resolver.Resolve(ReviewPeriodPreset.ThisWeek, Current));
        Assert.Equal(new ReviewDateRange(new DateOnly(2026, 9, 1), Current),
            _resolver.Resolve(ReviewPeriodPreset.ThisMonth, Current));
        Assert.Equal(new ReviewDateRange(Current.AddDays(-29), Current),
            _resolver.Resolve(ReviewPeriodPreset.LastThirtyDays, Current));
        Assert.Equal(new ReviewDateRange(Current.AddDays(-89), Current),
            _resolver.Resolve(ReviewPeriodPreset.LastNinetyDays, Current));
        Assert.Equal(new ReviewDateRange(new DateOnly(2026, 1, 1), Current),
            _resolver.Resolve(ReviewPeriodPreset.ThisYear, Current));
    }

    [Fact]
    public void AllHistory_UsesAvailableCompleteTradeDatesAndClampsFutureEnd()
    {
        var result = _resolver.Resolve(
            ReviewPeriodPreset.AllHistory,
            Current,
            new DateOnly(2022, 3, 4),
            Current.AddDays(10));

        Assert.Equal(new ReviewDateRange(new DateOnly(2022, 3, 4), Current), result);
    }

    [Fact]
    public void AllHistory_WithoutCompleteTradesFallsBackToCurrentDate()
    {
        Assert.Equal(new ReviewDateRange(Current, Current),
            _resolver.Resolve(ReviewPeriodPreset.AllHistory, Current));
    }

    [Fact]
    public void CustomRange_ClampsBothFutureEndpointsBeforeNormalizingOrder()
    {
        var result = _resolver.Resolve(
            ReviewPeriodPreset.Custom,
            Current,
            customFrom: Current.AddDays(20),
            customTo: Current.AddDays(-5));

        Assert.Equal(new ReviewDateRange(Current.AddDays(-5), Current), result);
    }
}
