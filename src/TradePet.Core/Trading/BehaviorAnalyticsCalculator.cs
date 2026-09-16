using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class BehaviorAnalyticsCalculator
{
    public BehaviorSummary EvaluatePeriod(
        ReviewFilter filter,
        IReadOnlyCollection<TradeRecord> selectedTrades,
        IReadOnlyCollection<LossZoneState> lossZones,
        IReadOnlyCollection<LossZoneAttempt> attempts,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata,
        BehaviorPolicy policy,
        IReadOnlyDictionary<DateOnly, DailyState>? dailyStates = null,
        DateTimeOffset? observedAtUtc = null,
        IReadOnlyDictionary<string, SymbolSpecification>? symbolSpecifications = null)
    {
        var now = observedAtUtc ?? DateTimeOffset.UtcNow;
        var trades = selectedTrades
            .Where(trade => trade.AccountKey == filter.AccountKey)
            .OrderBy(trade => trade.OpenedAtUtc)
            .ToArray();
        var activeDates = trades.Select(trade => trade.OpenServerDate).Distinct().OrderBy(date => date).ToArray();
        if (activeDates.Length == 0)
        {
            var empty = Evaluate(
                filter.AccountKey, filter.ToServerDate, [], [], [], metadata, policy, null, now, symbolSpecifications);
            return empty with
            {
                RiskLevel = BehaviorRiskLevel.Observing,
                Evaluations = empty.Evaluations.Select(item => item with
                {
                    Summary = "所选周期没有完整交易，暂无可计算的行为样本。",
                    Level = BehaviorRiskLevel.Observing,
                    Triggered = false,
                }).ToArray(),
                BaselineDayCount = 0,
                BaselineReady = false,
            };
        }

        var allowedSymbols = trades.Select(trade => trade.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopedZones = lossZones.Where(zone => allowedSymbols.Contains(zone.Symbol)).ToArray();
        var allowedZoneIds = scopedZones.Select(zone => zone.Id).ToHashSet(StringComparer.Ordinal);
        var scopedAttempts = attempts
            .Where(attempt => allowedZoneIds.Contains(attempt.ZoneId) &&
                              (filter.Side is null || attempt.Side == filter.Side))
            .ToArray();
        var daily = activeDates.Select(date => Evaluate(
                filter.AccountKey,
                date,
                trades,
                scopedZones,
                scopedAttempts,
                metadata,
                policy,
                dailyStates is not null && dailyStates.TryGetValue(date, out var state) ? state : null,
                now,
                symbolSpecifications))
            .ToArray();

        var evaluations = Enum.GetValues<BehaviorRuleKind>()
            .Select(rule => AggregateRule(rule, daily, filter, now))
            .ToArray();
        var baselineDays = activeDates.Length;
        var riskLevel = evaluations.Any(item => item.Level == BehaviorRiskLevel.Critical)
            ? BehaviorRiskLevel.Critical
            : evaluations.Any(item => item.Triggered)
                ? BehaviorRiskLevel.Attention
                : baselineDays >= 10 ? BehaviorRiskLevel.Normal : BehaviorRiskLevel.Observing;
        return new BehaviorSummary(riskLevel, evaluations, baselineDays, baselineDays >= 10);
    }

    public BehaviorSummary Evaluate(
        string accountKey,
        DateOnly serverDate,
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<LossZoneState> lossZones,
        IReadOnlyCollection<LossZoneAttempt> attempts,
        IReadOnlyDictionary<long, TradeReviewMetadata>? metadata,
        BehaviorPolicy policy,
        DailyState? dailyState = null,
        DateTimeOffset? observedAtUtc = null,
        IReadOnlyDictionary<string, SymbolSpecification>? symbolSpecifications = null)
    {
        var now = observedAtUtc ?? DateTimeOffset.UtcNow;
        var scoped = trades
            .Where(trade => trade.AccountKey == accountKey && trade.OpenServerDate == serverDate)
            .OrderBy(trade => trade.OpenedAtUtc)
            .ToArray();
        var scopedZones = lossZones
            .Where(zone => zone.AccountKey == accountKey && zone.ServerDate == serverDate)
            .ToArray();
        var scopedAttempts = attempts
            .Where(attempt => scopedZones.Any(zone => zone.Id == attempt.ZoneId))
            .ToArray();
        var completed = scoped.Where(trade => trade.IsComplete && trade.ClosedAtUtc is not null).ToArray();
        var evaluations = new List<BehaviorEvaluation>();
        Add(evaluations, accountKey, serverDate, null, BehaviorRuleKind.ReentryCount,
            CalculateReentryCount(scoped, scopedAttempts, symbolSpecifications), null, policy.ReentryThreshold, policy.EnabledRules.ReentryCount, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);
        Add(evaluations, accountKey, serverDate, null, BehaviorRuleKind.LossZonePersistence,
            CalculateLossZonePersistence(scopedAttempts), null, policy.LossZonePersistenceThreshold, policy.EnabledRules.LossZonePersistence, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var revenge = CalculateRevengeScore(scoped, policy.RapidReentrySeconds, policy.LotEscalationMultiplier);
        Add(evaluations, accountKey, serverDate, LatestPositionId(scoped), BehaviorRuleKind.RevengeScore,
            revenge.Value, revenge.Baseline, policy.RevengeScoreThreshold, policy.EnabledRules.RevengeScore, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var overtrade = CalculateOvertradeBurst(scoped, trades, serverDate, policy.OvertradeWindowMinutes);
        Add(evaluations, accountKey, serverDate, null, BehaviorRuleKind.OvertradeBurst,
            overtrade.Value, overtrade.Baseline, policy.OvertradeBaselineMultiplier, policy.EnabledRules.OvertradeBurst,
            now, hardThreshold: policy.OvertradeAbsoluteCount, sampleCount: overtrade.SampleCount,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var classified = completed.Where(trade => metadata?.ContainsKey(trade.PositionId) == true)
            .Select(trade => metadata![trade.PositionId].ComplianceStatus)
            .Where(status => status is PlanComplianceStatus.Matched or PlanComplianceStatus.ManualInside or
                                     PlanComplianceStatus.OutsidePlan or PlanComplianceStatus.ManualOutside)
            .ToArray();
        var outside = classified.Count(status => status is PlanComplianceStatus.OutsidePlan or PlanComplianceStatus.ManualOutside);
        var deviation = classified.Length == 0 ? 0m : outside * 100m / classified.Length;
        Add(evaluations, accountKey, serverDate, null, BehaviorRuleKind.PlanDeviationRate,
            deviation, null, policy.PlanDeviationRateThreshold, policy.EnabledRules.PlanDeviationRate, now,
            sampleCount: classified.Length, baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var givebackPercentage = dailyState is { HighWaterPnl: > 0m }
            ? dailyState.Giveback * 100m / dailyState.HighWaterPnl
            : 0m;
        Add(evaluations, accountKey, serverDate, null, BehaviorRuleKind.ProfitGiveback,
            givebackPercentage, null, policy.ProfitGivebackThreshold, policy.EnabledRules.ProfitGiveback, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var escalation = CalculateSizeEscalation(scoped);
        Add(evaluations, accountKey, serverDate, LatestPositionId(scoped), BehaviorRuleKind.SizeEscalationAfterLoss,
            escalation, null, policy.LotEscalationMultiplier, policy.EnabledRules.SizeEscalationAfterLoss, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var cooldown = CalculateCooldownViolations(scoped, policy.ConsecutiveLossThreshold, policy.CooldownSeconds);
        Add(evaluations, accountKey, serverDate, LatestPositionId(scoped), BehaviorRuleKind.CooldownViolation,
            cooldown, null, 1m, policy.EnabledRules.CooldownViolation, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var fixation = CalculatePriceFixation(scoped, trades, serverDate, symbolSpecifications);
        Add(evaluations, accountKey, serverDate, null, BehaviorRuleKind.PriceFixationScore,
            fixation.Value, fixation.Baseline, policy.PriceFixationScoreThreshold,
            policy.EnabledRules.PriceFixationScore, now, sampleCount: scoped.Length,
            minimumSamples: policy.PriceFixationMinimumTrades,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        return BuildSummary(evaluations, trades, serverDate, policy);
    }

    private static BehaviorEvaluation AggregateRule(
        BehaviorRuleKind rule,
        IReadOnlyCollection<BehaviorSummary> daily,
        ReviewFilter filter,
        DateTimeOffset observedAtUtc)
    {
        var items = daily.SelectMany(summary => summary.Evaluations).Where(item => item.Rule == rule).ToArray();
        var peak = items.OrderByDescending(item => item.Value).ThenByDescending(item => item.ServerDate).First();
        var triggeredDays = items.Count(item => item.Triggered);
        var baselineValues = items.Where(item => item.Baseline is not null).Select(item => item.Baseline!.Value).ToArray();
        var baseline = baselineValues.Length == 0 ? (decimal?)null : baselineValues.Average();
        var level = items.Any(item => item.Level == BehaviorRiskLevel.Critical)
            ? BehaviorRiskLevel.Critical
            : items.Any(item => item.Triggered) ? BehaviorRiskLevel.Attention : BehaviorRiskLevel.Normal;
        var countLike = rule is BehaviorRuleKind.ReentryCount or BehaviorRuleKind.LossZonePersistence or
            BehaviorRuleKind.CooldownViolation;
        var prefix = countLike
            ? $"周期累计 {items.Sum(item => item.Value):0.##}，单日最高 {peak.Value:0.##}（{peak.ServerDate:yyyy-MM-dd}）"
            : $"周期最高 {peak.Value:0.##}（{peak.ServerDate:yyyy-MM-dd}）";
        var suffix = triggeredDays == 0 ? "，未出现越线交易日" : $"，越线 {triggeredDays} 天";
        return peak with
        {
            Id = $"behavior-review:{filter.AccountKey}:{filter.FromServerDate}:{filter.ToServerDate}:{rule}",
            ServerDate = filter.ToServerDate,
            PositionId = null,
            Baseline = baseline,
            Level = level,
            Triggered = triggeredDays > 0,
            Summary = prefix + suffix,
            ObservedAtUtc = observedAtUtc,
        };
    }

    public BehaviorSummary EvaluateOpen(
        string accountKey,
        DateOnly serverDate,
        TradeRecord openingTrade,
        IReadOnlyCollection<TradeRecord> trades,
        BehaviorPolicy policy,
        DateTimeOffset? observedAtUtc = null,
        int? matchingLossZoneAttemptCount = null,
        IReadOnlyDictionary<string, SymbolSpecification>? symbolSpecifications = null)
    {
        var now = observedAtUtc ?? DateTimeOffset.UtcNow;
        var scoped = trades
            .Where(trade =>
                trade.AccountKey == accountKey &&
                trade.OpenServerDate == serverDate &&
                trade.PositionId != openingTrade.PositionId)
            .Append(openingTrade)
            .OrderBy(trade => trade.OpenedAtUtc)
            .ToArray();
        var evaluations = new List<BehaviorEvaluation>();

        if (matchingLossZoneAttemptCount is null)
        {
            var reentries = scoped.Count(trade =>
                trade.PositionId != openingTrade.PositionId &&
                string.Equals(trade.Symbol, openingTrade.Symbol, StringComparison.OrdinalIgnoreCase) &&
                Bucket(trade, symbolSpecifications) == Bucket(openingTrade, symbolSpecifications));
            Add(evaluations, accountKey, serverDate, openingTrade.PositionId, BehaviorRuleKind.ReentryCount,
                reentries, null, policy.ReentryThreshold, policy.EnabledRules.ReentryCount, now,
                baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);
        }
        else
        {
            Add(evaluations, accountKey, serverDate, openingTrade.PositionId, BehaviorRuleKind.LossZonePersistence,
                Math.Max(0, matchingLossZoneAttemptCount.Value - 1), null, policy.LossZonePersistenceThreshold,
                policy.EnabledRules.LossZonePersistence, now,
                baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);
        }

        var priorCompleted = CompletedBefore(scoped, openingTrade);
        var previousClosed = priorCompleted.LastOrDefault();
        var revenge = previousClosed is null
            ? 0m
            : CalculateRevengeScore(previousClosed, openingTrade, policy.RapidReentrySeconds, policy.LotEscalationMultiplier);
        Add(evaluations, accountKey, serverDate, openingTrade.PositionId, BehaviorRuleKind.RevengeScore,
            revenge, null, policy.RevengeScoreThreshold, policy.EnabledRules.RevengeScore, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var overtrade = CalculateOvertradeBurst(scoped, trades, serverDate, policy.OvertradeWindowMinutes);
        Add(evaluations, accountKey, serverDate, openingTrade.PositionId, BehaviorRuleKind.OvertradeBurst,
            overtrade.Value, overtrade.Baseline, policy.OvertradeBaselineMultiplier, policy.EnabledRules.OvertradeBurst,
            now, hardThreshold: policy.OvertradeAbsoluteCount, sampleCount: overtrade.SampleCount,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        var escalation = previousClosed is { NetPnl: < -0.01m, OpeningVolume: > 0m }
            ? openingTrade.OpeningVolume / previousClosed.OpeningVolume
            : 1m;
        Add(evaluations, accountKey, serverDate, openingTrade.PositionId, BehaviorRuleKind.SizeEscalationAfterLoss,
            escalation, null, policy.LotEscalationMultiplier, policy.EnabledRules.SizeEscalationAfterLoss, now,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        AddCooldownEvaluation(evaluations, accountKey, serverDate, openingTrade, priorCompleted, policy, now);

        var fixation = CalculatePriceFixation(scoped, trades, serverDate, symbolSpecifications);
        var currentBucketCount = scoped.Count(trade =>
            string.Equals(trade.Symbol, openingTrade.Symbol, StringComparison.OrdinalIgnoreCase) &&
            Bucket(trade, symbolSpecifications) == Bucket(openingTrade, symbolSpecifications));
        Add(evaluations, accountKey, serverDate, openingTrade.PositionId, BehaviorRuleKind.PriceFixationScore,
            currentBucketCount > 1 ? fixation.Value : 0m, fixation.Baseline, policy.PriceFixationScoreThreshold,
            policy.EnabledRules.PriceFixationScore, now, sampleCount: scoped.Length,
            minimumSamples: policy.PriceFixationMinimumTrades,
            baselineEnabled: policy.BaselineEnabled, hardLimitEnabled: policy.HardLimitEnabled);

        return BuildSummary(evaluations, trades, serverDate, policy);
    }

    private static BehaviorSummary BuildSummary(
        IReadOnlyCollection<BehaviorEvaluation> evaluations,
        IEnumerable<TradeRecord> trades,
        DateOnly serverDate,
        BehaviorPolicy policy)
    {
        var baselineDays = CountBaselineDays(trades, serverDate);
        var baselineReady = baselineDays >= 10;
        var normalized = evaluations.Select(item =>
            {
                if (baselineReady || item.Baseline is null)
                {
                    return item;
                }

                var triggered = item.Triggered && policy.HardLimitEnabled;
                var level = triggered && (item.Rule is BehaviorRuleKind.RevengeScore or BehaviorRuleKind.CooldownViolation)
                    ? BehaviorRiskLevel.Critical
                    : triggered ? BehaviorRiskLevel.Attention : BehaviorRiskLevel.Observing;
                return item with
                {
                    Summary = $"{item.Summary}（个人基线观察中）",
                    Triggered = triggered,
                    Level = level,
                };
            })
            .ToArray();
        var level = normalized.Any(item => item.Level == BehaviorRiskLevel.Critical)
            ? BehaviorRiskLevel.Critical
            : normalized.Any(item => item.Triggered)
                ? BehaviorRiskLevel.Attention
                : baselineReady ? BehaviorRiskLevel.Normal : BehaviorRiskLevel.Observing;
        return new BehaviorSummary(level, normalized, baselineDays, baselineReady);
    }

    private static void AddCooldownEvaluation(
        ICollection<BehaviorEvaluation> evaluations,
        string accountKey,
        DateOnly serverDate,
        TradeRecord openingTrade,
        IReadOnlyList<TradeRecord> priorCompleted,
        BehaviorPolicy policy,
        DateTimeOffset observedAtUtc)
    {
        var lossStreak = CountTrailingLosses(priorCompleted);
        var previousClosedAt = priorCompleted.LastOrDefault()?.ClosedAtUtc;
        var elapsedSeconds = previousClosedAt is null
            ? decimal.MaxValue
            : Math.Max(0m, (decimal)(openingTrade.OpenedAtUtc - previousClosedAt.Value).TotalSeconds);
        var triggered = policy.EnabledRules.CooldownViolation &&
                        policy.HardLimitEnabled &&
                        lossStreak >= policy.ConsecutiveLossThreshold &&
                        elapsedSeconds < policy.CooldownSeconds;
        var roundedElapsed = elapsedSeconds == decimal.MaxValue ? 0 : (int)Math.Round(elapsedSeconds);
        var remainingSeconds = triggered ? Math.Max(0, policy.CooldownSeconds - roundedElapsed) : 0;
        var summary = triggered
            ? $"最近连续亏损 {lossStreak} 笔，{roundedElapsed} 秒后又开仓；冷静期 {policy.CooldownSeconds} 秒，还剩 {remainingSeconds} 秒"
            : $"冷静期 {policy.CooldownSeconds} 秒，本次未违规";
        evaluations.Add(new BehaviorEvaluation(
            $"behavior:{accountKey}:{serverDate}:{BehaviorRuleKind.CooldownViolation}:{openingTrade.PositionId}:{observedAtUtc.Ticks}",
            accountKey,
            serverDate,
            openingTrade.PositionId,
            BehaviorRuleKind.CooldownViolation,
            roundedElapsed,
            null,
            policy.CooldownSeconds,
            triggered ? BehaviorRiskLevel.Critical : BehaviorRiskLevel.Normal,
            triggered,
            summary,
            observedAtUtc));
    }

    private static void Add(
        ICollection<BehaviorEvaluation> evaluations,
        string accountKey,
        DateOnly serverDate,
        long? positionId,
        BehaviorRuleKind rule,
        decimal value,
        decimal? baseline,
        decimal threshold,
        bool enabled,
        DateTimeOffset observedAtUtc,
        decimal? hardThreshold = null,
        int sampleCount = 0,
        int minimumSamples = 0,
        bool baselineEnabled = true,
        bool hardLimitEnabled = true)
    {
        var relativeTriggered = baselineEnabled && baseline is not null && baseline > 0m && value >= baseline.Value * threshold;
        var hardTriggered = hardLimitEnabled && value >= (hardThreshold ?? threshold) && sampleCount >= minimumSamples;
        var triggered = enabled && (relativeTriggered || hardTriggered);
        var level = triggered && (rule is BehaviorRuleKind.RevengeScore or BehaviorRuleKind.CooldownViolation)
            ? BehaviorRiskLevel.Critical
            : triggered ? BehaviorRiskLevel.Attention : BehaviorRiskLevel.Normal;
        evaluations.Add(new BehaviorEvaluation(
            $"behavior:{accountKey}:{serverDate}:{rule}:{positionId?.ToString() ?? "day"}:{observedAtUtc.Ticks}",
            accountKey,
            serverDate,
            positionId,
            rule,
            value,
            baseline,
            threshold,
            level,
            triggered,
            FormatSummary(rule, value, baseline, threshold),
            observedAtUtc));
    }

    private static int CalculateReentryCount(
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<LossZoneAttempt> attempts,
        IReadOnlyDictionary<string, SymbolSpecification>? symbolSpecifications)
    {
        var fromZones = attempts
            .GroupBy(item => item.ZoneId)
            .Select(group => Math.Max(0, group.Select(item => item.PositionId).Distinct().Count() - 1))
            .DefaultIfEmpty(0)
            .Max();
        var fromPrices = trades.GroupBy(trade => (trade.Symbol, Bucket(trade, symbolSpecifications)))
            .Select(group => Math.Max(0, group.Count() - 1)).DefaultIfEmpty(0).Max();
        return Math.Max(fromZones, fromPrices);
    }

    private static int CalculateLossZonePersistence(IReadOnlyCollection<LossZoneAttempt> attempts) =>
        attempts
            .GroupBy(attempt => attempt.ZoneId)
            .Select(group => Math.Max(0, group.Select(attempt => attempt.PositionId).Distinct().Count() - 1))
            .DefaultIfEmpty(0)
            .Max();

    private static (decimal Value, decimal? Baseline) CalculateRevengeScore(
        IReadOnlyList<TradeRecord> trades,
        int rapidSeconds,
        decimal lotMultiplier)
    {
        decimal maximum = 0m;
        foreach (var current in trades)
        {
            var previous = MostRecentCompletedBefore(trades, current);
            if (previous is null)
            {
                continue;
            }

            maximum = Math.Max(maximum, CalculateRevengeScore(previous, current, rapidSeconds, lotMultiplier));
        }

        return (maximum, null);
    }

    private static decimal CalculateRevengeScore(
        TradeRecord previous,
        TradeRecord current,
        int rapidSeconds,
        decimal lotMultiplier)
    {
        if (previous.NetPnl >= -0.01m || previous.ClosedAtUtc is null)
        {
            return 0m;
        }

        var elapsed = (current.OpenedAtUtc - previous.ClosedAtUtc.Value).TotalSeconds;
        if (elapsed < 0d)
        {
            return 0m;
        }

        var speed = rapidSeconds <= 0 ? 0m : Math.Clamp(1m - (decimal)elapsed / rapidSeconds, 0m, 1m);
        var ratio = previous.OpeningVolume <= 0m ? 1m : current.OpeningVolume / previous.OpeningVolume;
        var size = lotMultiplier <= 1m ? ratio > 1m ? 1m : 0m : Math.Clamp((ratio - 1m) / (lotMultiplier - 1m), 0m, 1m);
        return speed * 60m + size * 40m;
    }

    private static (decimal Value, decimal? Baseline, int SampleCount) CalculateOvertradeBurst(
        IReadOnlyList<TradeRecord> currentDay,
        IEnumerable<TradeRecord> allTrades,
        DateOnly serverDate,
        int windowMinutes)
    {
        var latest = currentDay.Select(item => item.OpenedAtUtc).DefaultIfEmpty(DateTimeOffset.UtcNow).Max();
        var current = currentDay.Count(item => latest - item.OpenedAtUtc <= TimeSpan.FromMinutes(windowMinutes));
        var baselineValues = allTrades
            .Where(item => item.OpenServerDate < serverDate)
            .GroupBy(item => item.OpenServerDate)
            .Select(group => MaxRollingCount(group.Select(item => item.OpenedAtUtc).OrderBy(item => item).ToArray(), windowMinutes))
            .OrderBy(item => item)
            .TakeLast(20)
            .ToArray();
        var baseline = baselineValues.Length == 0 ? (decimal?)null : baselineValues[baselineValues.Length / 2];
        return (current, baseline, currentDay.Count);
    }

    private static decimal CalculateSizeEscalation(IReadOnlyList<TradeRecord> trades)
    {
        var maximum = 1m;
        foreach (var current in trades)
        {
            var previous = MostRecentCompletedBefore(trades, current);
            if (previous is { NetPnl: < -0.01m, OpeningVolume: > 0m })
            {
                maximum = Math.Max(maximum, current.OpeningVolume / previous.OpeningVolume);
            }
        }

        return maximum;
    }

    private static int CalculateCooldownViolations(
        IReadOnlyList<TradeRecord> trades,
        int lossThreshold,
        int cooldownSeconds)
    {
        var violations = 0;
        foreach (var current in trades)
        {
            var priorCompleted = CompletedBefore(trades, current);
            if (CountTrailingLosses(priorCompleted) < lossThreshold)
            {
                continue;
            }

            var previousClosedAt = priorCompleted[^1].ClosedAtUtc!.Value;
            var elapsed = current.OpenedAtUtc - previousClosedAt;
            if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(cooldownSeconds))
            {
                violations++;
            }
        }

        return violations;
    }

    private static IReadOnlyList<TradeRecord> CompletedBefore(
        IReadOnlyList<TradeRecord> trades,
        TradeRecord openingTrade) =>
        trades
            .Where(trade =>
                trade.PositionId != openingTrade.PositionId &&
                trade.IsComplete &&
                trade.ClosedAtUtc is not null &&
                trade.ClosedAtUtc.Value <= openingTrade.OpenedAtUtc)
            .OrderBy(trade => trade.ClosedAtUtc)
            .ToArray();

    private static TradeRecord? MostRecentCompletedBefore(
        IReadOnlyList<TradeRecord> trades,
        TradeRecord openingTrade) => CompletedBefore(trades, openingTrade).LastOrDefault();

    private static int CountTrailingLosses(IReadOnlyList<TradeRecord> completedByCloseTime)
    {
        var count = 0;
        for (var index = completedByCloseTime.Count - 1; index >= 0; index--)
        {
            if (completedByCloseTime[index].NetPnl >= -0.01m)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static (decimal Value, decimal? Baseline) CalculatePriceFixation(
        IReadOnlyCollection<TradeRecord> currentTrades,
        IEnumerable<TradeRecord> allTrades,
        DateOnly serverDate,
        IReadOnlyDictionary<string, SymbolSpecification>? symbolSpecifications)
    {
        if (currentTrades.Count < 3)
        {
            return (0m, null);
        }

        var totalRepeats = currentTrades.Count - 1;
        var largest = currentTrades.GroupBy(trade => (trade.Symbol, Bucket(trade, symbolSpecifications))).Max(group => group.Count() - 1);
        var score = totalRepeats <= 0 ? 0m : largest * 100m / totalRepeats;
        var baselineValues = allTrades
            .Where(trade => trade.OpenServerDate < serverDate)
            .GroupBy(trade => trade.OpenServerDate)
            .Select(group =>
            {
                var repeats = group.Count() - 1;
                var concentrated = group
                    .GroupBy(trade => (trade.Symbol, Bucket(trade, symbolSpecifications)))
                    .Select(item => item.Count() - 1)
                    .DefaultIfEmpty(0)
                    .Max();
                return repeats <= 0 ? 0m : concentrated * 100m / repeats;
            })
            .TakeLast(20)
            .OrderBy(value => value)
            .ToArray();
        var baseline = baselineValues.Length == 0 ? (decimal?)null : baselineValues[baselineValues.Length / 2];
        return (score, baseline);
    }

    private static int CountBaselineDays(IEnumerable<TradeRecord> trades, DateOnly currentDay) =>
        trades.Where(item => item.OpenServerDate < currentDay).Select(item => item.OpenServerDate).Distinct().TakeLast(20).Count();

    private static int MaxRollingCount(IReadOnlyList<DateTimeOffset> times, int windowMinutes)
    {
        var maximum = 0;
        for (var left = 0; left < times.Count; left++)
        {
            var right = left;
            while (right < times.Count && times[right] - times[left] <= TimeSpan.FromMinutes(windowMinutes)) right++;
            maximum = Math.Max(maximum, right - left);
        }

        return maximum;
    }

    private static long? LatestPositionId(IReadOnlyList<TradeRecord> trades) =>
        trades.OrderByDescending(item => item.OpenedAtUtc).Select(item => (long?)item.PositionId).FirstOrDefault();

    private static decimal Bucket(
        TradeRecord trade,
        IReadOnlyDictionary<string, SymbolSpecification>? symbolSpecifications)
    {
        SymbolSpecification? specification = null;
        symbolSpecifications?.TryGetValue(trade.Symbol, out specification);
        return PriceDistancePolicy.Bucket(trade.EntryPrice, specification);
    }

    private static string FormatSummary(BehaviorRuleKind rule, decimal value, decimal? baseline, decimal threshold) => rule switch
    {
        BehaviorRuleKind.ReentryCount => $"同一价格区域重复进入 {value:0} 次",
        BehaviorRuleKind.LossZonePersistence => $"亏损区域重复攻击 {value:0} 次",
        BehaviorRuleKind.RevengeScore => $"报复性交易评分 {value:0}",
        BehaviorRuleKind.OvertradeBurst => $"最近交易频率 {value:0} 笔/窗口" + (baseline is null ? string.Empty : $"，个人基线 {baseline:0.##}"),
        BehaviorRuleKind.PlanDeviationRate => $"计划外交易占比 {value:0.##}%",
        BehaviorRuleKind.ProfitGiveback => $"盈利回吐 {value:0.##}%",
        BehaviorRuleKind.SizeEscalationAfterLoss => $"亏损后最大仓位放大 {value:0.##} 倍",
        BehaviorRuleKind.CooldownViolation => $"冷静期内重进 {value:0} 次",
        BehaviorRuleKind.PriceFixationScore => $"价格执着评分 {value:0}",
        _ => $"行为值 {value:0.##}（阈值 {threshold:0.##}）",
    };
}
