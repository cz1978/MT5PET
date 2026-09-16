using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class DailyStateCalculator
{
    private const decimal BreakevenEpsilon = 0.01m;
    public DailyCalculationResult Calculate(
        string accountKey,
        DateOnly serverDate,
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<PositionSnapshot> openPositions,
        DailyPlanSettings settings,
        DailyState? previousState,
        decimal? observedHighWaterPnl = null,
        decimal? realizedPnlOverride = null,
        DateTimeOffset? observedAtUtc = null)
    {
        var completed = trades
            .Where(trade => trade.IsComplete && trade.CloseServerDate == serverDate)
            .OrderBy(trade => trade.ClosedAtUtc)
            .ToArray();
        var openedCount = trades.Count(trade => trade.OpenServerDate == serverDate);
        var realized = realizedPnlOverride ?? completed.Sum(trade => trade.NetPnl);
        var floating = openPositions.Sum(position => position.Profit + position.Swap);
        var combined = realized + floating;
        var priorHigh = previousState?.HighWaterPnl ?? 0m;
        var highWater = Math.Max(0m, Math.Max(combined, observedHighWaterPnl ?? combined));
        var giveback = Math.Max(0m, highWater - combined);
        var consecutiveLossTrades = GetConsecutiveLosses(completed);
        var consecutiveLosses = consecutiveLossTrades.Count;
        var currentExposure = openPositions
            .GroupBy(position => (position.Symbol, position.Side))
            .Select(group => group.Sum(position => position.Volume))
            .DefaultIfEmpty(0m)
            .Max();
        var maximumExposure = Math.Max(
            previousState?.MaximumExposure ?? 0m,
            Math.Max(currentExposure, completed.Select(trade => trade.MaximumVolume).DefaultIfEmpty(0m).Max()));

        var facts = new List<RuleFact>();
        var targetAlerted = previousState?.TargetAlerted ?? false;
        var targetReachedAtUtc = previousState?.TargetReachedAtUtc;
        var targetRuleVersion = previousState?.TargetRuleVersion ?? string.Empty;
        var targetAmountAtReach = previousState?.TargetAmountAtReach;
        if (!targetAlerted && settings.DailyTarget is > 0m && combined >= settings.DailyTarget.Value)
        {
            targetAlerted = true;
            targetReachedAtUtc = observedAtUtc;
            targetRuleVersion = observedAtUtc is null ? string.Empty : CreateTargetRuleVersion(settings.DailyTarget.Value);
            targetAmountAtReach = observedAtUtc is null ? null : settings.DailyTarget.Value;
            facts.Add(new RuleFact(
                RuleFactKind.DailyTarget,
                AlertPriority.Important,
                20,
                "到目标了。",
                $"今日合计 {FormatSigned(combined)}"));
        }

        var lossAlerted = previousState?.LossAlerted ?? false;
        if (!lossAlerted && settings.DailyLoss is > 0m && combined <= -Math.Abs(settings.DailyLoss.Value))
        {
            lossAlerted = true;
            facts.Add(new RuleFact(
                RuleFactKind.DailyLoss,
                AlertPriority.Critical,
                5,
                "今天已经到你自己设的线了。",
                $"今日合计 {FormatSigned(combined)}"));
        }

        var givebackAlerted = previousState?.GivebackAlerted ?? false;
        if (highWater > priorHigh)
        {
            givebackAlerted = false;
        }

        var givebackThreshold = ResolveGivebackThreshold(settings, highWater);
        var targetWasReached = settings.DailyTarget is > 0m && highWater >= settings.DailyTarget.Value;
        if (!givebackAlerted && targetWasReached && givebackThreshold > 0m && giveback >= givebackThreshold)
        {
            givebackAlerted = true;
            facts.Add(new RuleFact(
                RuleFactKind.ProfitGiveback,
                AlertPriority.Important,
                30,
                $"今天最高 {FormatSigned(highWater)}，现在剩 {FormatSigned(combined)}。",
                $"已回吐 {giveback:0.##}"));
        }

        if (consecutiveLosses >= settings.ConsecutiveLossThreshold &&
            consecutiveLosses > (previousState?.ConsecutiveLosses ?? 0))
        {
            facts.Add(new RuleFact(
                RuleFactKind.ConsecutiveLosses,
                AlertPriority.Important,
                40,
                $"最近连续亏了 {consecutiveLosses} 笔完整交易。",
                FormatConsecutiveLossDetail(completed, consecutiveLossTrades)));
        }

        var tradeLimitAlerted = previousState?.TradeLimitAlerted ?? false;
        if (!tradeLimitAlerted && settings.MaximumTrades is > 0 && openedCount >= settings.MaximumTrades.Value)
        {
            tradeLimitAlerted = true;
            facts.Add(new RuleFact(
                RuleFactKind.MaximumTrades,
                AlertPriority.Important,
                10,
                $"今天已经开了 {openedCount} 笔。",
                $"你设定的当日入场提醒线是 {settings.MaximumTrades.Value} 笔；天禄只提醒，不会阻止下单。"));
        }

        var lotLimitAlerted = previousState?.LotLimitAlerted ?? false;
        if (!lotLimitAlerted && settings.MaximumLot is > 0m && currentExposure >= settings.MaximumLot.Value)
        {
            lotLimitAlerted = true;
            facts.Add(new RuleFact(
                RuleFactKind.MaximumLot,
                AlertPriority.Critical,
                15,
                $"当前同品种同方向仓位达到 {currentExposure:0.##} 手。",
                $"你设定的仓位提醒线是 {settings.MaximumLot.Value:0.##} 手；天禄只提醒，不会处理持仓。"));
        }

        var state = new DailyState(
            accountKey,
            serverDate,
            realized,
            floating,
            highWater,
            giveback,
            completed.Length,
            completed.Count(trade => trade.NetPnl > BreakevenEpsilon),
            completed.Count(trade => trade.NetPnl < -BreakevenEpsilon),
            consecutiveLosses,
            maximumExposure,
            targetAlerted,
            lossAlerted,
            givebackAlerted,
            tradeLimitAlerted,
            lotLimitAlerted,
            targetReachedAtUtc,
            targetRuleVersion,
            targetAmountAtReach,
            Math.Max(1, settings.ConsecutiveLossThreshold));
        return new DailyCalculationResult(state, facts);
    }

    public static string CreateTargetRuleVersion(decimal dailyTarget) =>
        $"daily-target/v1:{dailyTarget.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture)}";

    private static IReadOnlyList<TradeRecord> GetConsecutiveLosses(IReadOnlyList<TradeRecord> completed)
    {
        var firstLossIndex = completed.Count;
        for (var index = completed.Count - 1; index >= 0; index--)
        {
            if (completed[index].NetPnl >= -BreakevenEpsilon)
            {
                break;
            }

            firstLossIndex = index;
        }

        return completed.Skip(firstLossIndex).ToArray();
    }

    private static string FormatConsecutiveLossDetail(
        IReadOnlyList<TradeRecord> completed,
        IReadOnlyCollection<TradeRecord> consecutiveLossTrades)
    {
        var total = consecutiveLossTrades.Sum(trade => trade.NetPnl);
        var precedingIndex = completed.Count - consecutiveLossTrades.Count - 1;
        if (precedingIndex < 0)
        {
            return $"这段合计 {FormatSigned(total)}；当日记录从这段亏损开始。";
        }

        var preceding = completed[precedingIndex];
        var boundary = preceding.NetPnl > BreakevenEpsilon ? "盈利" : "保本";
        return $"这段合计 {FormatSigned(total)}；再前一笔为{boundary}，更早交易不计入连亏。";
    }

    private static decimal ResolveGivebackThreshold(DailyPlanSettings settings, decimal highWater) =>
        settings.GivebackMode == GivebackMode.Amount
            ? Math.Max(0m, settings.GivebackValue)
            : highWater * Math.Max(0m, settings.GivebackValue) / 100m;

    private static string FormatSigned(decimal value) => $"{(value >= 0m ? "+" : string.Empty)}{value:0.##}";
}
