using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TradePet.Application.Review;
using TradePet.Application.Runtime;
using TradePet.App.ViewModels;
using TradePet.Core.Domain;

namespace TradePet.App.ViewModels.Review;

public sealed class ReviewWorkspaceViewModel : ObservableObject
{
    private string _search = string.Empty;
    private string _strategy = string.Empty;
    private string _setup = string.Empty;
    private string _tags = string.Empty;
    private string _tagMode = "任一";
    private string _statusFilter = "全部";
    private string _assessmentFilter = "全部";
    private string _campaignFilter = string.Empty;
    private string _sort = "最新平仓";
    private string _comparisonMode = "周期前后";
    private string _filterName = string.Empty;
    private string _statusText = "等待复盘数据";
    private string _qualitySummary = "尚未计算覆盖率";
    private string _storageStatus = "本地数据库状态等待确认";
    private string _cacheStatus = "行情缓存只包含可重新获取的数据。";
    private int _page = 1;
    private int _totalCount;
    private long? _selectedPositionId;
    private string _selectedTradeTitle = "请选择一笔交易";
    private string _tradeFacts = "成交、费用和时间均来自 MT5，只读展示。";
    private string _tradeIdentity = "尚未选择交易";
    private string _planFacts = "没有绑定计划。";
    private string _riskFacts = "初始风险与持仓采样未知。";
    private string _entryReason = string.Empty;
    private string _exitReason = string.Empty;
    private string _didWell = string.Empty;
    private string _toImprove = string.Empty;
    private string _nextAction = string.Empty;
    private string _summary = string.Empty;
    private string _emotion = string.Empty;
    private string _marketCondition = string.Empty;
    private string _documentStatus = "待复盘";
    private int _documentRevision;
    private string _attachmentTitle = string.Empty;
    private string _attachmentEventReference = string.Empty;
    private string _dailyDate;
    private string _preMarketPlan = string.Empty;
    private string _intradayNotes = string.Empty;
    private string _postMarketSummary = string.Empty;
    private string _dailyDidWell = string.Empty;
    private string _dailyToImprove = string.Empty;
    private string _dailyNextAction = string.Empty;
    private int _dailyRevision;
    private string _dailyStatus = "尚未完成日总结";
    private string _playbookName = string.Empty;
    private string _playbookSymbols = string.Empty;
    private string _playbookConditions = string.Empty;
    private string _playbookInvalidWhen = string.Empty;
    private string _playbookRules = "入场|确认入场条件|写明事实依据|关键\r\n风险|开仓即有止损|检查首次风险证据|关键\r\n退出|按计划退出|记录退出依据|普通";
    private string _campaignName = string.Empty;
    private string _campaignThesis = string.Empty;
    private string _campaignMembers = string.Empty;
    private string _goalName = string.Empty;
    private string _goalMeasurement = string.Empty;
    private string _goalTarget = string.Empty;
    private string _goalRule = "人工检查";
    private bool _goalNotificationEnabled;
    private string _goalSymbols = string.Empty;
    private string _goalObservationWindow = "7";
    private string _goalEndCondition = string.Empty;
    private string? _selectedGoalId;
    private string _opportunityKind = "事前观察";
    private string _opportunitySymbol = string.Empty;
    private string _opportunitySide = "未指定";
    private string _opportunityPlaybook = string.Empty;
    private string _opportunityEntry = string.Empty;
    private string _opportunityStop = string.Empty;
    private string _opportunityTarget = string.Empty;
    private string _opportunityReason = string.Empty;
    private string _opportunityConditions = string.Empty;
    private string _opportunityLinkedTrade = string.Empty;
    private string _opportunityNotes = string.Empty;
    private string? _opportunityId;
    private int _opportunityRevision;
    private string _bulkStrategy = string.Empty;
    private string _bulkTags = string.Empty;
    private string _bulkStatus = "草稿";
    private string _periodFacts = string.Empty;
    private string _periodDidWell = string.Empty;
    private string _periodToImprove = string.Empty;
    private string _periodNextAction = string.Empty;
    private int _periodRevision;
    private string? _periodReviewId;
    private string _replayStatus = "选择交易后可加载真实历史行情";
    private string _replayPrecision = "K线";
    private double _replayProgress;
    private bool _isReplayPlaying;
    private string _replaySpeed = "1x";
    private bool _showFullReplayReview;
    private IReadOnlyList<ReplayEventPoint> _replayNavigationEvents = [];
    private DateTimeOffset _replayStartUtc;
    private DateTimeOffset _replayEndUtc;
    private int _replayCurrentEventIndex = -1;
    private string _exportStatus = "导出使用当前筛选与数据版本";
    private string _exportScope = "全部筛选结果";
    private string _exportPreview = "先生成导出预览；原始附件默认不包含。";
    private bool _canConfirmExport;
    private string _analysisDrilldownTitle = "点击分析结果查看对应交易";
    private string? _selectedBehaviorId;
    private string _behaviorFacts = "请选择一条行为事件查看原始证据。";
    private string _behaviorExplanation = string.Empty;
    private bool _behaviorEvidenceInsufficient;
    private int _behaviorRevision;
    private string _behaviorReviewStatus = "人工解释只补充结论，不改变规则事实。";
    private string _equitySummary = "当前范围没有账户净值采样。";
    private string? _tradingSessionId;
    private int _tradingSessionRevision;
    private string _tradingSessionName = string.Empty;
    private string _tradingSessionTimeZone = "China Standard Time";
    private string _tradingSessionStart = "09:00";
    private string _tradingSessionEnd = "17:00";
    private string _tradingSessionDays = "周一、周二、周三、周四、周五";
    private string _tradingSessionOrder = "0";
    private bool _tradingSessionActive = true;
    private PeriodReviewFacts? _periodFactsSnapshot;
    private ReviewComparison? _periodComparisonSnapshot;
    private bool _applyingDetail;
    private bool _hasUnsavedReviewChanges;
    private CancellationTokenSource? _draftDelay;
    private readonly IAsyncScheduler _scheduler;
    private readonly Dictionary<TradeKey, TradeDraftState> _tradeDrafts = [];
    private TradeKey? _tradeEditorKey;
    private EditIdentity? _tradeEditorIdentity;
    private long _tradeEditSequence;
    private readonly Dictionary<EditEntityKind, EditState> _workspaceEditorStates = [];
    private readonly Dictionary<WorkspaceDraftKey, WorkspaceEditorDraft> _workspaceDrafts = [];
    private CancellationTokenSource? _workspaceDraftDelay;
    private string? _workspaceAccountKey;
    private long _workspaceSessionGeneration;
    private bool _acceptingEdits = true;
    private string _editorSaveStatus = "编辑内容尚未更改";
    private EditEntityKind? _lastSaveIssueKind;

    public ReviewWorkspaceViewModel(IAsyncScheduler? scheduler = null, TimeProvider? timeProvider = null)
    {
        _scheduler = scheduler ?? new SystemAsyncScheduler();
        var clock = timeProvider ?? TimeProvider.System;
        _dailyDate = DateOnly.FromDateTime(clock.GetLocalNow().DateTime).ToString("yyyy-MM-dd");
        RefreshCommand = Command(() => RefreshAsync?.Invoke() ?? Task.CompletedTask);
        SaveFilterCommand = Command(() => SaveFilterAsync?.Invoke() ?? Task.CompletedTask);
        PreviousPageCommand = Command(async () => { Page = Math.Max(1, Page - 1); await (RefreshAsync?.Invoke() ?? Task.CompletedTask); });
        NextPageCommand = Command(async () => { Page++; await (RefreshAsync?.Invoke() ?? Task.CompletedTask); });
        PreviousTradeCommand = Command(() => StepTradeAsync(-1));
        NextTradeCommand = Command(() => StepTradeAsync(1));
        SaveReviewCommand = Command(() => SaveReviewAsync?.Invoke() ?? Task.CompletedTask);
        MarkReviewedCommand = Command(() => MarkReviewedAsync?.Invoke() ?? Task.CompletedTask);
        SaveAssessmentsCommand = Command(() => SaveAssessmentsAsync?.Invoke() ?? Task.CompletedTask);
        ImportAttachmentCommand = Command(() => ImportAttachmentAsync?.Invoke() ?? Task.CompletedTask);
        PasteAttachmentCommand = Command(() => PasteAttachmentAsync?.Invoke() ?? Task.CompletedTask);
        SaveDailyJournalCommand = Command(() => SaveDailyJournalAsync?.Invoke() ?? Task.CompletedTask);
        CompleteDailyJournalCommand = Command(() => CompleteDailyJournalAsync?.Invoke() ?? Task.CompletedTask);
        SavePlaybookCommand = Command(() => SavePlaybookAsync?.Invoke() ?? Task.CompletedTask);
        SaveCampaignCommand = Command(() => SaveCampaignAsync?.Invoke() ?? Task.CompletedTask);
        SaveGoalCommand = Command(() => SaveGoalAsync?.Invoke() ?? Task.CompletedTask);
        NewGoalCommand = new RelayCommand(ClearGoal);
        NewOpportunityCommand = new RelayCommand(ClearOpportunity);
        SaveOpportunityCommand = Command(() => SaveOpportunityAsync?.Invoke() ?? Task.CompletedTask);
        ImportOpportunityAttachmentCommand = Command(() => ImportOpportunityAttachmentAsync?.Invoke() ?? Task.CompletedTask);
        SelectPageCommand = new RelayCommand(SelectPage);
        BulkStrategyCommand = Command(() => BulkEditAsync?.Invoke(ReviewBulkEditKind.Strategy, BulkStrategy) ?? Task.CompletedTask);
        BulkTagsCommand = Command(() => BulkEditAsync?.Invoke(ReviewBulkEditKind.Tags, BulkTags) ?? Task.CompletedTask);
        BulkStatusCommand = Command(() => BulkEditAsync?.Invoke(ReviewBulkEditKind.Status, BulkStatus) ?? Task.CompletedTask);
        GeneratePeriodDraftCommand = new RelayCommand(GeneratePeriodDraft);
        SavePeriodReviewCommand = Command(() => SavePeriodReviewAsync?.Invoke() ?? Task.CompletedTask);
        LoadReplayCommand = Command(() => LoadReplayAsync?.Invoke() ?? Task.CompletedTask);
        PreviousReplayEventCommand = Command(() => StepReplayEventAsync(-1));
        NextReplayEventCommand = Command(() => StepReplayEventAsync(1));
        ToggleReplayCommand = Command(ToggleReplayAsync);
        ExportCommand = Command(() => ExportAsync?.Invoke() ?? Task.CompletedTask);
        ConfirmExportCommand = Command(() => ConfirmExportAsync?.Invoke() ?? Task.CompletedTask);
        BackupCommand = Command(() => BackupAsync?.Invoke() ?? Task.CompletedTask);
        RestoreCommand = Command(() => RestoreAsync?.Invoke() ?? Task.CompletedTask);
        ClearMarketDataCacheCommand = Command(() => ClearMarketDataCacheAsync?.Invoke() ?? Task.CompletedTask);
        SaveBehaviorReviewCommand = Command(() => SaveBehaviorReviewAsync?.Invoke() ?? Task.CompletedTask);
        SaveTradingSessionCommand = Command(() => SaveTradingSessionAsync?.Invoke() ?? Task.CompletedTask);
        NewTradingSessionCommand = new RelayCommand(ClearTradingSession);
        KeepLocalDraftCommand = new RelayCommand(KeepLocalDraft);
        DiscardAndReloadEditorCommand = Command(DiscardAndReloadEditorAsync);
    }

    public Func<Task>? RefreshAsync { get; set; }
    public Func<Task>? SaveFilterAsync { get; set; }
    public Func<ReviewSavedFilter, Task>? ApplySavedFilterAsync { get; set; }
    public Func<long, Task>? OpenTradeAsync { get; set; }
    public Func<Task>? SaveReviewAsync { get; set; }
    public Func<Task>? AutoSaveReviewAsync { get; set; }
    public Func<Task>? AutoSaveWorkspaceAsync { get; set; }
    public Func<Task>? MarkReviewedAsync { get; set; }
    public Func<Task>? SaveAssessmentsAsync { get; set; }
    public Func<string, bool, Task>? ApplyTagSuggestionAsync { get; set; }
    public Func<Task>? ImportAttachmentAsync { get; set; }
    public Func<Task>? PasteAttachmentAsync { get; set; }
    public Func<ReviewAttachment, Task>? OpenAttachmentAsync { get; set; }
    public Func<ReviewAttachment, Task>? DeleteAttachmentAsync { get; set; }
    public Func<ReviewAttachment, string?>? ResolveAttachmentPath { get; set; }
    public Func<Task>? SaveDailyJournalAsync { get; set; }
    public Func<Task>? CompleteDailyJournalAsync { get; set; }
    public Func<DateOnly, Task>? OpenDailyAsync { get; set; }
    public Func<Task>? SavePlaybookAsync { get; set; }
    public Func<Task>? SaveCampaignAsync { get; set; }
    public Func<Task>? SaveGoalAsync { get; set; }
    public Func<ImprovementGoal, Task>? ArchiveGoalAsync { get; set; }
    public Func<Task>? SaveOpportunityAsync { get; set; }
    public Func<Task>? ImportOpportunityAttachmentAsync { get; set; }
    public Func<ReviewBulkEditKind, string, Task>? BulkEditAsync { get; set; }
    public Func<Task>? SavePeriodReviewAsync { get; set; }
    public Func<Task>? LoadReplayAsync { get; set; }
    public Func<double, Task>? SeekReplayAsync { get; set; }
    public Func<Task>? ExportAsync { get; set; }
    public Func<Task>? ConfirmExportAsync { get; set; }
    public Func<Task>? BackupAsync { get; set; }
    public Func<Task>? RestoreAsync { get; set; }
    public Func<Task>? ClearMarketDataCacheAsync { get; set; }
    public Func<Task>? SaveBehaviorReviewAsync { get; set; }
    public Func<Task>? SaveTradingSessionAsync { get; set; }
    public Func<EditEntityKind, Task>? ReloadEditorAsync { get; set; }

    public ICommand RefreshCommand { get; }
    public ICommand SaveFilterCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PreviousTradeCommand { get; }
    public ICommand NextTradeCommand { get; }
    public ICommand SaveReviewCommand { get; }
    public ICommand MarkReviewedCommand { get; }
    public ICommand SaveAssessmentsCommand { get; }
    public ICommand ImportAttachmentCommand { get; }
    public ICommand PasteAttachmentCommand { get; }
    public ICommand SaveDailyJournalCommand { get; }
    public ICommand CompleteDailyJournalCommand { get; }
    public ICommand SavePlaybookCommand { get; }
    public ICommand SaveCampaignCommand { get; }
    public ICommand SaveGoalCommand { get; }
    public ICommand NewGoalCommand { get; }
    public ICommand NewOpportunityCommand { get; }
    public ICommand SaveOpportunityCommand { get; }
    public ICommand ImportOpportunityAttachmentCommand { get; }
    public ICommand SelectPageCommand { get; }
    public ICommand BulkStrategyCommand { get; }
    public ICommand BulkTagsCommand { get; }
    public ICommand BulkStatusCommand { get; }
    public ICommand GeneratePeriodDraftCommand { get; }
    public ICommand SavePeriodReviewCommand { get; }
    public ICommand LoadReplayCommand { get; }
    public ICommand PreviousReplayEventCommand { get; }
    public ICommand NextReplayEventCommand { get; }
    public ICommand ToggleReplayCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ConfirmExportCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ClearMarketDataCacheCommand { get; }
    public ICommand SaveBehaviorReviewCommand { get; }
    public ICommand SaveTradingSessionCommand { get; }
    public ICommand NewTradingSessionCommand { get; }
    public ICommand KeepLocalDraftCommand { get; }
    public ICommand DiscardAndReloadEditorCommand { get; }

