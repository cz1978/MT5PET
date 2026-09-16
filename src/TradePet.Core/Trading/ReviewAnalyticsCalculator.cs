using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class ReviewAnalyticsCalculator
{
    private const decimal BreakevenEpsilon = 0.01m;

    public ReviewSnapshot Calculate(
        ReviewFilter filter,
        IEnumerable<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata = null,
        IReadOnlyDictionary<long, TradeExcursion>? excursions = null,
        IReadOnlyCollection<BehaviorEvaluation>? evaluations = null,
        IReadOnlyCollection<DealRecord>? deals = null)
    {
        ArgumentNullException.ThrowIfNull(trades);
        var selected = trades
            .Where(trade => trade.IsComplete && trade.ClosedAtUtc is not null)
            .Where(trade => trade.AccountKey == filter.AccountKey)
            .Where(trade => trade.CloseServerDate >= filter.FromServerDate && trade.CloseServerDate <= filter.ToServerDate)
            .Where(trade => filter.Symbol is null || string.Equals(trade.Symbol, filter.Symbol, StringComparison.OrdinalIgnoreCase))
            .Where(trade => filter.Side is null || trade.Side == filter.Side)
            .Where(trade => MatchesMetadata(trade, filter, metadata))
            .OrderBy(trade => trade.ClosedAtUtc)
            .ToArray();

        var summary = CalculateSummary(selected, metadata, excursions);
        var behaviorItems = evaluations ?? [];
        var behavior = new BehaviorSummary(
            behaviorItems.Any(item => item.Level == BehaviorRiskLevel.Critical)
                ? BehaviorRiskLevel.Critical
                : behaviorItems.Any(item => item.Level == BehaviorRiskLevel.Attention)
                    ? BehaviorRiskLevel.Attention
                    : behaviorItems.Count == 0 ? BehaviorRiskLevel.Observing : BehaviorRiskLevel.Normal,
            behaviorItems.OrderByDescending(item => item.ObservedAtUtc).ToArray(),
            0,
            false);
        return new ReviewSnapshot(
            filter,
            summary,
            behavior,
            GroupBy(selected, trade => trade.Side == TradeSide.Buy ? "买入" : "卖出", excursions, deals),
            GroupBy(selected, trade => trade.Symbol, excursions, deals),
            GroupBy(selected, trade => GetChineseWeekday(ToServerTime(trade.OpenedAtUtc, filter.ServerUtcOffsetSeconds)), excursions, deals),
            GroupBy(selected, trade => ToServerTime(trade.OpenedAtUtc, filter.ServerUtcOffsetSeconds).Hour.ToString("00") + "时", excursions, deals),
            GroupBy(selected, trade => DurationBucket(trade), excursions, deals),
            GroupBy(selected, trade => MetadataValue(metadata, trade.PositionId, item => item.Strategy, "未分类"), excursions, deals),
            GroupBy(selected, trade => MetadataValue(metadata, trade.PositionId, item => item.Setup, "未分类"), excursions, deals),
            GroupByTags(selected, metadata, excursions, deals),
            selected);
    }

    public static PerformanceSummary CalculateSummary(
        IReadOnlyCollection<TradeRecord> selected,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata = null,
        IReadOnlyDictionary<long, TradeExcursion>? excursions = null)
    {
        var wins = selected.Where(trade => trade.NetPnl > BreakevenEpsilon).ToArray();
        var losses = selected.Where(trade => trade.NetPnl < -BreakevenEpsilon).ToArray();
        var breakeven = selected.Count - wins.Length - losses.Length;
        var net = selected.Sum(trade => trade.NetPnl);
        var grossWin = wins.Sum(trade => trade.NetPnl);
        var grossLoss = losses.Sum(trade => trade.NetPnl);
        var drawdowns = CalculateRealizedDrawdowns(selected);
        var reliableExcursions = excursions is null
            ? []
            : selected.Where(trade => excursions.TryGetValue(trade.PositionId, out var item) && item.IsReliable)
                .Select(trade => excursions[trade.PositionId])
                .ToArray();
        var riskCoveredTrades = excursions is null
            ? []
            : selected.Where(trade => excursions.TryGetValue(trade.PositionId, out var item) && item.HasReliableInitialRisk)
                .ToArray();
        var activeDays = selected.GroupBy(trade => trade.CloseServerDate!.Value).ToArray();
        var winningDays = activeDays.Count(day => day.Sum(trade => trade.NetPnl) > BreakevenEpsilon);

        var holding = selected.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)selected.Average(trade => (trade.ClosedAtUtc!.Value - trade.OpenedAtUtc).Ticks));
        var maxLoss = drawdowns.Count == 0 ? 0m : drawdowns.Max(item => item.DrawdownAmount);
        var maxDrawdownPercentage = drawdowns.Count == 0
            ? (decimal?)null
            : drawdowns.Max(item => item.DrawdownPercentage ?? 0m);
        var netProfit = selected.Sum(trade => trade.NetPnl);
        var averageMaeR = AverageOrNull(reliableExcursions
            .Where(item => item.InitialRiskAmount is > 0m)
            .Select(item => (decimal?)(Math.Max(0m, -item.MinimumPnl) / item.InitialRiskAmount!.Value)));
        var averageMfeR = AverageOrNull(reliableExcursions
            .Where(item => item.InitialRiskAmount is > 0m)
            .Select(item => (decimal?)(Math.Max(0m, item.MaximumPnl) / item.InitialRiskAmount!.Value)));
        return new PerformanceSummary(
            selected.Count,
            wins.Length,
            losses.Length,
            breakeven,
            net,
            wins.Length + losses.Length == 0 ? 0m : wins.Length * 100m / (wins.Length + losses.Length),
            grossLoss < -BreakevenEpsilon ? grossWin / Math.Abs(grossLoss) : null,
            selected.Count == 0 ? 0m : net / selected.Count,
            wins.Length == 0 ? 0m : grossWin / wins.Length,
            losses.Length == 0 ? 0m : grossLoss / losses.Length,
            wins.Length == 0 || losses.Length == 0 ? null : (grossWin / wins.Length) / Math.Abs(grossLoss / losses.Length),
            maxLoss,
            drawdowns.Count == 0 ? 0m : drawdowns.Average(item => item.DrawdownAmount),
            maxLoss <= BreakevenEpsilon ? null : netProfit / maxLoss,
            MaximumStreak(selected, positive: true),
            MaximumStreak(selected, positive: false),
            holding,
            selected.Sum(trade => trade.OpeningVolume),
            selected.Count == 0 ? 0m : selected.Average(trade => trade.OpeningVolume),
            selected.Count == 0 ? 0m : selected.Max(trade => trade.OpeningVolume),
            activeDays.Length == 0 ? 0m : winningDays * 100m / activeDays.Length,
            activeDays.Count(day => Math.Abs(day.Sum(trade => trade.NetPnl)) <= BreakevenEpsilon),
            AverageOrNull(riskCoveredTrades.Select(item => (decimal?)excursions![item.PositionId].PlannedRiskMultiple)),
            AverageOrNull(riskCoveredTrades.Select(item =>
                (decimal?)(item.NetPnl / excursions![item.PositionId].InitialRiskAmount!.Value))),
            AverageOrNull(reliableExcursions.Select(item => (decimal?)Math.Max(0m, -item.MinimumPnl))),
            AverageOrNull(reliableExcursions.Select(item => (decimal?)Math.Max(0m, item.MaximumPnl))),
            selected.Count == 0 ? 0m : riskCoveredTrades.Length * 100m / selected.Count,
            drawdowns.Count == 0 ? null : maxDrawdownPercentage,
            averageMaeR,
            averageMfeR);
    }

    public static IReadOnlyList<DrawdownEpisode> CalculateRealizedDrawdowns(
        IReadOnlyCollection<TradeRecord> selected)
    {
        var episodes = new List<DrawdownEpisode>();
        if (selected.Count == 0)
        {
            return episodes;
        }

        var ordered = selected.OrderBy(trade => trade.ClosedAtUtc).ToArray();
        var curve = 0m;
        var peak = 0m;
        DateTimeOffset peakAt = ordered[0].ClosedAtUtc!.Value;
        DateTimeOffset? troughAt = null;
        decimal trough = peak;
        foreach (var trade in ordered)
        {
            curve += trade.NetPnl;
            var at = trade.ClosedAtUtc!.Value;
            if (curve >= peak)
            {
                if (troughAt is not null)
                {
                    episodes.Add(CreateEpisode(ordered[0].AccountKey, peakAt, troughAt.Value, at, peak, trough));
                }

                if (curve > peak)
                {
                    peak = curve;
                    peakAt = at;
                }
                troughAt = null;
                trough = curve;
                continue;
            }

            if (curve < peak && (troughAt is null || curve < trough))
            {
                troughAt = at;
                trough = curve;
            }
        }

        if (troughAt is not null)
        {
            episodes.Add(CreateEpisode(ordered[0].AccountKey, peakAt, troughAt.Value, null, peak, trough));
        }

        return episodes;
    }

    public static IReadOnlyList<GroupMetricRow> CalculateNamedGroups(
        IEnumerable<(string Group, TradeRecord Trade)> memberships,
        IReadOnlyDictionary<long, TradeExcursion>? excursions = null,
        IReadOnlyCollection<DealRecord>? deals = null) =>
        memberships
            .GroupBy(item => item.Group, item => item.Trade, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToGroupMetric(
                group.Key,
                group.DistinctBy(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray(),
                excursions,
                deals))
            .ToArray();

    private static DrawdownEpisode CreateEpisode(
        string accountKey,
        DateTimeOffset peakAt,
        DateTimeOffset troughAt,
        DateTimeOffset? recoveredAt,
        decimal peak,
        decimal trough) => new(
        $"realized:{accountKey}:{peakAt.Ticks}:{troughAt.Ticks}",
        accountKey,
        "realized",
        DateOnly.FromDateTime(peakAt.UtcDateTime),
        recoveredAt is null ? null : DateOnly.FromDateTime(recoveredAt.Value.UtcDateTime),
        peakAt,
        troughAt,
        recoveredAt,
        peak,
        trough,
        peak - trough,
        peak > 0m ? (peak - trough) * 100m / peak : null);

    private static bool MatchesMetadata(
        TradeRecord trade,
        ReviewFilter filter,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata)
    {
        if (filter.Strategy is null && filter.Setup is null && filter.Tag is null)
        {
            return true;
        }

        if (metadata is null || !metadata.TryGetValue(trade.PositionId, out var value))
        {
            return false;
        }

        return (filter.Strategy is null || string.Equals(value.Strategy, filter.Strategy, StringComparison.OrdinalIgnoreCase)) &&
               (filter.Setup is null || string.Equals(value.Setup, filter.Setup, StringComparison.OrdinalIgnoreCase)) &&
               (filter.Tag is null || value.Tags.Any(tag => string.Equals(tag, filter.Tag, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<GroupMetricRow> GroupBy(
        IReadOnlyCollection<TradeRecord> trades,
        Func<TradeRecord, string> keySelector,
        IReadOnlyDictionary<long, TradeExcursion>? excursions,
        IReadOnlyCollection<DealRecord>? deals) =>
        trades.GroupBy(keySelector)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToGroupMetric(group.Key, group.ToArray(), excursions, deals))
            .ToArray();

    private static IReadOnlyList<GroupMetricRow> GroupByTags(
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata,
        IReadOnlyDictionary<long, TradeExcursion>? excursions,
        IReadOnlyCollection<DealRecord>? deals) =>
        trades.SelectMany(trade => metadata is not null && metadata.TryGetValue(trade.PositionId, out var item)
                ? item.Tags.Select(tag => (Tag: tag, Trade: trade))
                : [])
            .GroupBy(item => item.Tag, item => item.Trade, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToGroupMetric(group.Key,
                group.DistinctBy(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray(), excursions, deals))
            .ToArray();

    private static GroupMetricRow ToGroupMetric(
        string group,
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeExcursion>? excursions,
        IReadOnlyCollection<DealRecord>? deals)
    {
        var wins = trades.Count(trade => trade.NetPnl > BreakevenEpsilon);
        var losses = trades.Where(trade => trade.NetPnl < -BreakevenEpsilon).ToArray();
        var grossLoss = losses.Sum(trade => trade.NetPnl);
        var ids = trades.Select(item => item.PositionId).ToHashSet();
        var groupDeals = deals?.Where(item => ids.Contains(item.PositionId)).ToArray() ?? [];
        var riskTrades = excursions is null
            ? []
            : trades.Where(item => excursions.TryGetValue(item.PositionId, out var excursion) &&
                                   excursion.HasReliableInitialRisk)
                .ToArray();
        var actualRiskMultiples = riskTrades
            .Select(item => item.NetPnl / excursions![item.PositionId].InitialRiskAmount!.Value)
            .ToArray();
        return new GroupMetricRow(
            group,
            trades.Count,
            wins + losses.Length == 0 ? 0m : wins * 100m / (wins + losses.Length),
            trades.Sum(trade => trade.NetPnl),
            grossLoss < -BreakevenEpsilon ? trades.Where(trade => trade.NetPnl > BreakevenEpsilon).Sum(trade => trade.NetPnl) / Math.Abs(grossLoss) : null,
            trades.Count == 0 ? 0m : trades.Sum(trade => trade.NetPnl) / trades.Count,
            trades.Count == 0 ? 0m : trades.Average(item => item.NetPnl),
            actualRiskMultiples.Length == 0 ? null : actualRiskMultiples.Average(),
            riskTrades.Length,
            trades.Count == 0 ? 0m : riskTrades.Length * 100m / trades.Count,
            groupDeals.Sum(item => item.Commission),
            groupDeals.Sum(item => item.Swap),
            groupDeals.Sum(item => item.Fee),
            trades.Select(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray());
    }

    private static int MaximumStreak(IReadOnlyCollection<TradeRecord> trades, bool positive)
    {
        var current = 0;
        var maximum = 0;
        foreach (var trade in trades.OrderBy(item => item.ClosedAtUtc))
        {
            var matches = positive ? trade.NetPnl > BreakevenEpsilon : trade.NetPnl < -BreakevenEpsilon;
            current = matches ? current + 1 : 0;
            maximum = Math.Max(maximum, current);
        }

        return maximum;
    }

    private static string MetadataValue(
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata,
        long positionId,
        Func<TradeReviewMetadata, string> selector,
        string fallback) =>
        metadata is not null && metadata.TryGetValue(positionId, out var item) && !string.IsNullOrWhiteSpace(selector(item))
            ? selector(item)
            : fallback;

    private static decimal? AverageOrNull(IEnumerable<decimal?> values)
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    private static string DurationBucket(TradeRecord trade)
    {
        var duration = trade.ClosedAtUtc!.Value - trade.OpenedAtUtc;
        if (duration < TimeSpan.FromMinutes(5)) return "不足5分钟";
        if (duration < TimeSpan.FromMinutes(15)) return "5–15分钟";
        if (duration < TimeSpan.FromHours(1)) return "15–60分钟";
        if (duration < TimeSpan.FromHours(4)) return "1–4小时";
        return "4小时以上";
    }

    private static string GetChineseWeekday(DateTimeOffset value) => value.DayOfWeek switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };

    private static DateTimeOffset ToServerTime(DateTimeOffset value, int offsetSeconds) =>
        value.ToUniversalTime().AddSeconds(offsetSeconds);
}
