using System.Text.Json.Serialization;

namespace TradePet.Core.Domain;

public readonly record struct TradeKey
{
    [JsonConstructor]
    public TradeKey(string accountKey, long positionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        AccountKey = accountKey;
        PositionId = positionId;
    }

    public string AccountKey { get; }
    public long PositionId { get; }

    public override string ToString() => $"{AccountKey}:{PositionId}";
}

public enum ReviewCompletionStatus
{
    Pending,
    Draft,
    Reviewed,
    NeedsReview,
}

public enum ReviewEvidenceSource
{
    Mt5Deal,
    LiveObservation,
    PreTradePlan,
    UserContemporaneous,
    UserBackfill,
    HistoricalMarketData,
    RuleRecalculation,
}

public enum ReviewTimeBasis
{
    Utc,
    BrokerServer,
    Local,
    EstimatedBrokerServer,
}

public enum RuleAssessmentStatus
{
    Passed,
    Failed,
    Unknown,
    NotApplicable,
}

public enum PlaybookRuleSection
{
    Entry,
    Risk,
    Management,
    Exit,
}

public enum ReviewUnit
{
    Position,
    Campaign,
}

public enum ReviewTagMatchMode
{
    Any,
    All,
}

public enum ReviewSortOrder
{
    ClosedDescending,
    ClosedAscending,
    LargestLoss,
    LargestGiveback,
    BehaviorFirst,
    OldestPending,
}

public enum ReviewBulkEditKind
{
    Strategy,
    Tags,
    Status,
}

public enum ReviewComparisonMode
{
    PeriodHalves,
    Compliance,
    Side,
}

public enum BehaviorTradeRole
{
    Trigger,
    PreviousContext,
    OpenExposure,
    Subsequent,
}

public enum MarketCoverageStatus
{
    Complete,
    Partial,
    Empty,
    Failed,
    Cancelled,
}

public enum MarketDataPrecision
{
    DealEvents,
    Bars,
    Ticks,
}

public enum OpportunityRecordKind
{
    ObservedBeforeMove,
    DiscoveredAfterMove,
    DeliberatelySkipped,
}

public enum ImprovementGoalStatus
{
    Draft,
    Active,
    Archived,
}

public enum GoalObservationStatus
{
    Passed,
    Failed,
    NotApplicable,
    Unknown,
}

public sealed record ReviewEvidenceStamp(
    ReviewEvidenceSource Source,
    DateTimeOffset EventAtUtc,
    DateTimeOffset RecordedAtUtc,
    ReviewTimeBasis TimeBasis,
    string SourceVersion);

public sealed record TradeReviewDocument(
    TradeKey TradeKey,
    ReviewCompletionStatus Status,
    string EntryReason,
    string ExitReason,
    string DidWell,
    string ToImprove,
    string NextAction,
    string Summary,
    string Emotion,
    string MarketCondition,
    int Revision,
    string SourceVersion,
    string RuleVersion,
    string? ReviewedSourceVersion,
    string? ReviewedRuleVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ReviewedAtUtc = null)
{
    [JsonIgnore]
    public bool HasRequiredReviewContent =>
        !string.IsNullOrWhiteSpace(Summary) && !string.IsNullOrWhiteSpace(NextAction);

    public TradeReviewDocument Normalize() => this with
    {
        EntryReason = EntryReason.Trim(),
        ExitReason = ExitReason.Trim(),
        DidWell = DidWell.Trim(),
        ToImprove = ToImprove.Trim(),
        NextAction = NextAction.Trim(),
        Summary = Summary.Trim(),
        Emotion = Emotion.Trim(),
        MarketCondition = MarketCondition.Trim(),
    };
}

public sealed record DailyJournal(
    string AccountKey,
    DateOnly ServerDate,
    string PreMarketPlan,
    string IntradayNotes,
    string PostMarketSummary,
    string DidWell,
    string ToImprove,
    string NextAction,
    ReviewCompletionStatus Status,
    int Revision,
    string SourceVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ReviewedAtUtc = null,
    DateTimeOffset? PreMarketRecordedAtUtc = null,
    DateTimeOffset? IntradayRecordedAtUtc = null,
    DateTimeOffset? PostMarketRecordedAtUtc = null,
    string? ReviewedSourceVersion = null);

public sealed record PeriodReview(
    string Id,
    string AccountKey,
    DateOnly FromServerDate,
    DateOnly ToServerDate,
    string Facts,
    string DidWell,
    string ToImprove,
    string NextAction,
    IReadOnlyList<TradeKey> LinkedTrades,
    int Revision,
    string SourceVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<string>? LinkedBehaviorOccurrenceIds = null);