    public ObservableCollection<ReviewMetricRow> Metrics { get; } = [];
    public ObservableCollection<ReviewQualityRow> Quality { get; } = [];
    public ObservableCollection<ReviewCalendarRow> Calendar { get; } = [];
    public ObservableCollection<WorkspaceTradeRow> Trades { get; } = [];
    public ObservableCollection<ReviewCurveRow> Curve { get; } = [];
    public ObservableCollection<BehaviorOccurrenceRow> Behaviors { get; } = [];
    public ObservableCollection<BehaviorTradeLinkRow> BehaviorTradeLinks { get; } = [];
    public ObservableCollection<BehaviorSampleRow> BehaviorSamples { get; } = [];
    public ObservableCollection<PlaybookRow> Playbooks { get; } = [];
    public ObservableCollection<CampaignRow> Campaigns { get; } = [];
    public ObservableCollection<GoalRow> Goals { get; } = [];
    public ObservableCollection<OpportunityRow> Opportunities { get; } = [];
    public ObservableCollection<AttachmentRow> OpportunityAttachments { get; } = [];
    public ObservableCollection<ReviewComparisonRow> Comparisons { get; } = [];
    public ObservableCollection<FeeBreakdownRow> FeeBreakdown { get; } = [];
    public ObservableCollection<ReviewDailyCashRow> DailyCashSeries { get; } = [];
    public ObservableCollection<ReviewEquityRow> EquityCurve { get; } = [];
    public ObservableCollection<TradingSessionRow> TradingSessions { get; } = [];
    public ObservableCollection<AnalysisTradeRow> AnalysisDrilldown { get; } = [];
    public ObservableCollection<RiskScatterPoint> RiskScatterPoints { get; } = [];
    public ObservableCollection<SavedFilterRow> SavedFilters { get; } = [];
    public ObservableCollection<WorkspaceGroupRow> GroupPerformance { get; } = [];
    public ObservableCollection<RiskExcursionRow> RiskExcursions { get; } = [];
    public ObservableCollection<ReviewRuleRowViewModel> RuleAssessments { get; } = [];
    public ObservableCollection<ReviewTagSuggestionRow> TagSuggestions { get; } = [];
    public ObservableCollection<AttachmentRow> Attachments { get; } = [];
    public ObservableCollection<ReviewExportAttachmentOption> ExportAttachmentOptions { get; } = [];
    public ObservableCollection<TradeDealRow> TradeDeals { get; } = [];
    public ObservableCollection<TradeProcessRow> TradeProcess { get; } = [];
    public ObservableCollection<DailyFactRow> DailyFacts { get; } = [];
    public ObservableCollection<DailyTimelineRow> DailyTimeline { get; } = [];
    public ObservableCollection<string> ReplayEvents { get; } = [];

    public IReadOnlyList<string> TagModeOptions { get; } = ["任一", "全部"];
    public IReadOnlyList<string> StatusOptions { get; } = ["全部", "待复盘", "草稿", "已复盘", "需重审"];
    public IReadOnlyList<string> AssessmentFilterOptions { get; } = ["全部", "通过", "未通过", "未知", "不适用"];
    public IReadOnlyList<string> BulkStatusOptions { get; } = ["待复盘", "草稿", "已复盘", "需重审"];
    public IReadOnlyList<string> SortOptions { get; } = ["最新平仓", "最早平仓", "最大亏损", "最大回吐", "行为优先", "最早待复盘"];
    public IReadOnlyList<string> ComparisonModeOptions { get; } = ["周期前后", "合规与违规", "买入与卖出"];
    public IReadOnlyList<string> OpportunityKindOptions { get; } = ["事前观察", "事后发现", "主动放弃"];
    public IReadOnlyList<string> OpportunitySideOptions { get; } = ["未指定", "买入", "卖出"];
    public IReadOnlyList<string> OpportunityReasonOptions { get; } = ["不符合规则", "风险额度不足", "主动休息", "犹豫", "没有注意到", "其他"];
    public IReadOnlyList<string> ReplayPrecisionOptions { get; } = ["K线", "Tick"];
    public IReadOnlyList<string> ReplaySpeedOptions { get; } = ["0.5x", "1x", "2x", "4x"];
    public IReadOnlyList<string> ExportScopeOptions { get; } = ["全部筛选结果", "当前页选中交易"];
    public IReadOnlyList<string> GoalRuleOptions { get; } = ["人工检查", .. Enum.GetNames<BehaviorRuleKind>()];

