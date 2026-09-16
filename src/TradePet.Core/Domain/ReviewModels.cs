using System.Text.Json.Serialization;

namespace TradePet.Core.Domain;

public enum PlanComplianceStatus
{
    Unclassified,
    Matched,
    OutsidePlan,
    ManualInside,
    ManualOutside,
}

public enum BehaviorPreset
{
    Conservative,
    Balanced,
    Loose,
}

public enum BehaviorRiskLevel
{
    Observing,
    Normal,
    Attention,
    Critical,
}

public enum BehaviorRuleKind
{
    ReentryCount,
    LossZonePersistence,
    RevengeScore,
    OvertradeBurst,
    PlanDeviationRate,
    ProfitGiveback,
    SizeEscalationAfterLoss,
    CooldownViolation,
    PriceFixationScore,
}

public enum ReviewPeriodPreset
{
    Today,
    ThisWeek,
    ThisMonth,
    LastThirtyDays,
    LastNinetyDays,
    ThisYear,
    AllHistory,
    Custom,
}

public sealed record StructuredTradePlan(
    string Id,
    string AccountKey,
    DateOnly ServerDate,
    string Symbol,
    TradeSide Side,
    decimal? ReferenceEntryPrice,
    decimal? EntryLow,
    decimal? EntryHigh,
    decimal? StopPrice,
    decimal? TargetPrice,
    string Strategy,
    string Setup,
    IReadOnlyList<string> Tags,
    string Notes,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    [JsonIgnore]
    public decimal? PlannedRiskMultiple =>
        ReferenceEntryPrice is null || StopPrice is null || TargetPrice is null ||
        ReferenceEntryPrice == StopPrice
            ? null
            : Math.Abs(TargetPrice.Value - ReferenceEntryPrice.Value) /
              Math.Abs(ReferenceEntryPrice.Value - StopPrice.Value);
}

public sealed record TradeReviewMetadata(
    string AccountKey,
    long PositionId,
    string? PlanId,
    PlanComplianceStatus ComplianceStatus,
    string Strategy,
    string Setup,
    IReadOnlyList<string> Tags,
    bool UserEdited,
    DateTimeOffset UpdatedAtUtc);

public sealed record TradeExcursion(
    string AccountKey,
    long PositionId,
    decimal MinimumPnl,
    decimal MaximumPnl,
    decimal? InitialRiskAmount,
    decimal? PlannedRiskMultiple,
    decimal? ActualRiskMultiple,
    DateTimeOffset FirstSampleAtUtc,
    DateTimeOffset LastSampleAtUtc,
    long CoveredMilliseconds,
    long HoldingMilliseconds,
    bool StartedAtOpen,
    bool IsComplete,
    long MaximumGapMilliseconds = long.MaxValue,
    string AlgorithmVersion = "legacy-extrema-v1")
{
    [JsonIgnore]
    public decimal CoveragePercentage => HoldingMilliseconds <= 0
        ? 0m
        : Math.Clamp(CoveredMilliseconds * 100m / HoldingMilliseconds, 0m, 100m);

    [JsonIgnore]
    public bool HasReliableInitialRisk => StartedAtOpen && InitialRiskAmount is > 0m;

    [JsonIgnore]
    public bool IsReliable =>
        AlgorithmVersion == "position-pnl-v1" && StartedAtOpen && IsComplete &&
        CoveragePercentage >= 90m && MaximumGapMilliseconds <= 5_000;
}

public sealed record AccountCashFlow(
    string AccountKey,
    long Ticket,
    string Type,
    decimal Amount,
    DateTimeOffset OccurredAtUtc);

public sealed record EquitySample(
    string AccountKey,
    DateOnly ServerDate,
    DateTimeOffset CapturedAtUtc,
    decimal Balance,
    decimal Equity,
    decimal FloatingPnl,
    bool HasOpenPosition);

