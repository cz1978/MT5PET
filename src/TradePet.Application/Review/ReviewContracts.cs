using TradePet.Core.Domain;

namespace TradePet.Application.Review;

public enum ReviewSaveStatus
{
    Saved,
    ValidationFailed,
    Conflict,
    NotFound,
    StorageFailed,
}

public sealed record ReviewSaveResult<T>(ReviewSaveStatus Status, T? Value, string Message)
{
    public bool IsSaved => Status == ReviewSaveStatus.Saved;

    public static ReviewSaveResult<T> Saved(T value) => new(ReviewSaveStatus.Saved, value, string.Empty);
    public static ReviewSaveResult<T> Validation(string message) => new(ReviewSaveStatus.ValidationFailed, default, message);
    public static ReviewSaveResult<T> Conflict(string message) => new(ReviewSaveStatus.Conflict, default, message);
    public static ReviewSaveResult<T> Missing(string message) => new(ReviewSaveStatus.NotFound, default, message);
    public static ReviewSaveResult<T> Failed(string message) => new(ReviewSaveStatus.StorageFailed, default, message);
}

public sealed record ReviewBulkEditRequest(
    string AccountKey,
    IReadOnlyList<long> PositionIds,
    ReviewBulkEditKind Kind,
    string Value);

public sealed record ReviewBulkWriteItem(
    TradeKey TradeKey,
    TradeReviewMetadata? Metadata,
    DateTimeOffset? ExpectedMetadataUpdatedAtUtc,
    TradeReviewDocument? Document,
    int ExpectedDocumentRevision);

public sealed record ReviewBulkEditResult(int AffectedCount);

public sealed record ReviewCacheCleanupResult(int RangeCount, int BarCount, int TickCount)
{
    public int TotalCount => RangeCount + BarCount + TickCount;
}

public sealed record ReviewWorkspaceData(
    string AccountKey,
    string Currency,
    IReadOnlyList<TradeRecord> Trades,
    IReadOnlyList<DealRecord> Deals,
    IReadOnlyDictionary<long, TradeReviewMetadata> Metadata,
    IReadOnlyDictionary<long, TradeReviewDocument> Documents,
    IReadOnlyDictionary<long, TradeExcursion> Excursions,
    IReadOnlyDictionary<DateOnly, DailyJournal> DailyJournals,
    IReadOnlyList<PlaybookVersion> Playbooks,
    IReadOnlyList<TradeRuleAssessment> Assessments,
    IReadOnlyList<TradeCampaign> Campaigns,
    IReadOnlyList<BehaviorOccurrence> Behaviors,
    IReadOnlyList<ImprovementGoal> Goals,
    IReadOnlyList<GoalObservation> GoalObservations,
    IReadOnlyList<OpportunityRecord> Opportunities,
    IReadOnlyList<MarketDataRange> MarketRanges,
    IReadOnlyList<DateOnly> DataGapDates,
    ReviewDataVersion Version,
    IReadOnlyList<PeriodReview>? PeriodReviews = null,
    IReadOnlyList<ReviewSavedFilter>? SavedFilters = null,
    IReadOnlyList<ServerTimeSegment>? ServerTimeSegments = null,
    IReadOnlyList<ReviewAttachment>? OpportunityAttachments = null,
    IReadOnlyDictionary<DateOnly, DailyState>? DailyStates = null,
    IReadOnlyList<EquitySample>? EquitySamples = null,
    IReadOnlyList<AccountCashFlow>? CashFlows = null,
    IReadOnlyList<TradingSessionDefinition>? TradingSessions = null);

public sealed record TradeDetailData(
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
    ReviewDataVersion Version);

public interface IReviewWorkspaceRepository
{
    Task<ReviewDataVersion> LoadReviewDataVersionAsync(
        string accountKey,
        CancellationToken cancellationToken = default);