    public string Search { get => _search; set => SetProperty(ref _search, value); }
    public string Strategy { get => _strategy; set => SetProperty(ref _strategy, value); }
    public string Setup { get => _setup; set => SetProperty(ref _setup, value); }
    public string Tags { get => _tags; set => SetProperty(ref _tags, value); }
    public string TagMode { get => _tagMode; set => SetProperty(ref _tagMode, value); }
    public string StatusFilter { get => _statusFilter; set => SetProperty(ref _statusFilter, value); }
    public string AssessmentFilter { get => _assessmentFilter; set => SetProperty(ref _assessmentFilter, value); }
    public string CampaignFilter { get => _campaignFilter; set => SetProperty(ref _campaignFilter, value); }
    public string Sort { get => _sort; set => SetProperty(ref _sort, value); }
    public string ComparisonMode { get => _comparisonMode; set => SetProperty(ref _comparisonMode, value); }
    public string FilterName { get => _filterName; set => SetProperty(ref _filterName, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string QualitySummary { get => _qualitySummary; set => SetProperty(ref _qualitySummary, value); }
    public string StorageStatus { get => _storageStatus; set => SetProperty(ref _storageStatus, value); }
    public string CacheStatus { get => _cacheStatus; set => SetProperty(ref _cacheStatus, value); }
    public int Page { get => _page; set { if (SetProperty(ref _page, Math.Max(1, value))) RaisePropertyChanged(nameof(PageText)); } }
    public int TotalCount { get => _totalCount; set { if (SetProperty(ref _totalCount, value)) RaisePropertyChanged(nameof(PageText)); } }
    public string PageText => $"第 {Page} 页 · 共 {TotalCount} 笔";
    public long? SelectedPositionId { get => _selectedPositionId; set => SetProperty(ref _selectedPositionId, value); }
    public string SelectedTradeTitle { get => _selectedTradeTitle; set => SetProperty(ref _selectedTradeTitle, value); }
    public string TradeFacts { get => _tradeFacts; set => SetProperty(ref _tradeFacts, value); }
    public string TradeIdentity { get => _tradeIdentity; set => SetProperty(ref _tradeIdentity, value); }
    public string PlanFacts { get => _planFacts; set => SetProperty(ref _planFacts, value); }
    public string RiskFacts { get => _riskFacts; set => SetProperty(ref _riskFacts, value); }
    public string AttachmentTitle { get => _attachmentTitle; set => SetProperty(ref _attachmentTitle, value); }
    public string AttachmentEventReference { get => _attachmentEventReference; set => SetProperty(ref _attachmentEventReference, value); }
    public int DocumentRevision { get => _documentRevision; set => SetProperty(ref _documentRevision, value); }
    public string DocumentStatus { get => _documentStatus; set => SetProperty(ref _documentStatus, value); }
    public TradeKey? TradeEditorKey => _tradeEditorKey;
    public EditIdentity? TradeEditorIdentity
    {
        get => _tradeEditorIdentity;
        private set => SetProperty(ref _tradeEditorIdentity, value);
    }
    public long TradeEditSequence
    {
        get => _tradeEditSequence;
        private set => SetProperty(ref _tradeEditSequence, value);
    }
    public bool HasUnsavedReviewChanges
    {
        get => _hasUnsavedReviewChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedReviewChanges, value))
            {
                RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
            }
        }
    }

    public bool HasUnsavedWorkspaceChanges =>
        HasUnsavedReviewChanges || _workspaceEditorStates.Values.Any(item => item.IsDirty);
    public string EditorSaveStatus
    {
        get => _editorSaveStatus;
        private set => SetProperty(ref _editorSaveStatus, value);
    }
    public bool HasEditorSaveIssue => _lastSaveIssueKind is not null;

    public string EntryReason { get => _entryReason; set => SetReviewField(ref _entryReason, value); }
    public string ExitReason { get => _exitReason; set => SetReviewField(ref _exitReason, value); }
    public string DidWell { get => _didWell; set => SetReviewField(ref _didWell, value); }
    public string ToImprove { get => _toImprove; set => SetReviewField(ref _toImprove, value); }
    public string NextAction { get => _nextAction; set => SetReviewField(ref _nextAction, value); }
    public string Summary { get => _summary; set => SetReviewField(ref _summary, value); }
    public string Emotion { get => _emotion; set => SetReviewField(ref _emotion, value); }
    public string MarketCondition { get => _marketCondition; set => SetReviewField(ref _marketCondition, value); }

    public string DailyDate { get => _dailyDate; set => SetProperty(ref _dailyDate, value); }
    public string PreMarketPlan { get => _preMarketPlan; set => SetEditorField(ref _preMarketPlan, value, EditEntityKind.DailyJournal); }
    public string IntradayNotes { get => _intradayNotes; set => SetEditorField(ref _intradayNotes, value, EditEntityKind.DailyJournal); }
    public string PostMarketSummary { get => _postMarketSummary; set => SetEditorField(ref _postMarketSummary, value, EditEntityKind.DailyJournal); }
    public string DailyDidWell { get => _dailyDidWell; set => SetEditorField(ref _dailyDidWell, value, EditEntityKind.DailyJournal); }
    public string DailyToImprove { get => _dailyToImprove; set => SetEditorField(ref _dailyToImprove, value, EditEntityKind.DailyJournal); }
    public string DailyNextAction { get => _dailyNextAction; set => SetEditorField(ref _dailyNextAction, value, EditEntityKind.DailyJournal); }
    public int DailyRevision { get => _dailyRevision; set => SetProperty(ref _dailyRevision, value); }
    public string DailyStatus { get => _dailyStatus; set => SetProperty(ref _dailyStatus, value); }
    public string PlaybookName { get => _playbookName; set => SetProperty(ref _playbookName, value); }
    public string PlaybookSymbols { get => _playbookSymbols; set => SetProperty(ref _playbookSymbols, value); }
    public string PlaybookConditions { get => _playbookConditions; set => SetProperty(ref _playbookConditions, value); }
    public string PlaybookInvalidWhen { get => _playbookInvalidWhen; set => SetProperty(ref _playbookInvalidWhen, value); }
    public string PlaybookRules { get => _playbookRules; set => SetProperty(ref _playbookRules, value); }
    public string CampaignName { get => _campaignName; set => SetProperty(ref _campaignName, value); }
    public string CampaignThesis { get => _campaignThesis; set => SetProperty(ref _campaignThesis, value); }
    public string CampaignMembers { get => _campaignMembers; set => SetProperty(ref _campaignMembers, value); }
    public string GoalName { get => _goalName; set => SetEditorField(ref _goalName, value, EditEntityKind.ImprovementGoal); }
    public string GoalMeasurement { get => _goalMeasurement; set => SetEditorField(ref _goalMeasurement, value, EditEntityKind.ImprovementGoal); }
    public string GoalTarget { get => _goalTarget; set => SetEditorField(ref _goalTarget, value, EditEntityKind.ImprovementGoal); }
    public string GoalRule { get => _goalRule; set => SetEditorField(ref _goalRule, value, EditEntityKind.ImprovementGoal); }
    public bool GoalNotificationEnabled { get => _goalNotificationEnabled; set => SetEditorField(ref _goalNotificationEnabled, value, EditEntityKind.ImprovementGoal); }
    public string GoalSymbols { get => _goalSymbols; set => SetEditorField(ref _goalSymbols, value, EditEntityKind.ImprovementGoal); }
    public string GoalObservationWindow { get => _goalObservationWindow; set => SetEditorField(ref _goalObservationWindow, value, EditEntityKind.ImprovementGoal); }
    public string GoalEndCondition { get => _goalEndCondition; set => SetEditorField(ref _goalEndCondition, value, EditEntityKind.ImprovementGoal); }
    public string? SelectedGoalId { get => _selectedGoalId; private set => SetProperty(ref _selectedGoalId, value); }
    public string OpportunityKind { get => _opportunityKind; set => SetEditorField(ref _opportunityKind, value, EditEntityKind.Opportunity); }
    public string OpportunitySymbol { get => _opportunitySymbol; set => SetEditorField(ref _opportunitySymbol, value, EditEntityKind.Opportunity); }
    public string OpportunitySide { get => _opportunitySide; set => SetEditorField(ref _opportunitySide, value, EditEntityKind.Opportunity); }
    public string OpportunityPlaybook { get => _opportunityPlaybook; set => SetEditorField(ref _opportunityPlaybook, value, EditEntityKind.Opportunity); }
    public string OpportunityEntry { get => _opportunityEntry; set => SetEditorField(ref _opportunityEntry, value, EditEntityKind.Opportunity); }
    public string OpportunityStop { get => _opportunityStop; set => SetEditorField(ref _opportunityStop, value, EditEntityKind.Opportunity); }
    public string OpportunityTarget { get => _opportunityTarget; set => SetEditorField(ref _opportunityTarget, value, EditEntityKind.Opportunity); }
    public string OpportunityReason { get => _opportunityReason; set => SetEditorField(ref _opportunityReason, value, EditEntityKind.Opportunity); }
    public string OpportunityConditions { get => _opportunityConditions; set => SetEditorField(ref _opportunityConditions, value, EditEntityKind.Opportunity); }
    public string OpportunityLinkedTrade { get => _opportunityLinkedTrade; set => SetEditorField(ref _opportunityLinkedTrade, value, EditEntityKind.Opportunity); }
    public string OpportunityNotes { get => _opportunityNotes; set => SetEditorField(ref _opportunityNotes, value, EditEntityKind.Opportunity); }
    public string? OpportunityId { get => _opportunityId; private set => SetProperty(ref _opportunityId, value); }
    public int OpportunityRevision { get => _opportunityRevision; private set => SetProperty(ref _opportunityRevision, value); }
    public string BulkStrategy { get => _bulkStrategy; set => SetProperty(ref _bulkStrategy, value); }
    public string BulkTags { get => _bulkTags; set => SetProperty(ref _bulkTags, value); }
    public string BulkStatus { get => _bulkStatus; set => SetProperty(ref _bulkStatus, value); }
    public IReadOnlyList<long> SelectedTradeIds => Trades.Where(item => item.IsSelected).Select(item => item.PositionId).ToArray();
    public string BulkSelectionText => $"已选择 {SelectedTradeIds.Count} 笔（当前页）";
    public string PeriodFacts { get => _periodFacts; set => SetEditorField(ref _periodFacts, value, EditEntityKind.PeriodReview); }
    public string PeriodDidWell { get => _periodDidWell; set => SetEditorField(ref _periodDidWell, value, EditEntityKind.PeriodReview); }
    public string PeriodToImprove { get => _periodToImprove; set => SetEditorField(ref _periodToImprove, value, EditEntityKind.PeriodReview); }
    public string PeriodNextAction { get => _periodNextAction; set => SetEditorField(ref _periodNextAction, value, EditEntityKind.PeriodReview); }
    public int PeriodRevision { get => _periodRevision; set => SetProperty(ref _periodRevision, value); }
    public string? PeriodReviewId { get => _periodReviewId; set => SetProperty(ref _periodReviewId, value); }
    public string ReplayStatus { get => _replayStatus; set => SetProperty(ref _replayStatus, value); }
    public string ReplayPrecision { get => _replayPrecision; set => SetProperty(ref _replayPrecision, value); }
    public double ReplayProgress
    {
        get => _replayProgress;
        set
        {
            var normalized = Math.Clamp(value, 0d, 100d);
            if (SetProperty(ref _replayProgress, normalized) && !_applyingDetail && SeekReplayAsync is not null)
            {
                _ = SeekReplayAsync(normalized);
            }
        }
    }
    public bool IsReplayPlaying { get => _isReplayPlaying; set { if (SetProperty(ref _isReplayPlaying, value)) RaisePropertyChanged(nameof(ReplayButtonText)); } }
    public string ReplaySpeed { get => _replaySpeed; set => SetProperty(ref _replaySpeed, value); }
    public bool ShowFullReplayReview
    {
        get => _showFullReplayReview;
        set
        {
            if (SetProperty(ref _showFullReplayReview, value) && !_applyingDetail && SeekReplayAsync is not null)
            {
                _ = SeekReplayAsync(ReplayProgress);
            }
        }
    }
    public string ReplayButtonText => IsReplayPlaying ? "暂停" : "播放";
    public string ExportStatus { get => _exportStatus; set => SetProperty(ref _exportStatus, value); }
    public string ExportScope
    {
        get => _exportScope;
        set
        {
            if (SetProperty(ref _exportScope, value))
            {
                ClearExportPreview();
            }
        }
    }
    public string ExportPreview { get => _exportPreview; set => SetProperty(ref _exportPreview, value); }
    public bool CanConfirmExport { get => _canConfirmExport; set => SetProperty(ref _canConfirmExport, value); }
    public IReadOnlyList<string> SelectedExportAttachmentIds => ExportAttachmentOptions
        .Where(item => item.IsIncluded).Select(item => item.Id).ToArray();
    public string AnalysisDrilldownTitle { get => _analysisDrilldownTitle; set => SetProperty(ref _analysisDrilldownTitle, value); }
    public string EquitySummary { get => _equitySummary; private set => SetProperty(ref _equitySummary, value); }
    public string? TradingSessionId { get => _tradingSessionId; private set => SetProperty(ref _tradingSessionId, value); }
    public int TradingSessionRevision { get => _tradingSessionRevision; private set => SetProperty(ref _tradingSessionRevision, value); }
    public string TradingSessionName { get => _tradingSessionName; set => SetProperty(ref _tradingSessionName, value); }
    public string TradingSessionTimeZone { get => _tradingSessionTimeZone; set => SetProperty(ref _tradingSessionTimeZone, value); }
    public string TradingSessionStart { get => _tradingSessionStart; set => SetProperty(ref _tradingSessionStart, value); }
    public string TradingSessionEnd { get => _tradingSessionEnd; set => SetProperty(ref _tradingSessionEnd, value); }
    public string TradingSessionDays { get => _tradingSessionDays; set => SetProperty(ref _tradingSessionDays, value); }
    public string TradingSessionOrder { get => _tradingSessionOrder; set => SetProperty(ref _tradingSessionOrder, value); }
    public bool TradingSessionActive { get => _tradingSessionActive; set => SetProperty(ref _tradingSessionActive, value); }
    public string? SelectedBehaviorId { get => _selectedBehaviorId; private set => SetProperty(ref _selectedBehaviorId, value); }
    public string BehaviorFacts { get => _behaviorFacts; private set => SetProperty(ref _behaviorFacts, value); }
    public string BehaviorExplanation { get => _behaviorExplanation; set => SetEditorField(ref _behaviorExplanation, value, EditEntityKind.Annotation); }
    public bool BehaviorEvidenceInsufficient { get => _behaviorEvidenceInsufficient; set => SetEditorField(ref _behaviorEvidenceInsufficient, value, EditEntityKind.Annotation); }
    public int BehaviorRevision { get => _behaviorRevision; private set => SetProperty(ref _behaviorRevision, value); }
    public string BehaviorReviewStatus { get => _behaviorReviewStatus; set => SetProperty(ref _behaviorReviewStatus, value); }

    public void Apply(ReviewWorkspaceSnapshot snapshot, ReviewWorkspaceData data, long sessionGeneration)
    {
        ClearExportPreview();
        _workspaceAccountKey = data.AccountKey;
        _workspaceSessionGeneration = sessionGeneration;
        _periodFactsSnapshot = snapshot.PeriodFacts;
        _periodComparisonSnapshot = snapshot.PeriodComparison;
        Metrics.Clear();
        var performance = snapshot.Analytics.Performance;
        Metrics.Add(new("净盈亏", Signed(performance.NetPnl), "完整交易，含费用", FinancialPalette.For(performance.NetPnl)));
        Metrics.Add(new("胜率", $"{performance.WinRate:0.##}%", $"{performance.WinCount} 盈 / {performance.LossCount} 亏"));
        Metrics.Add(new("盈亏因子", performance.ProfitFactor?.ToString("0.##") ?? "无亏损样本", "总盈利 ÷ 总亏损绝对值"));
        Metrics.Add(new("期望值", Signed(performance.Expectancy), "每笔完整交易平均", FinancialPalette.For(performance.Expectancy)));
        Metrics.Add(new("利润峰值回吐", $"{performance.MaximumDrawdown:0.##}{(performance.MaximumDrawdownPercentage is null ? "" : $" / {performance.MaximumDrawdownPercentage:0.##}%")}", "按累计已实现利润峰值计算"));
        Metrics.Add(new("平均持仓", FormatDuration(performance.AverageHoldingTime), "首次入场到最终平仓"));
        Metrics.Add(new("初始风险覆盖", $"{snapshot.DataQuality.InitialRiskCoveragePercentage:0.##}%", "可靠开仓风险证据"));
        Metrics.Add(new("过程采样覆盖", $"{snapshot.DataQuality.ExcursionCoveragePercentage:0.##}%", "与 R 覆盖分开"));
        var feeSummary = snapshot.Fees ?? new ReviewFeeSummary(0, 0m, 0m, 0m, 0, 0m, []);
        Metrics.Add(new("费用合计", Signed(feeSummary.GrandTotal), "当前筛选交易费用；未分配费用单列",
            FinancialPalette.For(feeSummary.GrandTotal)));
        FeeBreakdown.ReplaceWith([
            new FeeBreakdownRow("Commission", Signed(feeSummary.Commission), $"{feeSummary.AllocatedDealCount} 笔已分配成交"),
            new FeeBreakdownRow("Swap", Signed(feeSummary.Swap), "保留返还或扣费的原始符号"),
            new FeeBreakdownRow("Fee", Signed(feeSummary.OtherFees), "MT5 原始其他费用"),
            new FeeBreakdownRow("账户级未分配费用", Signed(feeSummary.UnallocatedFees), $"{feeSummary.UnallocatedDealCount} 笔未映射成交，不摊到交易"),
        ]);

        Quality.Clear();
        var qualityIssues = snapshot.DataQuality.Issues ?? [];
        AddQuality("初始风险", snapshot.DataQuality.InitialRiskCoveredCount,
            snapshot.DataQuality.InitialRiskCoveragePercentage, ["risk-missing"]);
        AddQuality("过程采样", snapshot.DataQuality.ExcursionCoveredCount,
            snapshot.DataQuality.ExcursionCoveragePercentage, ["excursion-legacy", "excursion-gap", "excursion-incomplete"]);
        AddQuality("盘前计划", snapshot.DataQuality.PreTradePlanCoveredCount,
            snapshot.DataQuality.PreTradePlanCoveragePercentage, ["plan-missing"]);
        AddQuality("人工复盘", snapshot.DataQuality.ReviewedCount,
            snapshot.DataQuality.ReviewCompletionPercentage, ["review-pending"]);
        AddQuality("历史行情", snapshot.DataQuality.MarketDataCoveredCount,
            snapshot.DataQuality.MarketDataCoveragePercentage,
            ["market-failed", "market-cancelled", "market-partial", "market-empty", "market-missing"]);
        var latestTime = data.ServerTimeSegments?.OrderByDescending(item => item.FromUtc).FirstOrDefault();
        var timeLabel = latestTime is null
            ? "服务器时间来源未知"
            : latestTime.TimeBasis == ReviewTimeBasis.BrokerServer
                ? $"Bridge 精确服务器时间 UTC{FormatOffset(latestTime.UtcOffsetSeconds)}"
                : $"worker 推断服务器时间 UTC{FormatOffset(latestTime.UtcOffsetSeconds)}";
        QualitySummary = $"数据版本 {snapshot.Version.Token} · 完整交易 {snapshot.DataQuality.CompleteTradeCount} · {timeLabel}";

        void AddQuality(string label, int covered, decimal percentage, IReadOnlyCollection<string> issueCodes)
        {
            var matching = qualityIssues.Where(item => issueCodes.Contains(item.Code, StringComparer.Ordinal)).ToArray();
            var keys = matching.SelectMany(item => item.Trades).Distinct().ToArray();
            var details = matching.Length == 0
                ? "当前筛选没有这类缺口。"
                : string.Join("；", matching.Select(item => $"{item.Label} {item.Trades.Count} 笔：{item.Detail}"));
            Quality.Add(new ReviewQualityRow(label, covered, snapshot.DataQuality.CompleteTradeCount, percentage,
                details, new RelayCommand(() =>
                {
                    ApplyAnalysisDrilldown($"数据质量：{label}", keys, data);
                    StatusText = keys.Length == 0
                        ? $"{label}当前没有缺口。"
                        : $"已定位 {keys.Length} 笔{label}缺口；可在分析页下钻列表打开档案。";
                })));
        }

        Calendar.Clear();
        foreach (var day in snapshot.Calendar)
        {
            var journal = data.DailyJournals.GetValueOrDefault(day.ServerDate);
            var facts = snapshot.DailyFacts?.GetValueOrDefault(day.ServerDate);
            Calendar.Add(new ReviewCalendarRow(day.ServerDate.ToString("MM-dd"), Signed(day.RealizedCashPnl),
                day.OpeningTradeCount, day.CompleteTradeCount, day.PendingReviewCount,
                FormatDailyJournalStatus(journal, facts?.SourceVersion), day.HasDataGap ? "数据缺口" : string.Empty,
                new AsyncRelayCommand(() => OpenDailyOrApplyAsync(
                    day.ServerDate, journal, facts, facts?.SourceVersion ?? string.Empty))));
        }

        Trades.Clear();
        foreach (var trade in snapshot.Trades)
        {
            snapshot.Documents.TryGetValue(trade.PositionId, out var document);
            var status = document?.Status switch
            {
                ReviewCompletionStatus.Draft => "草稿",
                ReviewCompletionStatus.Reviewed => "已复盘",
                ReviewCompletionStatus.NeedsReview => "需重审",
                _ => "待复盘",
            };
            data.Metadata.TryGetValue(trade.PositionId, out var metadata);
            var row = new WorkspaceTradeRow(trade.PositionId, trade.ClosedAtUtc?.ToString("MM-dd HH:mm") ?? "持仓中",
                trade.Symbol, trade.Side == TradeSide.Buy ? "买" : "卖", Signed(trade.NetPnl), status,
                metadata?.Strategy ?? "未归类", string.Join("、", metadata?.Tags ?? []),
                FinancialPalette.For(trade.NetPnl), new AsyncRelayCommand(() => OpenTradeAsync?.Invoke(trade.PositionId) ?? Task.CompletedTask));
            row.SelectionChanged += OnTradeSelectionChanged;
            Trades.Add(row);
        }
        OnTradeSelectionChanged();

        Curve.Clear();
        var recentCurve = snapshot.RealizedCurve
            .GroupBy(point => point.ServerDate)
            .Select(group => new
            {
                Point = group.OrderBy(point => point.AtUtc).Last(),
                Trades = group.SelectMany(point => point.Trades).Distinct().ToArray(),
            })
            .OrderBy(item => item.Point.ServerDate)
            .TakeLast(7)
            .ToArray();
        var curveMinimum = recentCurve.Select(item => item.Point.Value).DefaultIfEmpty().Min();
        var curveMaximum = recentCurve.Select(item => item.Point.Value).DefaultIfEmpty().Max();
        var curveRange = curveMaximum - curveMinimum;
        foreach (var item in recentCurve)
        {
            var point = item.Point;
            var height = curveRange <= 0.01m
                ? 44d
                : 12d + 68d * (double)((point.Value - curveMinimum) / curveRange);
            Curve.Add(new ReviewCurveRow(point.ServerDate.ToString("MM-dd"), point.Value, point.Drawdown,
                point.DrawdownPercentage, height, FinancialPalette.For(point.Value), new RelayCommand(() => ApplyAnalysisDrilldown(
                    $"利润曲线节点 {point.ServerDate:yyyy-MM-dd}", item.Trades, data))));
        }
        var recentCash = (snapshot.DailyCash ?? [])
            .OrderBy(point => point.ServerDate)
            .TakeLast(7)
            .ToArray();
        var maximumCashMagnitude = recentCash.Select(point => Math.Abs(point.CashPnl)).DefaultIfEmpty(0m).Max();
        DailyCashSeries.ReplaceWith(recentCash.Select(point => new ReviewDailyCashRow(
            point.ServerDate.ToString("MM-dd"), point.CashPnl, point.DealTickets.Count,
            maximumCashMagnitude <= 0.01m ? 12d : 12d + 58d * (double)(Math.Abs(point.CashPnl) / maximumCashMagnitude),
            FinancialPalette.For(point.CashPnl), new RelayCommand(() => ApplyAnalysisDrilldown(
                $"现金盈亏 {point.ServerDate:yyyy-MM-dd}", point.Trades, data)))));
        var equity = snapshot.EquityAnalysis;
        EquitySummary = equity is null
            ? "当前范围没有账户净值分析。"
            : equity.CashFlowAdjustedMaximumDrawdownPercentage is { } adjusted
                ? $"观测最大回撤 {equity.ObservedMaximumDrawdownAmount:0.##} / {equity.ObservedMaximumDrawdownPercentage:0.##}% · 排除已核验资金流后 {adjusted:0.##}% · {equity.Message}"
                : $"观测最大回撤 {equity.ObservedMaximumDrawdownAmount?.ToString("0.##") ?? "未知"} / {equity.ObservedMaximumDrawdownPercentage?.ToString("0.##") ?? "未知"}% · 完整账户净值回撤率不可用 · {equity.Message}";
        var equityPoints = equity?.Points ?? [];
        var equityStride = Math.Max(1, (int)Math.Ceiling(equityPoints.Count / 120d));
        EquityCurve.ReplaceWith(equityPoints
            .Where((_, index) => index % equityStride == 0 || index == equityPoints.Count - 1)
            .Select(point => new ReviewEquityRow(
                point.AtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                point.Equity.ToString("0.##"),
                point.ObservedDrawdownAmount.ToString("0.##"),
                point.ObservedDrawdownPercentage?.ToString("0.##") ?? "未知",
                point.UnitizedValue.ToString("0.####"),
                point.Segment.ToString(CultureInfo.InvariantCulture),
                point.StartsAfterUnverifiedCashFlow ? "资金流边界缺口" : string.Empty)));
        Behaviors.Clear();
        foreach (var item in data.Behaviors.OrderByDescending(item => item.EventAtUtc))
        {
            Behaviors.Add(new BehaviorOccurrenceRow(item.EventAtUtc.ToString("MM-dd HH:mm"), item.Rule.ToString(),
                item.Level.ToString(), FormatBehaviorSource(item.Source), item.Summary,
                item.TradeLinks.Count, item.NotificationDisposition,
                new RelayCommand(() => SelectBehavior(item, data))));
        }
        if (SelectedBehaviorId is not null)
        {
            var selectedBehavior = data.Behaviors.FirstOrDefault(item => item.Id == SelectedBehaviorId);
            if (selectedBehavior is not null)
            {
                SelectBehavior(selectedBehavior, data);
            }
            else
            {
                ClearBehaviorSelection();
            }
        }
        var behaviorEvidence = snapshot.BehaviorEvidence;
        BehaviorSamples.ReplaceWith(behaviorEvidence is null
            ? []
            : [
                new BehaviorSampleRow("命中行为规则", behaviorEvidence.HitSample.TradeCount,
                    Signed(behaviorEvidence.HitSample.NetPnl),
                    $"实时 {behaviorEvidence.LiveOccurrenceCount} 次 · 历史重算 {behaviorEvidence.RecalculatedOccurrenceCount} 次",
                    new RelayCommand(() => ApplyAnalysisDrilldown(
                        "行为规则命中样本", behaviorEvidence.HitSample.Trades, data))),
                new BehaviorSampleRow("同筛选未命中", behaviorEvidence.UnhitSample.TradeCount,
                    Signed(behaviorEvidence.UnhitSample.NetPnl), "同一筛选集合，仅作相关样本对照",
                    new RelayCommand(() => ApplyAnalysisDrilldown(
                        "行为规则未命中样本", behaviorEvidence.UnhitSample.Trades, data))),
            ]);
        Playbooks.ReplaceWith(data.Playbooks.OrderByDescending(item => item.Version).Select(item =>
            new PlaybookRow(item.Name, $"v{item.Version}", item.Rules.Count, item.IsActive ? "启用" : "历史", item.EffectiveFromUtc.ToString("yyyy-MM-dd"))));
        Campaigns.ReplaceWith(data.Campaigns.Select(item => new CampaignRow(item.Name, item.Symbol, item.PositionIds.Count, item.Thesis)));
        var goalProgress = (snapshot.GoalProgress ?? []).ToDictionary(item => item.Goal.Id);
        Goals.ReplaceWith(data.Goals.Select(item =>
        {
            goalProgress.TryGetValue(item.Id, out var progress);
            var observationSummary = progress is null || progress.OpportunityCount == 0
                ? $"当前范围无触发机会 · 无机会日 {progress?.NoOpportunityDayCount ?? 0} 天 · 无法计算执行率"
                : $"机会 {progress.OpportunityCount} · 通过 {progress.PassCount} · 失败 {progress.FailCount} · 执行率 {progress.AdherencePercentage:0.##}% · 无机会日 {progress.NoOpportunityDayCount} 天";
            var baseline = item.BaselineOpportunityCount == 0
                ? "基线：缺少合格机会样本"
                : $"基线 {item.BaselineFromServerDate:yyyy-MM-dd}—{item.BaselineToServerDate:yyyy-MM-dd} · {item.BaselinePassCount}/{item.BaselinePassCount + item.BaselineFailCount} · {item.BaselineValue:0.##}%";
            return new GoalRow(item.Name, $"v{Math.Max(1, item.Version)} · {FormatGoalStatus(item.Status)}", item.Measurement,
                item.TargetValue?.ToString("0.##") ?? "人工检查", item.NotificationEnabled ? "桌宠提醒" : "仅复盘",
                $"{observationSummary}\n{baseline}\n窗口 {item.ObservationWindowDays} 天 · 适用品种 {(string.IsNullOrWhiteSpace(item.ApplicableSymbols) ? "全部" : item.ApplicableSymbols)}",
                item.Status != ImprovementGoalStatus.Archived,
                new RelayCommand(() => SelectGoal(item)),
                new AsyncRelayCommand(() => ArchiveGoalAsync?.Invoke(item) ?? Task.CompletedTask));
        }));
        Opportunities.ReplaceWith(data.Opportunities.OrderByDescending(item => item.ObservedAtUtc).Select(item =>
            new OpportunityRow(item.ServerDate.ToString("MM-dd"), item.Symbol, FormatOpportunityKind(item.Kind), item.Reason,
                item.Notes, new RelayCommand(() => SelectOpportunity(item, data.OpportunityAttachments ?? [])))));
        if (OpportunityId is not null)
        {
            var selectedOpportunity = data.Opportunities.FirstOrDefault(item => item.Id == OpportunityId);
            if (selectedOpportunity is not null)
            {
                SelectOpportunity(selectedOpportunity, data.OpportunityAttachments ?? []);
            }
            else
            {
                ClearOpportunity();
            }
        }
        GroupPerformance.Clear();
        AddGroups("方向", snapshot.Analytics.SidePerformance);
        AddGroups("品种", snapshot.Analytics.SymbolPerformance);
        AddGroups("星期", snapshot.Analytics.WeekdayPerformance);
        AddGroups("入场时段", snapshot.Analytics.HourPerformance);
        AddGroups("自定义时段", snapshot.TradingSessionPerformance ?? []);
        AddGroups("持仓时长", snapshot.Analytics.DurationPerformance);
        AddGroups("策略", snapshot.Analytics.StrategyPerformance);
        AddGroups("形态", snapshot.Analytics.SetupPerformance);
        AddGroups("标签", snapshot.Analytics.TagPerformance);
        RiskExcursions.ReplaceWith((snapshot.RiskSamples ?? []).Select(sample =>
            new RiskExcursionRow(sample.Trade.PositionId, sample.Symbol, sample.NetPnl, sample.OpeningVolume,
                sample.InitialRiskAmount, sample.Mae, sample.Mfe, sample.ActualRiskMultiple,
                sample.HasReliableExcursion
                    ? "R/波动可靠"
                    : sample.ExcursionAlgorithmVersion == "legacy-extrema-v1"
                        ? sample.HasReliableInitialRisk ? "R可用/旧极值" : "旧极值"
                        : sample.HasReliableInitialRisk ? "R可用/波动缺口" : data.Excursions.ContainsKey(sample.Trade.PositionId) ? "波动缺口" : "无采样",
                new RelayCommand(() => ApplyAnalysisDrilldown(
                    $"风险样本 position {sample.Trade.PositionId}", [sample.Trade], data)))));
        var reliableRiskSamples = (snapshot.RiskSamples ?? [])
            .Where(item => item.HasReliableExcursion && item.Mae is not null && item.Mfe is not null)
            .ToArray();
        var maximumMae = reliableRiskSamples.Select(item => Math.Max(0m, -item.Mae!.Value)).DefaultIfEmpty(0m).Max();
        var maximumMfe = reliableRiskSamples.Select(item => Math.Max(0m, item.Mfe!.Value)).DefaultIfEmpty(0m).Max();
        RiskScatterPoints.ReplaceWith(reliableRiskSamples.Select(sample =>
        {
            var mae = Math.Max(0m, -sample.Mae!.Value);
            var mfe = Math.Max(0m, sample.Mfe!.Value);
            return new RiskScatterPoint(
                18d + (maximumMae <= 0m ? 0d : (double)(mae / maximumMae) * 370d),
                168d - (maximumMfe <= 0m ? 0d : (double)(mfe / maximumMfe) * 145d),
                FinancialPalette.For(sample.NetPnl),
                $"position {sample.Trade.PositionId} · MAE {sample.Mae:0.##} · MFE {sample.Mfe:0.##} · R {sample.ActualRiskMultiple?.ToString("0.##") ?? "未知"}",
                new RelayCommand(() => ApplyAnalysisDrilldown(
                    $"MAE/MFE position {sample.Trade.PositionId}", [sample.Trade], data)));
        }));
        Comparisons.Clear();
        if (snapshot.PeriodComparison is { } comparison)
        {
            Comparisons.Add(new ReviewComparisonRow(comparison.LeftLabel, comparison.Left.TradeCount,
                Signed(comparison.Left.NetPnl), $"{comparison.Left.WinRate:0.##}%", comparison.Left.Expectancy,
                comparison.Left.AverageOpeningVolume.ToString("0.#####"),
                comparison.LeftAverageInitialRisk?.ToString("0.##") ?? "未知",
                $"{comparison.LeftRiskCoveragePercentage:0.##}%",
                comparison.Left.TradeCount < 30 ? "少于 30 笔，仅作样本提示" : string.Empty,
                new RelayCommand(() => ApplyAnalysisDrilldown(comparison.LeftLabel, comparison.LeftTrades ?? [], data))));
            Comparisons.Add(new ReviewComparisonRow(comparison.RightLabel, comparison.Right.TradeCount,
                Signed(comparison.Right.NetPnl), $"{comparison.Right.WinRate:0.##}%", comparison.Right.Expectancy,
                comparison.Right.AverageOpeningVolume.ToString("0.#####"),
                comparison.RightAverageInitialRisk?.ToString("0.##") ?? "未知",
                $"{comparison.RightRiskCoveragePercentage:0.##}%",
                comparison.Right.TradeCount < 30 ? "少于 30 笔，仅作样本提示" : string.Empty,
                new RelayCommand(() => ApplyAnalysisDrilldown(comparison.RightLabel, comparison.RightTrades ?? [], data))));
        }
        AnalysisDrilldown.Clear();
        AnalysisDrilldownTitle = "点击分析结果查看对应交易";
        var period = data.PeriodReviews?.FirstOrDefault(item =>
            item.FromServerDate == snapshot.Filter.FromServerDate && item.ToServerDate == snapshot.Filter.ToServerDate);
        var periodEntityId = period?.Id ??
            $"period:{snapshot.Filter.FromServerDate:yyyy-MM-dd}:{snapshot.Filter.ToServerDate:yyyy-MM-dd}";
        var restorePeriod = PrepareWorkspaceEditor(
            EditEntityKind.PeriodReview, data.AccountKey, periodEntityId, sessionGeneration);
        var wasApplying = _applyingDetail;
        _applyingDetail = true;
        try
        {
            PeriodReviewId = periodEntityId;
            PeriodFacts = period?.Facts ?? string.Empty;
            PeriodDidWell = period?.DidWell ?? string.Empty;
            PeriodToImprove = period?.ToImprove ?? string.Empty;
            PeriodNextAction = period?.NextAction ?? string.Empty;
            PeriodRevision = period?.Revision ?? 0;
        }
        finally
        {
            _applyingDetail = wasApplying;
        }
        if (restorePeriod)
        {
            RestoreWorkspaceDraft(EditEntityKind.PeriodReview);
        }
        SavedFilters.ReplaceWith((data.SavedFilters ?? []).Select(item => new SavedFilterRow(
            item.Name, item.Revision, $"{item.AccountKey} · 完整交易按最终平仓服务器日",
            new AsyncRelayCommand(() => ApplySavedFilterAsync?.Invoke(item) ?? Task.CompletedTask))));
        TradingSessions.ReplaceWith((data.TradingSessions ?? [])
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new TradingSessionRow(
                item.Name,
                $"{item.StartLocalTime:HH\\:mm}–{item.EndLocalTime:HH\\:mm}",
                item.TimeZoneId,
                FormatSessionDays(item.StartDays),
                item.IsActive ? "启用" : "停用",
                new RelayCommand(() => SelectTradingSession(item)))));
        if (TradingSessionId is not null && !(data.TradingSessions ?? []).Any(item => item.Id == TradingSessionId))
        {
            ClearTradingSession();
        }
        TotalCount = snapshot.TotalCount;
        Page = snapshot.Page;
        StatusText = $"{snapshot.Filter.FromServerDate:yyyy-MM-dd} 至 {snapshot.Filter.ToServerDate:yyyy-MM-dd} · 当前页 {snapshot.Trades.Count} 笔";

        void AddGroups(string dimension, IEnumerable<GroupMetricRow> groups)
        {
            foreach (var item in groups)
            {
                GroupPerformance.Add(new WorkspaceGroupRow(dimension, item.Group, item.TradeCount,
                    $"{item.WinRate:0.##}%", Signed(item.NetPnl), Signed(item.Expectancy),
                    item.ProfitFactor?.ToString("0.##") ?? "—", Signed(item.AveragePnl),
                    item.AverageActualRiskMultiple?.ToString("0.##") ?? "未知",
                    $"{item.RiskCoveredCount}/{item.TradeCount} · {item.RiskCoveragePercentage:0.##}%",
                    Signed(item.TotalFees), item.HasSmallSampleWarning ? "样本<30" : string.Empty,
                    new RelayCommand(() => ApplyAnalysisDrilldown(
                        $"{dimension} / {item.Group}", item.Trades ?? [], data))));
            }
        }
    }

    private void SelectTradingSession(TradingSessionDefinition session)
    {
        TradingSessionId = session.Id;
        TradingSessionRevision = session.Revision;
        TradingSessionName = session.Name;
        TradingSessionTimeZone = session.TimeZoneId;
        TradingSessionStart = session.StartLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        TradingSessionEnd = session.EndLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        TradingSessionDays = FormatSessionDays(session.StartDays);
        TradingSessionOrder = session.SortOrder.ToString(CultureInfo.InvariantCulture);
        TradingSessionActive = session.IsActive;
    }

    public void ApplyTradingSessionSave(TradingSessionDefinition session) => SelectTradingSession(session);

    private void ClearTradingSession()
    {
        TradingSessionId = null;
        TradingSessionRevision = 0;
        TradingSessionName = string.Empty;
        TradingSessionTimeZone = "China Standard Time";
        TradingSessionStart = "09:00";
        TradingSessionEnd = "17:00";
        TradingSessionDays = "周一、周二、周三、周四、周五";
        TradingSessionOrder = "0";
        TradingSessionActive = true;
    }

    private static string FormatSessionDays(IReadOnlyCollection<DayOfWeek> days)
    {
        if (days.Count == 0 || days.Count == 7)
        {
            return "每天";
        }
        return string.Join("、", days.Order().Select(day => day switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日",
        }));
    }

    public void ApplyDetail(
        TradeDetailData detail,
        long sessionGeneration,
        PlaybookVersion? fallbackPlaybook = null,
        string currency = "",
        IReadOnlyList<ReviewTagSuggestion>? tagSuggestions = null)
    {
        CancelPendingReviewAutoSave();
        var key = new TradeKey(detail.Trade.AccountKey, detail.Trade.PositionId);
        _workspaceAccountKey = key.AccountKey;
        _workspaceSessionGeneration = sessionGeneration;
        var restoreRuleDraft = PrepareWorkspaceEditor(
            EditEntityKind.RuleAssessment,
            key.AccountKey,
            key.PositionId.ToString(CultureInfo.InvariantCulture),
            sessionGeneration);
        var editorIdentity = EditIdentity.ForTrade(key, sessionGeneration, Guid.NewGuid());
        _tradeDrafts.TryGetValue(key, out var pendingDraft);
        _applyingDetail = true;
        try
        {
            _tradeEditorKey = key;
            TradeEditorIdentity = editorIdentity;
            TradeEditSequence = pendingDraft?.ContentSequence ?? 1;
            SelectedPositionId = detail.Trade.PositionId;
            SelectedTradeTitle = $"#{detail.Trade.PositionId} · {detail.Trade.Symbol} · {(detail.Trade.Side == TradeSide.Buy ? "买入" : "卖出")}";
            TradeIdentity = $"账户 {detail.Trade.AccountKey} · 币种 {(string.IsNullOrWhiteSpace(currency) ? "未知" : currency)} · position {detail.Trade.PositionId}";
            TradeFacts = $"{detail.Trade.OpenedAtUtc:yyyy-MM-dd HH:mm} → {detail.Trade.ClosedAtUtc:yyyy-MM-dd HH:mm} · 入场 {detail.Trade.EntryPrice} · 出场 {detail.Trade.ExitPrice} · 净盈亏 {Signed(detail.Trade.NetPnl)}";
            var plan = detail.Plan;
            var planTiming = plan is null ? string.Empty : plan.CreatedAtUtc <= detail.Trade.OpenedAtUtc ? "盘前记录" : "事后补录";
            PlanFacts = plan is null
                ? $"具体计划：未绑定 · 策略版本：{detail.Playbook?.Name ?? "未绑定"}"
                : $"{planTiming} · {plan.Strategy}/{plan.Setup} · 入场区 {plan.EntryLow?.ToString() ?? "?"}–{plan.EntryHigh?.ToString() ?? "?"} · 止损 {plan.StopPrice?.ToString() ?? "?"} · 目标 {plan.TargetPrice?.ToString() ?? "?"} · 策略版本 {detail.Playbook?.Name ?? "未绑定"}";
            RiskFacts = FormatRiskFacts(detail.Excursion, detail.Trade.NetPnl);
            var document = detail.Document;
            EntryReason = pendingDraft?.EntryReason ?? document?.EntryReason ?? string.Empty;
            ExitReason = pendingDraft?.ExitReason ?? document?.ExitReason ?? string.Empty;
            DidWell = pendingDraft?.DidWell ?? document?.DidWell ?? string.Empty;
            ToImprove = pendingDraft?.ToImprove ?? document?.ToImprove ?? string.Empty;
            NextAction = pendingDraft?.NextAction ?? document?.NextAction ?? string.Empty;
            Summary = pendingDraft?.Summary ?? document?.Summary ?? string.Empty;
            Emotion = pendingDraft?.Emotion ?? document?.Emotion ?? string.Empty;
            MarketCondition = pendingDraft?.MarketCondition ?? document?.MarketCondition ?? string.Empty;
            DocumentRevision = pendingDraft?.ExpectedRevision ?? document?.Revision ?? 0;
            DocumentStatus = document?.Status switch
            {
                ReviewCompletionStatus.Draft => "草稿",
                ReviewCompletionStatus.Reviewed => "已复盘",
                ReviewCompletionStatus.NeedsReview => "需重审",
                _ => "待复盘",
            };
            Attachments.ReplaceWith(detail.Attachments.Select(CreateAttachmentRow));
            TradeDeals.ReplaceWith(detail.Deals.OrderBy(item => item.OccurredAtUtc).Select(item => new TradeDealRow(
                item.OccurredAtUtc.ToString("MM-dd HH:mm:ss"), item.Ticket.ToString(CultureInfo.InvariantCulture),
                FormatDealKind(item.EntryKind), item.Volume.ToString("0.#####"), item.Price.ToString("0.#####"),
                Signed(item.Profit), Signed(item.Commission), Signed(item.Swap), Signed(item.Fee), Signed(item.NetPnl))));
            var process = detail.Deals.Select(item => new TradeProcessRow(
                    item.OccurredAtUtc, FormatDealKind(item.EntryKind),
                    $"{item.Volume:0.#####} @ {item.Price:0.#####} · 净额 {Signed(item.NetPnl)}", "MT5 成交"))
                .Concat(detail.PnlSamples.Select(item => new TradeProcessRow(
                    item.CapturedAtUtc, "持仓观测",
                    $"数量 {item.Volume:0.#####} · 浮动 {Signed(item.FloatingPnl)} · SL {item.StopLoss?.ToString() ?? "未知"} · TP {item.TakeProfit?.ToString() ?? "未知"} · 间隔 {item.GapMilliseconds}ms",
                    item.GapMilliseconds > 0 ? "盘中采样（有缺口）" : "盘中采样")))
                .Concat(detail.Behaviors.Select(item => new TradeProcessRow(
                    item.EventAtUtc, $"行为：{item.Rule}",
                    $"{item.Summary} · 提醒 {item.NotificationDisposition} · {item.MissingData}", item.Source.ToString())))
                .OrderBy(item => item.OccurredAtUtc)
                .ToArray();
            TradeProcess.ReplaceWith(process);
            TagSuggestions.ReplaceWith((tagSuggestions ?? [])
                .Select(item => new ReviewTagSuggestionRow(
                    item.Tag,
                    item.IsAccepted ? "已采用" : "候选",
                    item.Level == BehaviorRiskLevel.Critical ? "严重" : "关注",
                    item.Reason,
                    $"来源行为事件：{string.Join("、", item.BehaviorOccurrenceIds)}",
                    new AsyncRelayCommand(() => ApplyTagSuggestionAsync?.Invoke(item.Tag, !item.IsAccepted)
                        ?? Task.CompletedTask),
                    item.IsAccepted ? "撤销采用" : "采用")));
            RuleAssessments.Clear();
            var playbook = detail.Playbook ?? fallbackPlaybook;
            if (playbook is not null)
            {
                foreach (var rule in playbook.Rules.OrderBy(item => item.Order))
                {
                    var assessment = detail.Assessments.FirstOrDefault(item => item.PlaybookVersionId == playbook.Id && item.RuleId == rule.Id);
                    RuleAssessments.Add(new ReviewRuleRowViewModel(playbook.Id, rule.Id, rule.Section.ToString(), rule.Name,
                        rule.IsCritical, assessment?.Status ?? RuleAssessmentStatus.Unknown, assessment?.Notes ?? string.Empty,
                        assessment?.Revision ?? 0,
                        () => MarkWorkspaceEditorChanged(EditEntityKind.RuleAssessment)));
                }
            }
            CampaignMembers = detail.Campaign is null
                ? detail.Trade.PositionId.ToString(CultureInfo.InvariantCulture)
                : string.Join(",", detail.Campaign.PositionIds);
            CampaignName = detail.Campaign?.Name ?? string.Empty;
            CampaignThesis = detail.Campaign?.Thesis ?? string.Empty;
            ReplayEvents.Clear();
            ReplayProgress = 0d;
            ShowFullReplayReview = false;
            _replayNavigationEvents = [];
            _replayCurrentEventIndex = -1;
            ReplayStatus = "已载入交易事实；点击加载行情开始真实回放。";
        }
        finally
        {
            _applyingDetail = false;
            HasUnsavedReviewChanges = pendingDraft is not null;
            if (pendingDraft is not null)
            {
                _tradeDrafts[key] = pendingDraft with { Identity = editorIdentity };
                StatusText = "已恢复此账户尚未提交的交易草稿。";
            }
        }
        if (restoreRuleDraft)
        {
            RestoreWorkspaceDraft(EditEntityKind.RuleAssessment);
        }
    }

    public TradeReviewEditSubmission? CaptureTradeReviewEdit(
        TradeReviewBasis basis,
        ReviewCompletionStatus requestedStatus = ReviewCompletionStatus.Draft)
    {
        if (_tradeEditorIdentity is not { } identity || _tradeEditorKey is not { } key || TradeEditSequence < 1)
        {
            return null;
        }
        if (!identity.IsForTrade(key))
        {
            return null;
        }

        var command = new SaveTradeReviewCommand(
            key,
            EntryReason,
            ExitReason,
            DidWell,
            ToImprove,
            NextAction,
            Summary,
            Emotion,
            MarketCondition,
            basis.SourceVersion,
            basis.RuleVersion,
            requestedStatus);
        return new TradeReviewEditSubmission(
            new EditSnapshot<SaveTradeReviewCommand>(identity, TradeEditSequence, command),
            DocumentRevision);
    }

    public bool ApplyTradeReviewSaveReceipt(EditSaveReceipt<TradeReviewDocument> receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.IsSaved)
        {
            var savedDraft = _tradeDrafts.FirstOrDefault(item =>
                item.Value.Identity == receipt.Identity &&
                item.Value.ContentSequence == receipt.ContentSequence);
            if (!savedDraft.Equals(default(KeyValuePair<TradeKey, TradeDraftState>)))
            {
                _tradeDrafts.Remove(savedDraft.Key);
            }
        }

        if (_tradeEditorIdentity is null ||
            !receipt.CanAcknowledge(_tradeEditorIdentity, TradeEditSequence) ||
            receipt.Value is null)
        {
            return false;
        }

        CancelPendingReviewAutoSave();
        HasUnsavedReviewChanges = false;
        DocumentRevision = receipt.Value.Revision;
        DocumentStatus = receipt.Value.Status switch
        {
            ReviewCompletionStatus.Draft => "草稿",
            ReviewCompletionStatus.Reviewed => "已复盘",
            ReviewCompletionStatus.NeedsReview => "需重审",
            _ => "待复盘",
        };
        if (_tradeEditorKey is { } key)
        {
            _tradeDrafts.Remove(key);
        }
        return true;
    }

    public bool IsCurrentTradeEdit(EditIdentity identity, long? contentSequence = null) =>
        TradeEditorIdentity == identity &&
        (contentSequence is null || TradeEditSequence == contentSequence.Value);

    public void ResetAccountState(string message, bool preserveTradeDraft = true)
    {
        ClearExportPreview();
        CancelPendingReviewAutoSave();
        CancelPendingWorkspaceAutoSave();
        if (preserveTradeDraft && HasUnsavedReviewChanges && _tradeEditorKey is { } key &&
            _tradeEditorIdentity is { } identity && TradeEditSequence > 0)
        {
            _tradeDrafts[key] = new TradeDraftState(
                identity,
                TradeEditSequence,
                DocumentRevision,
                EntryReason,
                ExitReason,
                DidWell,
                ToImprove,
                NextAction,
                Summary,
                Emotion,
                MarketCondition);
        }
        if (preserveTradeDraft)
        {
            PreserveAllWorkspaceDrafts();
        }
        else
        {
            _tradeDrafts.Clear();
            _workspaceDrafts.Clear();
        }
        _workspaceEditorStates.Clear();
        _lastSaveIssueKind = null;
        RaisePropertyChanged(nameof(HasEditorSaveIssue));
        _workspaceAccountKey = null;
        _workspaceSessionGeneration = 0;
        _acceptingEdits = true;

        _applyingDetail = true;
        try
        {
            _tradeEditorKey = null;
            TradeEditorIdentity = null;
            TradeEditSequence = 0;
            SelectedPositionId = null;
            SelectedTradeTitle = "请选择一笔交易";
            TradeIdentity = "尚未选择交易";
            TradeFacts = "成交、费用和时间均来自 MT5，只读展示。";
            PlanFacts = "没有绑定计划。";
            RiskFacts = "初始风险与持仓采样未知。";
            EntryReason = string.Empty;
            ExitReason = string.Empty;
            DidWell = string.Empty;
            ToImprove = string.Empty;
            NextAction = string.Empty;
            Summary = string.Empty;
            Emotion = string.Empty;
            MarketCondition = string.Empty;
            DocumentRevision = 0;
            DocumentStatus = "待复盘";
            AttachmentTitle = string.Empty;
            AttachmentEventReference = string.Empty;

            PreMarketPlan = string.Empty;
            IntradayNotes = string.Empty;
            PostMarketSummary = string.Empty;
            DailyDidWell = string.Empty;
            DailyToImprove = string.Empty;
            DailyNextAction = string.Empty;
            DailyRevision = 0;
            DailyStatus = "尚未完成日总结";
            PlaybookName = string.Empty;
            PlaybookSymbols = string.Empty;
            PlaybookConditions = string.Empty;
            PlaybookInvalidWhen = string.Empty;
            PlaybookRules = "入场|确认入场条件|写明事实依据|关键\r\n风险|开仓即有止损|检查首次风险证据|关键\r\n退出|按计划退出|记录退出依据|普通";
            CampaignName = string.Empty;
            CampaignThesis = string.Empty;
            CampaignMembers = string.Empty;
            ClearGoal();
            ClearOpportunity();
            BulkStrategy = string.Empty;
            BulkTags = string.Empty;
            BulkStatus = "草稿";
            PeriodFacts = string.Empty;
            PeriodDidWell = string.Empty;
            PeriodToImprove = string.Empty;
            PeriodNextAction = string.Empty;
            PeriodRevision = 0;
            PeriodReviewId = null;
            SelectedBehaviorId = null;
            BehaviorFacts = "请选择一条行为事件查看原始证据。";
            BehaviorExplanation = string.Empty;
            BehaviorEvidenceInsufficient = false;
            BehaviorRevision = 0;
            BehaviorReviewStatus = "人工解释只补充结论，不改变规则事实。";
            ClearTradingSession();
            ReplayProgress = 0;
            IsReplayPlaying = false;
            ShowFullReplayReview = false;
            ReplayStatus = "选择交易后可加载真实历史行情";
            _periodFactsSnapshot = null;
            _periodComparisonSnapshot = null;
            _replayNavigationEvents = [];
            _replayCurrentEventIndex = -1;

            Metrics.Clear();
            Quality.Clear();
            Calendar.Clear();
            Trades.Clear();
            Curve.Clear();
            Behaviors.Clear();
            BehaviorTradeLinks.Clear();
            BehaviorSamples.Clear();
            Playbooks.Clear();
            Campaigns.Clear();
            Goals.Clear();
            Opportunities.Clear();
            OpportunityAttachments.Clear();
            Comparisons.Clear();
            FeeBreakdown.Clear();
            DailyCashSeries.Clear();
            EquityCurve.Clear();
            TradingSessions.Clear();
            AnalysisDrilldown.Clear();
            RiskScatterPoints.Clear();
            SavedFilters.Clear();
            GroupPerformance.Clear();
            RiskExcursions.Clear();
            RuleAssessments.Clear();
            TagSuggestions.Clear();
            Attachments.Clear();
            TradeDeals.Clear();
            TradeProcess.Clear();
            DailyFacts.Clear();
            DailyTimeline.Clear();
            ReplayEvents.Clear();
        }
        finally
        {
            _applyingDetail = false;
            HasUnsavedReviewChanges = false;
            EditorSaveStatus = preserveTradeDraft && _workspaceDrafts.Count > 0
                ? "账户已切换；未提交编辑按原账户和实体保留。"
                : "当前账户编辑器已清理。";
            RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
            StatusText = message;
        }
    }

    public void CancelPendingReviewAutoSave()
    {
        _draftDelay?.Cancel();
        _draftDelay?.Dispose();
        _draftDelay = null;
    }

    public void ApplyReplay(MarketHistoryResult history, ReplayFrame frame, double progress)
    {
        _applyingDetail = true;
        ReplayProgress = progress;
        _applyingDetail = false;
        _replayStartUtc = history.Range.ActualFromUtc ?? history.Range.RequestedFromUtc;
        _replayEndUtc = history.Range.RequestedToUtc;
        _replayNavigationEvents = frame.NavigationEvents ?? [];
        _replayCurrentEventIndex = frame.CurrentEventIndex;
        ReplayEvents.Clear();
        foreach (var bar in frame.VisibleBars.TakeLast(80))
        {
            ReplayEvents.Add($"{bar.OpenedAtUtc:HH:mm} K线已完成 O {bar.Open} H {bar.High} L {bar.Low} C {bar.Close}");
        }
        foreach (var tick in frame.VisibleTicks.TakeLast(60))
        {
            ReplayEvents.Add($"{tick.OccurredAtUtc:HH:mm:ss.fff} 报价 Bid {tick.Bid} Ask {tick.Ask} Last {tick.Last}");
        }
        foreach (var deal in frame.VisibleDeals)
        {
            ReplayEvents.Add($"{deal.OccurredAtUtc:HH:mm:ss} 成交 #{deal.Ticket} {deal.EntryKind} {deal.Volume}");
        }
        foreach (var behavior in frame.VisibleBehaviors)
        {
            ReplayEvents.Add($"{behavior.EventAtUtc:HH:mm:ss} 行为 {behavior.Rule} · {behavior.Summary}");
        }
        foreach (var note in frame.VisibleNotes)
        {
            ReplayEvents.Add($"{note.EventAtUtc:HH:mm:ss} 笔记证据 · {note.Source}（记录 {note.RecordedAtUtc:HH:mm:ss}）");
        }
        if (frame.VisiblePlan is not null)
        {
            ReplayEvents.Add($"计划可见 · {frame.VisiblePlan.Strategy}/{frame.VisiblePlan.Setup} · 创建 {frame.VisiblePlan.CreatedAtUtc:HH:mm:ss}");
        }
        if (frame.VisibleFinalNetPnl is { } pnl)
        {
            ReplayEvents.Add($"最终结果可见 · 净盈亏 {Signed(pnl)}");
        }
        if (frame.VisibleReview is not null)
        {
            ReplayEvents.Add($"完整复盘 · {frame.VisibleReview.Summary}");
        }
        var mode = frame.IsFullReviewVisible ? "完整复盘已展开" : "未来结果与事后记录隐藏";
        var gap = string.IsNullOrWhiteSpace(frame.Message) ? string.Empty : $" · {frame.Message}";
        ReplayStatus = $"{history.Range.Coverage} · K线 {history.Bars.Count} · ticks {history.Ticks.Count} · 游标 {frame.CursorUtc:yyyy-MM-dd HH:mm:ss} · {mode}{gap}";
    }

    public void ApplySavedOpportunity(OpportunityRecord opportunity)
    {
        OpportunityId = opportunity.Id;
        OpportunityRevision = opportunity.Revision;
    }

    public void ApplyOpportunityForReload(
        OpportunityRecord opportunity,
        IReadOnlyList<ReviewAttachment> attachments) =>
        SelectOpportunity(opportunity, attachments);

    public void ApplySavedGoal(ImprovementGoal goal) => SelectGoal(goal);
    public void ApplySavedBehaviorReview(BehaviorOccurrence occurrence)
    {
        BehaviorRevision = occurrence.Revision;
        BehaviorReviewStatus = "人工解释已保存；原始规则事实保持不变。";
    }

    private void SelectOpportunity(
        OpportunityRecord opportunity,
        IReadOnlyList<ReviewAttachment> attachments)
    {
        var restoreDraft = PrepareWorkspaceEditor(
            EditEntityKind.Opportunity,
            opportunity.AccountKey,
            opportunity.Id,
            _workspaceSessionGeneration);
        _applyingDetail = true;
        try
        {
            OpportunityId = opportunity.Id;
            OpportunityRevision = opportunity.Revision;
            OpportunityKind = FormatOpportunityKind(opportunity.Kind);
            OpportunitySymbol = opportunity.Symbol;
            OpportunitySide = opportunity.Side switch
            {
                TradeSide.Buy => "买入",
                TradeSide.Sell => "卖出",
                _ => "未指定",
            };
            OpportunityPlaybook = opportunity.PlaybookVersionId ?? string.Empty;
            OpportunityEntry = opportunity.EntryPrice?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            OpportunityStop = opportunity.StopPrice?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            OpportunityTarget = opportunity.TargetPrice?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            OpportunityReason = opportunity.Reason;
            OpportunityConditions = opportunity.Conditions;
            OpportunityLinkedTrade = opportunity.LinkedTradeKey ?? string.Empty;
            OpportunityNotes = opportunity.Notes;
        }
        finally
        {
            _applyingDetail = false;
        }
        OpportunityAttachments.ReplaceWith(attachments
            .Where(item => item.OwnerId == opportunity.Id)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(CreateAttachmentRow));
        if (restoreDraft)
        {
            RestoreWorkspaceDraft(EditEntityKind.Opportunity);
        }
    }

    private void ClearOpportunity()
    {
        var restoreDraft = false;
        if (_workspaceAccountKey is { } accountKey)
        {
            WorkspaceDraftKey? existingDraft = _workspaceEditorStates.ContainsKey(EditEntityKind.Opportunity)
                ? null
                : _workspaceDrafts.Keys
                    .Where(item => item.Kind == EditEntityKind.Opportunity && item.AccountKey == accountKey &&
                                   item.EntityId.StartsWith("opportunity-", StringComparison.Ordinal))
                    .Select(item => (WorkspaceDraftKey?)item)
                    .LastOrDefault();
            var entityId = existingDraft?.EntityId ?? $"opportunity-{Guid.NewGuid():N}";
            restoreDraft = PrepareWorkspaceEditor(
                EditEntityKind.Opportunity, accountKey, entityId, _workspaceSessionGeneration);
        }
        _applyingDetail = true;
        try
        {
            OpportunityId = null;
            OpportunityRevision = 0;
            OpportunityKind = "事前观察";
            OpportunitySymbol = string.Empty;
            OpportunitySide = "未指定";
            OpportunityPlaybook = string.Empty;
            OpportunityEntry = string.Empty;
            OpportunityStop = string.Empty;
            OpportunityTarget = string.Empty;
            OpportunityReason = string.Empty;
            OpportunityConditions = string.Empty;
            OpportunityLinkedTrade = string.Empty;
            OpportunityNotes = string.Empty;
        }
        finally
        {
            _applyingDetail = false;
        }
        if (restoreDraft)
        {
            RestoreWorkspaceDraft(EditEntityKind.Opportunity);
        }
        OpportunityAttachments.Clear();
    }

    private void SelectPage()
    {
        var selected = Trades.Count > 0 && Trades.All(item => item.IsSelected);
        foreach (var row in Trades)
        {
            row.IsSelected = !selected;
        }
        OnTradeSelectionChanged();
    }

    public void ClearExportPreview()
    {
        CanConfirmExport = false;
        ExportPreview = "先生成导出预览；原始附件默认不包含。";
        ExportAttachmentOptions.Clear();
    }

    private void OnTradeSelectionChanged()
    {
        RaisePropertyChanged(nameof(SelectedTradeIds));
        RaisePropertyChanged(nameof(BulkSelectionText));
    }

    private AttachmentRow CreateAttachmentRow(ReviewAttachment attachment)
    {
        var path = ResolveAttachmentPath?.Invoke(attachment);
        var thumbnail = attachment.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? LoadThumbnail(path)
            : null;
        return new AttachmentRow(
            attachment.Id, attachment.Title, attachment.FileName, attachment.Evidence.Source.ToString(),
            attachment.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm"), attachment.EventReference, thumbnail,
            new AsyncRelayCommand(() => OpenAttachmentAsync?.Invoke(attachment) ?? Task.CompletedTask),
            new AsyncRelayCommand(() => DeleteAttachmentAsync?.Invoke(attachment) ?? Task.CompletedTask));
    }

    private static ImageSource? LoadThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var thumbnail = new BitmapImage();
            thumbnail.BeginInit();
            thumbnail.CacheOption = BitmapCacheOption.OnLoad;
            thumbnail.DecodePixelHeight = 184;
            thumbnail.StreamSource = stream;
            thumbnail.EndInit();
            thumbnail.Freeze();
            return thumbnail;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task ToggleReplayAsync()
    {
        IsReplayPlaying = !IsReplayPlaying;
        while (IsReplayPlaying && ReplayProgress < 100d)
        {
            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(450d / ReplaySpeedMultiplier()));
            if (!IsReplayPlaying)
            {
                break;
            }
            await StepReplayEventAsync(1);
        }
        if (ReplayProgress >= 100d)
        {
            IsReplayPlaying = false;
        }
    }

    private async Task StepReplayEventAsync(int delta)
    {
        if (SeekReplayAsync is null)
        {
            return;
        }
        if (_replayNavigationEvents.Count == 0 || _replayEndUtc <= _replayStartUtc)
        {
            await SeekReplayAsync(Math.Clamp(ReplayProgress + delta, 0d, 100d));
            return;
        }
        var targetIndex = delta > 0
            ? Math.Min(_replayNavigationEvents.Count - 1, _replayCurrentEventIndex + 1)
            : Math.Max(0, _replayCurrentEventIndex - 1);
        if (delta > 0 && _replayCurrentEventIndex >= _replayNavigationEvents.Count - 1)
        {
            await SeekReplayAsync(100d);
            return;
        }
        var at = _replayNavigationEvents[targetIndex].AtUtc;
        var progress = (at - _replayStartUtc).TotalMilliseconds /
                       (_replayEndUtc - _replayStartUtc).TotalMilliseconds * 100d;
        await SeekReplayAsync(Math.Clamp(progress, 0d, 100d));
    }

    private double ReplaySpeedMultiplier() => ReplaySpeed switch
    {
        "0.5x" => 0.5d,
        "2x" => 2d,
        "4x" => 4d,
        _ => 1d,
    };

    public void ApplyDaily(
        DateOnly date,
        DailyJournal? journal,
        DailyReviewFacts? facts,
        string currentSourceVersion)
    {
        var accountKey = journal?.AccountKey ?? _workspaceAccountKey;
        var restoreDraft = accountKey is not null && PrepareWorkspaceEditor(
            EditEntityKind.DailyJournal,
            accountKey,
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _workspaceSessionGeneration);
        _applyingDetail = true;
        try
        {
            DailyDate = date.ToString("yyyy-MM-dd");
            PreMarketPlan = journal?.PreMarketPlan ?? string.Empty;
            IntradayNotes = journal?.IntradayNotes ?? string.Empty;
            PostMarketSummary = journal?.PostMarketSummary ?? string.Empty;
            DailyDidWell = journal?.DidWell ?? string.Empty;
            DailyToImprove = journal?.ToImprove ?? string.Empty;
            DailyNextAction = journal?.NextAction ?? string.Empty;
            DailyRevision = journal?.Revision ?? 0;
            DailyStatus = journal switch
            {
                null => "尚未记录",
                { Status: ReviewCompletionStatus.Reviewed } when
                    (journal.ReviewedSourceVersion ?? journal.SourceVersion) != currentSourceVersion => "日数据更新，需要重审",
                { Status: ReviewCompletionStatus.Reviewed } => $"已完成 · {journal.ReviewedAtUtc:yyyy-MM-dd HH:mm}",
                { Status: ReviewCompletionStatus.NeedsReview } => "日数据更新，需要重审",
                _ => "草稿",
            };
        }
        finally
        {
            _applyingDetail = false;
        }
        if (restoreDraft)
        {
            RestoreWorkspaceDraft(EditEntityKind.DailyJournal);
        }
        DailyFacts.Clear();
        DailyTimeline.Clear();
        if (facts is null)
        {
            DailyFacts.Add(new DailyFactRow("每日事实", "暂无", "当前日期尚未生成事实快照。"));
            return;
        }

        DailyFacts.Add(new DailyFactRow("当日现金盈亏", Signed(facts.RealizedCashPnl),
            $"按成交发生的服务器日汇总，共 {facts.Timeline.Count(item => item.Kind == "成交")} 笔成交事件。"));
        DailyFacts.Add(new DailyFactRow("当日完整交易净盈亏", Signed(facts.CompleteTradeNetPnl),
            $"按最终平仓服务器日汇总，共 {facts.CompleteTradeCount} 笔。"));
        DailyFacts.Add(new DailyFactRow("费用总额", Signed(facts.Fees), "佣金、隔夜费和其他费用，按成交发生日汇总。"));
        DailyFacts.Add(new DailyFactRow("复盘完成率", $"{facts.ReviewCompletionPercentage:0.##}%",
            $"{facts.ReviewedTradeCount} / {facts.CompleteTradeCount} 笔完整交易。"));
        DailyFacts.Add(new DailyFactRow("连续亏损后的开仓",
            facts.OpeningsAfterLossStreakCount?.ToString(CultureInfo.InvariantCulture) ?? "无法判断",
            facts.OpeningsAfterLossStreakCount is null
                ? $"当日末连续亏损 {facts.ConsecutiveLosses} 笔；缺少当时有效的连亏阈值记录。"
                : $"达到当时连亏阈值后又首次开仓 {facts.OpeningsAfterLossStreakCount} 笔；当日末连续亏损 {facts.ConsecutiveLosses} 笔。"));
        DailyFacts.Add(new DailyFactRow("冷静期内开仓", facts.CooldownViolationCount.ToString(CultureInfo.InvariantCulture),
            "仅统计已有 CooldownViolation 规则证据的开仓。"));
        if (!facts.HasReliableTargetMilestone)
        {
            DailyFacts.Add(new DailyFactRow("达标后新开交易最终净盈亏", "无法判断", "缺少可靠的首次达标时点或当时目标版本。"));
            DailyFacts.Add(new DailyFactRow("达标后发生的成交现金盈亏", "无法判断", "缺少可靠的首次达标时点或当时目标版本。"));
        }
        else
        {
            var targetTime = facts.TargetReachedAtUtc!.Value
                .ToOffset(TimeSpan.FromSeconds(facts.ServerUtcOffsetSeconds));
            var targetContext = $"首次达标 {targetTime:HH:mm:ss}（服务器时间） · 目标 {facts.TargetAmount:0.##} · {facts.TargetRuleVersion}";
            DailyFacts.Add(new DailyFactRow("达标后新开交易最终净盈亏", Signed(facts.AfterTargetNewTradeNetPnl ?? 0m),
                $"{targetContext}；完整 {facts.AfterTargetNewCompleteTradeCount} 笔，仍持仓 {facts.AfterTargetNewOpenTradeCount} 笔；关联 position：{JoinTradeIds(facts.AfterTargetTradeKeys)}。"));
            DailyFacts.Add(new DailyFactRow("达标后发生的成交现金盈亏", Signed(facts.AfterTargetDealCashPnl ?? 0m),
                $"{targetContext}；成交 {facts.AfterTargetDealCount} 笔；tickets：{JoinIds(facts.AfterTargetDealTickets)}。"));
        }
        DailyTimeline.ReplaceWith(facts.Timeline.Select(item => new DailyTimelineRow(
            item.AtUtc.ToOffset(TimeSpan.FromSeconds(facts.ServerUtcOffsetSeconds)).ToString("HH:mm:ss"),
            item.Kind, item.Summary, item.Source.ToString(),
            JoinTradeIds(item.LinkedTrades))));
    }

    private async Task OpenDailyOrApplyAsync(
        DateOnly date,
        DailyJournal? journal,
        DailyReviewFacts? facts,
        string currentSourceVersion)
    {
        if (OpenDailyAsync is not null)
        {
            await OpenDailyAsync(date);
            return;
        }
        ApplyDaily(date, journal, facts, currentSourceVersion);
    }

    private void ApplyAnalysisDrilldown(
        string title,
        IEnumerable<TradeKey> keys,
        ReviewWorkspaceData data)
    {
        var positions = keys.Where(item => item.AccountKey == data.AccountKey)
            .Select(item => item.PositionId).Distinct().ToHashSet();
        AnalysisDrilldownTitle = $"{title} · {positions.Count} 笔原始 position";
        AnalysisDrilldown.ReplaceWith(data.Trades
            .Where(item => positions.Contains(item.PositionId))
            .OrderByDescending(item => item.ClosedAtUtc)
            .Select(item => new AnalysisTradeRow(
                item.PositionId,
                item.ClosedAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "持仓中",
                item.Symbol,
                item.Side == TradeSide.Buy ? "买" : "卖",
                Signed(item.NetPnl),
                new AsyncRelayCommand(() => OpenTradeAsync?.Invoke(item.PositionId) ?? Task.CompletedTask))));
    }

    private void SelectBehavior(BehaviorOccurrence occurrence, ReviewWorkspaceData data)
    {
        var restoreDraft = PrepareWorkspaceEditor(
            EditEntityKind.Annotation,
            occurrence.AccountKey,
            occurrence.Id,
            _workspaceSessionGeneration);
        _applyingDetail = true;
        try
        {
            SelectedBehaviorId = occurrence.Id;
            BehaviorRevision = occurrence.Revision;
            BehaviorExplanation = occurrence.UserExplanation ?? string.Empty;
            BehaviorEvidenceInsufficient = occurrence.EvidenceInsufficient;
        }
        finally
        {
            _applyingDetail = false;
        }
        var delivery = occurrence.Source == ReviewEvidenceSource.RuleRecalculation
            ? "历史重算不适用提醒交付"
            : occurrence.NotificationDelivered
                ? $"提醒已交付（{occurrence.NotificationDisposition}）"
                : $"提醒未交付（{occurrence.NotificationDisposition}）";
        BehaviorFacts =
            $"事件日 {occurrence.ServerDate:yyyy-MM-dd} · 来源 {FormatBehaviorSource(occurrence.Source)} · 规则版本 {occurrence.RuleVersion}\n" +
            $"事实值 {occurrence.Value:0.####} · 基线 {(occurrence.Baseline is null ? "未知" : occurrence.Baseline.Value.ToString("0.####"))} · 阈值 {occurrence.Threshold:0.####} · {delivery}\n" +
            $"事件时间 {occurrence.EventAtUtc:O} · 观察时间 {occurrence.ObservedAtUtc:O}\n" +
            $"证据：{occurrence.Summary}\n缺失：{(string.IsNullOrWhiteSpace(occurrence.MissingData) ? "无" : occurrence.MissingData)}";
        BehaviorReviewStatus = occurrence.HumanReviewedAtUtc is null
            ? "尚未填写人工解释。"
            : $"人工结论修订 {occurrence.Revision} · {occurrence.HumanReviewedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        BehaviorTradeLinks.ReplaceWith(occurrence.TradeLinks.Select(link =>
        {
            var trade = data.Trades.FirstOrDefault(item =>
                item.AccountKey == link.TradeKey.AccountKey && item.PositionId == link.TradeKey.PositionId);
            return new BehaviorTradeLinkRow(
                link.TradeKey.PositionId,
                FormatBehaviorRole(link.Role),
                trade?.Symbol ?? "交易记录不在当前载入范围",
                trade is null ? "未知" : trade.Side == TradeSide.Buy ? "买" : "卖",
                trade is null ? "未知" : Signed(trade.NetPnl),
                new AsyncRelayCommand(() => OpenTradeAsync?.Invoke(link.TradeKey.PositionId) ?? Task.CompletedTask));
        }));
        ApplyAnalysisDrilldown($"行为事件 {occurrence.Id}", occurrence.TradeLinks.Select(item => item.TradeKey), data);
        if (restoreDraft)
        {
            RestoreWorkspaceDraft(EditEntityKind.Annotation);
        }
    }

    private void ClearBehaviorSelection()
    {
        if (_workspaceEditorStates.TryGetValue(EditEntityKind.Annotation, out var current) && current.IsDirty)
        {
            CaptureWorkspaceDraft(EditEntityKind.Annotation, current);
        }
        _workspaceEditorStates.Remove(EditEntityKind.Annotation);
        SelectedBehaviorId = null;
        BehaviorRevision = 0;
        BehaviorExplanation = string.Empty;
        BehaviorEvidenceInsufficient = false;
        BehaviorFacts = "请选择一条行为事件查看原始证据。";
        BehaviorReviewStatus = "人工解释只补充结论，不改变规则事实。";
        BehaviorTradeLinks.Clear();
    }

    private static string FormatBehaviorSource(ReviewEvidenceSource source) => source switch
    {
        ReviewEvidenceSource.LiveObservation => "实时观察",
        ReviewEvidenceSource.RuleRecalculation => "历史重算",
        ReviewEvidenceSource.HistoricalMarketData => "历史行情",
        _ => source.ToString(),
    };

    private static string FormatBehaviorRole(BehaviorTradeRole role) => role switch
    {
        BehaviorTradeRole.Trigger => "触发交易",
        BehaviorTradeRole.PreviousContext => "前序背景",
        BehaviorTradeRole.OpenExposure => "当时持仓",
        BehaviorTradeRole.Subsequent => "后续交易",
        _ => role.ToString(),
    };

    private void GeneratePeriodDraft()
    {
        var facts = _periodFactsSnapshot;
        if (facts is null)
        {
            PeriodFacts = "当前没有可用的周期事实快照。";
            return;
        }
        var adherence = facts.RuleAdherencePercentage is null
            ? "执行率无法计算（没有已知适用评价）"
            : $"已知规则执行率 {facts.RuleAdherencePercentage:0.##}%";
        var repeated = facts.RepeatedBehaviors.Count == 0
            ? "没有命中行为事件"
            : string.Join("；", facts.RepeatedBehaviors.Select(item =>
                $"{item.Rule} {item.OccurrenceCount} 次/{item.Trades.Count} 笔交易"));
        var comparison = _periodComparisonSnapshot is null ||
                         _periodComparisonSnapshot.Left.TradeCount == 0 ||
                         _periodComparisonSnapshot.Right.TradeCount == 0
            ? "同口径前后表现无法比较（至少一组无合格样本）"
            : $"同口径对照：{_periodComparisonSnapshot.LeftLabel} {_periodComparisonSnapshot.Left.NetPnl:+0.##;-0.##;0}；{_periodComparisonSnapshot.RightLabel} {_periodComparisonSnapshot.Right.NetPnl:+0.##;-0.##;0}";
        PeriodFacts =
            $"完整交易 {facts.CompleteTradeCount} 笔，已复盘 {facts.ReviewedTradeCount} 笔，复盘覆盖 {facts.DataQuality.ReviewCompletionPercentage:0.##}%。\n" +
            $"规则通过 {facts.PassedRuleCount}、未通过 {facts.FailedRuleCount}、未知 {facts.UnknownRuleCount}、不适用 {facts.NotApplicableRuleCount}；{adherence}。\n" +
            $"行为事实：{repeated}。\n{comparison}。";
        if (string.IsNullOrWhiteSpace(PeriodNextAction))
        {
            PeriodNextAction = "从重复出现的执行问题中选择一项，作为下一周期唯一改进动作。";
        }
    }

    private void SelectGoal(ImprovementGoal goal)
    {
        var restoreDraft = PrepareWorkspaceEditor(
            EditEntityKind.ImprovementGoal,
            goal.AccountKey,
            goal.Id,
            _workspaceSessionGeneration);
        _applyingDetail = true;
        try
        {
            SelectedGoalId = goal.Id;
            GoalName = goal.Name;
            GoalRule = goal.Rule?.ToString() ?? "人工检查";
            GoalMeasurement = goal.Measurement;
            GoalTarget = goal.TargetValue?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
            GoalSymbols = goal.ApplicableSymbols;
            GoalObservationWindow = goal.ObservationWindowDays.ToString(CultureInfo.CurrentCulture);
            GoalEndCondition = goal.EndCondition;
            GoalNotificationEnabled = goal.NotificationEnabled;
        }
        finally
        {
            _applyingDetail = false;
        }
        if (restoreDraft)
        {
            RestoreWorkspaceDraft(EditEntityKind.ImprovementGoal);
        }
        StatusText = goal.Status == ImprovementGoalStatus.Active
            ? $"已选择“{goal.Name}”v{Math.Max(1, goal.Version)}；保存将建立下一版本并从新起点观察。"
            : "历史目标只读；请选择活动版本或新建目标。";
    }

    private void ClearGoal()
    {
        var restoreDraft = false;
        if (_workspaceAccountKey is { } accountKey)
        {
            WorkspaceDraftKey? existingDraft = _workspaceEditorStates.ContainsKey(EditEntityKind.ImprovementGoal)
                ? null
                : _workspaceDrafts.Keys
                    .Where(item => item.Kind == EditEntityKind.ImprovementGoal && item.AccountKey == accountKey &&
                                   item.EntityId.StartsWith("goal-", StringComparison.Ordinal))
                    .Select(item => (WorkspaceDraftKey?)item)
                    .LastOrDefault();
            var entityId = existingDraft?.EntityId ?? $"goal-{Guid.NewGuid():N}";
            restoreDraft = PrepareWorkspaceEditor(
                EditEntityKind.ImprovementGoal, accountKey, entityId, _workspaceSessionGeneration);
        }
        _applyingDetail = true;
        try
        {
            SelectedGoalId = null;
            GoalName = string.Empty;
            GoalRule = "人工检查";
            GoalMeasurement = string.Empty;
            GoalTarget = string.Empty;
            GoalSymbols = string.Empty;
            GoalObservationWindow = "7";
            GoalEndCondition = string.Empty;
            GoalNotificationEnabled = false;
        }
        finally
        {
            _applyingDetail = false;
        }
        if (restoreDraft)
        {
            RestoreWorkspaceDraft(EditEntityKind.ImprovementGoal);
        }
        StatusText = "填写新目标；自动目标会冻结当前规则版本。";
    }

    public EditIdentity? GetEditorIdentity(EditEntityKind kind) =>
        _workspaceEditorStates.GetValueOrDefault(kind)?.Identity;

    public bool IsEditorDirty(EditEntityKind kind) =>
        _workspaceEditorStates.TryGetValue(kind, out var state) && state.IsDirty;

    public IReadOnlyList<EditEntityKind> GetDirtyEditorKinds()
    {
        var kinds = _workspaceEditorStates
            .Where(item => item.Value.IsDirty)
            .Select(item => item.Key)
            .ToList();
        if (HasUnsavedReviewChanges)
        {
            kinds.Insert(0, EditEntityKind.TradeReview);
        }
        return kinds.Distinct().ToArray();
    }

    public EditSnapshot<T>? CaptureWorkspaceEdit<T>(EditEntityKind kind, T content)
    {
        if (!_workspaceEditorStates.TryGetValue(kind, out var state))
        {
            return null;
        }
        CaptureWorkspaceDraft(kind, state);
        _workspaceEditorStates[kind] = state.Saving();
        EditorSaveStatus = $"{FormatEditorKind(kind)}正在保存…";
        RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
        return new EditSnapshot<T>(state.Identity, state.ContentSequence, content);
    }

    public bool ApplyWorkspaceSaveReceipt<T>(
        EditEntityKind kind,
        EditSaveReceipt<T> receipt,
        Action<T>? applySavedValue = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!_workspaceEditorStates.TryGetValue(kind, out var state) || state.Identity != receipt.Identity)
        {
            return false;
        }

        var next = state.Apply(receipt);
        _workspaceEditorStates[kind] = next;
        var acknowledged = receipt.IsSaved && receipt.ContentSequence == state.ContentSequence && receipt.Value is not null;
        if (acknowledged)
        {
            _workspaceDrafts.Remove(ToDraftKey(state.Identity));
            applySavedValue?.Invoke(receipt.Value!);
            EditorSaveStatus = $"{FormatEditorKind(kind)}已保存。";
            if (_lastSaveIssueKind == kind)
            {
                _lastSaveIssueKind = null;
                RaisePropertyChanged(nameof(HasEditorSaveIssue));
            }
        }
        else
        {
            CaptureWorkspaceDraft(kind, next);
            EditorSaveStatus = receipt.IsSaved
                ? $"{FormatEditorKind(kind)}较早版本已保存，当前修改仍待保存。"
                : $"{FormatEditorKind(kind)}未保存：{receipt.Message}";
            if (!receipt.IsSaved)
            {
                _lastSaveIssueKind = kind;
                RaisePropertyChanged(nameof(HasEditorSaveIssue));
            }
        }
        RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
        return acknowledged;
    }

    public void BeginShutdownEdits()
    {
        _acceptingEdits = false;
        CancelPendingReviewAutoSave();
        CancelPendingWorkspaceAutoSave();
        PreserveAllWorkspaceDrafts();
    }

    private void KeepLocalDraft()
    {
        if (_lastSaveIssueKind is not { } kind ||
            !_workspaceEditorStates.TryGetValue(kind, out var state))
        {
            return;
        }
        _workspaceEditorStates[kind] = state with
        {
            PersistenceState = EditPersistenceState.Dirty,
            Message = "本地副本已保留，等待重试或人工合并。",
        };
        CaptureWorkspaceDraft(kind, _workspaceEditorStates[kind]);
        EditorSaveStatus = $"已保留{FormatEditorKind(kind)}本地副本；可继续编辑后重试。";
        RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
    }

    private async Task DiscardAndReloadEditorAsync()
    {
        if (_lastSaveIssueKind is not { } kind ||
            !_workspaceEditorStates.TryGetValue(kind, out var state))
        {
            return;
        }
        _workspaceDrafts.Remove(ToDraftKey(state.Identity));
        _workspaceEditorStates[kind] = EditState.Clean(state.Identity);
        _lastSaveIssueKind = null;
        RaisePropertyChanged(nameof(HasEditorSaveIssue));
        RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
        EditorSaveStatus = $"已放弃{FormatEditorKind(kind)}本地副本，正在重新载入持久化版本。";
        if (ReloadEditorAsync is not null)
        {
            await ReloadEditorAsync(kind);
        }
    }

    public void ResumeEdits()
    {
        _acceptingEdits = true;
        EditorSaveStatus = HasUnsavedWorkspaceChanges ? "仍有本地修改等待保存。" : "全部编辑内容已保存。";
    }

    public IReadOnlyList<PendingEditDraft> ExportPendingDrafts()
    {
        PreserveAllWorkspaceDrafts();
        var drafts = _workspaceDrafts.Select(item => new PendingEditDraft(
            item.Key.AccountKey,
            item.Key.Kind,
            item.Key.EntityId,
            item.Value.ContentSequence,
            item.Value.ExpectedRevision,
            DescribeDraft(item.Value.Payload))).ToList();
        if (HasUnsavedReviewChanges && _tradeEditorKey is { } tradeKey && _tradeEditorIdentity is { } tradeIdentity)
        {
            drafts.Add(new PendingEditDraft(
                tradeKey.AccountKey,
                EditEntityKind.TradeReview,
                tradeKey.PositionId.ToString(CultureInfo.InvariantCulture),
                TradeEditSequence,
                DocumentRevision,
                new Dictionary<string, string>
                {
                    [nameof(EntryReason)] = EntryReason,
                    [nameof(ExitReason)] = ExitReason,
                    [nameof(DidWell)] = DidWell,
                    [nameof(ToImprove)] = ToImprove,
                    [nameof(NextAction)] = NextAction,
                    [nameof(Summary)] = Summary,
                    [nameof(Emotion)] = Emotion,
                    [nameof(MarketCondition)] = MarketCondition,
                    ["editorInstanceId"] = tradeIdentity.EditorInstanceId.ToString("D"),
                }));
        }
        return drafts;
    }

    private bool PrepareWorkspaceEditor(
        EditEntityKind kind,
        string accountKey,
        string entityId,
        long sessionGeneration)
    {
        if (_workspaceEditorStates.TryGetValue(kind, out var current) && current.IsDirty)
        {
            CaptureWorkspaceDraft(kind, current);
            if (!string.Equals(current.Identity.AccountKey, accountKey, StringComparison.Ordinal) ||
                !string.Equals(current.Identity.EntityId, entityId, StringComparison.Ordinal))
            {
                EditorSaveStatus = $"已按原实体保留{FormatEditorKind(kind)}本地草稿。";
            }
        }
        var draftKey = new WorkspaceDraftKey(accountKey, kind, entityId);
        if (!_workspaceEditorStates.TryGetValue(kind, out current) ||
            !string.Equals(current.Identity.AccountKey, accountKey, StringComparison.Ordinal) ||
            !string.Equals(current.Identity.EntityId, entityId, StringComparison.Ordinal) ||
            current.Identity.SessionGeneration != sessionGeneration)
        {
            var identity = new EditIdentity(accountKey, kind, entityId, sessionGeneration, Guid.NewGuid());
            _workspaceEditorStates[kind] = EditState.Clean(identity);
        }
        return _workspaceDrafts.ContainsKey(draftKey);
    }

    private void RestoreWorkspaceDraft(EditEntityKind kind)
    {
        if (!_workspaceEditorStates.TryGetValue(kind, out var state) ||
            !_workspaceDrafts.TryGetValue(ToDraftKey(state.Identity), out var draft))
        {
            return;
        }
        var wasApplying = _applyingDetail;
        _applyingDetail = true;
        try
        {
            switch (draft.Payload)
            {
                case DailyEditorDraft value:
                    PreMarketPlan = value.PreMarketPlan;
                    IntradayNotes = value.IntradayNotes;
                    PostMarketSummary = value.PostMarketSummary;
                    DailyDidWell = value.DidWell;
                    DailyToImprove = value.ToImprove;
                    DailyNextAction = value.NextAction;
                    DailyRevision = draft.ExpectedRevision;
                    break;
                case PeriodEditorDraft value:
                    PeriodFacts = value.Facts;
                    PeriodDidWell = value.DidWell;
                    PeriodToImprove = value.ToImprove;
                    PeriodNextAction = value.NextAction;
                    PeriodRevision = draft.ExpectedRevision;
                    break;
                case GoalEditorDraft value:
                    GoalName = value.Name;
                    GoalRule = value.Rule;
                    GoalMeasurement = value.Measurement;
                    GoalTarget = value.Target;
                    GoalSymbols = value.Symbols;
                    GoalObservationWindow = value.ObservationWindow;
                    GoalEndCondition = value.EndCondition;
                    GoalNotificationEnabled = value.NotificationEnabled;
                    break;
                case OpportunityEditorDraft value:
                    OpportunityKind = value.Kind;
                    OpportunitySymbol = value.Symbol;
                    OpportunitySide = value.Side;
                    OpportunityPlaybook = value.Playbook;
                    OpportunityEntry = value.Entry;
                    OpportunityStop = value.Stop;
                    OpportunityTarget = value.Target;
                    OpportunityReason = value.Reason;
                    OpportunityConditions = value.Conditions;
                    OpportunityLinkedTrade = value.LinkedTrade;
                    OpportunityNotes = value.Notes;
                    OpportunityRevision = draft.ExpectedRevision;
                    break;
                case BehaviorEditorDraft value:
                    BehaviorExplanation = value.Explanation;
                    BehaviorEvidenceInsufficient = value.EvidenceInsufficient;
                    BehaviorRevision = draft.ExpectedRevision;
                    break;
                case RuleAssessmentEditorDraft value:
                    var savedRows = value.Rows.ToDictionary(item => (item.PlaybookVersionId, item.RuleId));
                    foreach (var row in RuleAssessments)
                    {
                        if (savedRows.TryGetValue((row.PlaybookVersionId, row.RuleId), out var saved))
                        {
                            row.ApplyDraft(saved.Status, saved.Notes);
                        }
                    }
                    break;
            }
        }
        finally
        {
            _applyingDetail = wasApplying;
        }
        _workspaceEditorStates[kind] = state with
        {
            ContentSequence = Math.Max(1, draft.ContentSequence),
            PersistenceState = EditPersistenceState.Dirty,
            Message = "已恢复尚未提交的本地草稿。",
        };
        EditorSaveStatus = $"已恢复{FormatEditorKind(kind)}本地草稿。";
        RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
    }

    private void MarkWorkspaceEditorChanged(EditEntityKind kind)
    {
        if (!_workspaceEditorStates.TryGetValue(kind, out var state))
        {
            return;
        }
        _workspaceEditorStates[kind] = state.Changed();
        EditorSaveStatus = $"{FormatEditorKind(kind)}有未保存修改。";
        RaisePropertyChanged(nameof(HasUnsavedWorkspaceChanges));
        QueueWorkspaceAutoSave();
    }

    private void CaptureWorkspaceDraft(EditEntityKind kind, EditState state)
    {
        object? payload = kind switch
        {
            EditEntityKind.DailyJournal => new DailyEditorDraft(
                PreMarketPlan, IntradayNotes, PostMarketSummary, DailyDidWell, DailyToImprove, DailyNextAction),
            EditEntityKind.PeriodReview => new PeriodEditorDraft(
                PeriodFacts, PeriodDidWell, PeriodToImprove, PeriodNextAction),
            EditEntityKind.ImprovementGoal => new GoalEditorDraft(
                GoalName, GoalRule, GoalMeasurement, GoalTarget, GoalSymbols,
                GoalObservationWindow, GoalEndCondition, GoalNotificationEnabled),
            EditEntityKind.Opportunity => new OpportunityEditorDraft(
                OpportunityKind, OpportunitySymbol, OpportunitySide, OpportunityPlaybook,
                OpportunityEntry, OpportunityStop, OpportunityTarget, OpportunityReason,
                OpportunityConditions, OpportunityLinkedTrade, OpportunityNotes),
            EditEntityKind.Annotation => new BehaviorEditorDraft(
                BehaviorExplanation, BehaviorEvidenceInsufficient),
            EditEntityKind.RuleAssessment => new RuleAssessmentEditorDraft(
                RuleAssessments.Select(item => new RuleAssessmentDraftRow(
                    item.PlaybookVersionId, item.RuleId, item.Status, item.Notes)).ToArray()),
            _ => null,
        };
        if (payload is not null)
        {
            _workspaceDrafts[ToDraftKey(state.Identity)] = new WorkspaceEditorDraft(
                state.ContentSequence,
                GetEditorRevision(kind),
                payload);
        }
    }

    private void PreserveAllWorkspaceDrafts()
    {
        foreach (var item in _workspaceEditorStates.Where(item => item.Value.IsDirty).ToArray())
        {
            CaptureWorkspaceDraft(item.Key, item.Value);
        }
    }

    private int GetEditorRevision(EditEntityKind kind) => kind switch
    {
        EditEntityKind.DailyJournal => DailyRevision,
        EditEntityKind.PeriodReview => PeriodRevision,
        EditEntityKind.Opportunity => OpportunityRevision,
        EditEntityKind.Annotation => BehaviorRevision,
        EditEntityKind.RuleAssessment => RuleAssessments.Select(item => item.Revision).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    private bool SetEditorField<T>(ref T field, T value, EditEntityKind kind)
    {
        if (!_acceptingEdits && !_applyingDetail)
        {
            return false;
        }
        if (!SetProperty(ref field, value) || _applyingDetail)
        {
            return false;
        }
        MarkWorkspaceEditorChanged(kind);
        return true;
    }

    private async void QueueWorkspaceAutoSave()
    {
        _workspaceDraftDelay?.Cancel();
        _workspaceDraftDelay?.Dispose();
        _workspaceDraftDelay = new CancellationTokenSource();
        try
        {
            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(900), _workspaceDraftDelay.Token);
            if (AutoSaveWorkspaceAsync is not null)
            {
                await AutoSaveWorkspaceAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            EditorSaveStatus = $"自动保存失败：{exception.Message}";
        }
    }

    public void CancelPendingWorkspaceAutoSave()
    {
        _workspaceDraftDelay?.Cancel();
        _workspaceDraftDelay?.Dispose();
        _workspaceDraftDelay = null;
    }

    private static WorkspaceDraftKey ToDraftKey(EditIdentity identity) =>
        new(identity.AccountKey, identity.EntityKind, identity.EntityId);

    private static string FormatEditorKind(EditEntityKind kind) => kind switch
    {
        EditEntityKind.DailyJournal => "日记",
        EditEntityKind.PeriodReview => "周期总结",
        EditEntityKind.RuleAssessment => "规则评价",
        EditEntityKind.Opportunity => "机会记录",
        EditEntityKind.ImprovementGoal => "改进目标",
        EditEntityKind.Annotation => "行为批注",
        _ => "编辑内容",
    };

    private static IReadOnlyDictionary<string, string> DescribeDraft(object payload) => payload switch
    {
        DailyEditorDraft value => new Dictionary<string, string>
        {
            [nameof(PreMarketPlan)] = value.PreMarketPlan,
            [nameof(IntradayNotes)] = value.IntradayNotes,
            [nameof(PostMarketSummary)] = value.PostMarketSummary,
            [nameof(DailyDidWell)] = value.DidWell,
            [nameof(DailyToImprove)] = value.ToImprove,
            [nameof(DailyNextAction)] = value.NextAction,
        },
        PeriodEditorDraft value => new Dictionary<string, string>
        {
            [nameof(PeriodFacts)] = value.Facts,
            [nameof(PeriodDidWell)] = value.DidWell,
            [nameof(PeriodToImprove)] = value.ToImprove,
            [nameof(PeriodNextAction)] = value.NextAction,
        },
        GoalEditorDraft value => new Dictionary<string, string>
        {
            [nameof(GoalName)] = value.Name,
            [nameof(GoalRule)] = value.Rule,
            [nameof(GoalMeasurement)] = value.Measurement,
            [nameof(GoalTarget)] = value.Target,
            [nameof(GoalSymbols)] = value.Symbols,
            [nameof(GoalObservationWindow)] = value.ObservationWindow,
            [nameof(GoalEndCondition)] = value.EndCondition,
            [nameof(GoalNotificationEnabled)] = value.NotificationEnabled.ToString(),
        },
        OpportunityEditorDraft value => new Dictionary<string, string>
        {
            [nameof(OpportunityKind)] = value.Kind,
            [nameof(OpportunitySymbol)] = value.Symbol,
            [nameof(OpportunitySide)] = value.Side,
            [nameof(OpportunityPlaybook)] = value.Playbook,
            [nameof(OpportunityEntry)] = value.Entry,
            [nameof(OpportunityStop)] = value.Stop,
            [nameof(OpportunityTarget)] = value.Target,
            [nameof(OpportunityReason)] = value.Reason,
            [nameof(OpportunityConditions)] = value.Conditions,
            [nameof(OpportunityLinkedTrade)] = value.LinkedTrade,
            [nameof(OpportunityNotes)] = value.Notes,
        },
        BehaviorEditorDraft value => new Dictionary<string, string>
        {
            [nameof(BehaviorExplanation)] = value.Explanation,
            [nameof(BehaviorEvidenceInsufficient)] = value.EvidenceInsufficient.ToString(),
        },
        RuleAssessmentEditorDraft value => value.Rows.ToDictionary(
            item => $"{item.PlaybookVersionId}/{item.RuleId}",
            item => $"{item.Status}|{item.Notes}"),
        _ => new Dictionary<string, string>(),
    };

    private bool SetReviewField(ref string field, string value)
    {
        if (!_acceptingEdits && !_applyingDetail)
        {
            return false;
        }
        if (!SetProperty(ref field, value) || _applyingDetail)
        {
            return false;
        }
        HasUnsavedReviewChanges = true;
        if (TradeEditorIdentity is not null)
        {
            TradeEditSequence = Math.Max(1, TradeEditSequence + 1);
        }
        QueueAutoSave();
        return true;
    }

    private async Task StepTradeAsync(int delta)
    {
        if (Trades.Count == 0 || OpenTradeAsync is null)
        {
            return;
        }
        var ordered = Trades.ToArray();
        var currentIndex = SelectedPositionId is null
            ? (delta > 0 ? -1 : ordered.Length)
            : Array.FindIndex(ordered, item => item.PositionId == SelectedPositionId.Value);
        if (currentIndex < 0)
        {
            currentIndex = delta > 0 ? -1 : ordered.Length;
        }
        var nextIndex = Math.Clamp(currentIndex + delta, 0, Trades.Count - 1);
        await OpenTradeAsync(ordered[nextIndex].PositionId);
    }

    private async void QueueAutoSave()
    {
        _draftDelay?.Cancel();
        _draftDelay?.Dispose();
        _draftDelay = new CancellationTokenSource();
        try
        {
            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(900), _draftDelay.Token);
            if (SelectedPositionId is not null && AutoSaveReviewAsync is not null)
            {
                await AutoSaveReviewAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"草稿保存失败：{exception.Message}";
        }
    }

    private sealed record TradeDraftState(
        EditIdentity Identity,
        long ContentSequence,
        int ExpectedRevision,
        string EntryReason,
        string ExitReason,
        string DidWell,
        string ToImprove,
        string NextAction,
        string Summary,
        string Emotion,
        string MarketCondition);

    private readonly record struct WorkspaceDraftKey(
        string AccountKey,
        EditEntityKind Kind,
        string EntityId);

    private sealed record WorkspaceEditorDraft(
        long ContentSequence,
        int ExpectedRevision,
        object Payload);

    private sealed record DailyEditorDraft(
        string PreMarketPlan,
        string IntradayNotes,
        string PostMarketSummary,
        string DidWell,
        string ToImprove,
        string NextAction);

    private sealed record PeriodEditorDraft(
        string Facts,
        string DidWell,
        string ToImprove,
        string NextAction);

    private sealed record GoalEditorDraft(
        string Name,
        string Rule,
        string Measurement,
        string Target,
        string Symbols,
        string ObservationWindow,
        string EndCondition,
        bool NotificationEnabled);

    private sealed record OpportunityEditorDraft(
        string Kind,
        string Symbol,
        string Side,
        string Playbook,
        string Entry,
        string Stop,
        string Target,
        string Reason,
        string Conditions,
        string LinkedTrade,
        string Notes);

    private sealed record BehaviorEditorDraft(string Explanation, bool EvidenceInsufficient);
    private sealed record RuleAssessmentEditorDraft(IReadOnlyList<RuleAssessmentDraftRow> Rows);

    private AsyncRelayCommand Command(Func<Task> action) => new(action, onError: exception => StatusText = exception.Message);
    private static string Signed(decimal value) => value > 0 ? $"+{value:0.##}" : value.ToString("0.##");
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#} 小时" : $"{duration.TotalMinutes:0} 分钟";
    private static string FormatRiskFacts(TradeExcursion? excursion, decimal netPnl)
    {
        if (excursion is null)
        {
            return "初始风险未知 · 持仓过程无采样";
        }
        var actualR = excursion.HasReliableInitialRisk
            ? (netPnl / excursion.InitialRiskAmount!.Value).ToString("0.##")
            : "未知";
        var risk = $"初始风险 {excursion.InitialRiskAmount?.ToString("0.##") ?? "未知"}（{(excursion.StartedAtOpen ? "开仓时观测" : "开仓后补采")}） · 实际 R {actualR}";
        if (excursion.AlgorithmVersion == "legacy-extrema-v1")
        {
            return $"{risk} · 旧口径极值 MAE {excursion.MinimumPnl:0.##} / MFE {excursion.MaximumPnl:0.##}，仅单独展示，不纳入新过程统计";
        }
        return excursion.IsReliable
            ? $"{risk} · MAE {excursion.MinimumPnl:0.##} · MFE {excursion.MaximumPnl:0.##} · 覆盖 {excursion.CoveragePercentage:0.##}% · 最大间隔 {excursion.MaximumGapMilliseconds} ms"
            : $"{risk} · 过程采样降级（覆盖 {excursion.CoveragePercentage:0.##}% / 最大间隔 {excursion.MaximumGapMilliseconds} ms），不输出 MAE/MFE 统计";
    }
    private static string FormatOffset(int seconds)
    {
        var offset = TimeSpan.FromSeconds(seconds);
        return $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
    }
    private static string FormatOpportunityKind(OpportunityRecordKind kind) => kind switch
    {
        OpportunityRecordKind.ObservedBeforeMove => "事前观察",
        OpportunityRecordKind.DiscoveredAfterMove => "事后发现",
        _ => "主动放弃",
    };

    private static string FormatDealKind(DealEntryKind kind) => kind switch
    {
        DealEntryKind.In => "首次/加仓",
        DealEntryKind.Out => "部分/退出",
        DealEntryKind.OutBy => "对冲平仓",
        DealEntryKind.InOut => "反手",
        _ => kind.ToString(),
    };

    private static string FormatGoalStatus(ImprovementGoalStatus status) => status switch
    {
        ImprovementGoalStatus.Active => "进行中",
        ImprovementGoalStatus.Archived => "已归档",
        _ => "草稿",
    };

    private static string FormatDailyJournalStatus(DailyJournal? journal, string? currentSourceVersion) => journal?.Status switch
    {
        ReviewCompletionStatus.Reviewed when
            (journal.ReviewedSourceVersion ?? journal.SourceVersion) != currentSourceVersion => "需重审",
        ReviewCompletionStatus.Reviewed => "已完成",
        ReviewCompletionStatus.NeedsReview => "需重审",
        ReviewCompletionStatus.Draft => "草稿",
        _ => "待记录",
    };

    private static string JoinTradeIds(IEnumerable<TradeKey> keys)
    {
        var values = keys.Select(item => item.PositionId).Distinct().ToArray();
        return values.Length == 0 ? "无" : string.Join("、", values);
    }

    private static string JoinIds(IEnumerable<long> values)
    {
        var ids = values.Distinct().ToArray();
        return ids.Length == 0 ? "无" : string.Join("、", ids);
    }

}