public sealed record DrawdownEpisode(
    string Id,
    string AccountKey,
    string CurveKind,
    DateOnly StartServerDate,
    DateOnly? EndServerDate,
    DateTimeOffset PeakAtUtc,
    DateTimeOffset TroughAtUtc,
    DateTimeOffset? RecoveredAtUtc,
    decimal PeakValue,
    decimal TroughValue,
    decimal DrawdownAmount,
    decimal? DrawdownPercentage);

public sealed record HistorySyncState(
    string AccountKey,
    int RangeYear,
    bool IsComplete,
    int DealCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record BehaviorRuleSwitches(
    bool ReentryCount = true,
    bool LossZonePersistence = true,
    bool RevengeScore = true,
    bool OvertradeBurst = true,
    bool PlanDeviationRate = true,
    bool ProfitGiveback = true,
    bool SizeEscalationAfterLoss = true,
    bool CooldownViolation = true,
    bool PriceFixationScore = true)
{
    public bool IsEnabled(BehaviorRuleKind kind) => kind switch
    {
        BehaviorRuleKind.ReentryCount => ReentryCount,
        BehaviorRuleKind.LossZonePersistence => LossZonePersistence,
        BehaviorRuleKind.RevengeScore => RevengeScore,
        BehaviorRuleKind.OvertradeBurst => OvertradeBurst,
        BehaviorRuleKind.PlanDeviationRate => PlanDeviationRate,
        BehaviorRuleKind.ProfitGiveback => ProfitGiveback,
        BehaviorRuleKind.SizeEscalationAfterLoss => SizeEscalationAfterLoss,
        BehaviorRuleKind.CooldownViolation => CooldownViolation,
        BehaviorRuleKind.PriceFixationScore => PriceFixationScore,
        _ => false,
    };
}

public sealed record BehaviorPolicy(
    BehaviorPreset Preset,
    int RapidReentrySeconds,
    int CooldownSeconds,
    int ConsecutiveLossThreshold,
    decimal LotEscalationMultiplier,
    int OvertradeWindowMinutes,
    decimal OvertradeBaselineMultiplier,
    int OvertradeAbsoluteCount,
    decimal RevengeScoreThreshold,
    int ReentryThreshold,
    decimal PlanDeviationRateThreshold,
    decimal PriceFixationScoreThreshold,
    int PriceFixationMinimumTrades,
    bool BaselineEnabled,
    bool HardLimitEnabled,
    BehaviorRuleSwitches EnabledRules)
{
    public int LossZonePersistenceThreshold { get; init; } = ReentryThreshold;
    public decimal ProfitGivebackThreshold { get; init; } = 50m;

    public static BehaviorPolicy Conservative { get; } = new BehaviorPolicy(
        BehaviorPreset.Conservative, 120, 120, 2, 1.5m, 30, 1.5m, 4, 45m, 2, 20m, 50m, 3,
        true, true, new BehaviorRuleSwitches()) with { LossZonePersistenceThreshold = 2 };

    public static BehaviorPolicy Balanced { get; } = new BehaviorPolicy(
        BehaviorPreset.Balanced, 60, 60, 2, 2m, 30, 2m, 6, 60m, 3, 35m, 60m, 3,
        true, true, new BehaviorRuleSwitches()) with { LossZonePersistenceThreshold = 3 };

    public static BehaviorPolicy Loose { get; } = new BehaviorPolicy(
        BehaviorPreset.Loose, 30, 30, 3, 3m, 30, 3m, 8, 75m, 4, 50m, 70m, 4,
        true, true, new BehaviorRuleSwitches()) with { LossZonePersistenceThreshold = 4 };
}

public sealed record BehaviorPolicySet(
    string AccountKey,
    BehaviorPreset SelectedPreset,
    BehaviorPolicy Conservative,
    BehaviorPolicy Balanced,
    BehaviorPolicy Loose)
{
    [JsonIgnore]
    public BehaviorPolicy Selected => SelectedPreset switch
    {
        BehaviorPreset.Conservative => Conservative,
        BehaviorPreset.Loose => Loose,
        _ => Balanced,
    };

    public static BehaviorPolicySet CreateDefault(string accountKey) => new(
        accountKey,
        BehaviorPreset.Balanced,
        BehaviorPolicy.Conservative,
        BehaviorPolicy.Balanced,
        BehaviorPolicy.Loose);
}

public sealed record BehaviorEvaluation(
    string Id,
    string AccountKey,
    DateOnly ServerDate,
    long? PositionId,
    BehaviorRuleKind Rule,
    decimal Value,
    decimal? Baseline,
    decimal Threshold,
    BehaviorRiskLevel Level,
    bool Triggered,
    string Summary,
    DateTimeOffset ObservedAtUtc);

public sealed record ReviewFilter(
    string AccountKey,
    DateOnly FromServerDate,
    DateOnly ToServerDate,
    string? Symbol = null,
    TradeSide? Side = null,
    string? Strategy = null,
    string? Setup = null,
    string? Tag = null,
    int ServerUtcOffsetSeconds = 0);

public sealed record PerformanceSummary(
    int TradeCount,
    int WinCount,
    int LossCount,
    int BreakevenCount,
    decimal NetPnl,
    decimal WinRate,
    decimal? ProfitFactor,
    decimal Expectancy,
    decimal AverageWin,
    decimal AverageLoss,
    decimal? AverageWinLossRatio,
    decimal MaximumDrawdown,
    decimal AverageDrawdown,
    decimal? RecoveryFactor,
    int MaximumWinStreak,
    int MaximumLossStreak,
    TimeSpan AverageHoldingTime,
    decimal TotalOpeningVolume,
    decimal AverageOpeningVolume,
    decimal MaximumOpeningVolume,
    decimal DailyWinRate,
    int BreakevenDayCount,
    decimal? AveragePlannedRiskMultiple,
    decimal? AverageActualRiskMultiple,
    decimal? AverageAdverseExcursion,
    decimal? AverageFavorableExcursion,
    decimal AdvancedDataCoverage,
    decimal? MaximumDrawdownPercentage = null,
    decimal? AverageAdverseExcursionRiskMultiple = null,
    decimal? AverageFavorableExcursionRiskMultiple = null);

public sealed record GroupMetricRow(
    string Group,
    int TradeCount,
    decimal WinRate,
    decimal NetPnl,
    decimal? ProfitFactor,
    decimal Expectancy,
    decimal AveragePnl = 0m,
    decimal? AverageActualRiskMultiple = null,
    int RiskCoveredCount = 0,
    decimal RiskCoveragePercentage = 0m,
    decimal Commission = 0m,
    decimal Swap = 0m,
    decimal OtherFees = 0m,
    IReadOnlyList<TradeKey>? Trades = null)
{
    public bool HasSmallSampleWarning => TradeCount < 30;
    public decimal TotalFees => Commission + Swap + OtherFees;
}

public sealed record BehaviorSummary(
    BehaviorRiskLevel RiskLevel,
    IReadOnlyList<BehaviorEvaluation> Evaluations,
    int BaselineDayCount,
    bool BaselineReady);

public sealed record ReviewSnapshot(
    ReviewFilter Filter,
    PerformanceSummary Performance,
    BehaviorSummary Behavior,
    IReadOnlyList<GroupMetricRow> SidePerformance,
    IReadOnlyList<GroupMetricRow> SymbolPerformance,
    IReadOnlyList<GroupMetricRow> WeekdayPerformance,
    IReadOnlyList<GroupMetricRow> HourPerformance,
    IReadOnlyList<GroupMetricRow> DurationPerformance,
    IReadOnlyList<GroupMetricRow> StrategyPerformance,
    IReadOnlyList<GroupMetricRow> SetupPerformance,
    IReadOnlyList<GroupMetricRow> TagPerformance,
    IReadOnlyList<TradeRecord> Trades);
