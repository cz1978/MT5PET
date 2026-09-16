using System.Globalization;

namespace TradePet.Core.Domain;

public sealed record AccountScope(string Server, long Login)
{
    public string AccountKey => $"{Server.Trim()}|{Login.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record AccountSnapshot(
    AccountScope Scope,
    string Currency,
    decimal Balance,
    decimal Equity,
    decimal FloatingPnl,
    int MarginMode,
    DateTimeOffset CapturedAtUtc);

public sealed record SymbolSpecification(
    string Symbol,
    decimal Point,
    decimal TickSize,
    int Digits)
{
    public decimal PriceStep => TickSize > 0m ? TickSize : Point;
}

public sealed record PositionSnapshot(
    long Ticket,
    long PositionId,
    string Symbol,
    TradeSide Side,
    decimal Volume,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal Profit,
    decimal StopLoss,
    decimal TakeProfit,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset CapturedAtUtc,
    decimal Swap = 0m,
    decimal? InitialRiskAmount = null);

public sealed record OrderSnapshot(
    long Ticket,
    string Symbol,
    string Type,
    decimal Volume,
    decimal Price,
    decimal StopLoss,
    decimal TakeProfit,
    DateTimeOffset CreatedAtUtc);

public sealed record DealRecord(
    long Ticket,
    long OrderTicket,
    long PositionId,
    string Symbol,
    TradeSide Side,
    DealEntryKind EntryKind,
    decimal Volume,
    decimal Price,
    decimal Profit,
    decimal Commission,
    decimal Swap,
    decimal Fee,
    DateTimeOffset OccurredAtUtc)
{
    public decimal NetPnl => Profit + Commission + Swap + Fee;
}

public enum EconomicEventImportance
{
    None,
    Low,
    Moderate,
    High,
}

public enum EconomicEventType
{
    Event,
    Indicator,
    Holiday,
}

public sealed record EconomicCalendarEvent(
    long ValueId,
    long EventId,
    DateTimeOffset ScheduledAtUtc,
    string CountryCode,
    string CountryName,
    string Currency,
    string Name,
    EconomicEventType Type,
    EconomicEventImportance Importance,
    string TimeMode,
    string Unit,
    string Multiplier,
    int Digits,
    decimal? PreviousValue,
    decimal? RevisedPreviousValue,
    decimal? ForecastValue,
    decimal? ActualValue,
    string Impact,
    string SourceUrl,
    string EventCode);

public sealed record TradeRecord(
    string AccountKey,
    long PositionId,
    string Symbol,
    TradeSide Side,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    DateOnly OpenServerDate,
    DateOnly? CloseServerDate,
    decimal EntryPrice,
    decimal? ExitPrice,
    decimal OpeningVolume,
    decimal MaximumVolume,
    decimal RemainingVolume,
    decimal NetPnl,
    bool IsComplete);

public sealed record PriceAnchor(DateTimeOffset? Time, decimal? Price);

public sealed record ChartObjectSnapshot(
    string TerminalId,
    long ChartId,
    string ObjectName,
    string Symbol,
    string Timeframe,
    ChartObjectKind Kind,
    IReadOnlyList<PriceAnchor> Anchors,
    string? Text,
    int ColorArgb,
    DateTimeOffset CapturedAtUtc,
    bool IsDeleted = false)
{
    public string ObjectKey => $"{TerminalId}/{ChartId}/{ObjectName}";
}

public sealed record PlanItem(
    string Id,
    string AccountKey,
    DateOnly ServerDate,
    string ObjectKey,
    PlanCategory Category,
    string Symbol,
    decimal? PriceLow,
    decimal? PriceHigh,
    string? Text,
    bool IsActive,
    DateTimeOffset UpdatedAtUtc);

public sealed record LossZoneState(
    string Id,
    string AccountKey,
    DateOnly ServerDate,
    string Symbol,
    decimal CenterPrice,
    decimal Tolerance,
    int AttemptCount,
    int LossCount,
    decimal CumulativeLoss,
    DateTimeOffset LastAttemptAtUtc);

