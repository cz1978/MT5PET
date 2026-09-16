using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed record ReviewDateRange(DateOnly From, DateOnly To);

public sealed class ReviewRangeResolver
{
    public ReviewDateRange Resolve(
        ReviewPeriodPreset preset,
        DateOnly currentServerDate,
        DateOnly? availableFrom = null,
        DateOnly? availableTo = null,
        DateOnly? customFrom = null,
        DateOnly? customTo = null)
    {
        var from = currentServerDate.AddDays(-29);
        var to = currentServerDate;
        switch (preset)
        {
            case ReviewPeriodPreset.Today:
                from = currentServerDate;
                break;
            case ReviewPeriodPreset.ThisWeek:
                var daysFromMonday = ((int)currentServerDate.DayOfWeek + 6) % 7;
                from = currentServerDate.AddDays(-daysFromMonday);
                break;
            case ReviewPeriodPreset.ThisMonth:
                from = new DateOnly(currentServerDate.Year, currentServerDate.Month, 1);
                break;
            case ReviewPeriodPreset.LastNinetyDays:
                from = currentServerDate.AddDays(-89);
                break;
            case ReviewPeriodPreset.ThisYear:
                from = new DateOnly(currentServerDate.Year, 1, 1);
                break;
            case ReviewPeriodPreset.AllHistory:
                from = availableFrom ?? currentServerDate;
                to = availableTo ?? currentServerDate;
                break;
            case ReviewPeriodPreset.Custom:
                from = customFrom ?? from;
                to = customTo ?? to;
                break;
            case ReviewPeriodPreset.LastThirtyDays:
            default:
                break;
        }

        from = from > currentServerDate ? currentServerDate : from;
        to = to > currentServerDate ? currentServerDate : to;
        return from <= to
            ? new ReviewDateRange(from, to)
            : new ReviewDateRange(to, from);
    }
}