public sealed record ReviewQualityRow(
    string Label,
    int Covered,
    int Total,
    decimal Percentage,
    string Detail,
    ICommand OpenCommand)
{
    public string Fraction => $"{Covered} / {Total}";
    public string PercentageText => $"{Percentage:0.##}%";
}

public sealed record ReviewCalendarRow(string Date, string CashPnl, int Opened, int Closed, int Pending, string Journal, string Gap, ICommand OpenCommand);
public sealed record PendingEditDraft(
    string AccountKey,
    EditEntityKind Kind,
    string EntityId,
    long ContentSequence,
    int ExpectedRevision,
    IReadOnlyDictionary<string, string> Fields);
public sealed record RuleAssessmentDraftRow(
    string PlaybookVersionId,
    string RuleId,
    RuleAssessmentStatus Status,
    string Notes);
public sealed record DailyFactRow(string Title, string Value, string Detail);
public sealed record DailyTimelineRow(string Time, string Kind, string Summary, string Source, string LinkedTrades);
public sealed class WorkspaceTradeRow : ObservableObject
{
    private bool _isSelected;

    public WorkspaceTradeRow(long positionId, string closedAt, string symbol, string side, string netPnl,
        string status, string strategy, string tags, string pnlColor, ICommand openCommand)
    {
        PositionId = positionId;
        ClosedAt = closedAt;
        Symbol = symbol;
        Side = side;
        NetPnl = netPnl;
        Status = status;
        Strategy = strategy;
        Tags = tags;
        PnlColor = pnlColor;
        OpenCommand = openCommand;
    }