    Task<ReviewWorkspaceData> LoadWorkspaceAsync(
        string accountKey,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    Task<TradeDetailData?> LoadTradeDetailAsync(TradeKey key, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<TradeDetailData>> LoadTradeDetailsAsync(
        IReadOnlyCollection<TradeKey> keys,
        CancellationToken cancellationToken = default)
    {
        var details = new List<TradeDetailData>(keys.Count);
        foreach (var key in keys.Distinct())
        {
            var detail = await LoadTradeDetailAsync(key, cancellationToken);
            if (detail is not null)
            {
                details.Add(detail);
            }
        }
        return details;
    }
    Task<TradeReviewDocument?> LoadTradeReviewDocumentAsync(TradeKey key, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<TradeReviewDocument>> SaveTradeReviewDocumentAsync(
        TradeReviewDocument document, int expectedRevision, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<ReviewBulkEditResult>> SaveBulkReviewAsync(
        IReadOnlyList<ReviewBulkWriteItem> items, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<DailyJournal>> SaveDailyJournalAsync(
        DailyJournal journal, int expectedRevision, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<PeriodReview>> SavePeriodReviewAsync(
        PeriodReview review, int expectedRevision, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<PlaybookVersion>> SavePlaybookVersionAsync(
        PlaybookVersion playbook, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<IReadOnlyList<TradeRuleAssessment>>> SaveRuleAssessmentsAsync(
        TradeKey key, IReadOnlyList<TradeRuleAssessment> assessments, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<TradeCampaign>> SaveCampaignAsync(
        TradeCampaign campaign, int expectedRevision, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<ImprovementGoal>> SaveGoalAsync(
        ImprovementGoal goal, int expectedRevision, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<ImprovementGoal>> SaveGoalVersionAsync(
        ImprovementGoal previousVersion,
        int expectedPreviousRevision,
        ImprovementGoal nextVersion,
        CancellationToken cancellationToken = default);
    Task SaveGoalObservationAsync(GoalObservation observation, CancellationToken cancellationToken = default);
    Task SaveBehaviorOccurrenceAsync(BehaviorOccurrence occurrence, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<BehaviorOccurrence>> SaveBehaviorReviewAsync(
        string accountKey,
        string occurrenceId,
        string? userExplanation,
        bool evidenceInsufficient,
        int expectedRevision,
        DateTimeOffset reviewedAtUtc,
        CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<OpportunityRecord>> SaveOpportunityAsync(
        OpportunityRecord opportunity, int expectedRevision, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<ReviewSavedFilter>> SaveFilterAsync(
        ReviewSavedFilter filter, int expectedRevision, CancellationToken cancellationToken = default);
    Task<ReviewSaveResult<TradingSessionDefinition>> SaveTradingSessionAsync(
        TradingSessionDefinition session, int expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewSavedFilter>> LoadFiltersAsync(string accountKey, CancellationToken cancellationToken = default);
    Task SaveServerTimeSegmentAsync(ServerTimeSegment segment, CancellationToken cancellationToken = default);
    Task SaveMarketDataAsync(
        MarketDataRange range,
        IReadOnlyList<MarketBar> bars,
        IReadOnlyList<MarketTick> ticks,
        CancellationToken cancellationToken = default);
    Task<(MarketDataRange? Range, IReadOnlyList<MarketBar> Bars, IReadOnlyList<MarketTick> Ticks)> LoadMarketDataAsync(
        string accountKey,
        string terminalId,
        string symbol,
        string timeframe,
        MarketDataPrecision precision,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
    Task<ReviewCacheCleanupResult> ClearMarketDataCacheAsync(
        string accountKey,
        CancellationToken cancellationToken = default);
}

public interface IReviewAttachmentStore
{
    Task<ReviewAttachment> ImportAsync(
        string accountKey,
        string ownerKind,
        string ownerId,
        string sourcePath,
        string title,
        ReviewEvidenceStamp evidence,
        CancellationToken cancellationToken = default,
        string eventReference = "");
    Task DeleteAsync(
        string attachmentId,
        string accountKey,
        string ownerKind,
        string ownerId,
        CancellationToken cancellationToken = default);
    string ResolvePath(ReviewAttachment attachment);
}

public sealed record MarketHistoryRequest(
    string RequestId,
    string TerminalId,
    string ExpectedAccountKey,
    string Symbol,
    string Timeframe,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    MarketDataPrecision Precision,
    int MaximumBars = 5000,
    int MaximumTicks = 10000);

public sealed record MarketHistoryResult(
    MarketDataRange Range,
    IReadOnlyList<MarketBar> Bars,
    IReadOnlyList<MarketTick> Ticks);

public interface IMarketHistorySource
{
    Task<MarketHistoryResult> LoadAsync(MarketHistoryRequest request, CancellationToken cancellationToken = default);
}

public sealed record ReviewQueryContext(string QueryId, long SessionGeneration, string ExpectedAccountKey);

public sealed record TradeDetailRequestContext(
    string RequestId,
    long SessionGeneration,
    TradeKey TradeKey)
{
    public bool CanApply(
        string? latestRequestId,
        string? currentAccountKey,
        long currentSessionGeneration,
        TradeKey returnedTradeKey) =>
        string.Equals(RequestId, latestRequestId, StringComparison.Ordinal) &&
        SessionGeneration == currentSessionGeneration &&
        string.Equals(TradeKey.AccountKey, currentAccountKey, StringComparison.Ordinal) &&
        returnedTradeKey == TradeKey;
}

public sealed record ReviewQueryResult(
    ReviewQueryContext Context,
    ReviewWorkspaceSnapshot Snapshot,
    ReviewWorkspaceData Data,
    bool IsCurrentSession);

public sealed record SaveTradeReviewCommand(
    TradeKey TradeKey,
    string EntryReason,
    string ExitReason,
    string DidWell,
    string ToImprove,
    string NextAction,
    string Summary,
    string Emotion,
    string MarketCondition,
    string SourceVersion,
    string RuleVersion,
    ReviewCompletionStatus RequestedStatus);

public sealed record TradeReviewEditSubmission(
    EditSnapshot<SaveTradeReviewCommand> Snapshot,
    int ExpectedRevision);

public sealed record ReviewExportEntry(
    string Path,
    string MediaType,
    byte[] Content,
    string? SourcePath = null,
    string? ExpectedSha256 = null,
    long? ExpectedSizeBytes = null);
public sealed record ReviewExportPackage(string FileName, string SnapshotToken, IReadOnlyList<ReviewExportEntry> Entries);

public interface IReviewPackageWriter
{
    Task<string> WriteAsync(
        ReviewExportPackage package,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default);
}

public sealed record ReviewBackupFile(string Path, string Sha256, long SizeBytes);

public sealed record ReviewBackupManifest(
    int FormatVersion,
    DateTimeOffset CreatedAtUtc,
    string DatabaseEntry,
    IReadOnlyList<ReviewBackupFile> Files,
    IReadOnlyList<string> AccountKeys);

public sealed record ReviewBackupRestoreResult(
    string SafetyBackupPath,
    IReadOnlyList<string> AccountKeys,
    int AttachmentCount);

public interface IReviewBackupService
{
    Task<bool> RecoverPendingAsync(CancellationToken cancellationToken = default);
    Task<string> CreateAsync(string? outputDirectory = null, CancellationToken cancellationToken = default);
    Task<ReviewBackupManifest> ValidateAsync(string packagePath, CancellationToken cancellationToken = default);
    Task<ReviewBackupRestoreResult> RestoreAsync(
        string packagePath,
        string? safetyBackupDirectory = null,
        CancellationToken cancellationToken = default,
        bool preserveUnreadableCurrent = false);
}

public enum ReviewExportMode
{
    PublicShare,
    LocalArchive,
}

public enum ReviewExportScope
{
    AllFiltered,
    SelectedTrades,
}
