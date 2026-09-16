using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class TradePlanMatcher
{
    public PlanValidationResult Validate(StructuredTradePlan plan)
    {
        if (plan.EntryLow is null || plan.EntryHigh is null ||
            plan.StopPrice is null || plan.TargetPrice is null)
        {
            return new(false, "入场区、止损和目标必须完整填写。");
        }

        var low = Math.Min(plan.EntryLow.Value, plan.EntryHigh.Value);
        var high = Math.Max(plan.EntryLow.Value, plan.EntryHigh.Value);
        if (plan.ReferenceEntryPrice is not null &&
            (plan.ReferenceEntryPrice < low || plan.ReferenceEntryPrice > high))
        {
            return new(false, "参考入场价必须位于入场区内。");
        }

        var directionValid = plan.Side == TradeSide.Buy
            ? plan.StopPrice < low && plan.TargetPrice > high
            : plan.StopPrice > high && plan.TargetPrice < low;
        return directionValid
            ? new(true, null)
            : new(false, plan.Side == TradeSide.Buy
                ? "买入计划的止损必须低于入场区，目标必须高于入场区。"
                : "卖出计划的止损必须高于入场区，目标必须低于入场区。");
    }

    public StructuredTradePlan? FindBest(
        TradeRecord trade,
        IEnumerable<StructuredTradePlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        return plans
            .Where(plan => WasEffectiveAtOpen(trade, plan) &&
                          plan.AccountKey == trade.AccountKey &&
                          plan.ServerDate == trade.OpenServerDate &&
                          string.Equals(plan.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase) &&
                          plan.Side == trade.Side &&
                          plan.EntryLow is not null && plan.EntryHigh is not null &&
                          trade.EntryPrice >= Math.Min(plan.EntryLow.Value, plan.EntryHigh.Value) &&
                          trade.EntryPrice <= Math.Max(plan.EntryLow.Value, plan.EntryHigh.Value))
            .OrderBy(plan => Math.Abs(plan.EntryHigh!.Value - plan.EntryLow!.Value))
            .ThenBy(plan => Math.Abs((plan.ReferenceEntryPrice ?? ((plan.EntryLow!.Value + plan.EntryHigh!.Value) / 2m)) - trade.EntryPrice))
            .ThenByDescending(plan => plan.CreatedAtUtc)
            .FirstOrDefault();
    }

    public TradeReviewMetadata CreateAutomaticMetadata(
        TradeRecord trade,
        IEnumerable<StructuredTradePlan> plans,
        DateTimeOffset observedAtUtc)
    {
        var applicable = plans
            .Where(plan => IsApplicableAtOpen(trade, plan))
            .ToArray();
        var matched = FindBest(trade, applicable);
        if (matched is not null)
        {
            return CreateMatchedMetadata(trade, matched) with { UpdatedAtUtc = observedAtUtc };
        }

        var nearest = applicable
            .OrderBy(plan => DistanceToRange(trade.EntryPrice, plan.EntryLow, plan.EntryHigh))
            .ThenByDescending(plan => plan.CreatedAtUtc)
            .FirstOrDefault();
        return new TradeReviewMetadata(
            trade.AccountKey,
            trade.PositionId,
            nearest?.Id,
            nearest is null ? PlanComplianceStatus.Unclassified : PlanComplianceStatus.OutsidePlan,
            nearest?.Strategy ?? string.Empty,
            nearest?.Setup ?? string.Empty,
            nearest?.Tags ?? [],
            false,
            observedAtUtc);
    }

    public TradeReviewMetadata CreateMatchedMetadata(TradeRecord trade, StructuredTradePlan plan) =>
        new(
            trade.AccountKey,
            trade.PositionId,
            plan.Id,
            PlanComplianceStatus.Matched,
            plan.Strategy,
            plan.Setup,
            plan.Tags,
            false,
            DateTimeOffset.UtcNow);

    private static bool IsApplicableAtOpen(TradeRecord trade, StructuredTradePlan plan) =>
        WasEffectiveAtOpen(trade, plan) &&
        plan.AccountKey == trade.AccountKey &&
        plan.ServerDate == trade.OpenServerDate &&
        string.Equals(plan.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase) &&
        plan.Side == trade.Side;

    private static bool WasEffectiveAtOpen(TradeRecord trade, StructuredTradePlan plan) =>
        plan.CreatedAtUtc <= trade.OpenedAtUtc &&
        (plan.IsActive || plan.UpdatedAtUtc > trade.OpenedAtUtc);

    private static decimal DistanceToRange(decimal price, decimal? lowValue, decimal? highValue)
    {
        if (lowValue is null || highValue is null)
        {
            return decimal.MaxValue;
        }
        var low = Math.Min(lowValue.Value, highValue.Value);
        var high = Math.Max(lowValue.Value, highValue.Value);
        return price < low ? low - price : price > high ? price - high : 0m;
    }
}

public sealed record PlanValidationResult(bool IsValid, string? Error);