    public event Action? SelectionChanged;
    public long PositionId { get; }
    public string ClosedAt { get; }
    public string Symbol { get; }
    public string Side { get; }
    public string NetPnl { get; }
    public string Status { get; }
    public string Strategy { get; }
    public string Tags { get; }
    public string PnlColor { get; }
    public ICommand OpenCommand { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }
}
public sealed record ReviewCurveRow(
    string Date,
    decimal Value,
    decimal Drawdown,
    decimal? Percentage,
    double Height,
    string ValueColor,
    ICommand OpenCommand)
{
    public string ValueText => Value > 0m ? $"+{Value:0.##}" : Value.ToString("0.##");
    public string DrawdownText => Drawdown <= 0m ? "峰值" : $"回吐 {Drawdown:0.##}";
    public string ToolTipText => Percentage is { } percentage
        ? $"累计利润 {ValueText} · 峰值回吐 {Drawdown:0.##}（{percentage:0.##}%）"
        : $"累计利润 {ValueText} · 峰值回吐 {Drawdown:0.##}";
}
public sealed record ReviewDailyCashRow(
    string Date,
    decimal CashPnl,
    int DealCount,
    double Height,
    string ValueColor,
    ICommand OpenCommand)
{
    public string CashPnlText => CashPnl > 0m ? $"+{CashPnl:0.##}" : CashPnl.ToString("0.##");
}
public sealed record ReviewEquityRow(
    string Time, string Equity, string ObservedDrawdown, string ObservedDrawdownPercentage,
    string UnitizedValue, string Segment, string Gap);