public sealed record LossZoneAttempt(
    string Id,
    string ZoneId,
    long PositionId,
    TradeSide Side,
    decimal EntryPrice,
    decimal OpeningVolume,
    decimal? NetPnl,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record DailyPlanSettings(
    decimal? DailyTarget,
    decimal? DailyLoss,
    int? MaximumTrades,
    decimal? MaximumLot,
    int ConsecutiveLossThreshold,
    int RapidReentrySeconds,
    decimal LotEscalationMultiplier,
    bool StopLossReminderEnabled,
    int StopLossReminderSeconds,
    GivebackMode GivebackMode,
    decimal GivebackValue)
{
    public static DailyPlanSettings BalancedDefault { get; } = new(
        null,
        null,
        null,
        null,
        2,
        60,
        2m,
        false,
        60,
        GivebackMode.Percentage,
        50m);
}

public sealed record FloatingLossAlertLevel(
    int Stage,
    bool Enabled,
    decimal LossPercentage);

public sealed record FloatingLossAlertPolicy(
    IReadOnlyList<FloatingLossAlertLevel> Levels)
{
    public static FloatingLossAlertPolicy BalancedDefault { get; } = new(CreateDefaultLevels());

    public FloatingLossAlertPolicy Normalize()
    {
        var configured = (Levels ?? [])
            .Where(level => level.Stage is >= 1 and <= 3)
            .GroupBy(level => level.Stage)
            .ToDictionary(group => group.Key, group => group.Last());
        return new FloatingLossAlertPolicy(CreateDefaultLevels()
            .Select(fallback => configured.TryGetValue(fallback.Stage, out var level)
                ? level with { LossPercentage = Math.Clamp(level.LossPercentage, 0.01m, 100m) }
                : fallback)
            .ToArray());
    }

    private static FloatingLossAlertLevel[] CreateDefaultLevels() =>
    [
        new(1, true, 1m),
        new(2, true, 2m),
        new(3, true, 3m),
    ];
}

public sealed record FloatingLossExposure(
    string Symbol,
    TradeSide Side,
    decimal Volume,
    decimal FloatingPnl,
    int PositionCount);

public sealed record FloatingLossAlertTrigger(
    int Stage,
    decimal ThresholdPercentage,
    decimal ActualLossPercentage,
    decimal FloatingLoss,
    decimal Balance,
    FloatingLossExposure? PrimaryExposure);

public sealed record FloatingLossAlertEvaluation(
    decimal LossPercentage,
    decimal EpisodePeakLossPercentage,
    IReadOnlyList<int> ActiveStages,
    IReadOnlyList<FloatingLossAlertTrigger> NewTriggers);

public sealed record DailyState(
    string AccountKey,
    DateOnly ServerDate,
    decimal RealizedPnl,
    decimal FloatingPnl,
    decimal HighWaterPnl,
    decimal Giveback,
    int TradeCount,
    int WinCount,
    int LossCount,
    int ConsecutiveLosses,
    decimal MaximumExposure,
    bool TargetAlerted,
    bool LossAlerted,
    bool GivebackAlerted,
    bool TradeLimitAlerted = false,
    bool LotLimitAlerted = false,
    DateTimeOffset? TargetReachedAtUtc = null,
    string TargetRuleVersion = "",
    decimal? TargetAmountAtReach = null,
    int ConsecutiveLossThresholdAtObservation = 0);

public sealed record TimelineEvent(
    string Id,
    string AccountKey,
    DateOnly ServerDate,
    DateTimeOffset OccurredAtUtc,
    TimelineKind Kind,
    string Summary,
    string DetailsJson);

public sealed record PetState(
    PetMood Mood,
    PetActivity Activity,
    TradeContextState TradeContext,
    AlertPriority? ActiveAlertPriority,
    DateTimeOffset ActivityStartedAtUtc);

public sealed record TradeDomainEvent(
    TradeDomainEventKind Kind,
    long PositionId,
    string Symbol,
    TradeSide Side,
    decimal VolumeDelta,
    PositionSnapshot? Previous,
    PositionSnapshot? Current,
    DateTimeOffset ObservedAtUtc);

public sealed record RuleFact(
    RuleFactKind Kind,
    AlertPriority Priority,
    int DisplayOrder,
    string Summary,
    string Detail);

public sealed record CombinedAlert(
    string Id,
    AlertPriority Priority,
    string Headline,
    IReadOnlyList<RuleFact> Facts,
    DateTimeOffset OccurredAtUtc);

public sealed record DailyCalculationResult(
    DailyState State,
    IReadOnlyList<RuleFact> NewFacts);

public sealed record LossZoneOpenResult(
    LossZoneState? Zone,
    LossZoneAttempt? Attempt,
    CombinedAlert? Alert,
    IReadOnlyList<RuleFact> AllFacts);

public sealed record LossZoneCloseResult(
    LossZoneState? Zone,
    LossZoneAttempt? Attempt);

public sealed record LossZoneProjection(
    IReadOnlyList<LossZoneState> Zones,
    IReadOnlyList<LossZoneAttempt> Attempts);

public sealed record PetBehaviorState(
    PetState Current,
    PetActivity? ResumeActivity,
    IReadOnlyList<PetActivity> RecentActivities,
    bool FocusMode);
