using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradePet.Core.Domain;
using TradePet.Core.Trading;

namespace TradePet.Core.Review;

public sealed class ReviewWorkspaceCalculator
{
    private const decimal BreakevenEpsilon = 0.01m;
    private static readonly TimeSpan MaximumCashFlowBoundaryGap = TimeSpan.FromMinutes(15);

    public IReadOnlyList<TradeRecord> FilterAndSort(
        ReviewWorkspaceFilter filter,
        IEnumerable<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeReviewMetadata> metadata,
        IReadOnlyDictionary<long, TradeReviewDocument> documents,
        IReadOnlyCollection<TradeRuleAssessment> assessments,
        IReadOnlyCollection<TradeCampaign> campaigns,
        IReadOnlyCollection<BehaviorOccurrence> behaviors,
        IReadOnlyCollection<DealRecord>? deals = null,
        IReadOnlyDictionary<long, TradeExcursion>? excursions = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var tags = NormalizeTags(filter.Tags);
        var campaignMembers = filter.CampaignId is null
            ? null
            : campaigns.FirstOrDefault(item => item.Id == filter.CampaignId && item.AccountKey == filter.AccountKey)
                ?.PositionIds.ToHashSet();
        var behaviorTrades = behaviors
            .SelectMany(item => item.TradeLinks)
            .Where(item => item.Role == BehaviorTradeRole.Trigger)
            .Select(item => item.TradeKey)
            .ToHashSet();
        var dealTickets = string.IsNullOrWhiteSpace(filter.Search) || deals is null
            ? new Dictionary<long, string[]>()
            : deals.GroupBy(item => item.PositionId)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Ticket.ToString()).ToArray());
        var assessmentTrades = filter.Assessment is null
            ? null
            : assessments.Where(item => item.Status == filter.Assessment)
                .Select(item => item.TradeKey)
                .ToHashSet();

        var selected = trades
            .Where(trade => trade.IsComplete && trade.ClosedAtUtc is not null && trade.CloseServerDate is not null)
            .Where(trade => trade.AccountKey == filter.AccountKey)
            .Where(trade => trade.CloseServerDate >= filter.FromServerDate && trade.CloseServerDate <= filter.ToServerDate)
            .Where(trade => filter.Symbol is null || string.Equals(filter.Symbol.Trim(), trade.Symbol, StringComparison.OrdinalIgnoreCase))
            .Where(trade => filter.Side is null || trade.Side == filter.Side)
            .Where(trade => MatchesMetadata(trade, filter, tags, metadata))
            .Where(trade => filter.Status is null || GetStatus(trade, documents) == filter.Status)
            .Where(trade => assessmentTrades is null || assessmentTrades.Contains(new TradeKey(trade.AccountKey, trade.PositionId)))
            .Where(trade => campaignMembers is null || campaignMembers.Contains(trade.PositionId))
            .Where(trade => MatchesSearch(trade, filter.Search, metadata, documents, dealTickets))
            .ToArray();

        return filter.Sort switch
        {
            ReviewSortOrder.ClosedAscending => selected.OrderBy(item => item.ClosedAtUtc).ToArray(),
            ReviewSortOrder.LargestLoss => selected.OrderBy(item => item.NetPnl).ThenByDescending(item => item.ClosedAtUtc).ToArray(),
            ReviewSortOrder.LargestGiveback => selected.OrderByDescending(item =>
                    excursions is not null && excursions.TryGetValue(item.PositionId, out var excursion)
                        ? Math.Max(0m, excursion.MaximumPnl - item.NetPnl)
                        : 0m)
                .ThenBy(item => item.NetPnl).ToArray(),
            ReviewSortOrder.BehaviorFirst => selected.OrderByDescending(item =>
                    behaviorTrades.Contains(new TradeKey(item.AccountKey, item.PositionId)))
                .ThenByDescending(item => item.ClosedAtUtc).ToArray(),
            ReviewSortOrder.OldestPending => selected.OrderBy(item => GetStatus(item, documents) == ReviewCompletionStatus.Reviewed)
                .ThenBy(item => item.ClosedAtUtc).ToArray(),
            _ => selected.OrderByDescending(item => item.ClosedAtUtc).ToArray(),
        };
    }

    public IReadOnlyList<ReviewCalendarDay> BuildCalendar(
        string accountKey,
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyDictionary<long, TradeReviewDocument> documents,
        IReadOnlyDictionary<DateOnly, DailyJournal> journals,
        IReadOnlyCollection<DateOnly>? dataGapDates,
        int serverUtcOffsetSeconds)
    {
        var offset = TimeSpan.FromSeconds(serverUtcOffsetSeconds);
        var cashByDate = deals
            .GroupBy(item => DateOnly.FromDateTime(item.OccurredAtUtc.ToOffset(offset).DateTime))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.NetPnl));
        var openingByDate = trades.GroupBy(item => item.OpenServerDate)
            .ToDictionary(group => group.Key, group => group.Count());
        var completeByDate = trades.Where(item => item.IsComplete && item.CloseServerDate is not null)
            .GroupBy(item => item.CloseServerDate!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var gaps = dataGapDates?.ToHashSet() ?? [];
        var result = new List<ReviewCalendarDay>();
        for (var date = from; ; date = date.AddDays(1))
        {
            var completed = completeByDate.GetValueOrDefault(date) ?? [];
            result.Add(new ReviewCalendarDay(
                date,
                cashByDate.GetValueOrDefault(date),
                openingByDate.GetValueOrDefault(date),
                completed.Length,
                completed.Count(item => GetStatus(item, documents) is not ReviewCompletionStatus.Reviewed),
                journals.ContainsKey(date),
                gaps.Contains(date)));
            if (date == to)
            {
                break;
            }
        }
        return result;
    }

    public IReadOnlyDictionary<DateOnly, DailyReviewFacts> BuildDailyFacts(
        string accountKey,
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyDictionary<long, TradeReviewDocument> documents,
        IReadOnlyCollection<BehaviorOccurrence> behaviors,
        IReadOnlyDictionary<DateOnly, DailyState> dailyStates,
        int serverUtcOffsetSeconds)
    {
        var offset = TimeSpan.FromSeconds(serverUtcOffsetSeconds);
        var scopedTrades = trades.Where(item => item.AccountKey == accountKey).ToArray();
        var dealsByDate = deals
            .GroupBy(item => DateOnly.FromDateTime(item.OccurredAtUtc.ToOffset(offset).DateTime))
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.OccurredAtUtc).ToArray());
        var openingsByDate = scopedTrades.GroupBy(item => item.OpenServerDate)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.OpenedAtUtc).ToArray());
        var completionsByDate = scopedTrades
            .Where(item => item.IsComplete && item.CloseServerDate is not null && item.ClosedAtUtc is not null)
            .GroupBy(item => item.CloseServerDate!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.ClosedAtUtc).ToArray());
        var behaviorsByDate = behaviors.Where(item => item.AccountKey == accountKey)
            .GroupBy(item => item.ServerDate)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.EventAtUtc).ToArray());
        var activeByDate = new Dictionary<DateOnly, List<TradeRecord>>();
        foreach (var trade in scopedTrades)
        {
            var firstDate = trade.OpenServerDate < from ? from : trade.OpenServerDate;
            var lastTradeDate = trade.CloseServerDate ?? to;
            var lastDate = lastTradeDate > to ? to : lastTradeDate;
            if (firstDate > lastDate)
            {
                continue;
            }
            for (var activeDate = firstDate; ; activeDate = activeDate.AddDays(1))
            {
                if (!activeByDate.TryGetValue(activeDate, out var activeTrades))
                {
                    activeTrades = [];
                    activeByDate[activeDate] = activeTrades;
                }
                activeTrades.Add(trade);
                if (activeDate == lastDate)
                {
                    break;
                }
            }
        }
        var result = new Dictionary<DateOnly, DailyReviewFacts>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var dailyDeals = dealsByDate.GetValueOrDefault(date) ?? [];
            var opened = openingsByDate.GetValueOrDefault(date) ?? [];
            var completed = completionsByDate.GetValueOrDefault(date) ?? [];
            var dailyBehaviors = behaviorsByDate.GetValueOrDefault(date) ?? [];
            IReadOnlyCollection<TradeRecord> activeDuringDay = activeByDate.GetValueOrDefault(date) ?? [];
            dailyStates.TryGetValue(date, out var state);
            var targetAt = IsReliableTargetMilestone(state, date, offset) ? state!.TargetReachedAtUtc : null;
            var targetRuleVersion = targetAt is null ? string.Empty : state!.TargetRuleVersion;
            var targetAmount = targetAt is null ? null : state!.TargetAmountAtReach;
            var afterTargetTrades = targetAt is null
                ? []
                : opened.Where(item => item.OpenedAtUtc > targetAt.Value).ToArray();
            var afterTargetDeals = targetAt is null
                ? []
                : dailyDeals.Where(item => item.OccurredAtUtc > targetAt.Value).ToArray();
            var reviewedCount = completed.Count(item =>
                documents.TryGetValue(item.PositionId, out var document) &&
                document.Status == ReviewCompletionStatus.Reviewed);
            var lossThresholdAt = FindLossThresholdReachedAt(completed, state?.ConsecutiveLossThresholdAtObservation ?? 0);
            var openingsAfterLoss = lossThresholdAt is null
                ? (int?)null
                : opened.Count(item => item.OpenedAtUtc > lossThresholdAt.Value);
            var cooldown = dailyBehaviors.Where(item => item.Rule == BehaviorRuleKind.CooldownViolation).ToArray();
            var timeline = BuildDailyTimeline(
                date, offset, accountKey, activeDuringDay, opened, completed, dailyDeals, dailyBehaviors);

            result[date] = new DailyReviewFacts(
                date,
                CreateDailySourceVersion(date, dailyDeals, activeDuringDay, state),
                serverUtcOffsetSeconds,
                dailyDeals.Sum(item => item.NetPnl),
                completed.Sum(item => item.NetPnl),
                dailyDeals.Sum(item => item.Commission + item.Swap + item.Fee),
                opened.Length,
                completed.Length,
                reviewedCount,
                Percentage(reviewedCount, completed.Length),
                GetConsecutiveLossCount(completed),
                openingsAfterLoss,
                cooldown.Length,
                targetAt,
                targetRuleVersion,
                targetAmount,
                targetAt is null ? null : afterTargetTrades.Where(item => item.IsComplete).Sum(item => item.NetPnl),
                targetAt is null ? null : afterTargetTrades.Count(item => item.IsComplete),
                targetAt is null ? null : afterTargetTrades.Count(item => !item.IsComplete),
                targetAt is null ? null : afterTargetDeals.Sum(item => item.NetPnl),
                targetAt is null ? null : afterTargetDeals.Length,
                afterTargetTrades.Select(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray(),
                afterTargetDeals.Select(item => item.Ticket).ToArray(),
                timeline);
            if (date == to)
            {
                break;
            }
        }

        return result;
    }

    private static string CreateDailySourceVersion(
        DateOnly date,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyCollection<TradeRecord> trades,
        DailyState? state)
    {
        var builder = new StringBuilder(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var deal in deals.OrderBy(item => item.Ticket))
        {
            builder.Append("|D|").Append(deal.Ticket).Append('|').Append(deal.PositionId).Append('|')
                .Append(deal.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Profit.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Commission.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Swap.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Fee.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var trade in trades.OrderBy(item => item.PositionId))
        {
            builder.Append("|T|").Append(trade.PositionId).Append('|')
                .Append(trade.OpenedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(trade.ClosedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
                .Append(trade.NetPnl.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(trade.RemainingVolume.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(trade.IsComplete ? '1' : '0');
        }
        if (state is not null)
        {
            builder.Append("|S|").Append(state.TargetReachedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('|').Append(state.TargetRuleVersion).Append('|')
                .Append(state.TargetAmountAtReach?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    public TradeReviewBasis BuildTradeReviewBasis(
        TradeRecord trade,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyCollection<TradeRuleAssessment> assessments,
        TradeExcursion? excursion)
    {
        var source = new StringBuilder()
            .Append(trade.AccountKey).Append('|').Append(trade.PositionId).Append('|')
            .Append(trade.Symbol).Append('|').Append(trade.Side).Append('|')
            .Append(trade.OpenedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(trade.ClosedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
            .Append(trade.OpenServerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
            .Append(trade.CloseServerDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
            .Append(trade.EntryPrice.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(trade.ExitPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
            .Append(trade.OpeningVolume.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(trade.MaximumVolume.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(trade.RemainingVolume.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(trade.NetPnl.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(trade.IsComplete ? '1' : '0');
        foreach (var deal in deals.Where(item => item.PositionId == trade.PositionId).OrderBy(item => item.Ticket))
        {
            source.Append("|D|").Append(deal.Ticket).Append('|').Append(deal.OrderTicket).Append('|')
                .Append(deal.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.EntryKind).Append('|').Append(deal.Volume.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Price.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Profit.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Commission.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Swap.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(deal.Fee.ToString(CultureInfo.InvariantCulture));
        }
        if (excursion is not null)
        {
            source.Append("|E|").Append(excursion.InitialRiskAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('|').Append(excursion.ActualRiskMultiple?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('|').Append(excursion.AlgorithmVersion).Append('|').Append(excursion.MaximumGapMilliseconds)
                .Append('|').Append(excursion.IsReliable ? '1' : '0');
        }

        var rule = new StringBuilder(trade.AccountKey).Append('|').Append(trade.PositionId);
        foreach (var assessment in assessments.Where(item => item.TradeKey == new TradeKey(trade.AccountKey, trade.PositionId))
                     .OrderBy(item => item.PlaybookVersionId, StringComparer.Ordinal)
                     .ThenBy(item => item.RuleId, StringComparer.Ordinal))
        {
            rule.Append("|A|").Append(assessment.PlaybookVersionId).Append('|').Append(assessment.RuleId)
                .Append('|').Append(assessment.Status).Append('|').Append(assessment.Source)
                .Append('|').Append(assessment.EvidenceReference).Append('|').Append(assessment.Notes)
                .Append('|').Append(assessment.Revision);
        }
        return new TradeReviewBasis(
            $"trade-v1:{Hash(source)}",
            $"assessment-v1:{Hash(rule)}");
    }

    public IReadOnlyDictionary<long, TradeReviewDocument> ProjectReviewStatuses(
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyDictionary<long, TradeReviewDocument> documents,
        IReadOnlyCollection<TradeRuleAssessment> assessments,
        IReadOnlyDictionary<long, TradeExcursion> excursions,
        ReviewDataVersion legacyVersion)
    {
        var result = new Dictionary<long, TradeReviewDocument>(documents);
        if (!documents.Values.Any(item => item.Status == ReviewCompletionStatus.Reviewed))
        {
            return result;
        }
        var dealsByPosition = deals.GroupBy(item => item.PositionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<DealRecord>)group.ToArray());
        var assessmentsByTrade = assessments.GroupBy(item => item.TradeKey)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<TradeRuleAssessment>)group.ToArray());
        foreach (var trade in trades)
        {
            if (!documents.TryGetValue(trade.PositionId, out var document) ||
                document.Status != ReviewCompletionStatus.Reviewed)
            {
                continue;
            }
            excursions.TryGetValue(trade.PositionId, out var excursion);
            var key = new TradeKey(trade.AccountKey, trade.PositionId);
            var basis = BuildTradeReviewBasis(
                trade,
                dealsByPosition.GetValueOrDefault(trade.PositionId) ?? [],
                assessmentsByTrade.GetValueOrDefault(key) ?? [],
                excursion);
            var sourceChanged = document.ReviewedSourceVersion?.StartsWith("trade-v1:", StringComparison.Ordinal) == true
                ? document.ReviewedSourceVersion != basis.SourceVersion
                : document.ReviewedSourceVersion != legacyVersion.SourceVersion.ToString(CultureInfo.InvariantCulture);
            var ruleChanged = document.ReviewedRuleVersion?.StartsWith("assessment-v1:", StringComparison.Ordinal) == true
                ? document.ReviewedRuleVersion != basis.RuleVersion
                : document.ReviewedRuleVersion != legacyVersion.RuleVersion;
            if (sourceChanged || ruleChanged)
            {
                result[trade.PositionId] = document with
                {
                    Status = ReviewCompletionStatus.NeedsReview,
                    SourceVersion = basis.SourceVersion,
                    RuleVersion = basis.RuleVersion,
                };
            }
        }
        return result;
    }

    private static string Hash(StringBuilder builder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];

    public IReadOnlyList<ReviewCurvePoint> BuildRealizedCurve(IReadOnlyCollection<TradeRecord> trades)
    {
        var result = new List<ReviewCurvePoint>();
        var value = 0m;
        var peak = 0m;
        foreach (var trade in trades.Where(item => item.IsComplete && item.ClosedAtUtc is not null && item.CloseServerDate is not null)
                     .OrderBy(item => item.ClosedAtUtc))
        {
            value += trade.NetPnl;
            peak = Math.Max(peak, value);
            var drawdown = Math.Max(0m, peak - value);
            result.Add(new ReviewCurvePoint(
                $"{trade.AccountKey}:{trade.PositionId}",
                trade.CloseServerDate!.Value,
                trade.ClosedAtUtc!.Value,
                value,
                drawdown,
                peak > BreakevenEpsilon ? drawdown * 100m / peak : null,
                [new TradeKey(trade.AccountKey, trade.PositionId)]));
        }
        return result;
    }

    public ReviewEquityAnalysis BuildEquityAnalysis(
        string accountKey,
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<EquitySample> samples,
        IReadOnlyCollection<AccountCashFlow> cashFlows,
        int serverUtcOffsetSeconds)
    {
        var ordered = samples
            .Where(item => item.AccountKey == accountKey && item.ServerDate >= from && item.ServerDate <= to)
            .OrderBy(item => item.CapturedAtUtc)
            .ToArray();
        var offset = TimeSpan.FromSeconds(serverUtcOffsetSeconds);
        var relevantFlows = cashFlows
            .Where(item => item.AccountKey == accountKey && item.Amount != 0m)
            .Where(item =>
            {
                var date = DateOnly.FromDateTime(item.OccurredAtUtc.ToOffset(offset).DateTime);
                return date >= from && date <= to;
            })
            .OrderBy(item => item.OccurredAtUtc)
            .ToArray();
        if (ordered.Length == 0)
        {
            return new ReviewEquityAnalysis([], relevantFlows.Length, 0, 0, null, null, null, false,
                "当前范围没有账户净值采样。");
        }

        var points = new List<ReviewEquityPoint>(ordered.Length);
        var observedPeak = ordered[0].Equity;
        var observedMaxAmount = 0m;
        decimal? observedMaxPercentage = observedPeak > 0m ? 0m : null;
        var segment = 1;
        var unitized = 100m;
        var unitizedPeak = unitized;
        var unitizedMaxDrawdown = 0m;
        var verifiedFlowCount = 0;
        var hasInvalidBoundary = ordered[0].Equity <= 0m;
        points.Add(CreatePoint(ordered[0], false));

        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var intervalFlows = relevantFlows
                .Where(item => item.OccurredAtUtc > previous.CapturedAtUtc &&
                               item.OccurredAtUtc <= current.CapturedAtUtc)
                .ToArray();
            var boundaryVerified = intervalFlows.Length == 0 ||
                previous.Equity > 0m &&
                current.Equity - intervalFlows.Sum(item => item.Amount) > 0m &&
                intervalFlows.All(item =>
                    item.OccurredAtUtc - previous.CapturedAtUtc <= MaximumCashFlowBoundaryGap &&
                    current.CapturedAtUtc - item.OccurredAtUtc <= MaximumCashFlowBoundaryGap);
            var startsAfterGap = false;
            if (previous.Equity <= 0m || !boundaryVerified)
            {
                hasInvalidBoundary = true;
                segment++;
                unitized = 100m;
                unitizedPeak = unitized;
                startsAfterGap = true;
            }
            else
            {
                var externalFlow = intervalFlows.Sum(item => item.Amount);
                unitized *= (current.Equity - externalFlow) / previous.Equity;
                unitizedPeak = Math.Max(unitizedPeak, unitized);
                var unitizedDrawdown = unitizedPeak > 0m
                    ? Math.Max(0m, (unitizedPeak - unitized) * 100m / unitizedPeak)
                    : 0m;
                unitizedMaxDrawdown = Math.Max(unitizedMaxDrawdown, unitizedDrawdown);
                verifiedFlowCount += intervalFlows.Length;
            }

            observedPeak = Math.Max(observedPeak, current.Equity);
            var observedAmount = Math.Max(0m, observedPeak - current.Equity);
            var observedPercentage = observedPeak > 0m
                ? observedAmount * 100m / observedPeak
                : (decimal?)null;
            observedMaxAmount = Math.Max(observedMaxAmount, observedAmount);
            if (observedPercentage is not null)
            {
                observedMaxPercentage = Math.Max(observedMaxPercentage ?? 0m, observedPercentage.Value);
            }
            points.Add(CreatePoint(current, startsAfterGap));
        }

        var flowsOutsideSampleRange = relevantFlows.Count(item =>
            item.OccurredAtUtc <= ordered[0].CapturedAtUtc || item.OccurredAtUtc > ordered[^1].CapturedAtUtc);
        if (flowsOutsideSampleRange > 0)
        {
            hasInvalidBoundary = true;
        }
        var unverifiedFlows = relevantFlows.Length - verifiedFlowCount;
        var complete = !hasInvalidBoundary && unverifiedFlows == 0;
        var message = complete
            ? $"{ordered.Length} 个实际采样点；{verifiedFlowCount} 笔资金流均有 15 分钟内的前后采样。曲线可能遗漏采样间波动。"
            : $"{ordered.Length} 个实际采样点；{unverifiedFlows} 笔资金流或净值边界不可核验，已分为 {segment} 段，不输出完整账户净值回撤率。";
        return new ReviewEquityAnalysis(
            points,
            relevantFlows.Length,
            verifiedFlowCount,
            segment,
            observedMaxAmount,
            observedMaxPercentage,
            complete ? unitizedMaxDrawdown : null,
            complete,
            message);

        ReviewEquityPoint CreatePoint(EquitySample sample, bool startsAfterGap)
        {
            var observedAmount = Math.Max(0m, observedPeak - sample.Equity);
            var observedPercentage = observedPeak > 0m
                ? observedAmount * 100m / observedPeak
                : (decimal?)null;
            var unitizedDrawdown = unitizedPeak > 0m
                ? Math.Max(0m, (unitizedPeak - unitized) * 100m / unitizedPeak)
                : (decimal?)null;
            return new ReviewEquityPoint(
                sample.CapturedAtUtc, sample.ServerDate, sample.Equity, observedAmount,
                observedPercentage, segment, unitized, unitizedDrawdown, startsAfterGap);
        }
    }

    public IReadOnlyList<GroupMetricRow> BuildTradingSessionPerformance(
        IReadOnlyCollection<TradeRecord> selectedTrades,
        IReadOnlyCollection<TradingSessionDefinition> sessions,
        IReadOnlyDictionary<long, TradeExcursion> excursions,
        IReadOnlyCollection<DealRecord> deals)
    {
        var active = sessions.Where(item => item.IsActive)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => (Definition: item, TimeZone: TryResolveTimeZone(item.TimeZoneId)))
            .Where(item => item.TimeZone is not null)
            .ToArray();
        if (active.Length == 0)
        {
            return [];
        }

        var memberships = selectedTrades.Select(trade =>
        {
            var match = active.FirstOrDefault(item => MatchesTradingSession(
                trade.OpenedAtUtc, item.Definition, item.TimeZone!));
            return (Group: match.Definition?.Name ?? "未匹配自定义时段", Trade: trade);
        });
        return ReviewAnalyticsCalculator.CalculateNamedGroups(memberships, excursions, deals);
    }

    private static TimeZoneInfo? TryResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static bool MatchesTradingSession(
        DateTimeOffset openedAtUtc,
        TradingSessionDefinition session,
        TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(openedAtUtc, timeZone);
        var localTime = TimeOnly.FromDateTime(local.DateTime);
        var days = session.StartDays is { Count: > 0 }
            ? session.StartDays
            : Enum.GetValues<DayOfWeek>();
        if (session.StartLocalTime == session.EndLocalTime)
        {
            return days.Contains(local.DayOfWeek);
        }
        if (session.StartLocalTime < session.EndLocalTime)
        {
            return localTime >= session.StartLocalTime && localTime < session.EndLocalTime &&
                   days.Contains(local.DayOfWeek);
        }
        if (localTime >= session.StartLocalTime)
        {
            return days.Contains(local.DayOfWeek);
        }
        return localTime < session.EndLocalTime && days.Contains(local.AddDays(-1).DayOfWeek);
    }

    public ReviewFeeSummary CalculateFees(
        string accountKey,
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<TradeRecord> selectedTrades,
        IReadOnlyCollection<TradeRecord> knownTrades,
        IReadOnlyCollection<DealRecord> deals,
        int serverUtcOffsetSeconds)
    {
        var selectedIds = selectedTrades.Where(item => item.AccountKey == accountKey)
            .Select(item => item.PositionId).ToHashSet();
        var knownIds = knownTrades.Where(item => item.AccountKey == accountKey)
            .Select(item => item.PositionId).ToHashSet();
        var allocated = deals.Where(item => selectedIds.Contains(item.PositionId)).ToArray();
        var offset = TimeSpan.FromSeconds(serverUtcOffsetSeconds);
        var unallocated = deals.Where(item => !knownIds.Contains(item.PositionId))
            .Where(item =>
            {
                var date = DateOnly.FromDateTime(item.OccurredAtUtc.ToOffset(offset).DateTime);
                return date >= from && date <= to;
            }).ToArray();
        return new ReviewFeeSummary(
            allocated.Length,
            allocated.Sum(item => item.Commission),
            allocated.Sum(item => item.Swap),
            allocated.Sum(item => item.Fee),
            unallocated.Length,
            unallocated.Sum(item => item.Commission + item.Swap + item.Fee),
            selectedTrades.Select(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray());
    }

    public IReadOnlyList<ReviewDailyCashPoint> BuildDailyCashSeries(
        string accountKey,
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<TradeRecord> selectedTrades,
        IReadOnlyCollection<DealRecord> deals,
        int serverUtcOffsetSeconds)
    {
        var selectedIds = selectedTrades.Where(item => item.AccountKey == accountKey)
            .Select(item => item.PositionId).ToHashSet();
        var offset = TimeSpan.FromSeconds(serverUtcOffsetSeconds);
        return deals.Where(item => selectedIds.Contains(item.PositionId))
            .Select(item => (Deal: item, Date: DateOnly.FromDateTime(item.OccurredAtUtc.ToOffset(offset).DateTime)))
            .Where(item => item.Date >= from && item.Date <= to)
            .GroupBy(item => item.Date)
            .OrderBy(group => group.Key)
            .Select(group => new ReviewDailyCashPoint(
                group.Key,
                group.Sum(item => item.Deal.NetPnl),
                group.Select(item => item.Deal.Ticket).Distinct().ToArray(),
                group.Select(item => new TradeKey(accountKey, item.Deal.PositionId)).Distinct().ToArray()))
            .ToArray();
    }

    public IReadOnlyList<ReviewRiskSample> BuildRiskSamples(
        IReadOnlyCollection<TradeRecord> selectedTrades,
        IReadOnlyDictionary<long, TradeExcursion> excursions) =>
        selectedTrades.OrderByDescending(item => item.ClosedAtUtc)
            .Select(trade =>
            {
                excursions.TryGetValue(trade.PositionId, out var excursion);
                var reliable = excursion?.IsReliable == true;
                var reliableRisk = excursion?.HasReliableInitialRisk == true;
                var initialRisk = reliableRisk ? excursion!.InitialRiskAmount : null;
                return new ReviewRiskSample(
                    new TradeKey(trade.AccountKey, trade.PositionId),
                    trade.Symbol,
                    trade.NetPnl,
                    trade.OpeningVolume,
                    initialRisk,
                    reliable ? excursion!.MinimumPnl : null,
                    reliable ? excursion!.MaximumPnl : null,
                    reliableRisk && initialRisk is > 0m ? trade.NetPnl / initialRisk.Value : null,
                    reliable,
                    reliableRisk,
                    excursion?.AlgorithmVersion ?? "none");
            }).ToArray();

    private static bool IsReliableTargetMilestone(DailyState? state, DateOnly date, TimeSpan offset) =>
        state is
        {
            TargetAlerted: true,
            TargetReachedAtUtc: not null,
            TargetAmountAtReach: > 0m,
        } &&
        !string.IsNullOrWhiteSpace(state.TargetRuleVersion) &&
        DateOnly.FromDateTime(state.TargetReachedAtUtc.Value.ToOffset(offset).DateTime) == date;

    private static DateTimeOffset? FindLossThresholdReachedAt(
        IReadOnlyCollection<TradeRecord> completed,
        int threshold)
    {
        if (threshold <= 0)
        {
            return null;
        }

        var streak = 0;
        foreach (var trade in completed.OrderBy(item => item.ClosedAtUtc))
        {
            streak = trade.NetPnl < -BreakevenEpsilon ? streak + 1 : 0;
            if (streak >= threshold)
            {
                return trade.ClosedAtUtc;
            }
        }
        return null;
    }

    private static int GetConsecutiveLossCount(IReadOnlyCollection<TradeRecord> completed)
    {
        var count = 0;
        foreach (var trade in completed.OrderByDescending(item => item.ClosedAtUtc))
        {
            if (trade.NetPnl >= -BreakevenEpsilon)
            {
                break;
            }
            count++;
        }
        return count;
    }

    private static IReadOnlyList<DailyProcessEvent> BuildDailyTimeline(
        DateOnly date,
        TimeSpan offset,
        string accountKey,
        IReadOnlyCollection<TradeRecord> allTrades,
        IReadOnlyCollection<TradeRecord> opened,
        IReadOnlyCollection<TradeRecord> completed,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyCollection<BehaviorOccurrence> behaviors)
    {
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), offset).ToUniversalTime();
        var events = new List<DailyProcessEvent>();
        events.AddRange(allTrades
            .Where(item => item.OpenServerDate < date &&
                (!item.IsComplete || item.CloseServerDate is null || item.CloseServerDate >= date))
            .Select(item => new DailyProcessEvent(dayStart, "前日持有",
                $"#{item.PositionId} {item.Symbol} {item.Side} · 剩余 {item.RemainingVolume:0.#####}",
                ReviewEvidenceSource.Mt5Deal, [new TradeKey(item.AccountKey, item.PositionId)])));
        events.AddRange(opened.Select(item => new DailyProcessEvent(item.OpenedAtUtc, "首次开仓",
            $"#{item.PositionId} {item.Symbol} {item.Side} · {item.OpeningVolume:0.#####} @ {item.EntryPrice:0.#####}",
            ReviewEvidenceSource.Mt5Deal, [new TradeKey(item.AccountKey, item.PositionId)])));
        events.AddRange(deals.Select(item => new DailyProcessEvent(item.OccurredAtUtc, "成交",
            $"ticket {item.Ticket} · #{item.PositionId} {item.EntryKind} {item.Volume:0.#####} @ {item.Price:0.#####} · 现金 {item.NetPnl:+0.##;-0.##;0}",
            ReviewEvidenceSource.Mt5Deal, [new TradeKey(accountKey, item.PositionId)])));
        events.AddRange(completed.Select(item => new DailyProcessEvent(item.ClosedAtUtc!.Value, "最终平仓",
            $"#{item.PositionId} {item.Symbol} · 完整交易净额 {item.NetPnl:+0.##;-0.##;0}",
            ReviewEvidenceSource.Mt5Deal, [new TradeKey(item.AccountKey, item.PositionId)])));
        events.AddRange(behaviors.Select(item => new DailyProcessEvent(item.EventAtUtc, $"规则：{item.Rule}",
            item.Summary, item.Source, item.TradeLinks.Select(link => link.TradeKey).Distinct().ToArray())));
        return events.OrderBy(item => item.AtUtc).ThenBy(item => item.Kind, StringComparer.Ordinal).ToArray();
    }

    public ReviewDataQuality CalculateDataQuality(
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeExcursion> excursions,
        IReadOnlyDictionary<long, TradeReviewDocument> documents,
        IReadOnlyDictionary<long, TradeReviewMetadata> metadata,
        IReadOnlyCollection<MarketDataRange> marketRanges)
    {
        var complete = trades.Where(item => item.IsComplete).ToArray();
        var missingRisk = complete.Where(item =>
            !excursions.TryGetValue(item.PositionId, out var value) || !value.HasReliableInitialRisk).ToArray();
        var legacyExcursions = complete.Where(item => excursions.TryGetValue(item.PositionId, out var value) &&
            value.AlgorithmVersion == "legacy-extrema-v1").ToArray();
        var excessiveGaps = complete.Where(item => excursions.TryGetValue(item.PositionId, out var value) &&
            value.AlgorithmVersion == "position-pnl-v1" && value.MaximumGapMilliseconds > 5_000).ToArray();
        var otherIncompleteExcursions = complete.Where(item =>
            !excursions.TryGetValue(item.PositionId, out var value) ||
            (!value.IsReliable && value.AlgorithmVersion != "legacy-extrema-v1" && value.MaximumGapMilliseconds <= 5_000)).ToArray();
        var missingPlans = complete.Where(item =>
            !metadata.TryGetValue(item.PositionId, out var value) || value.PlanId is null).ToArray();
        var missingReviews = complete.Where(item =>
            !documents.TryGetValue(item.PositionId, out var value) || value.Status != ReviewCompletionStatus.Reviewed).ToArray();
        var marketCovered = complete.Where(item => marketRanges.Any(range =>
            range.AccountKey == item.AccountKey &&
            string.Equals(range.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase) &&
            range.Coverage == MarketCoverageStatus.Complete &&
            range.ActualFromUtc <= item.OpenedAtUtc && range.ActualToUtc >= item.ClosedAtUtc)).ToArray();
        var marketCoveredIds = marketCovered.Select(item => item.PositionId).ToHashSet();
        var marketIssues = complete.Where(item => !marketCoveredIds.Contains(item.PositionId))
            .GroupBy(item => MarketIssueCode(item, marketRanges))
            .ToArray();
        var risk = complete.Length - missingRisk.Length;
        var excursion = complete.Count(item => excursions.TryGetValue(item.PositionId, out var value) && value.IsReliable);
        var planned = complete.Length - missingPlans.Length;
        var reviewed = complete.Length - missingReviews.Length;
        var market = marketCovered.Length;
        var missing = new Dictionary<string, int>
        {
            ["缺少初始风险"] = missingRisk.Length,
            ["持仓采样不完整"] = complete.Length - excursion,
            ["旧极值口径"] = legacyExcursions.Length,
            ["采样缺口超过 5 秒"] = excessiveGaps.Length,
            ["缺少盘前计划"] = missingPlans.Length,
            ["尚未完成复盘"] = missingReviews.Length,
            ["缺少历史行情"] = complete.Length - market,
        };
        var issues = new List<ReviewDataQualityIssue>();
        AddIssue("risk-missing", "缺少初始风险", "没有可靠的首开风险证据；实际 R 保持未知。", missingRisk);
        AddIssue("excursion-legacy", "旧极值口径", "迁移前只保存了极值；保留查看，但不进入新版 MAE/MFE 均值。", legacyExcursions);
        AddIssue("excursion-gap", "采样缺口超过 5 秒", "实际 R 可继续使用可靠初始风险；MAE/MFE 降级。", excessiveGaps);
        AddIssue("excursion-incomplete", "持仓采样不完整", "缺少新版过程样本，或首开/平仓和 90% 时长覆盖条件未满足。", otherIncompleteExcursions);
        AddIssue("plan-missing", "缺少盘前计划", "没有可核验的当时计划关联。", missingPlans);
        AddIssue("review-pending", "尚未完成复盘", "交易仍待复盘、为草稿，或依据变化后需重审。", missingReviews);
        foreach (var group in marketIssues)
        {
            var evidence = group.SelectMany(item => RelevantRanges(item, marketRanges)).Select(item => item.RequestId)
                .Distinct(StringComparer.Ordinal).ToArray();
            var (label, detail) = group.Key switch
            {
                "market-failed" => ("行情读取失败", "历史源明确返回失败；仅保留成交事件。"),
                "market-cancelled" => ("行情读取已取消", "历史请求被取消，没有把未完成结果记为成功。"),
                "market-partial" => ("行情区间截短", "实际覆盖不能包住整笔持仓，缺口不补造。"),
                "market-empty" => ("行情结果为空", "历史源成功响应，但所选区间没有报价。"),
                _ => ("尚未请求历史行情", "没有找到覆盖该交易的历史行情请求。"),
            };
            issues.Add(new ReviewDataQualityIssue(group.Key, label, detail,
                group.Select(TradeKeyOf).ToArray(), evidence));
        }
        return new ReviewDataQuality(
            complete.Length, risk, excursion, planned, reviewed, market,
            Percentage(risk, complete.Length), Percentage(excursion, complete.Length),
            Percentage(planned, complete.Length), Percentage(reviewed, complete.Length),
            Percentage(market, complete.Length), missing, issues);

        void AddIssue(string code, string label, string detail, IReadOnlyCollection<TradeRecord> affected)
        {
            if (affected.Count > 0)
            {
                issues.Add(new ReviewDataQualityIssue(code, label, detail,
                    affected.Select(TradeKeyOf).ToArray(), []));
            }
        }

        static TradeKey TradeKeyOf(TradeRecord trade) => new(trade.AccountKey, trade.PositionId);

        static IReadOnlyList<MarketDataRange> RelevantRanges(
            TradeRecord trade,
            IReadOnlyCollection<MarketDataRange> ranges) => ranges.Where(range =>
                range.AccountKey == trade.AccountKey &&
                string.Equals(range.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase) &&
                range.RequestedToUtc >= trade.OpenedAtUtc &&
                range.RequestedFromUtc <= trade.ClosedAtUtc).ToArray();

        static string MarketIssueCode(TradeRecord trade, IReadOnlyCollection<MarketDataRange> ranges)
        {
            var relevant = RelevantRanges(trade, ranges);
            if (relevant.Any(item => item.Coverage == MarketCoverageStatus.Failed)) return "market-failed";
            if (relevant.Any(item => item.Coverage == MarketCoverageStatus.Cancelled)) return "market-cancelled";
            if (relevant.Any(item => item.Coverage == MarketCoverageStatus.Partial)) return "market-partial";
            if (relevant.Any(item => item.Coverage == MarketCoverageStatus.Empty)) return "market-empty";
            return "market-missing";
        }
    }

    public RuleAssessmentSummary SummarizeRules(IReadOnlyCollection<TradeRuleAssessment> assessments)
    {
        var passed = assessments.Count(item => item.Status == RuleAssessmentStatus.Passed);
        var failed = assessments.Count(item => item.Status == RuleAssessmentStatus.Failed);
        var unknown = assessments.Count(item => item.Status == RuleAssessmentStatus.Unknown);
        var notApplicable = assessments.Count(item => item.Status == RuleAssessmentStatus.NotApplicable);
        var applicable = passed + failed + unknown;
        var known = passed + failed;
        return new RuleAssessmentSummary(
            passed, failed, unknown, notApplicable,
            known == 0 ? null : Percentage(passed, known),
            applicable == 0 ? 0m : Percentage(known, applicable),
            failed > 0,
            known > 0 && failed == 0 && unknown == 0);
    }

    public BehaviorImpactSummary CalculateBehaviorImpact(
        IReadOnlyCollection<BehaviorOccurrence> occurrences,
        IReadOnlyDictionary<TradeKey, TradeRecord> trades,
        BehaviorRuleKind? rule = null)
    {
        var selected = occurrences.Where(item => rule is null || item.Rule == rule).ToArray();
        var keys = selected.SelectMany(item => item.TradeLinks)
            .Where(link => link.Role == BehaviorTradeRole.Trigger)
            .Select(link => link.TradeKey)
            .Distinct()
            .Where(trades.ContainsKey)
            .ToArray();
        return new BehaviorImpactSummary(rule, selected.Length, keys.Length, keys.Sum(key => trades[key].NetPnl), keys);
    }

    public IReadOnlyList<ReviewTagSuggestion> BuildTagSuggestions(
        TradeKey tradeKey,
        IReadOnlyCollection<BehaviorOccurrence> behaviors,
        IReadOnlyCollection<string> currentTags)
    {
        var accepted = currentTags
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return behaviors
            .Where(item => item.AccountKey == tradeKey.AccountKey &&
                           !item.EvidenceInsufficient &&
                           item.Level is BehaviorRiskLevel.Attention or BehaviorRiskLevel.Critical &&
                           item.TradeLinks.Any(link => link.TradeKey == tradeKey && link.Role == BehaviorTradeRole.Trigger))
            .GroupBy(item => SuggestedTag(item.Rule), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var occurrences = group.OrderByDescending(item => item.EventAtUtc).ToArray();
                var latest = occurrences[0];
                return new ReviewTagSuggestion(
                    group.Key,
                    latest.Summary,
                    latest.Rule,
                    occurrences.Any(item => item.Level == BehaviorRiskLevel.Critical)
                        ? BehaviorRiskLevel.Critical
                        : BehaviorRiskLevel.Attention,
                    occurrences.Select(item => item.Id).Distinct(StringComparer.Ordinal).ToArray(),
                    accepted.Contains(group.Key));
            })
            .OrderByDescending(item => item.Level)
            .ThenBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string SuggestedTag(BehaviorRuleKind rule) => rule switch
    {
        BehaviorRuleKind.ReentryCount => "重复入场",
        BehaviorRuleKind.LossZonePersistence => "亏损区域重复",
        BehaviorRuleKind.RevengeScore => "疑似报复交易",
        BehaviorRuleKind.OvertradeBurst => "短时过度交易",
        BehaviorRuleKind.PlanDeviationRate => "计划偏离",
        BehaviorRuleKind.ProfitGiveback => "利润回吐",
        BehaviorRuleKind.SizeEscalationAfterLoss => "亏损后放大仓位",
        BehaviorRuleKind.CooldownViolation => "冷静期违规",
        BehaviorRuleKind.PriceFixationScore => "价格执着",
        _ => string.Empty,
    };

    public BehaviorEvidenceAnalysis CompareBehaviorSamples(
        IReadOnlyCollection<BehaviorOccurrence> occurrences,
        IReadOnlyDictionary<TradeKey, TradeRecord> trades,
        BehaviorRuleKind? rule = null)
    {
        var selected = occurrences.Where(item => rule is null || item.Rule == rule).ToArray();
        var hitKeys = selected.SelectMany(item => item.TradeLinks)
            .Where(link => link.Role == BehaviorTradeRole.Trigger)
            .Select(link => link.TradeKey)
            .Distinct()
            .Where(trades.ContainsKey)
            .ToArray();
        var hitSet = hitKeys.ToHashSet();
        var unhitKeys = trades.Keys.Where(key => !hitSet.Contains(key)).ToArray();
        return new BehaviorEvidenceAnalysis(
            rule,
            selected.Count(item => item.Source == ReviewEvidenceSource.LiveObservation),
            selected.Count(item => item.Source == ReviewEvidenceSource.RuleRecalculation),
            new BehaviorTradeSample(hitKeys.Length, hitKeys.Sum(key => trades[key].NetPnl), hitKeys),
            new BehaviorTradeSample(unhitKeys.Length, unhitKeys.Sum(key => trades[key].NetPnl), unhitKeys));
    }

    public PeriodReviewFacts BuildPeriodFacts(
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeReviewDocument> documents,
        IReadOnlyCollection<TradeRuleAssessment> assessments,
        IReadOnlyCollection<BehaviorOccurrence> occurrences,
        ReviewDataQuality dataQuality)
    {
        var keys = trades.Select(item => new TradeKey(item.AccountKey, item.PositionId)).Distinct().ToArray();
        var keySet = keys.ToHashSet();
        var selectedAssessments = assessments.Where(item => keySet.Contains(item.TradeKey)).ToArray();
        var ruleSummary = SummarizeRules(selectedAssessments);
        var selectedOccurrences = occurrences.Where(item =>
            item.TradeLinks.Any(link => link.Role == BehaviorTradeRole.Trigger && keySet.Contains(link.TradeKey))).ToArray();
        var repeated = selectedOccurrences.GroupBy(item => item.Rule)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
            .Select(group => new BehaviorRuleFrequency(
                group.Key,
                group.Count(),
                group.Select(item => item.Id).Distinct(StringComparer.Ordinal).ToArray(),
                group.SelectMany(item => item.TradeLinks)
                    .Where(link => link.Role == BehaviorTradeRole.Trigger && keySet.Contains(link.TradeKey))
                    .Select(link => link.TradeKey).Distinct().ToArray()))
            .ToArray();
        return new PeriodReviewFacts(
            trades.Count,
            trades.Count(item => documents.TryGetValue(item.PositionId, out var document) &&
                                 document.Status == ReviewCompletionStatus.Reviewed),
            ruleSummary.Passed,
            ruleSummary.Failed,
            ruleSummary.Unknown,
            ruleSummary.NotApplicable,
            ruleSummary.AdherencePercentage,
            selectedOccurrences.Length,
            repeated,
            keys,
            selectedOccurrences.Select(item => item.Id).Distinct(StringComparer.Ordinal).ToArray(),
            dataQuality);
    }

    public IReadOnlyList<ImprovementGoalProgress> BuildGoalProgress(
        IReadOnlyCollection<ImprovementGoal> goals,
        IReadOnlyCollection<GoalObservation> observations,
        DateOnly from,
        DateOnly to)
    {
        return goals.OrderByDescending(item => item.UpdatedAtUtc).Select(goal =>
        {
            var activeFrom = goal.StartServerDate > from ? goal.StartServerDate : from;
            var activeTo = goal.EndServerDate is { } end && end < to ? end : to;
            var applicableDays = activeTo < activeFrom ? 0 : activeTo.DayNumber - activeFrom.DayNumber + 1;
            var selected = observations.Where(item => item.GoalId == goal.Id &&
                item.ServerDate >= activeFrom && item.ServerDate <= activeTo).ToArray();
            var observedDays = selected.Select(item => item.ServerDate).Distinct().Count();
            var pass = selected.Sum(item => item.PassCount);
            var fail = selected.Sum(item => item.FailCount);
            return new ImprovementGoalProgress(
                goal,
                applicableDays,
                observedDays,
                Math.Max(0, applicableDays - observedDays),
                selected.Sum(item => item.OpportunityCount),
                pass,
                fail,
                selected.Count(item => item.Status == GoalObservationStatus.Unknown),
                selected.Count(item => item.Status == GoalObservationStatus.NotApplicable),
                pass + fail == 0 ? null : Percentage(pass, pass + fail),
                selected.SelectMany(item => item.EvidenceIds ??
                    (string.IsNullOrWhiteSpace(item.Evidence) ? [] : [item.Evidence]))
                    .Distinct(StringComparer.Ordinal).ToArray());
        }).ToArray();
    }

    public ReviewComparison Compare(
        string leftLabel,
        IReadOnlyCollection<TradeRecord> left,
        string rightLabel,
        IReadOnlyCollection<TradeRecord> right,
        IReadOnlyDictionary<long, TradeReviewMetadata> metadata,
        IReadOnlyDictionary<long, TradeExcursion> excursions,
        int serverUtcOffsetSeconds = 0)
    {
        var calculator = new ReviewAnalyticsCalculator();
        var accountKey = left.Concat(right).Select(item => item.AccountKey).FirstOrDefault() ?? string.Empty;
        var allDates = left.Concat(right).Where(item => item.CloseServerDate is not null)
            .Select(item => item.CloseServerDate!.Value).ToArray();
        var from = allDates.Length == 0 ? DateOnly.MinValue : allDates.Min();
        var to = allDates.Length == 0 ? DateOnly.MinValue : allDates.Max();
        var filter = new ReviewFilter(accountKey, from, to, ServerUtcOffsetSeconds: serverUtcOffsetSeconds);
        var leftPerformance = calculator.Calculate(filter, left, metadata, excursions).Performance;
        var rightPerformance = calculator.Calculate(filter, right, metadata, excursions).Performance;
        var overlap = left.Select(item => new TradeKey(item.AccountKey, item.PositionId))
            .Intersect(right.Select(item => new TradeKey(item.AccountKey, item.PositionId))).Count();
        var leftRisks = GetInitialRisks(left, excursions);
        var rightRisks = GetInitialRisks(right, excursions);
        return new ReviewComparison(leftLabel, leftPerformance, rightLabel, rightPerformance, overlap,
            left.Count < 30 || right.Count < 30,
            left.Select(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray(),
            right.Select(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray(),
            leftRisks.Length == 0 ? null : leftRisks.Average(),
            rightRisks.Length == 0 ? null : rightRisks.Average(),
            Percentage(leftRisks.Length, left.Count),
            Percentage(rightRisks.Length, right.Count));
    }

    private static decimal[] GetInitialRisks(
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeExcursion> excursions) =>
        trades.Where(item => excursions.TryGetValue(item.PositionId, out var excursion) &&
                             excursion.HasReliableInitialRisk)
            .Select(item => excursions[item.PositionId].InitialRiskAmount!.Value)
            .ToArray();

    public TradeCampaignSummary BuildCampaignSummary(
        TradeCampaign campaign,
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<DealRecord> deals)
    {
        var members = trades.Where(item => item.AccountKey == campaign.AccountKey && campaign.PositionIds.Contains(item.PositionId))
            .OrderBy(item => item.OpenedAtUtc).ToArray();
        var memberIds = members.Select(item => item.PositionId).ToHashSet();
        var memberDeals = deals.Where(item => memberIds.Contains(item.PositionId)).ToArray();
        return new TradeCampaignSummary(
            campaign,
            members.Length,
            Math.Max(0, members.Length - 1),
            members.Select(item => item.Side).Distinct().Count() > 1,
            members.Length == 0 ? null : members.Min(item => item.OpenedAtUtc),
            members.Any(item => item.ClosedAtUtc is null) ? null : members.Max(item => item.ClosedAtUtc),
            members.Sum(item => item.NetPnl),
            memberDeals.Sum(item => item.Commission + item.Swap + item.Fee),
            CalculateMaximumConcurrentExposure(memberDeals));
    }

    public ReplayFrame BuildReplayFrame(
        DateTimeOffset cursorUtc,
        MarketDataRange range,
        IReadOnlyCollection<MarketBar> bars,
        IReadOnlyCollection<MarketTick> ticks,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyCollection<PositionPnlSample> samples,
        IReadOnlyCollection<BehaviorOccurrence> behaviors,
        IReadOnlyCollection<ReviewEvidenceStamp> notes,
        TradeRecord? trade = null,
        StructuredTradePlan? plan = null,
        TradeReviewDocument? review = null,
        bool revealFullReview = false)
    {
        var message = range.Coverage switch
        {
            MarketCoverageStatus.Failed => $"仅成交事件，行情读取失败：{range.Error}",
            MarketCoverageStatus.Empty => "仅成交事件，所选范围没有可用行情。",
            MarketCoverageStatus.Partial => "行情覆盖不完整，缺口不会补造。",
            MarketCoverageStatus.Cancelled => "行情读取已取消。",
            _ => string.Empty,
        };
        var barDuration = ParseTimeframe(range.Timeframe);
        var visibleBars = bars.Where(item => item.OpenedAtUtc + barDuration <= cursorUtc)
            .OrderBy(item => item.OpenedAtUtc).ToArray();
        var visibleTicks = ticks.Where(item => item.OccurredAtUtc <= cursorUtc)
            .OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.TimeMilliseconds).ToArray();
        var visibleDeals = deals.Where(item => item.OccurredAtUtc <= cursorUtc)
            .OrderBy(item => item.OccurredAtUtc).ToArray();
        var visibleSamples = samples.Where(item => item.CapturedAtUtc <= cursorUtc)
            .OrderBy(item => item.CapturedAtUtc).ToArray();
        var visibleBehaviors = behaviors.Where(item => revealFullReview ||
                item.EventAtUtc <= cursorUtc &&
                (item.Source is ReviewEvidenceSource.Mt5Deal or ReviewEvidenceSource.HistoricalMarketData ||
                 item.ObservedAtUtc <= cursorUtc))
            .OrderBy(item => item.EventAtUtc).ToArray();
        var visibleNotes = notes.Where(item => revealFullReview ||
                item.EventAtUtc <= cursorUtc && item.RecordedAtUtc <= cursorUtc)
            .OrderBy(item => item.EventAtUtc).ToArray();
        var navigation = bars.Select(item => new ReplayEventPoint(
                item.OpenedAtUtc + barDuration, "K线完成",
                $"O {item.Open} H {item.High} L {item.Low} C {item.Close}", ReviewEvidenceSource.HistoricalMarketData))
            .Concat(deals.Select(item => new ReplayEventPoint(
                item.OccurredAtUtc, "成交", $"#{item.Ticket} {item.EntryKind} {item.Volume}", ReviewEvidenceSource.Mt5Deal)))
            .Concat(samples.Select(item => new ReplayEventPoint(
                item.CapturedAtUtc, "盈亏采样", $"净额 {item.NetPnl}", ReviewEvidenceSource.LiveObservation)))
            .Concat(behaviors.Select(item => new ReplayEventPoint(
                item.Source is ReviewEvidenceSource.Mt5Deal or ReviewEvidenceSource.HistoricalMarketData ||
                item.ObservedAtUtc <= item.EventAtUtc ? item.EventAtUtc : item.ObservedAtUtc,
                "行为", item.Summary, item.Source)))
            .Concat(notes.Select(item => new ReplayEventPoint(
                item.RecordedAtUtc <= item.EventAtUtc ? item.EventAtUtc : item.RecordedAtUtc,
                "笔记", item.Source.ToString(), item.Source)))
            .Concat(plan is null
                ? []
                : [new ReplayEventPoint(plan.CreatedAtUtc, "计划记录", plan.Id, ReviewEvidenceSource.PreTradePlan)])
            .Where(item => item.AtUtc >= range.RequestedFromUtc && item.AtUtc <= range.RequestedToUtc)
            .OrderBy(item => item.AtUtc).ThenBy(item => item.Kind, StringComparer.Ordinal).ToArray();
        var currentEventIndex = Array.FindLastIndex(navigation, item => item.AtUtc <= cursorUtc);
        return new ReplayFrame(
            cursorUtc,
            visibleBars,
            visibleTicks,
            visibleDeals,
            visibleSamples,
            visibleBehaviors,
            visibleNotes,
            range.Coverage,
            message,
            navigation,
            currentEventIndex,
            revealFullReview || trade?.ClosedAtUtc is { } closedAt && closedAt <= cursorUtc ? trade?.NetPnl : null,
            revealFullReview || plan is not null && plan.CreatedAtUtc <= cursorUtc ? plan : null,
            revealFullReview || review is not null && review.UpdatedAtUtc <= cursorUtc ? review : null,
            revealFullReview);
    }

    private static TimeSpan ParseTimeframe(string timeframe) => timeframe.ToUpperInvariant() switch
    {
        "M1" => TimeSpan.FromMinutes(1),
        "M5" => TimeSpan.FromMinutes(5),
        "M15" => TimeSpan.FromMinutes(15),
        "H1" => TimeSpan.FromHours(1),
        _ => TimeSpan.Zero,
    };

    private static bool MatchesMetadata(
        TradeRecord trade,
        ReviewWorkspaceFilter filter,
        IReadOnlySet<string> tags,
        IReadOnlyDictionary<long, TradeReviewMetadata> metadata)
    {
        metadata.TryGetValue(trade.PositionId, out var item);
        if (filter.Strategy is not null && !string.Equals(item?.Strategy, filter.Strategy.Trim(), StringComparison.OrdinalIgnoreCase) ||
            filter.Setup is not null && !string.Equals(item?.Setup, filter.Setup.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (tags.Count == 0)
        {
            return true;
        }
        var actual = item?.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return filter.TagMatchMode == ReviewTagMatchMode.All
            ? tags.All(actual.Contains)
            : tags.Any(actual.Contains);
    }

    private static bool MatchesSearch(
        TradeRecord trade,
        string? search,
        IReadOnlyDictionary<long, TradeReviewMetadata> metadata,
        IReadOnlyDictionary<long, TradeReviewDocument> documents,
        IReadOnlyDictionary<long, string[]> tickets)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }
        var value = search.Trim();
        metadata.TryGetValue(trade.PositionId, out var meta);
        documents.TryGetValue(trade.PositionId, out var document);
        return trade.Symbol.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               trade.PositionId.ToString().Contains(value, StringComparison.OrdinalIgnoreCase) ||
               (tickets.TryGetValue(trade.PositionId, out var dealTickets) && dealTickets.Any(item => item.Contains(value, StringComparison.OrdinalIgnoreCase))) ||
               (meta is not null && ($"{meta.Strategy} {meta.Setup} {string.Join(' ', meta.Tags)}").Contains(value, StringComparison.OrdinalIgnoreCase)) ||
               (document is not null && ($"{document.EntryReason} {document.ExitReason} {document.DidWell} {document.ToImprove} {document.NextAction} {document.Summary} {document.Emotion} {document.MarketCondition}")
                   .Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static ReviewCompletionStatus GetStatus(TradeRecord trade, IReadOnlyDictionary<long, TradeReviewDocument> documents) =>
        documents.TryGetValue(trade.PositionId, out var document) ? document.Status : ReviewCompletionStatus.Pending;

    private static IReadOnlySet<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        tags is null ? new HashSet<string>() : tags.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static decimal Percentage(int numerator, int denominator) => denominator == 0 ? 0m : numerator * 100m / denominator;

    private static decimal? CalculateMaximumConcurrentExposure(IReadOnlyCollection<DealRecord> deals)
    {
        if (deals.Count == 0)
        {
            return null;
        }
        var positions = new Dictionary<long, decimal>();
        var maximum = 0m;
        foreach (var deal in deals.OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.Ticket))
        {
            var delta = deal.EntryKind switch
            {
                DealEntryKind.In => deal.Volume,
                DealEntryKind.Out or DealEntryKind.OutBy => -deal.Volume,
                DealEntryKind.InOut => deal.Volume,
                _ => 0m,
            };
            positions[deal.PositionId] = Math.Max(0m, positions.GetValueOrDefault(deal.PositionId) + delta);
            maximum = Math.Max(maximum, positions.Values.Sum());
        }
        return maximum;
    }
}