public sealed record TradingSessionRow(
    string Name, string Time, string TimeZone, string Days, string Status, ICommand OpenCommand);
public sealed record FeeBreakdownRow(string Label, string Value, string Hint);
public sealed record AnalysisTradeRow(long PositionId, string ClosedAt, string Symbol, string Side, string NetPnl, ICommand OpenCommand);
public sealed record RiskScatterPoint(double X, double Y, string Color, string ToolTip, ICommand OpenCommand);
public sealed record BehaviorOccurrenceRow(
    string Time, string Rule, string Level, string Source, string Summary, int TradeCount,
    string Notification, ICommand OpenCommand);
public sealed record BehaviorTradeLinkRow(
    long PositionId, string Role, string Symbol, string Side, string NetPnl, ICommand OpenCommand);
public sealed record BehaviorSampleRow(
    string Label, int TradeCount, string NetPnl, string Detail, ICommand OpenCommand);
public sealed record PlaybookRow(string Name, string Version, int RuleCount, string Status, string EffectiveFrom);
public sealed record CampaignRow(string Name, string Symbol, int TradeCount, string Thesis);
public sealed record GoalRow(
    string Name,
    string Status,
    string Measurement,
    string Target,
    string Notification,
    string ObservationSummary,
    bool CanArchive,
    ICommand OpenCommand,
    ICommand ArchiveCommand);