public sealed record ReviewAttachment(
    string Id,
    string AccountKey,
    string Sha256,
    string FileName,
    string MediaType,
    long SizeBytes,
    string RelativePath,
    string Title,
    string OwnerKind,
    string OwnerId,
    ReviewEvidenceStamp Evidence,
    DateTimeOffset CreatedAtUtc,
    string EventReference = "");

public sealed record PlaybookRule(
    string Id,
    PlaybookRuleSection Section,
    string Name,
    string Description,
    bool IsCritical,
    int Order);

public sealed record PlaybookVersion(
    string Id,
    string PlaybookId,
    string AccountKey,
    int Version,
    string Name,
    string ApplicableSymbols,
    string MarketConditions,
    string InvalidWhen,
    IReadOnlyList<PlaybookRule> Rules,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset CreatedAtUtc,
    bool IsActive);

public sealed record TradeRuleAssessment(
    TradeKey TradeKey,
    string PlaybookVersionId,
    string RuleId,
    RuleAssessmentStatus Status,
    ReviewEvidenceSource Source,
    string EvidenceReference,
    string Notes,
    int Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record RuleAssessmentSummary(
    int Passed,
    int Failed,
    int Unknown,
    int NotApplicable,
    decimal? AdherencePercentage,
    decimal CoveragePercentage,
    bool HasViolation,
    bool IsCompliant);

public sealed record TradeCampaign(
    string Id,
    string AccountKey,
    string Symbol,
    string Name,
    string Thesis,
    IReadOnlyList<long> PositionIds,
    int Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record TradeCampaignSummary(
    TradeCampaign Campaign,
    int MemberCount,
    int ReentryCount,
    bool ContainsDirectionChange,
    DateTimeOffset? OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    decimal NetPnl,
    decimal Fees,
    decimal? MaximumConcurrentExposure);

public sealed record PositionPnlSample(
    TradeKey TradeKey,
    DateTimeOffset CapturedAtUtc,
    decimal RealizedPnl,
    decimal FloatingPnl,
    decimal NetPnl,
    decimal Volume,
    decimal? StopLoss,
    decimal? TakeProfit,
    long GapMilliseconds,
    string AlgorithmVersion);

public sealed record BehaviorOccurrence(
    string Id,
    string AccountKey,
    DateOnly ServerDate,
    BehaviorRuleKind Rule,
    string RuleVersion,
    decimal Value,
    decimal? Baseline,
    decimal Threshold,
    BehaviorRiskLevel Level,
    ReviewEvidenceSource Source,
    DateTimeOffset EventAtUtc,
    DateTimeOffset ObservedAtUtc,
    string Summary,
    string MissingData,
    bool NotificationDelivered,
    string NotificationDisposition,
    string? UserExplanation,
    IReadOnlyList<BehaviorTradeLink> TradeLinks,
    bool EvidenceInsufficient = false,
    int Revision = 0,
    DateTimeOffset? HumanReviewedAtUtc = null,
    IReadOnlyList<string>? RelatedGoalIds = null);

public sealed record BehaviorTradeLink(TradeKey TradeKey, BehaviorTradeRole Role);

public sealed record ReviewTagSuggestion(
    string Tag,
    string Reason,
    BehaviorRuleKind Rule,
    BehaviorRiskLevel Level,
    IReadOnlyList<string> BehaviorOccurrenceIds,
    bool IsAccepted);

public sealed record ImprovementGoal(
    string Id,
    string AccountKey,
    string Name,
    BehaviorRuleKind? Rule,
    string RuleVersion,
    DateOnly StartServerDate,
    DateOnly? EndServerDate,
    decimal? TargetValue,
    decimal? BaselineValue,
    string Measurement,
    string ApplicableSymbols,
    bool NotificationEnabled,
    ImprovementGoalStatus Status,
    int Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string GoalKey = "",
    int Version = 1,
    int ObservationWindowDays = 1,
    DateOnly? BaselineFromServerDate = null,
    DateOnly? BaselineToServerDate = null,
    int BaselineOpportunityCount = 0,
    int BaselinePassCount = 0,
    int BaselineFailCount = 0,
    string EndCondition = "",
    string? PreviousVersionId = null);

public sealed record GoalObservation(
    string Id,
    string GoalId,
    string AccountKey,
    DateOnly ServerDate,
    int OpportunityCount,
    int PassCount,
    int FailCount,
    GoalObservationStatus Status,
    string Evidence,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<string>? EvidenceIds = null,
    string RuleVersion = "");

public sealed record OpportunityRecord(
    string Id,
    string AccountKey,
    OpportunityRecordKind Kind,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset RecordedAtUtc,
    DateOnly ServerDate,
    string Symbol,
    TradeSide? Side,
    string? PlaybookVersionId,
    decimal? EntryPrice,
    decimal? StopPrice,
    decimal? TargetPrice,
    string Reason,
    string Notes,
    string? LinkedTradeKey,
    int Revision,
    string Conditions = "");

public sealed record MarketBar(
    string TerminalId,
    string AccountKey,
    string Symbol,
    string Timeframe,
    DateTimeOffset OpenedAtUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long TickVolume,
    int Spread,
    long RealVolume);

public sealed record MarketTick(
    string TerminalId,
    string AccountKey,
    string Symbol,
    DateTimeOffset OccurredAtUtc,
    long TimeMilliseconds,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume,
    long Flags,
    string Fingerprint);

public sealed record MarketDataRange(
    string RequestId,
    string TerminalId,
    string AccountKey,
    string Symbol,
    string Timeframe,
    DateTimeOffset RequestedFromUtc,
    DateTimeOffset RequestedToUtc,
    DateTimeOffset? ActualFromUtc,
    DateTimeOffset? ActualToUtc,
    MarketDataPrecision Precision,
    MarketCoverageStatus Coverage,
    string SourceVersion,
    string Error,
    DateTimeOffset UpdatedAtUtc);

public sealed record ServerTimeSegment(
    string AccountKey,
    string TerminalId,
    DateTimeOffset FromUtc,
    DateTimeOffset? ToUtc,
    int UtcOffsetSeconds,
    ReviewTimeBasis TimeBasis,
    string SourceVersion);

public sealed record TradingSessionDefinition(
    string Id,
    string AccountKey,
    string Name,
    string TimeZoneId,
    TimeOnly StartLocalTime,
    TimeOnly EndLocalTime,
    IReadOnlyList<DayOfWeek> StartDays,
    int SortOrder,
    bool IsActive,
    int Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReviewSavedFilter(
    string Id,
    string AccountKey,
    string Name,
    string FilterJson,
    int Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReviewDataVersion(
    string AccountKey,
    long SourceVersion,
    long MetadataVersion,
    long ObservationVersion,
    string RuleVersion,
    string TimeVersion,
    DateTimeOffset UpdatedAtUtc)
{
    public string Token => $"{SourceVersion}:{MetadataVersion}:{ObservationVersion}:{RuleVersion}:{TimeVersion}";
}

public sealed record ReviewDataQuality(
    int CompleteTradeCount,
    int InitialRiskCoveredCount,
    int ExcursionCoveredCount,
    int PreTradePlanCoveredCount,
    int ReviewedCount,
    int MarketDataCoveredCount,
    decimal InitialRiskCoveragePercentage,
    decimal ExcursionCoveragePercentage,
    decimal PreTradePlanCoveragePercentage,
    decimal ReviewCompletionPercentage,
    decimal MarketDataCoveragePercentage,
    IReadOnlyDictionary<string, int> MissingReasons,
    IReadOnlyList<ReviewDataQualityIssue>? Issues = null);

public sealed record ReviewDataQualityIssue(
    string Code,
    string Label,
    string Detail,
    IReadOnlyList<TradeKey> Trades,
    IReadOnlyList<string> EvidenceIds);

public sealed record ReviewCalendarDay(
    DateOnly ServerDate,
    decimal RealizedCashPnl,
    int OpeningTradeCount,
    int CompleteTradeCount,
    int PendingReviewCount,
    bool HasJournal,
    bool HasDataGap);

public sealed record DailyProcessEvent(
    DateTimeOffset AtUtc,
    string Kind,
    string Summary,
    ReviewEvidenceSource Source,
    IReadOnlyList<TradeKey> LinkedTrades);

public sealed record DailyReviewFacts(
    DateOnly ServerDate,
    string SourceVersion,
    int ServerUtcOffsetSeconds,
    decimal RealizedCashPnl,
    decimal CompleteTradeNetPnl,
    decimal Fees,
    int OpeningTradeCount,
    int CompleteTradeCount,
    int ReviewedTradeCount,
    decimal ReviewCompletionPercentage,
    int ConsecutiveLosses,
    int? OpeningsAfterLossStreakCount,
    int CooldownViolationCount,
    DateTimeOffset? TargetReachedAtUtc,
    string TargetRuleVersion,
    decimal? TargetAmount,
    decimal? AfterTargetNewTradeNetPnl,
    int? AfterTargetNewCompleteTradeCount,
    int? AfterTargetNewOpenTradeCount,
    decimal? AfterTargetDealCashPnl,
    int? AfterTargetDealCount,
    IReadOnlyList<TradeKey> AfterTargetTradeKeys,
    IReadOnlyList<long> AfterTargetDealTickets,
    IReadOnlyList<DailyProcessEvent> Timeline)
{
    public bool HasReliableTargetMilestone =>
        TargetReachedAtUtc is not null &&
        TargetAmount is > 0m &&
        !string.IsNullOrWhiteSpace(TargetRuleVersion);
}

public sealed record ReviewFeeSummary(
    int AllocatedDealCount,
    decimal Commission,
    decimal Swap,
    decimal OtherFees,
    int UnallocatedDealCount,
    decimal UnallocatedFees,
    IReadOnlyList<TradeKey> Trades)
{
    public decimal AllocatedTotal => Commission + Swap + OtherFees;
    public decimal GrandTotal => AllocatedTotal + UnallocatedFees;
}

public sealed record ReviewDailyCashPoint(
    DateOnly ServerDate,
    decimal CashPnl,
    IReadOnlyList<long> DealTickets,
    IReadOnlyList<TradeKey> Trades);

public sealed record ReviewRiskSample(
    TradeKey Trade,
    string Symbol,
    decimal NetPnl,
    decimal OpeningVolume,
    decimal? InitialRiskAmount,
    decimal? Mae,
    decimal? Mfe,
    decimal? ActualRiskMultiple,
    bool HasReliableExcursion,
    bool HasReliableInitialRisk = false,
    string ExcursionAlgorithmVersion = "none");

public sealed record TradeReviewBasis(string SourceVersion, string RuleVersion);

public sealed record ReviewCurvePoint(
    string Key,
    DateOnly ServerDate,
    DateTimeOffset AtUtc,
    decimal Value,
    decimal Drawdown,
    decimal? DrawdownPercentage,
    IReadOnlyList<TradeKey> Trades);

public sealed record ReviewEquityPoint(
    DateTimeOffset AtUtc,
    DateOnly ServerDate,
    decimal Equity,
    decimal ObservedDrawdownAmount,
    decimal? ObservedDrawdownPercentage,
    int Segment,
    decimal UnitizedValue,
    decimal? UnitizedDrawdownPercentage,
    bool StartsAfterUnverifiedCashFlow);

public sealed record ReviewEquityAnalysis(
    IReadOnlyList<ReviewEquityPoint> Points,
    int CashFlowCount,
    int VerifiedCashFlowCount,
    int SegmentCount,
    decimal? ObservedMaximumDrawdownAmount,
    decimal? ObservedMaximumDrawdownPercentage,
    decimal? CashFlowAdjustedMaximumDrawdownPercentage,
    bool HasCompleteCashFlowCoverage,
    string Message);

public sealed record ReviewComparison(
    string LeftLabel,
    PerformanceSummary Left,
    string RightLabel,
    PerformanceSummary Right,
    int OverlapCount,
    bool HasSmallSampleWarning,
    IReadOnlyList<TradeKey>? LeftTrades = null,
    IReadOnlyList<TradeKey>? RightTrades = null,
    decimal? LeftAverageInitialRisk = null,
    decimal? RightAverageInitialRisk = null,
    decimal LeftRiskCoveragePercentage = 0m,
    decimal RightRiskCoveragePercentage = 0m);

public sealed record BehaviorImpactSummary(
    BehaviorRuleKind? Rule,
    int OccurrenceCount,
    int UniqueTradeCount,
    decimal RelatedNetPnl,
    IReadOnlyList<TradeKey> Trades);

public sealed record BehaviorTradeSample(
    int TradeCount,
    decimal NetPnl,
    IReadOnlyList<TradeKey> Trades);

public sealed record BehaviorEvidenceAnalysis(
    BehaviorRuleKind? Rule,
    int LiveOccurrenceCount,
    int RecalculatedOccurrenceCount,
    BehaviorTradeSample HitSample,
    BehaviorTradeSample UnhitSample);

public sealed record BehaviorRuleFrequency(
    BehaviorRuleKind Rule,
    int OccurrenceCount,
    IReadOnlyList<string> OccurrenceIds,
    IReadOnlyList<TradeKey> Trades);

public sealed record PeriodReviewFacts(
    int CompleteTradeCount,
    int ReviewedTradeCount,
    int PassedRuleCount,
    int FailedRuleCount,
    int UnknownRuleCount,
    int NotApplicableRuleCount,
    decimal? RuleAdherencePercentage,
    int BehaviorOccurrenceCount,
    IReadOnlyList<BehaviorRuleFrequency> RepeatedBehaviors,
    IReadOnlyList<TradeKey> Trades,
    IReadOnlyList<string> BehaviorOccurrenceIds,
    ReviewDataQuality DataQuality);

public sealed record ImprovementGoalProgress(
    ImprovementGoal Goal,
    int ApplicableDayCount,
    int ObservedDayCount,
    int NoOpportunityDayCount,
    int OpportunityCount,
    int PassCount,
    int FailCount,
    int UnknownObservationCount,
    int NotApplicableObservationCount,
    decimal? AdherencePercentage,
    IReadOnlyList<string> EvidenceIds);

public sealed record ReviewWorkspaceFilter(
    string AccountKey,
    DateOnly FromServerDate,
    DateOnly ToServerDate,
    string? Symbol = null,
    TradeSide? Side = null,
    string? Strategy = null,
    string? Setup = null,
    IReadOnlyList<string>? Tags = null,
    ReviewTagMatchMode TagMatchMode = ReviewTagMatchMode.Any,
    ReviewCompletionStatus? Status = null,
    RuleAssessmentStatus? Assessment = null,
    string? CampaignId = null,
    string? Search = null,
    ReviewUnit Unit = ReviewUnit.Position,
    ReviewSortOrder Sort = ReviewSortOrder.ClosedDescending,
    int Page = 1,
    int PageSize = 100,
    int ServerUtcOffsetSeconds = 0,
    ReviewComparisonMode ComparisonMode = ReviewComparisonMode.PeriodHalves);

public sealed record ReviewWorkspaceSnapshot(
    ReviewWorkspaceFilter Filter,
    ReviewSnapshot Analytics,
    IReadOnlyList<TradeRecord> Trades,
    IReadOnlyDictionary<long, TradeReviewDocument> Documents,
    IReadOnlyList<ReviewCalendarDay> Calendar,
    IReadOnlyList<ReviewCurvePoint> RealizedCurve,
    IReadOnlyList<BehaviorOccurrence> BehaviorOccurrences,
    ReviewDataQuality DataQuality,
    ReviewDataVersion Version,
    int TotalCount,
    int Page,
    int PageSize,
    ReviewComparison? PeriodComparison = null,
    IReadOnlyDictionary<DateOnly, DailyReviewFacts>? DailyFacts = null,
    ReviewFeeSummary? Fees = null,
    IReadOnlyList<ReviewDailyCashPoint>? DailyCash = null,
    IReadOnlyList<ReviewRiskSample>? RiskSamples = null,
    BehaviorEvidenceAnalysis? BehaviorEvidence = null,
    PeriodReviewFacts? PeriodFacts = null,
    IReadOnlyList<ImprovementGoalProgress>? GoalProgress = null,
    ReviewEquityAnalysis? EquityAnalysis = null,
    IReadOnlyList<GroupMetricRow>? TradingSessionPerformance = null,
    IReadOnlyList<TradeRecord>? AllFilteredTrades = null);

public sealed record TradeDetailSnapshot(
    TradeRecord Trade,
    IReadOnlyList<DealRecord> Deals,
    TradeReviewMetadata? Metadata,
    TradeReviewDocument? Document,
    TradeExcursion? Excursion,
    IReadOnlyList<PositionPnlSample> PnlSamples,
    StructuredTradePlan? Plan,
    PlaybookVersion? Playbook,
    IReadOnlyList<TradeRuleAssessment> Assessments,
    IReadOnlyList<BehaviorOccurrence> Behaviors,
    IReadOnlyList<ReviewAttachment> Attachments,
    TradeCampaign? Campaign,
    string SourceVersion);

public sealed record ReplayFrame(
    DateTimeOffset CursorUtc,
    IReadOnlyList<MarketBar> VisibleBars,
    IReadOnlyList<MarketTick> VisibleTicks,
    IReadOnlyList<DealRecord> VisibleDeals,
    IReadOnlyList<PositionPnlSample> VisiblePnlSamples,
    IReadOnlyList<BehaviorOccurrence> VisibleBehaviors,
    IReadOnlyList<ReviewEvidenceStamp> VisibleNotes,
    MarketCoverageStatus Coverage,
    string Message,
    IReadOnlyList<ReplayEventPoint>? NavigationEvents = null,
    int CurrentEventIndex = -1,
    decimal? VisibleFinalNetPnl = null,
    StructuredTradePlan? VisiblePlan = null,
    TradeReviewDocument? VisibleReview = null,
    bool IsFullReviewVisible = false);

public sealed record ReplayEventPoint(
    DateTimeOffset AtUtc,
    string Kind,
    string Summary,
    ReviewEvidenceSource Source);