public sealed record OpportunityRow(string Date, string Symbol, string Kind, string Reason, string Notes, ICommand OpenCommand);
public sealed record ReviewComparisonRow(
    string Label,
    int Trades,
    string NetPnl,
    string WinRate,
    decimal Expectancy,
    string AverageOpeningVolume,
    string AverageInitialRisk,
    string RiskCoverage,
    string Warning,
    ICommand OpenCommand)
{
    public string ExpectancyText => Expectancy > 0 ? $"+{Expectancy:0.##}" : Expectancy.ToString("0.##");
}
public sealed record SavedFilterRow(string Name, int Revision, string Scope, ICommand ApplyCommand);
public sealed record WorkspaceGroupRow(
    string Dimension,
    string Group,
    int Trades,
    string WinRate,
    string NetPnl,
    string Expectancy,
    string ProfitFactor,
    string AveragePnl,
    string AverageR,
    string RiskCoverage,
    string Fees,
    string Warning,
    ICommand OpenCommand);
public sealed record RiskExcursionRow(
    long PositionId,
    string Symbol,
    decimal NetPnl,
    decimal OpeningVolume,
    decimal? InitialRisk,
    decimal? Mae,
    decimal? Mfe,
    decimal? ActualR,
    string Coverage,
    ICommand OpenCommand)
{
    public string NetPnlText => NetPnl > 0 ? $"+{NetPnl:0.##}" : NetPnl.ToString("0.##");
    public string OpeningVolumeText => OpeningVolume.ToString("0.#####");
    public string InitialRiskText => InitialRisk?.ToString("0.##") ?? "—";
    public string MaeText => Mae?.ToString("0.##") ?? "—";
    public string MfeText => Mfe?.ToString("0.##") ?? "—";
    public string ActualRText => ActualR?.ToString("0.##") ?? "—";
}
public sealed record AttachmentRow(
    string Id,
    string Title,
    string FileName,
    string Source,
    string CreatedAt,
    string EventReference,
    ImageSource? Thumbnail,
    ICommand OpenCommand,
    ICommand DeleteCommand);
public sealed record TradeDealRow(
    string Time, string Ticket, string Kind, string Volume, string Price, string Profit,
    string Commission, string Swap, string Fee, string NetPnl);
public sealed record TradeProcessRow(DateTimeOffset OccurredAtUtc, string Kind, string Summary, string Source)
{
    public string Time => OccurredAtUtc.ToString("MM-dd HH:mm:ss");
}

public sealed class ReviewRuleRowViewModel : ObservableObject
{
    private readonly Action? _changed;
    private bool _applyingDraft;
    private RuleAssessmentStatus _status;
    private string _notes;

    public ReviewRuleRowViewModel(string playbookVersionId, string ruleId, string section, string name,
        bool isCritical, RuleAssessmentStatus status, string notes, int revision, Action? changed = null)
    {
        PlaybookVersionId = playbookVersionId;
        RuleId = ruleId;
        Section = section;
        Name = name;
        IsCritical = isCritical;
        _status = status;
        _notes = notes;
        Revision = revision;
        _changed = changed;
    }

    public string PlaybookVersionId { get; }
    public string RuleId { get; }
    public string Section { get; }
    public string Name { get; }
    public bool IsCritical { get; }
    public int Revision { get; }
    public IReadOnlyList<RuleAssessmentStatus> StatusOptions { get; } = Enum.GetValues<RuleAssessmentStatus>();
    public RuleAssessmentStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value) && !_applyingDraft)
            {
                _changed?.Invoke();
            }
        }
    }
    public string Notes
    {
        get => _notes;
        set
        {
            if (SetProperty(ref _notes, value) && !_applyingDraft)
            {
                _changed?.Invoke();
            }
        }
    }

    public void ApplyDraft(RuleAssessmentStatus status, string notes)
    {
        _applyingDraft = true;
        try
        {
            Status = status;
            Notes = notes;
        }
        finally
        {
            _applyingDraft = false;
        }
    }
}

public sealed class ReviewExportAttachmentOption : ObservableObject
{
    private bool _isIncluded;
    private string _publicNote = string.Empty;

    public ReviewExportAttachmentOption(string id, string title, string fileName, long sizeBytes)
    {
        Id = id;
        Title = title;
        FileName = fileName;
        SizeBytes = sizeBytes;
    }

    public string Id { get; }
    public string Title { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public string Display => $"{Title} · {FileName} · {SizeBytes:N0} bytes";
    public bool IsIncluded { get => _isIncluded; set => SetProperty(ref _isIncluded, value); }
    public string PublicNote { get => _publicNote; set => SetProperty(ref _publicNote, value); }
}

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }
}
