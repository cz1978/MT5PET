using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using TradePet.Application.Review;
using TradePet.Application.Runtime;
using TradePet.App.ViewModels;
using TradePet.App.ViewModels.Review;
using TradePet.App.Views;
using TradePet.Core.Domain;
using TradePet.Core.Pet;
using TradePet.Core.Protocol;
using TradePet.Core.Review;
using TradePet.Core.Session;
using TradePet.Core.Trading;
using TradePet.Infrastructure.Mt5;
using TradePet.Infrastructure.Persistence;

namespace TradePet.App.Runtime;

public sealed class TradePetRuntime : IAsyncDisposable
{
    private const string GlobalScope = "global";
    private const string DesktopSettingKey = "desktop";
    private const string BehaviorSettingKey = "behavior-profiles-v1";
    private const string FloatingLossSettingKey = "floating-loss-alerts-v1";
    private const string FloatingLossFallbackFileName = "floating-loss-alerts.json";
    private const string LossZoneProjectionSettingKey = "loss-zone-projection-version";
    private const string DailyReportLastShownSettingKey = "daily-report-last-shown-v1";
    private const int LossZoneProjectionVersion = 3;
    private const string PositionPnlAlgorithmVersion = "position-pnl-v1";

    private readonly MainViewModel _viewModel;
    private readonly AppDatabase _database;
    private readonly IReviewWorkspaceRepository _reviewRepository;
    private readonly Mt5TerminalDiscovery _terminalDiscovery = new();
    private readonly BridgePipeServer _bridge = new();
    private readonly PositionSnapshotDiffer _positionDiffer = new();
    private readonly TradeProjector _tradeProjector = new();
    private readonly DealBatchProjector _dealBatchProjector = new();
    private readonly DailyStateCalculator _dailyCalculator = new();
    private readonly DailyHighWaterCalculator _dailyHighWaterCalculator = new();
    private readonly LossZoneEngine _lossZoneEngine = new();
    private readonly AlertComposer _alertComposer = new();
    private readonly AlertDeliveryGate _alertDeliveryGate = new();
    private readonly ReviewQueryEngine _reviewQueryEngine = new();
    private readonly ReviewRangeResolver _reviewRangeResolver = new();
    private readonly ReviewWorkspaceCalculator _reviewWorkspaceCalculator = new();
    private readonly BehaviorAnalyticsCalculator _behaviorCalculator = new();
    private readonly FloatingLossAlertEvaluator _floatingLossAlertEvaluator = new();
    private readonly TradePlanMatcher _tradePlanMatcher = new();
    private readonly ServerClock _serverClock = new();
    private readonly WorkerSession _workerSession = new();
    private readonly HistorySyncTracker _historySyncTracker = new();
    private readonly PetBehaviorEngine _petBehavior = new();
    private readonly PetAdviceCatalog _petAdviceCatalog = new();
    private readonly ReviewQueryService _workspaceQueryService;
    private readonly JournalService _journalService;
    private readonly ReviewBulkEditService _bulkEditService;
    private readonly ReviewTagSuggestionService _reviewTagSuggestionService;
    private readonly PlaybookService _playbookService;
    private readonly CampaignService _campaignService;
    private readonly ImprovementService _improvementService;
    private readonly BehaviorReviewService _behaviorReviewService;
    private readonly OpportunityService _opportunityService;
    private readonly ReviewFilterService _reviewFilterService;
    private readonly TradingSessionService _tradingSessionService;
    private readonly ReviewCacheService _reviewCacheService;
    private readonly ReviewExportService _reviewExportService;
    private readonly IReviewPackageWriter _reviewPackageWriter;
    private readonly IReviewAttachmentStore _reviewAttachmentStore;
    private readonly IReviewBackupService _reviewBackupService;
    private readonly TimeProvider _timeProvider;
    private readonly IAsyncScheduler _scheduler;
    private readonly IAccountSessionCoordinator _accountSessions;
    private readonly IMaintenanceCoordinator _maintenance;
    private readonly SemaphoreSlim _reviewSaveGate = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _dailyReportGate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Task> _backgroundTasks = [];
    private readonly Dictionary<string, long> _lastTransientSequences = [];
    private readonly Dictionary<long, PositionSnapshot> _positions = [];
    private readonly Dictionary<long, DealRecord> _deals = [];
    private readonly Dictionary<long, TradeRecord> _trades = [];
    private readonly Dictionary<string, SymbolSpecification> _symbolSpecifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EquitySample> _equitySamples = [];
    private readonly Dictionary<long, AccountCashFlow> _cashFlows = [];
    private readonly Dictionary<long, DateOnly> _exactCloseServerDates = [];
    private readonly Dictionary<long, TradeExcursion> _excursions = [];
    private readonly Dictionary<long, DateTimeOffset> _excursionPersistedAt = [];
    private readonly Dictionary<long, DateTimeOffset> _pnlSamplePersistedAt = [];
    private readonly Dictionary<string, ChartObjectSnapshot> _chartObjects = [];
    private readonly Dictionary<string, PlanItem> _planItems = [];
    private readonly Dictionary<string, StructuredTradePlan> _structuredPlans = [];
    private readonly Dictionary<long, TradeReviewMetadata> _reviewMetadata = [];
    private readonly List<LossZoneState> _lossZones = [];
    private readonly List<LossZoneAttempt> _lossZoneAttempts = [];
    private readonly List<ImprovementGoal> _activeImprovementGoals = [];
    private readonly HashSet<string> _planBaseline = [];
    private readonly HashSet<int> _activeFloatingLossStages = [];
    private readonly HashSet<long> _evaluatedOpenPositionIds = [];
    private readonly HashSet<long> _missingStopLossAlerted = [];
    private readonly Queue<string> _recentPetAdviceKeys = new();
    private Mt5WorkerClient? _worker;
    private Mt5TerminalInstallation? _terminal;
    private AccountSnapshot? _account;
    private string? _loadedScope;
    private DateOnly _serverDate => _serverClock.ServerDate;
    private int _serverUtcOffsetSeconds => _serverClock.UtcOffsetSeconds;
    private bool _serverDateAuthoritative => _serverClock.IsAuthoritative;
    private long? _hostChartId;
    private bool _hasInitialSnapshot => _workerSession.HasInitialSnapshot;
    private bool _hasInitialDeals => _workerSession.HasInitialDeals;
    private bool _workerConnected => _workerSession.IsConnected;
    private string? _workerAccountKey => _workerSession.AccountKey;
    private WorkerSessionPhase _workerSessionPhase => _workerSession.Phase;
    private bool _suppressNextFloatingLossNotification = true;
    private decimal _floatingLossEpisodePeakPercentage;
    private bool _persistenceAvailable = true;
    private bool _databaseRecoveryRequired;
    private readonly AsyncLocal<RuntimeOperationMarker?> _runtimeOperation = new();
    private DateTimeOffset _lastEquitySampleAtUtc;
    private DateTimeOffset _lastSnapshotPersistedAtUtc;
    private DateTimeOffset _lastDailyStatePersistedAtUtc;
    private DateTimeOffset _lastReviewUiAtUtc;
    private DailyPlanSettings _settings = DailyPlanSettings.BalancedDefault;
    private FloatingLossAlertPolicy _floatingLossPolicy = FloatingLossAlertPolicy.BalancedDefault;
    private BehaviorPolicySet? _behaviorPolicies;
    private DailyState? _dailyState;
    private PetBehaviorState _petState;
    private long _alertVersion;
    private string? _activeAlertId;
    private AlertPriority _activeAlertPriority;
    private readonly HashSet<string> _mutedTradeAlerts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dailyReportsShownThisRun = new(StringComparer.Ordinal);
    private readonly HashSet<long> _macroThirtyMinuteReminders = [];
    private readonly HashSet<long> _macroFiveMinuteReminders = [];
    private readonly ConcurrentDictionary<string, byte> _pendingQuickReviews = new(StringComparer.Ordinal);
    private CancellationTokenSource? _reviewQueryCancellation;
    private CancellationTokenSource? _tradeDetailCancellation;
    private Task? _reviewRefreshTask;
    private string? _latestReviewQueryId;
    private string? _latestTradeDetailRequestId;
    private ReviewQueryResult? _lastWorkspaceQuery;
    private PreparedReviewExport? _preparedReviewExport;
    private DailyTradingReportWindow? _dailyReportWindow;
    private MacroCalendarWindow? _macroCalendarWindow;
    private EconomicCalendarEvent[] _economicCalendarEvents = [];
    private bool _macroCalendarSnapshotReceived;
    private TradeDetailData? _lastTradeDetail;
    private MarketHistoryResult? _lastReplayHistory;
    private DateTimeOffset _lastPersistenceWarningAtUtc;
    private int _suppressedPersistenceWarnings;
    private bool _disposed;

    public TradePetRuntime(MainViewModel viewModel)
        : this(viewModel, TradePetRuntimeDependencies.CreateDefault())
    {
    }

    public TradePetRuntime(MainViewModel viewModel, TradePetRuntimeDependencies dependencies)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(dependencies);
        _database = dependencies.Database;
        _reviewRepository = dependencies.ReviewRepository;
        _reviewPackageWriter = dependencies.ReviewPackageWriter;
        _reviewAttachmentStore = dependencies.ReviewAttachmentStore;
        _reviewBackupService = dependencies.ReviewBackupService;
        _timeProvider = dependencies.TimeProvider;
        _scheduler = dependencies.Scheduler;
        _accountSessions = dependencies.AccountSessions;
        _maintenance = dependencies.Maintenance;
        _petState = _petBehavior.CreateInitial(_timeProvider.GetUtcNow());
        _workspaceQueryService = new ReviewQueryService(_reviewRepository);
        _journalService = new JournalService(_reviewRepository, _timeProvider);
        _bulkEditService = new ReviewBulkEditService(_reviewRepository, _timeProvider);
        _reviewTagSuggestionService = new ReviewTagSuggestionService(_reviewRepository, _timeProvider);
        _playbookService = new PlaybookService(_reviewRepository, _timeProvider);
        _campaignService = new CampaignService(_reviewRepository, _timeProvider);
        _improvementService = new ImprovementService(_reviewRepository, _timeProvider);
        _behaviorReviewService = new BehaviorReviewService(_reviewRepository, _timeProvider);
        _opportunityService = new OpportunityService(_reviewRepository, _timeProvider);
        _reviewFilterService = new ReviewFilterService(_reviewRepository, _timeProvider);
        _tradingSessionService = new TradingSessionService(_reviewRepository);
        _reviewCacheService = new ReviewCacheService(_reviewRepository);
        _reviewExportService = new ReviewExportService(_timeProvider);
        ConfigureReviewWorkspace();
        _viewModel.AcknowledgeAlert = AcknowledgeAlert;
        _viewModel.SnoozeAlert = SnoozeAlert;
        _viewModel.MuteCurrentTradeAlert = MuteCurrentTradeAlert;
    }

    private void ConfigureReviewWorkspace()
    {
        var review = _viewModel.ReviewWorkspace;
        review.RefreshAsync = RefreshReviewAsync;
        review.SaveFilterAsync = () => WithReviewWriteGateAsync(SaveReviewFilterAsync);
        review.ApplySavedFilterAsync = ApplySavedReviewFilterAsync;
        review.OpenTradeAsync = OpenTradeReviewAsync;
        review.SaveReviewAsync = () => WithReviewWriteGateAsync(() => SaveTradeReviewDraftAsync(refresh: true));
        review.AutoSaveReviewAsync = () => WithReviewWriteGateAsync(() => SaveTradeReviewDraftAsync(refresh: false));
        review.AutoSaveWorkspaceAsync = () => WithReviewWriteGateAsync(FlushDirtyWorkspaceEditorsAsync);
        review.ReloadEditorAsync = kind => WithMaintenanceOperationAsync(
            MaintenanceOperationKind.Read, () => ReloadWorkspaceEditorAsync(kind));
        review.MarkReviewedAsync = () => WithReviewWriteGateAsync(MarkTradeReviewedAsync);
        review.SaveAssessmentsAsync = () => WithReviewWriteGateAsync(SaveRuleAssessmentsAsync);
        review.ApplyTagSuggestionAsync = (tag, accept) =>
            WithReviewWriteGateAsync(() => ApplyTradeTagSuggestionAsync(tag, accept));
        review.ImportAttachmentAsync = () => WithReviewWriteGateAsync(ImportReviewAttachmentAsync);
        review.PasteAttachmentAsync = () => WithReviewWriteGateAsync(PasteReviewAttachmentAsync);
        review.OpenAttachmentAsync = OpenReviewAttachmentAsync;
        review.DeleteAttachmentAsync = attachment => WithReviewWriteGateAsync(() => DeleteReviewAttachmentAsync(attachment));
        review.ResolveAttachmentPath = ResolveReviewAttachmentPath;
        review.OpenDailyAsync = date => WithReviewWriteGateAsync(() => OpenDailyReviewAsync(date));
        review.SaveDailyJournalAsync = () => WithReviewWriteGateAsync(SaveDailyJournalAsync);
        review.CompleteDailyJournalAsync = () => WithReviewWriteGateAsync(CompleteDailyJournalAsync);
        review.SavePlaybookAsync = () => WithReviewWriteGateAsync(SavePlaybookVersionAsync);
        review.SaveCampaignAsync = () => WithReviewWriteGateAsync(SaveCampaignAsync);
        review.SaveGoalAsync = () => WithReviewWriteGateAsync(SaveImprovementGoalAsync);
        review.ArchiveGoalAsync = goal => WithReviewWriteGateAsync(() => ArchiveImprovementGoalAsync(goal));
        review.SaveOpportunityAsync = () => WithReviewWriteGateAsync(SaveOpportunityAsync);
        review.ImportOpportunityAttachmentAsync = () => WithReviewWriteGateAsync(ImportOpportunityAttachmentAsync);
        review.BulkEditAsync = (kind, value) => WithReviewWriteGateAsync(() => ApplyBulkReviewEditAsync(kind, value));
        review.SavePeriodReviewAsync = () => WithReviewWriteGateAsync(SavePeriodReviewAsync);
        review.LoadReplayAsync = LoadTradeReplayAsync;
        review.SeekReplayAsync = SeekTradeReplayAsync;
        review.ExportAsync = ExportReviewAsync;
        review.ConfirmExportAsync = ConfirmReviewExportAsync;
        review.BackupAsync = BackupReviewAsync;
        review.RestoreAsync = RestoreReviewAsync;
        review.ClearMarketDataCacheAsync = () => WithReviewWriteGateAsync(ClearReviewMarketDataCacheAsync);
        review.SaveBehaviorReviewAsync = () => WithReviewWriteGateAsync(SaveBehaviorReviewAsync);
        review.SaveTradingSessionAsync = () => WithReviewWriteGateAsync(SaveTradingSessionAsync);
    }

    public async Task StartAsync()
    {
        try
        {
            var interruptedRestoreRecovered = await _reviewBackupService.RecoverPendingAsync(_cancellation.Token);
            var initialization = await _database.InitializeWithRecoveryAsync(
                TradePetPaths.GetDatabaseBackupDirectory(),
                _cancellation.Token);
            await OnUiAsync(() => _viewModel.ReviewWorkspace.StorageStatus =
                interruptedRestoreRecovered
                    ? $"上次中断的恢复已校验并收束 · 本地 SQLite 可写 · migration {initialization.SchemaVersion}"
                    : $"本地 SQLite 健康且可写 · migration {initialization.SchemaVersion}");
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (DatabaseRecoveryRequiredException exception)
        {
            _persistenceAvailable = false;
            _databaseRecoveryRequired = true;
            AppLog.Write($"Database recovery requires a user decision: {exception}");
            var salvage = exception.SalvageReport;
            var salvageText = salvage is null
                ? "当前格式无法读取表记录。"
                : $"副本预检：可读账户 {salvage.ReadableRowCounts.GetValueOrDefault("accounts")} 条、人工交易复盘 {salvage.ReadableRowCounts.GetValueOrDefault("trade_review_documents")} 条、附件登记 {salvage.ReadableRowCounts.GetValueOrDefault("attachment_assets")} 条；失败表 {salvage.FailedTables.Count} 个，当前缺失附件 {salvage.MissingAttachmentCount} 件。可读人工记录已导出到副本目录。";
            await OnUiAsync(() =>
            {
                _viewModel.ReviewWorkspace.StorageStatus =
                    "数据库格式或完整性检查未通过；原文件未被替换，复盘写入已停用。请检查备份并决定恢复方式。";
                _viewModel.DiagnosticText = "本地资料待恢复；实时提醒可继续，但本次运行不会写入复盘库。";
                _viewModel.ShowMainWindow?.Invoke();
                System.Windows.MessageBox.Show(
                    $"{exception.Message}\n\n{salvageText}\n保护副本：{exception.SafetySnapshotDirectory ?? "未创建；原库仍在原位"}\n\n请先保留这些文件，并从有效的完整备份恢复；不要继续在旧程序中写入该工作区。",
                    "TradePet 数据库需要恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
        catch (Exception exception)
        {
            _persistenceAvailable = false;
            await OnUiAsync(() =>
            {
                _viewModel.ReviewWorkspace.StorageStatus =
                    "本地数据初始化或中断恢复未完成；复盘写入已停用。若存在恢复记录，原文件已保留且采集暂停，请先处理再继续。";
                _viewModel.ShowMainWindow?.Invoke();
                System.Windows.MessageBox.Show(
                    $"{exception.Message}\n\n工作区尚未进入可写状态；现有数据未被自动重建。请先检查备份、版本和数据目录权限。",
                    "TradePet 工作区暂不可写", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
            await ReportPersistenceFailureAsync("初始化本地数据库", exception);
        }

        if (File.Exists(TradePetPaths.GetDatabasePath() + ".restore-state.json"))
        {
            return;
        }

        try
        {
            await LoadDesktopSettingsAsync();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportPersistenceFailureAsync("加载桌宠设置", exception);
        }

        try
        {
            foreach (var chartObject in _persistenceAvailable
                         ? await _database.LoadChartObjectsAsync(cancellationToken: _cancellation.Token)
                         : [])
            {
                _chartObjects[chartObject.ObjectKey] = chartObject;
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportPersistenceFailureAsync("加载图表对象", exception);
        }

        _bridge.DiagnosticReceived += UpdateBridgeDiagnostic;
        _bridge.ConnectionChanged += connected =>
        {
            AppLog.Write(connected ? "Bridge pipe connected." : "Bridge pipe disconnected.");
            if (!connected)
            {
                _serverClock.MarkBridgeDisconnected();
            }
            _ = OnUiAsync(() => _viewModel.BridgeText = connected ? "桥接插件正在校验" : "桥接插件已断开");
        };
        StartBackgroundTask("桌宠状态循环", RunPetLifeAsync);
        StartBackgroundTask("交易日报定时器", RunDailyReportScheduleAsync);
        StartBackgroundTask("宏观事件提醒", RunMacroCalendarMonitorAsync);

        var terminals = _terminalDiscovery.Discover();
        await OnUiAsync(() =>
        {
            _viewModel.TerminalOptions.Clear();
            for (var index = 0; index < terminals.Count; index++)
            {
                _viewModel.TerminalOptions.Add(new TerminalOption(terminals[index].TerminalPath, $"交易终端 {index + 1}"));
            }
        });
        _terminal = _terminalDiscovery.FindPreferred(_viewModel.SelectedTerminalPath);
        if (_terminal is null)
        {
            await OnUiAsync(() =>
            {
                _viewModel.ConnectionText = "未找到交易终端";
                _viewModel.DiagnosticText = "请先启动交易终端，桌宠仍会正常活动。";
            });
            return;
        }

        await OnUiAsync(() => _viewModel.SelectedTerminalPath = _terminal.TerminalPath);

        StartBackgroundTask("Bridge 管道", _bridge.RunAsync);
        StartBackgroundTask("Bridge 事件消费", ConsumeBridgeEventsAsync);
        await InstallBridgeAsync();
        var paths = RuntimePaths.Resolve();
        if (!File.Exists(paths.WorkerScript))
        {
            AppLog.Write($"Missing worker: {paths.WorkerScript}");
            await OnUiAsync(() => _viewModel.DiagnosticText = "数据采集组件缺失，请重新解压完整发布包。");
            return;
        }

        _worker = new Mt5WorkerClient(new Mt5WorkerOptions(paths.PythonExecutable, paths.WorkerScript, _terminal.TerminalPath));
        _worker.DiagnosticReceived += message => UpdateWorkerDiagnostic(message, paths.PythonSetupScript);
        StartBackgroundTask("MT5 采集进程", _worker.RunAsync);
        StartBackgroundTask("MT5 事件消费", ConsumeWorkerEventsAsync);
        await OnUiAsync(() =>
        {
            _viewModel.ConnectionText = "正在连接交易终端…";
            _viewModel.DiagnosticText = "交易终端已选定，正在启动只读采集。";
            _viewModel.PetActivity = _petState.Current.Activity;
        });
    }

    public void RequestStop() => _cancellation.Cancel();

    private void StartBackgroundTask(string name, Func<CancellationToken, Task> operation)
    {
        _backgroundTasks.Add(Task.Run(
            () => RunSupervisedAsync(name, operation, _cancellation.Token),
            _cancellation.Token));
    }

    private async Task RunSupervisedAsync(
        string name,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await operation(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                AppLog.Write($"Background task '{name}' completed unexpectedly; restarting.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Write($"Background task '{name}' failed; restarting: {exception}");
                await OnUiAsync(() => _viewModel.DiagnosticText = $"{name}发生错误，正在自动恢复。");
            }

            await _scheduler.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task WithStateGateAsync(Func<Task> operation)
    {
        await using var operationLease = await EnterRuntimeOperationAsync(
            MaintenanceOperationKind.Write, _cancellation.Token);
        await WithStateGateOnlyAsync(operation);
    }

    private async Task WithStateGateOnlyAsync(Func<Task> operation)
    {
        await _stateGate.WaitAsync(_cancellation.Token);
        try
        {
            await operation();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task WithMaintenanceOperationAsync(MaintenanceOperationKind kind, Func<Task> operation)
    {
        await using var operationLease = await EnterRuntimeOperationAsync(kind, _cancellation.Token);
        await operation();
    }

    private async ValueTask<IAsyncDisposable> EnterRuntimeOperationAsync(
        MaintenanceOperationKind kind, CancellationToken cancellationToken)
    {
        if (_runtimeOperation.Value is { Active: true })
        {
            return NoopRuntimeLease.Instance;
        }
        var lease = await _maintenance.EnterOperationAsync(kind, cancellationToken);
        var marker = new RuntimeOperationMarker();
        _runtimeOperation.Value = marker;
        return new RuntimeOperationLease(this, marker, lease);
    }

    private sealed class RuntimeOperationMarker
    {
        public bool Active { get; set; } = true;
    }

    private sealed class RuntimeOperationLease(
        TradePetRuntime runtime, RuntimeOperationMarker marker, IAsyncDisposable lease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            marker.Active = false;
            runtime._runtimeOperation.Value = null;
            await lease.DisposeAsync();
        }
    }

    private sealed class NoopRuntimeLease : IAsyncDisposable
    {
        public static NoopRuntimeLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task WithReviewWriteGateAsync(Func<Task> operation)
    {
        await using var operationLease = await EnterRuntimeOperationAsync(
            MaintenanceOperationKind.Write,
            _cancellation.Token);
        await _reviewSaveGate.WaitAsync(_cancellation.Token);
        try
        {
            await operation();
        }
        finally
        {
            _reviewSaveGate.Release();
        }
    }

    public async Task TogglePlanRecordingAsync()
    {
        await using var operationLease = await EnterRuntimeOperationAsync(
            MaintenanceOperationKind.Read, _cancellation.Token);
        await _stateGate.WaitAsync(_cancellation.Token);
        try
        {
            if (_viewModel.IsPlanRecording)
            {
                _viewModel.IsPlanRecording = false;
                _planBaseline.Clear();
                await ShowSpeechAsync("今日计划记录结束。", string.Empty, TimeSpan.FromSeconds(5));
            }
            else
            {
                _planBaseline.Clear();
                foreach (var key in _chartObjects.Where(pair => !pair.Value.IsDeleted).Select(pair => pair.Key))
                {
                    _planBaseline.Add(key);
                }

                _viewModel.IsPlanRecording = true;
                await ShowSpeechAsync("开始记录今日计划。", "现在新画的对象会出现在交易计划页面。", TimeSpan.FromSeconds(7));
            }

            await OnUiAsync(_viewModel.NotifyPlanRecordingChanged);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task ImportCurrentChartAsync()
    {
        await using var operationLease = await EnterRuntimeOperationAsync(
            MaintenanceOperationKind.Write, _cancellation.Token);
        await _stateGate.WaitAsync(_cancellation.Token);
        try
        {
            if (_account is null)
            {
                await ShowSpeechAsync("还没连接账户。", "连接后再导入图表对象。", TimeSpan.FromSeconds(6));
                return;
            }

            var candidates = _chartObjects.Values
                .Where(item => !item.IsDeleted && (_hostChartId is null || item.ChartId == _hostChartId.Value))
                .ToArray();
            foreach (var chartObject in candidates)
            {
                await _database.UpsertChartObjectAsync(
                    chartObject, BridgePayloadMapper.ComputeContentHash(chartObject), _cancellation.Token);
                await CreateOrUpdatePlanItemAsync(chartObject, createIfMissing: true);
            }

            await ShowSpeechAsync("图表对象已导入。", $"共 {candidates.Length} 个。", TimeSpan.FromSeconds(6));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public Task SaveSettingsAsync() => WithStateGateAsync(SaveSettingsCoreAsync);

    private async Task SaveSettingsCoreAsync()
    {
        if (!TryParseDailyReportTime(_viewModel.DailyReportTimeText, out var dailyReportTime))
        {
            await ShowSpeechAsync("日报时间格式不对。", "请直接输入 HH:mm，例如 23:55。", TimeSpan.FromSeconds(7));
            return;
        }
        _viewModel.DailyReportTimeText = dailyReportTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        _settings = DailyPlanSettings.BalancedDefault with
        {
            DailyTarget = _viewModel.DailyTarget,
            DailyLoss = _viewModel.DailyLoss,
            MaximumTrades = _viewModel.MaximumTrades,
            MaximumLot = _viewModel.MaximumLot,
            StopLossReminderEnabled = _viewModel.StopLossReminderEnabled,
            StopLossReminderSeconds = _viewModel.StopLossReminderSeconds,
            GivebackMode = GivebackMode.Percentage,
            GivebackValue = _viewModel.GivebackValue,
        };
        _floatingLossPolicy = _viewModel.GetFloatingLossPolicy();
        var desktop = new DesktopSettings(
            _viewModel.IsFocusMode,
            _viewModel.IsTopmost,
            _viewModel.IsPositionLocked,
            _viewModel.IsMouseThrough,
            _viewModel.PetOpacity,
            _viewModel.PetScale,
            _viewModel.LossZoneTolerance,
            _viewModel.SelectedTerminalPath,
            1,
            _viewModel.ExpandCardOnHover,
            _viewModel.MiniPositionVisible,
            _viewModel.MiniPositionPinned,
            1,
            _viewModel.DailyReportEnabled,
            _viewModel.DailyReportTimeText);
        var settingsPersisted = await TryPersistLiveAsync(
            () => _database.SaveSettingAsync(GlobalScope, DesktopSettingKey, desktop, _cancellation.Token),
            "保存桌宠设置");
        StartupRegistration.SetEnabled(_viewModel.StartWithWindows);
        if (_account is not null && _dailyState is not null)
        {
            settingsPersisted &= await TryPersistLiveAsync(
                () => _database.UpsertTradingDayAsync(_dailyState, _settings, _cancellation.Token),
                "保存每日设置");
        }
        var floatingPolicyPersisted = false;
        if (_account is not null)
        {
            var floatingPolicyPersistedToDatabase = await TryPersistLiveAsync(
                () => _database.SaveSettingAsync(
                    $"account:{_account.Scope.AccountKey}", FloatingLossSettingKey, _floatingLossPolicy, _cancellation.Token),
                "保存浮亏提醒设置");
            var floatingPolicyPersistedToFallback = await SaveFloatingLossFallbackAsync(
                _account.Scope.AccountKey, _floatingLossPolicy);
            floatingPolicyPersisted = floatingPolicyPersistedToDatabase || floatingPolicyPersistedToFallback;
            settingsPersisted &= floatingPolicyPersistedToDatabase;
        }

        if (_terminal is not null &&
            !string.Equals(_terminal.TerminalPath, _viewModel.SelectedTerminalPath, StringComparison.OrdinalIgnoreCase))
        {
            UpdateDiagnostic("交易终端选择已保存，重启天禄后生效。");
        }

        var saveHeadline = settingsPersisted
            ? "设置记住了。"
            : floatingPolicyPersisted
                ? "浮亏提醒比例记住了。"
                : "设置已应用，重启前有效。";
        var saveDetail = settingsPersisted
            ? string.Empty
            : floatingPolicyPersisted
                ? "本地历史库异常；多段浮亏比例已单独保存。"
                : "本地数据库异常，暂时无法永久保存。";
        await ShowSpeechAsync(saveHeadline, saveDetail, TimeSpan.FromSeconds(settingsPersisted ? 4 : 7));
        if (_account is not null)
        {
            await EvaluateFloatingLossAlertsAsync(_account, suppressNotification: false);
        }
    }

    public async Task InstallBridgeAsync()
    {
        if (_terminal is null)
        {
            UpdateDiagnostic("没有可安装桥接插件的交易终端。");
            return;
        }

        try
        {
            var paths = RuntimePaths.Resolve();
            var result = new BridgeInstaller().Install(_terminal, paths.BridgeCompiled, paths.BridgeSource);
            await OnUiAsync(() =>
            {
                _viewModel.BridgeText = _bridge.IsConnected ? "桥接插件已连接" : "桥接插件已安装，等待挂图";
                _viewModel.DiagnosticText = "桥接插件已安装，请将它挂到任意图表。";
            });
            AppLog.Write($"Bridge installed: {result.ExpertDirectory}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write($"Bridge installation failed: {exception}");
            UpdateDiagnostic("桥接插件安装失败，请检查日志。");
        }
    }

    public Task ReplayCoreScenarioAsync() => WithStateGateAsync(ReplayCoreScenarioCoreAsync);

    private async Task ReplayCoreScenarioCoreAsync()
    {
        var date = new DateOnly(2026, 8, 30);
        const string account = "replay|1";
        var zones = new List<LossZoneState>();
        var attempts = new List<LossZoneAttempt>();
        var first = ReplayTrade(account, 1, TradeSide.Buy, 3352m, 0.02m, -8m, ReplayAt(14, 31), ReplayAt(14, 35), date);
        var firstClose = _lossZoneEngine.RegisterClose(first, date, 2m, zones, attempts);
        zones.Add(firstClose.Zone!);
        attempts.Add(firstClose.Attempt!);
        var secondOpen = ReplayTrade(account, 2, TradeSide.Buy, 3351m, 0.02m, 0m, ReplayAt(14, 36), null, date);
        var secondOpening = _lossZoneEngine.EvaluateOpen(secondOpen, date, 2m, zones, attempts, [], first, DailyPlanSettings.BalancedDefault);
        zones[0] = secondOpening.Zone!;
        attempts.Add(secondOpening.Attempt!);
        var second = secondOpen with
        {
            ClosedAtUtc = ReplayAt(14, 40), CloseServerDate = date, NetPnl = -11m, IsComplete = true, RemainingVolume = 0m,
        };
        var secondClose = _lossZoneEngine.RegisterClose(second, date, 2m, zones, attempts);
        zones[0] = secondClose.Zone!;
        attempts[1] = secondClose.Attempt!;
        var plan = new PlanItem("replay-plan", account, date, "replay/chart/zone", PlanCategory.NoTradeZone,
            "XAUUSD.s", 3349m, 3355m, "不交易区", true, ReplayAt(8, 0));
        var third = ReplayTrade(account, 3, TradeSide.Sell, 3352m, 0.05m, 0m, ReplayAt(14, 40).AddSeconds(40), null, date);
        var result = _lossZoneEngine.EvaluateOpen(third, date, 2m, zones, attempts, [plan], second, DailyPlanSettings.BalancedDefault);
        if (result.Alert is not null)
        {
            await ShowAlertAsync(
                result.Alert,
                PetSignal.Risk,
                result.Zone,
                third.EntryPrice,
                persistDelivery: false,
                deduplicateInMemory: false);
            UpdateDiagnostic("核心场景回放完成：未连接交易接口、未写入真实账户数据。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _accountSessions.Dispose();
        _reviewQueryCancellation?.Cancel();
        _tradeDetailCancellation?.Cancel();
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        await _bridge.DisposeAsync();
        try
        {
            await Task.WhenAll(_backgroundTasks);
            if (_reviewRefreshTask is not null)
            {
                await _reviewRefreshTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _reviewQueryCancellation?.Dispose();
            _tradeDetailCancellation?.Dispose();
            _reviewSaveGate.Dispose();
            _stateGate.Dispose();
            _dailyReportGate.Dispose();
            _cancellation.Dispose();
        }
    }

    public async Task<ShutdownPreparationResult> PrepareForShutdownAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var review = _viewModel.ReviewWorkspace;
        await OnUiAsync(review.BeginShutdownEdits);
        if (!review.HasUnsavedWorkspaceChanges)
        {
            return new ShutdownPreparationResult(true, "全部编辑内容已保存。", null);
        }

        try
        {
            var flushTask = WithReviewWriteGateAsync(FlushDirtyWorkspaceEditorsAsync);
            await flushTask.WaitAsync(
                timeout ?? TimeSpan.FromSeconds(8),
                _timeProvider,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            var timeoutPath = await ExportPendingReviewDraftsAsync(cancellationToken);
            return new ShutdownPreparationResult(
                false,
                "等待保存超时；尚未确认的内容仍保持为未保存。",
                timeoutPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelledPath = await ExportPendingReviewDraftsAsync(CancellationToken.None);
            return new ShutdownPreparationResult(false, "退出前保存已取消。", cancelledPath);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Shutdown review flush failed: {exception}");
        }

        if (!review.HasUnsavedWorkspaceChanges)
        {
            return new ShutdownPreparationResult(true, "全部编辑内容已保存。", null);
        }

        var draftPath = await ExportPendingReviewDraftsAsync(cancellationToken);
        return new ShutdownPreparationResult(
            false,
            "仍有验证失败、冲突或存储失败的编辑内容；没有把它们标记为已保存。",
            draftPath);
    }

    public Task ResumeAfterCancelledShutdownAsync() => OnUiAsync(_viewModel.ReviewWorkspace.ResumeEdits);

    private async Task<string?> ExportPendingReviewDraftsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var drafts = _viewModel.ReviewWorkspace.ExportPendingDrafts();
            if (drafts.Count == 0)
            {
                return null;
            }
            var directory = Path.Combine(TradePetPaths.GetDataDirectory(), "recovery-drafts");
            Directory.CreateDirectory(directory);
            var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            var destination = Path.Combine(directory, $"pending-review-{stamp}.json");
            var temporary = destination + ".tmp";
            var payload = JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                createdAtUtc = _timeProvider.GetUtcNow(),
                drafts,
            }, ProtocolJson.Options);
            await File.WriteAllTextAsync(temporary, payload, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Pending review draft export failed: {exception}");
            return null;
        }
    }

    private async Task ConsumeWorkerEventsAsync(CancellationToken cancellationToken)
    {
        if (_worker is null)
        {
            return;
        }

        await foreach (var envelope in _worker.Events.ReadAllAsync(cancellationToken))
        {
            try
            {
                await using var operationLease = await EnterRuntimeOperationAsync(
                    MaintenanceOperationKind.Write, cancellationToken);
                await _stateGate.WaitAsync(cancellationToken);
                try
                {
                    try
                    {
                        if (!await ShouldProcessEnvelopeAsync(envelope, cancellationToken))
                        {
                            continue;
                        }

                        switch (envelope.Kind)
                        {
                            case "connection":
                                await HandleConnectionAsync(envelope);
                                break;
                            case "snapshot":
                                await HandleSnapshotAsync(Mt5PayloadMapper.MapSnapshot(envelope));
                                break;
                            case "deals":
                                await HandleDealsAsync(Mt5PayloadMapper.MapDealBatch(envelope));
                                break;
                            case "error":
                                AppLog.Write($"Worker error: {envelope.Payload}");
                                UpdateDiagnostic("数据采集发生错误，正在尝试恢复。");
                                break;
                        }
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        AppLog.Write($"Worker event '{envelope.Kind}' failed and was skipped: {exception}");
                        await OnUiAsync(() => _viewModel.DiagnosticText = "一条采集数据处理失败，实时同步仍在继续。");
                    }
                }
                finally
                {
                    _stateGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Write($"Worker envelope '{envelope.Kind}' could not enter the processing pipeline: {exception}");
                await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private async Task ConsumeBridgeEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in _bridge.Events.ReadAllAsync(cancellationToken))
        {
            try
            {
                await using var operationLease = await EnterRuntimeOperationAsync(
                    MaintenanceOperationKind.Write, cancellationToken);
                await _stateGate.WaitAsync(cancellationToken);
                try
                {
                    try
                    {
                        if (!BridgePayloadMapper.MatchesSource(
                                envelope,
                                _terminal?.TerminalId,
                                _account?.Scope.AccountKey ?? _workerAccountKey))
                        {
                            AppLog.Write($"Ignored Bridge event '{envelope.Kind}' from an unexpected terminal or account.");
                            continue;
                        }

                        if (!await ShouldProcessEnvelopeAsync(envelope, cancellationToken))
                        {
                            continue;
                        }

                        switch (envelope.Kind)
                        {
                            case "heartbeat":
                                await HandleHeartbeatAsync(BridgePayloadMapper.MapHeartbeat(envelope));
                                break;
                            case "trade_dirty":
                                var wakeLatency = Math.Max(0, (_timeProvider.GetUtcNow() - envelope.OccurredAtUtc).TotalMilliseconds);
                                AppLog.Write($"Bridge trade_dirty received; wake latency {wakeLatency:0} ms.");
                                if (_worker is not null)
                                {
                                    await _worker.RefreshAsync(_serverUtcOffsetSeconds, _serverDate, cancellationToken);
                                }
                                break;
                            case "chart_upsert":
                            case "chart_delete":
                                await HandleChartObjectAsync(BridgePayloadMapper.MapChartObject(envelope));
                                break;
                            case "chart_snapshot":
                                await HandleChartSnapshotAsync(BridgePayloadMapper.MapChartSnapshot(envelope));
                                break;
                            case "calendar_snapshot":
                                await HandleEconomicCalendarAsync(BridgePayloadMapper.MapEconomicCalendar(envelope));
                                break;
                        }
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        AppLog.Write($"Bridge event '{envelope.Kind}' failed and was skipped: {exception}");
                        await OnUiAsync(() => _viewModel.DiagnosticText = "一条桥接数据处理失败，图表同步仍在继续。");
                    }
                }
                finally
                {
                    _stateGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Write($"Bridge envelope '{envelope.Kind}' could not enter the processing pipeline: {exception}");
                await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private async Task<bool> ShouldProcessEnvelopeAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        var transient = envelope.Kind is "hello" or "connection" or "snapshot" or "deals" or "error" or
            "heartbeat" or "trade_dirty" or "chart_snapshot" or "chart_upsert" or "chart_delete" or
            "calendar_snapshot";
        if (!transient && _persistenceAvailable)
        {
            return await _database.TryMarkEventProcessedAsync(
                envelope.SourceInstanceId, envelope.Sequence, cancellationToken);
        }

        if (_lastTransientSequences.TryGetValue(envelope.SourceInstanceId, out var sequence) &&
            sequence >= envelope.Sequence)
        {
            return false;
        }

        _lastTransientSequences[envelope.SourceInstanceId] = envelope.Sequence;
        return true;
    }

    private async Task HandleConnectionAsync(ProtocolEnvelope envelope)
    {
        var connected = envelope.Payload.TryGetProperty("connected", out var connectedValue) && connectedValue.GetBoolean();
        if (!connected)
        {
            var waitingForTerminal = envelope.Payload.TryGetProperty("error", out var connectionError) &&
                                     connectionError.ValueKind == JsonValueKind.String &&
                                     connectionError.GetString() == "terminal_not_running";
            _workerSession.Disconnect();
            _suppressNextFloatingLossNotification = true;
            _historySyncTracker.Reset();
            _petState = _petBehavior.ApplySignal(_petState, PetSignal.Disconnected, _timeProvider.GetUtcNow());
            await OnUiAsync(() =>
            {
                _viewModel.ConnectionText = waitingForTerminal ? "等待手动启动 MT5" : "交易终端已断开";
                _viewModel.PositionDataStale = true;
                _viewModel.RiskText = _viewModel.ConnectionText;
                _viewModel.PetActivity = _petState.Current.Activity;
            });
            return;
        }

        _workerSession.Connect(envelope.AccountKey);
        _historySyncTracker.Reset();
        if (_serverDateAuthoritative && _worker is not null && !string.IsNullOrWhiteSpace(envelope.AccountKey))
        {
            await _worker.RefreshAsync(_serverUtcOffsetSeconds, _serverDate, _cancellation.Token);
            await OnUiAsync(() => _viewModel.ReviewSyncText = "正在核对今日与近期成交");
        }
        await OnUiAsync(() =>
        {
            _viewModel.ConnectionText = "交易终端已连接";
            _viewModel.PositionDataStale = false;
            _viewModel.RiskText = FormatRiskText(_dailyState);
        });
    }

    private async Task HandleSnapshotAsync(Mt5SnapshotBatch batch)
    {
        ServerClockUpdate? clockUpdate = null;
        if (batch.ServerUtcOffsetSeconds is { } detectedOffset)
        {
            clockUpdate = _serverClock.ApplyWorkerOffset(detectedOffset);
        }
        var accountChanged = _account?.Scope.AccountKey != batch.Account.Scope.AccountKey;
        var suppressFloatingLossNotification =
            _suppressNextFloatingLossNotification || accountChanged || !_hasInitialSnapshot ||
            _workerSessionPhase != WorkerSessionPhase.Live;
        if (accountChanged)
        {
            await OnUiAsync(() => _viewModel.ResetReviewForAccountChange(
                "账户已切换，正在读取对应复盘数据。"));
            _tradeDetailCancellation?.Cancel();
            Volatile.Write(ref _latestTradeDetailRequestId, null);
            await _accountSessions.SwitchAsync(batch.Account.Scope.AccountKey, _cancellation.Token);
            _reviewQueryCancellation?.Cancel();
            _lastWorkspaceQuery = null;
            _lastTradeDetail = null;
            _lastReplayHistory = null;
            _account = batch.Account;
            _loadedScope = null;
            _positions.Clear();
            _deals.Clear();
            _trades.Clear();
            _symbolSpecifications.Clear();
            _equitySamples.Clear();
            _cashFlows.Clear();
            _exactCloseServerDates.Clear();
            _excursions.Clear();
            _excursionPersistedAt.Clear();
            _pnlSamplePersistedAt.Clear();
            _lossZones.Clear();
            _lossZoneAttempts.Clear();
            _activeImprovementGoals.Clear();
            _planItems.Clear();
            _structuredPlans.Clear();
            _reviewMetadata.Clear();
            _historySyncTracker.Reset();
            _behaviorPolicies = null;
            _floatingLossPolicy = FloatingLossAlertPolicy.BalancedDefault;
            _activeFloatingLossStages.Clear();
            _evaluatedOpenPositionIds.Clear();
            _missingStopLossAlerted.Clear();
            _floatingLossEpisodePeakPercentage = 0m;
            _workerSession.ResetForAccount(batch.Account.Scope.AccountKey);
            _lastEquitySampleAtUtc = default;
            _lastSnapshotPersistedAtUtc = default;
            _lastDailyStatePersistedAtUtc = default;
        }
        else
        {
            _account = batch.Account;
        }

        if (!SupportsCompleteTradeProjection)
        {
            await OnUiAsync(() =>
            {
                _viewModel.ConnectionText = "交易终端已连接 · 当前仅监控持仓";
                _viewModel.DiagnosticText =
                    "完整交易复盘目前只支持 MT5 对冲账户；当前账户继续提供持仓和账户浮亏监控。";
                _viewModel.ClearReview("当前账户不是对冲模式，完整交易复盘已停用，避免生成错误统计。");
            });
        }

        await UpdateSymbolSpecificationsAsync(batch.Account.Scope.AccountKey, batch.SymbolSpecifications);

        var previous = _positions.Values.ToArray();
        var changes = _hasInitialSnapshot
            ? _positionDiffer.Diff(previous, batch.Positions, batch.Account.CapturedAtUtc)
            : [];
        var persistSnapshot = accountChanged || !_hasInitialSnapshot || changes.Count > 0 ||
                              batch.Account.CapturedAtUtc - _lastSnapshotPersistedAtUtc >= TimeSpan.FromSeconds(1);

        if (persistSnapshot)
        {
            await TryPersistLiveAsync(
                () => _database.UpsertAccountAsync(batch.Account, _cancellation.Token),
                "更新账户快照");
        }
        if ((accountChanged || clockUpdate?.ContextChanged == true) && batch.ServerUtcOffsetSeconds is not null)
        {
            await PersistServerTimeSegmentAsync(ReviewTimeBasis.EstimatedBrokerServer, "mt5-python-worker-v1");
        }
        if (_serverDateAuthoritative)
        {
            await EnsureScopeLoadedForLiveAsync();
            await EnsureReviewHistorySyncAsync();
        }
        if (_serverDateAuthoritative)
        {
            await UpdateExcursionSamplingAsync(previous, batch.Positions, batch.Account.CapturedAtUtc);
            await SampleEquityAsync(batch.Account, batch.Positions.Count > 0, force: false);
        }
        _positions.Clear();
        foreach (var position in batch.Positions)
        {
            _positions[position.Ticket] = position;
        }

        if (persistSnapshot)
        {
            await TryPersistLiveAsync(
                () => _database.ReplacePositionsAsync(batch.Account.Scope.AccountKey, batch.Positions, _cancellation.Token),
                "保存当前持仓");
            _lastSnapshotPersistedAtUtc = batch.Account.CapturedAtUtc;
        }
        await UpdatePositionUiAsync(batch.Positions);
        if (_hasInitialSnapshot && _serverDateAuthoritative)
        {
            foreach (var change in changes)
            {
                await HandlePositionChangeAsync(change);
            }
        }

        _workerSession.MarkSnapshotReceived();
        if (_serverDateAuthoritative)
        {
            await RecalculateDailyAsync(isRecovery: !_hasInitialDeals);
            await EvaluateFloatingLossAlertsAsync(batch.Account, suppressFloatingLossNotification);
            await EvaluateMissingStopLossRemindersAsync(batch.Account.CapturedAtUtc);
        }
        else
        {
            await EvaluateFloatingLossAlertsAsync(batch.Account, suppressNotification: true);
        }
        _suppressNextFloatingLossNotification = false;
    }

    private async Task HandleDealsAsync(Mt5DealBatch batch)
    {
        if (_account is null)
        {
            return;
        }

        await UpdateSymbolSpecificationsAsync(_account.Scope.AccountKey, batch.SymbolSpecifications);

        var recovery = _workerSessionPhase != WorkerSessionPhase.Live || !_hasInitialDeals ||
                       batch.HistoryProgress is not null;
        HistorySyncState? historyState = null;
        if (batch.HistoryProgress is not null)
        {
            historyState = new HistorySyncState(
                _account.Scope.AccountKey,
                batch.HistoryProgress.RangeYear,
                _historySyncTracker.CanMarkComplete(
                    batch.HistoryProgress.RangeYear,
                    batch.HistoryProgress.IsComplete),
                batch.HistoryProgress.SourceCount,
                _timeProvider.GetUtcNow());
        }

        var batchPersisted = await TryPersistLiveAsync(
            () => _database.SaveDealBatchAsync(
                _account.Scope.AccountKey,
                batch.Deals,
                batch.CashFlows,
                historyState,
                _cancellation.Token),
            "保存成交批次");
        var historyBatchResult = batch.HistoryProgress is null
            ? null
            : _historySyncTracker.RecordBatch(
                batch.HistoryProgress.RangeYear,
                batch.HistoryProgress.IsComplete,
                batchPersisted);

        foreach (var cashFlow in batch.CashFlows)
        {
            if (ResolveServerDate(cashFlow.OccurredAtUtc) == _serverDate)
            {
                _cashFlows[cashFlow.Ticket] = cashFlow;
            }
        }
        foreach (var deal in batch.Deals)
        {
            _deals[deal.Ticket] = deal;
        }
        if (!_serverDateAuthoritative)
        {
            return;
        }
        if (batch.HistoryProgress is not null)
        {
            var requiresRetry = historyBatchResult!.RequiresRetry;
            var progressMonth = batch.HistoryProgress.RangeFromUtc.AddSeconds(_serverUtcOffsetSeconds).Month;
            await OnUiAsync(() => _viewModel.ReviewSyncText = requiresRetry
                ? $"{batch.HistoryProgress.RangeYear} 年写入未完成 · 正在重新核对"
                : batch.HistoryProgress.IsComplete
                ? historyBatchResult.PendingYearCount == 0
                    ? $"历史同步完成 · {batch.HistoryProgress.RangeYear} 年及以后"
                    : $"{batch.HistoryProgress.RangeYear} 年完成 · 继续向前同步"
                : $"正在同步 {batch.HistoryProgress.RangeYear} 年 · 已处理至 {progressMonth} 月");
        }

        var projection = _dealBatchProjector.Project(
            _account.Scope.AccountKey,
            _deals.Values.ToArray(),
            batch.Deals,
            _trades,
            _exactCloseServerDates,
            ResolveServerDate,
            _serverDate,
            batch.ServerDate,
            batch.HistoryProgress is not null);
        foreach (var pair in projection.ExactCloseDateUpdates)
        {
            _exactCloseServerDates[pair.Key] = pair.Value;
        }
        foreach (var trade in projection.NewlyOpened)
        {
            if (_evaluatedOpenPositionIds.Add(trade.PositionId))
            {
                if (!recovery)
                {
                    await HandleTradeOpenedAsync(trade, trade.OpenedAtUtc);
                }
            }
        }
        foreach (var trade in projection.Upserts)
        {
            _trades[trade.PositionId] = trade;
        }
        await TryPersistLiveAsync(
            () => _database.SaveTradesAsync(projection.Upserts, _cancellation.Token),
            "保存交易投影批次");
        await ApplyAutomaticPlanMetadataAsync(projection.Upserts);

        foreach (var trade in projection.NewlyCompleted)
        {
            await CompleteTradeExcursionAsync(trade);
            if (recovery)
            {
                await RegisterClosedTradeZoneAsync(trade, showFeedback: false);
            }
            else
            {
                await RegisterClosedTradeZoneAsync(trade, showFeedback: true);
                await OfferQuickReviewAsync(trade);
            }
        }

        if (batch.HistoryProgress?.IsComplete == true)
        {
            await RebuildCurrentLossZoneProjectionAsync(updateUi: true);
        }

        _workerSession.MarkDealsReceived();
        if (projection.AffectsCurrentServerDate)
        {
            await SampleEquityAsync(_account, _positions.Count > 0, force: true);
            await RecalculateDailyAsync(recovery);
        }
        if (batch.HistoryProgress is null || batch.HistoryProgress.IsComplete)
        {
            await RefreshReviewUiAsync();
        }
        var previousServerDate = _serverDate.AddDays(-1);
        if (batch.HistoryProgress?.IsComplete == true &&
            historyBatchResult is { RequiresRetry: false } &&
            batch.HistoryProgress.RangeYear == previousServerDate.Year)
        {
            await OfferDailyTradingReportAsync(previousServerDate);
        }
        if (_historySyncTracker.ShouldRequest(_account.Scope.AccountKey) &&
            (batch.HistoryProgress is null || batch.HistoryProgress.IsComplete))
        {
            await EnsureReviewHistorySyncAsync();
        }
    }

    private async Task HandlePositionChangeAsync(TradeDomainEvent change)
    {
        if (_account is null)
        {
            return;
        }

        switch (change.Kind)
        {
            case TradeDomainEventKind.Opened:
                // Entry rules are evaluated from the confirmed deal projection. A position
                // snapshot can arrive before its opening deal and does not carry full costs.
                break;
            case TradeDomainEventKind.Increased:
                await AddTimelineAsync(TimelineKind.TradeIncreased, $"加仓 +{change.VolumeDelta:0.##} {change.Symbol}");
                break;
            case TradeDomainEventKind.Reduced:
                await AddTimelineAsync(TimelineKind.TradeReduced, $"减仓 -{change.VolumeDelta:0.##} {change.Symbol}");
                break;
            case TradeDomainEventKind.AddingToLoss:
                var fact = new RuleFact(RuleFactKind.AddingToLoss, AlertPriority.Important, 10,
                    "这笔是在浮亏状态下加的仓。", $"总仓位增加 {change.VolumeDelta:0.##} 手");
                var alert = _alertComposer.Compose($"add-loss:{change.PositionId}", [fact], change.ObservedAtUtc);
                if (alert is not null)
                {
                    await AddTimelineAsync(TimelineKind.AddingToLoss, fact.Detail);
                    await ShowAlertAsync(alert, PetSignal.Risk);
                }
                break;
            case TradeDomainEventKind.StopLossAdded:
            case TradeDomainEventKind.StopLossModified:
            case TradeDomainEventKind.StopLossRemoved:
                await AddTimelineAsync(TimelineKind.StopLossChanged, $"{change.Symbol} {FormatTradeChange(change.Kind)}");
                if (change.Kind == TradeDomainEventKind.StopLossRemoved)
                {
                    await ShowSpeechAsync("止损被撤掉了。", change.Symbol, TimeSpan.FromSeconds(7));
                }
                break;
            case TradeDomainEventKind.TakeProfitAdded:
            case TradeDomainEventKind.TakeProfitModified:
            case TradeDomainEventKind.TakeProfitRemoved:
                await AddTimelineAsync(TimelineKind.TakeProfitChanged, $"{change.Symbol} {FormatTradeChange(change.Kind)}");
                break;
        }
    }

    private async Task HandleTradeOpenedAsync(TradeRecord trade, DateTimeOffset observedAtUtc)
    {
        if (_account is null || !SupportsCompleteTradeProjection)
        {
            return;
        }

        var previousClosed = _trades.Values
            .Where(item => item.IsComplete && item.ClosedAtUtc <= trade.OpenedAtUtc)
            .OrderByDescending(item => item.ClosedAtUtc)
            .FirstOrDefault();
        var result = _lossZoneEngine.EvaluateOpen(
            trade, _serverDate, ResolveLossZoneTolerance(trade), _lossZones, _lossZoneAttempts,
            _planItems.Values, previousClosed, EffectiveAlertSettings());
        await PersistLossZoneOpenResultAsync(result);
        var liveBehavior = _behaviorCalculator.EvaluateOpen(
            _account.Scope.AccountKey,
            _serverDate,
            trade,
            _trades.Values.Append(trade).ToArray(),
            _behaviorPolicies?.Selected ?? BehaviorPolicy.Balanced,
            observedAtUtc,
            result.Zone?.AttemptCount,
            _symbolSpecifications);
        foreach (var evaluation in liveBehavior.Evaluations)
        {
            await TryPersistLiveAsync(
                () => _database.AddBehaviorEvaluationAsync(evaluation, _cancellation.Token),
                "保存行为评估");
        }
        var behaviorFacts = liveBehavior.Evaluations
            .Where(item => item.Triggered)
            .Select(ToBehaviorFact)
            .ToArray();
        var openAlert = _alertComposer.Compose(
            $"open:{trade.AccountKey}:{trade.PositionId}",
            result.AllFacts.Concat(behaviorFacts),
            observedAtUtc);
        await AddTimelineAsync(TimelineKind.TradeOpened,
            $"{trade.Symbol} {FormatSide(trade.Side)} {trade.OpeningVolume:0.##} 手，入场价 {trade.EntryPrice:0.#####}");
        _petState = _petBehavior.ApplySignal(_petState, PetSignal.TradeOpened, observedAtUtc);
        var alertDelivered = false;
        if (openAlert is not null)
        {
            if (liveBehavior.RiskLevel == BehaviorRiskLevel.Critical)
            {
                openAlert = openAlert with { Headline = "手离鼠标远一点，别上头。" };
            }
            foreach (var behaviorFact in behaviorFacts)
            {
                await AddTimelineAsync(TimelineKind.Alert, behaviorFact.Summary);
            }
            alertDelivered = await ShowAlertAsync(
                openAlert,
                PetSignal.Risk,
                LossZoneEngine.IsConfirmedLossZone(result.Zone) ? result.Zone : null,
                trade.EntryPrice);
        }
        else
        {
            await ShowSpeechAsync("开仓了。", $"{trade.Symbol}\n{FormatSide(trade.Side)} {trade.OpeningVolume:0.##} 手\n入场价 {trade.EntryPrice:0.#####}", TimeSpan.FromSeconds(6));
        }
        foreach (var evaluation in liveBehavior.Evaluations.Where(item => item.Triggered))
        {
            var relatedGoals = _activeImprovementGoals.Where(item =>
                    AppliesToGoal(item, evaluation.Rule, evaluation.ServerDate, trade.Symbol))
                .Select(item => item.Id).Distinct(StringComparer.Ordinal).ToArray();
            var links = new List<BehaviorTradeLink>
            {
                new(new TradeKey(trade.AccountKey, trade.PositionId), BehaviorTradeRole.Trigger),
            };
            if (previousClosed is not null)
            {
                links.Add(new BehaviorTradeLink(
                    new TradeKey(previousClosed.AccountKey, previousClosed.PositionId),
                    BehaviorTradeRole.PreviousContext));
            }
            var occurrence = new BehaviorOccurrence(
                evaluation.Id, evaluation.AccountKey, evaluation.ServerDate, evaluation.Rule,
                $"behavior-{_behaviorPolicies?.SelectedPreset ?? BehaviorPreset.Balanced}-v1",
                evaluation.Value, evaluation.Baseline, evaluation.Threshold, evaluation.Level,
                ReviewEvidenceSource.LiveObservation, trade.OpenedAtUtc, evaluation.ObservedAtUtc,
                evaluation.Summary, string.Empty, alertDelivered,
                alertDelivered ? "Delivered" : openAlert is null ? "NoAlertComposed" : "SuppressedOrDuplicate",
                null, links, RelatedGoalIds: relatedGoals);
            await TryPersistLiveAsync(
                () => _behaviorReviewService.RecordOccurrenceAsync(occurrence, _cancellation.Token),
                "保存行为证据关联");
        }
        foreach (var evaluation in liveBehavior.Evaluations)
        {
            foreach (var goal in _activeImprovementGoals.Where(item =>
                         AppliesToGoal(item, evaluation.Rule, evaluation.ServerDate, trade.Symbol)))
            {
                var observation = new GoalObservation(
                    $"{goal.Id}:{evaluation.Id}", goal.Id, goal.AccountKey, evaluation.ServerDate, 1,
                    evaluation.Triggered ? 0 : 1, evaluation.Triggered ? 1 : 0,
                    evaluation.Triggered ? GoalObservationStatus.Failed : GoalObservationStatus.Passed,
                    evaluation.Id, evaluation.ObservedAtUtc, [evaluation.Id], goal.RuleVersion);
                await TryPersistLiveAsync(
                    () => _behaviorReviewService.RecordGoalObservationAsync(observation, _cancellation.Token),
                    "保存改进目标观察");
            }
        }
    }

    private static bool AppliesToGoal(
        ImprovementGoal goal,
        BehaviorRuleKind rule,
        DateOnly serverDate,
        string symbol)
    {
        if (goal.Status != ImprovementGoalStatus.Active || goal.Rule != rule ||
            goal.StartServerDate > serverDate || goal.EndServerDate is { } end && end < serverDate)
        {
            return false;
        }
        var symbols = SplitValues(goal.ApplicableSymbols);
        return symbols.Count == 0 || symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RegisterClosedTradeZoneAsync(TradeRecord trade, bool showFeedback)
    {
        var result = _lossZoneEngine.RegisterClose(
            trade,
            trade.CloseServerDate ?? _serverDate,
            ResolveLossZoneTolerance(trade),
            _lossZones,
            _lossZoneAttempts);
        if (result.Attempt is not null)
        {
            UpsertAttemptInMemory(result.Attempt);
        }
        if (result.Zone is not null)
        {
            var reconciled = _lossZoneEngine.Reconcile(result.Zone, _lossZoneAttempts);
            UpsertZoneInMemory(reconciled);
            await TryPersistLiveAsync(
                () => _database.UpsertLossZoneAsync(reconciled, _cancellation.Token),
                "保存亏损区域");
        }
        if (result.Attempt is not null)
        {
            await TryPersistLiveAsync(
                () => _database.UpsertLossZoneAttemptAsync(result.Attempt, _cancellation.Token),
                "保存亏损区域尝试");
        }

        await UpdateLossZoneUiAsync();
        if (!showFeedback)
        {
            return;
        }

        await AddTimelineAsync(TimelineKind.TradeClosed,
            $"{trade.Symbol} 平仓 {(trade.NetPnl >= 0m ? "+" : string.Empty)}{trade.NetPnl:0.##}");
        var signal = trade.NetPnl >= 0m ? PetSignal.ProfitClosed : PetSignal.LossClosed;
        _petState = _petBehavior.ApplySignal(_petState, signal, trade.ClosedAtUtc ?? _timeProvider.GetUtcNow());
        var shouldNotifyLossZone = LossZoneEngine.ShouldNotifyLossClose(trade, result.Zone);
        if (trade.NetPnl < -0.01m && !shouldNotifyLossZone)
        {
            return;
        }

        await ShowSpeechAsync(
            trade.NetPnl >= 0m
                ? $"+{trade.NetPnl:0.##}"
                : "又在这个区间交学费了。",
            trade.NetPnl >= 0m
                ? trade.Symbol
                : $"{trade.Symbol} · {trade.NetPnl:0.##}",
            TimeSpan.FromSeconds(trade.NetPnl < -0.01m ? 10 : 6),
            trade.NetPnl < -0.01m ? result.Zone : null,
            trade.EntryPrice);
    }

    private async Task OfferQuickReviewAsync(TradeRecord trade)
    {
        if (!_persistenceAvailable || _account is null) return;
        var key = new TradeKey(trade.AccountKey, trade.PositionId);
        var pendingKey = $"{trade.AccountKey}|{trade.PositionId}";
        if (!_pendingQuickReviews.TryAdd(pendingKey, 0)) return;
        try
        {
            var detail = await _reviewRepository.LoadTradeDetailAsync(key, _cancellation.Token);
            if (detail is null || detail.Document is not null) { _pendingQuickReviews.TryRemove(pendingKey, out _); return; }
            await OnUiAsync(() =>
            {
                var window = new QuickReviewWindow(detail);
                window.Completed += response =>
                {
                    _pendingQuickReviews.TryRemove(pendingKey, out _);
                    if (response.SaveRequested) _ = SaveQuickReviewAsync(trade, detail.Version, response);
                    else if (response.RemindLater) _ = RemindQuickReviewLaterAsync(trade);
                };
                window.Closed += (_, _) => _pendingQuickReviews.TryRemove(pendingKey, out _);
                window.Show();
            });
        }
        catch
        {
            _pendingQuickReviews.TryRemove(pendingKey, out _);
            throw;
        }
    }

    private async Task SaveQuickReviewAsync(TradeRecord trade, ReviewDataVersion version, QuickReviewWindow dialog)
    {
        var planText = $"是否按计划：{dialog.PlanCompliance}";
        var command = new SaveTradeReviewCommand(new TradeKey(trade.AccountKey, trade.PositionId), string.Empty,
            dialog.ExitReason, dialog.PlanCompliance == "是" ? planText : string.Empty,
            dialog.Improvement, dialog.Improvement, planText, string.Empty, string.Empty,
            version.SourceVersion.ToString(CultureInfo.InvariantCulture), version.RuleVersion, ReviewCompletionStatus.Draft);
        var result = await _journalService.SaveTradeReviewAsync(command, 0, _cancellation.Token);
        if (!result.IsSaved) await ShowSpeechAsync("快速复盘未保存", result.Message, TimeSpan.FromSeconds(7));
    }

    private async Task RemindQuickReviewLaterAsync(TradeRecord trade)
    {
        try
        {
            await _scheduler.DelayAsync(TimeSpan.FromMinutes(10), _cancellation.Token);
            await OfferQuickReviewAsync(trade);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RecalculateDailyAsync(bool isRecovery)
    {
        if (_account is null)
        {
            return;
        }

        var result = _dailyCalculator.Calculate(
            _account.Scope.AccountKey,
            _serverDate,
            _trades.Values.ToArray(),
            _positions.Values.ToArray(),
            EffectiveAlertSettings(),
            _dailyState,
            CalculateObservedHighWater(),
            CalculateDailyRealizedPnl(),
            isRecovery ? null : _account.CapturedAtUtc);
        _dailyState = result.State;
        if (result.NewFacts.Count > 0 ||
            _timeProvider.GetUtcNow() - _lastDailyStatePersistedAtUtc >= TimeSpan.FromSeconds(1))
        {
            await TryPersistLiveAsync(
                () => _database.UpsertTradingDayAsync(result.State, _settings, _cancellation.Token),
                "保存今日状态");
            _lastDailyStatePersistedAtUtc = _timeProvider.GetUtcNow();
        }
        await UpdateDailyUiAsync(result.State);
        await UpdateTodayBehaviorUiAsync();
        if (!isRecovery && result.NewFacts.Count > 0)
        {
            var now = _timeProvider.GetUtcNow();
            var alert = _alertComposer.Compose($"daily:{_account.Scope.AccountKey}:{_serverDate}:{now.Ticks}", result.NewFacts, now);
            if (alert is not null)
            {
                foreach (var fact in result.NewFacts)
                {
                    var kind = fact.Kind switch
                    {
                        RuleFactKind.DailyTarget => TimelineKind.DailyTargetReached,
                        RuleFactKind.DailyLoss => TimelineKind.DailyLossReached,
                        RuleFactKind.ProfitGiveback => TimelineKind.ProfitGiveback,
                        _ => TimelineKind.Alert,
                    };
                    await AddTimelineAsync(kind, fact.Summary);
                }
                await ShowAlertAsync(alert, alert.Priority >= AlertPriority.Important ? PetSignal.Risk : PetSignal.Holding);
            }
        }
    }

    private async Task UpdateTodayBehaviorUiAsync()
    {
        if (_account is null || _behaviorPolicies is null || _dailyState is null ||
            !SupportsCompleteTradeProjection)
        {
            return;
        }

        var summary = _behaviorCalculator.Evaluate(
            _account.Scope.AccountKey,
            _serverDate,
            _trades.Values.ToArray(),
            _lossZones,
            _lossZoneAttempts,
            _reviewMetadata,
            _behaviorPolicies.Selected,
            _dailyState,
            _timeProvider.GetUtcNow(),
            _symbolSpecifications);
        await OnUiAsync(() => _viewModel.ApplyTodayBehavior(summary));
    }

    private async Task EvaluateFloatingLossAlertsAsync(AccountSnapshot account, bool suppressNotification)
    {
        var evaluation = _floatingLossAlertEvaluator.Evaluate(
            _floatingLossPolicy,
            account.Balance,
            account.FloatingPnl,
            _positions.Values.ToArray(),
            _activeFloatingLossStages,
            _floatingLossEpisodePeakPercentage);

        _floatingLossEpisodePeakPercentage = evaluation.EpisodePeakLossPercentage;

        _activeFloatingLossStages.Clear();
        foreach (var stage in evaluation.ActiveStages)
        {
            _activeFloatingLossStages.Add(stage);
        }

        await OnUiAsync(() => _viewModel.CurrentFloatingLossPercentage = evaluation.LossPercentage);
        if (suppressNotification || evaluation.NewTriggers.Count == 0)
        {
            return;
        }

        var trigger = evaluation.NewTriggers
            .OrderByDescending(item => item.ThresholdPercentage)
            .ThenByDescending(item => item.Stage)
            .First();
        var priority = trigger.Stage switch
        {
            >= 3 => AlertPriority.Critical,
            2 => AlertPriority.Important,
            _ => AlertPriority.Normal,
        };
        var exposure = trigger.PrimaryExposure;
        var sideText = exposure?.Side == TradeSide.Buy ? "买入" : "卖出";
        var headline = exposure is null
            ? trigger.Stage switch
            {
                >= 3 => "够了。离开鼠标，先保住账户。",
                2 => "亏损在扩大，手别再往仓位上伸。",
                _ => "浮亏到线了，别拿希望当计划。",
            }
            : trigger.Stage switch
            {
                >= 3 => $"{exposure.Symbol} {sideText}已经失控，别再加。",
                2 => $"{exposure.Symbol} {sideText}正在拖账户后腿。",
                _ => $"{exposure.Symbol} {sideText}浮亏到线了。",
            };
        var exposureDetail = exposure is null
            ? string.Empty
            : $"主要拖累：{exposure.Symbol} {sideText} {exposure.Volume:0.##} 手，" +
              $"浮亏 {exposure.FloatingPnl:0.##}" +
              (exposure.PositionCount > 1 ? $"（{exposure.PositionCount} 笔持仓）\n" : "\n");
        var detail = exposureDetail +
            $"账户合计浮亏 {trigger.FloatingLoss:0.##}，占余额 {trigger.ActualLossPercentage:0.##}%；" +
            $"触发第 {trigger.Stage} 段（{trigger.ThresholdPercentage:0.##}%）。";
        var fact = new RuleFact(RuleFactKind.Alert, priority, 5, headline, detail);
        var alert = _alertComposer.Compose(
            $"floating-loss:{account.Scope.AccountKey}:{_serverDate:yyyy-MM-dd}:{trigger.Stage}:{account.CapturedAtUtc.Ticks}",
            [fact],
            account.CapturedAtUtc);
        if (alert is null)
        {
            return;
        }

        await AddTimelineAsync(TimelineKind.Alert, detail);
        await ShowAlertAsync(alert, PetSignal.Risk);
    }

    private async Task EvaluateMissingStopLossRemindersAsync(DateTimeOffset capturedAtUtc)
    {
        if (!_settings.StopLossReminderEnabled)
        {
            _missingStopLossAlerted.Clear();
            return;
        }

        _missingStopLossAlerted.RemoveWhere(positionId =>
            !_positions.Values.Any(position => position.PositionId == positionId && position.StopLoss <= 0m));
        if (_account is null || _workerSessionPhase != WorkerSessionPhase.Live)
        {
            return;
        }

        foreach (var position in _positions.Values
                     .Where(position => position.StopLoss <= 0m)
                     .OrderBy(position => position.OpenedAtUtc))
        {
            if (capturedAtUtc - position.OpenedAtUtc < TimeSpan.FromSeconds(_settings.StopLossReminderSeconds) ||
                !_missingStopLossAlerted.Add(position.PositionId))
            {
                continue;
            }

            var side = position.Side == TradeSide.Buy ? "买入" : "卖出";
            var fact = new RuleFact(
                RuleFactKind.MissingStopLoss,
                AlertPriority.Important,
                5,
                $"{position.Symbol} 仍然没有止损。",
                $"{side} {position.Volume:0.##} 手已持有至少 {_settings.StopLossReminderSeconds} 秒；天禄只提醒，不会修改持仓。");
            var alert = _alertComposer.Compose(
                $"missing-stop-loss:{_account.Scope.AccountKey}:{_serverDate:yyyy-MM-dd}:{position.PositionId}",
                [fact],
                capturedAtUtc);
            if (alert is not null && await ShowAlertAsync(alert, PetSignal.Risk))
            {
                await AddTimelineAsync(TimelineKind.Alert, fact.Detail);
            }
        }
    }

    private DailyPlanSettings EffectiveAlertSettings()
    {
        var policy = _behaviorPolicies?.Selected;
        return policy is null
            ? _settings
            : _settings with
            {
                ConsecutiveLossThreshold = policy.ConsecutiveLossThreshold,
                RapidReentrySeconds = policy.RapidReentrySeconds,
                LotEscalationMultiplier = policy.LotEscalationMultiplier,
            };
    }

    private static RuleFact ToBehaviorFact(BehaviorEvaluation evaluation)
    {
        var (kind, displayOrder, priority) = evaluation.Rule switch
        {
            BehaviorRuleKind.ReentryCount => (RuleFactKind.BehaviorReentry, 20, AlertPriority.Important),
            BehaviorRuleKind.LossZonePersistence => (RuleFactKind.BehaviorLossZonePersistence, 10, AlertPriority.Important),
            BehaviorRuleKind.RevengeScore => (RuleFactKind.BehaviorRevenge, 20, AlertPriority.Critical),
            BehaviorRuleKind.OvertradeBurst => (RuleFactKind.BehaviorOvertrade, 50, AlertPriority.Important),
            BehaviorRuleKind.PlanDeviationRate => (RuleFactKind.BehaviorPlanDeviation, 40, AlertPriority.Important),
            BehaviorRuleKind.ProfitGiveback => (RuleFactKind.ProfitGiveback, 45, AlertPriority.Important),
            BehaviorRuleKind.SizeEscalationAfterLoss => (RuleFactKind.BehaviorSizeEscalation, 30, AlertPriority.Critical),
            BehaviorRuleKind.CooldownViolation => (RuleFactKind.BehaviorCooldown, 20, AlertPriority.Critical),
            BehaviorRuleKind.PriceFixationScore => (RuleFactKind.BehaviorPriceFixation, 50, AlertPriority.Important),
            _ => (RuleFactKind.Alert, 50, AlertPriority.Important),
        };
        var detail = FormatBehaviorBubbleDetail(evaluation);
        return new RuleFact(kind, priority, displayOrder, evaluation.Summary, detail);
    }

    private static string FormatBehaviorBubbleDetail(BehaviorEvaluation evaluation) => evaluation.Rule switch
    {
        BehaviorRuleKind.ReentryCount =>
            $"同一价位冲了 {evaluation.Value:0} 次。它欠你钱，还是你欠它一口气？",
        BehaviorRuleKind.LossZonePersistence =>
            $"同一个亏损区打了 {evaluation.Value:0} 次。坑没变，踩法倒挺坚定。",
        BehaviorRuleKind.RevengeScore =>
            $"报复分 {evaluation.Value:0}。市场没惹你，手先离开鼠标。",
        BehaviorRuleKind.OvertradeBurst when evaluation.Baseline is not null =>
            $"窗口内 {evaluation.Value:0} 笔，平时才 {evaluation.Baseline:0.##}。鼠标都替你累。",
        BehaviorRuleKind.OvertradeBurst =>
            $"窗口内 {evaluation.Value:0} 笔。交易不是连点器。",
        BehaviorRuleKind.PlanDeviationRate =>
            $"计划外交易 {evaluation.Value:0.##}%。计划写得挺好，下单时全忘了。",
        BehaviorRuleKind.ProfitGiveback =>
            $"利润吐回 {evaluation.Value:0.##}%。赚到手，又亲自送回去？",
        BehaviorRuleKind.SizeEscalationAfterLoss =>
            $"亏后仓位放大到 {evaluation.Value:0.##} 倍。手数不是情绪扩音器。",
        BehaviorRuleKind.CooldownViolation =>
            $"{evaluation.Summary}。这点时间都等不了，凭什么等行情？",
        BehaviorRuleKind.PriceFixationScore =>
            $"价格执着分 {evaluation.Value:0}。市场不会因为你较劲就认错。",
        _ => evaluation.Summary,
    };

    private async Task HandleHeartbeatAsync(BridgeHeartbeat heartbeat)
    {
        var previousServerDate = _serverDate;
        var previousDateWasAuthoritative = _serverDateAuthoritative;
        var clockUpdate = _serverClock.ApplyBridgeHeartbeat(
            heartbeat.ServerDate,
            heartbeat.ServerUtcOffsetSeconds);
        var timeContextChanged = clockUpdate.ContextChanged;
        var dateChanged = clockUpdate.DateChanged;
        _hostChartId = heartbeat.HostChartId;
        if (timeContextChanged)
        {
            await PersistServerTimeSegmentAsync(ReviewTimeBasis.BrokerServer, "mt5-bridge-heartbeat-v1");
        }
        if (dateChanged)
        {
            _loadedScope = null;
            await EnsureScopeLoadedForLiveAsync();
            if (_hasInitialDeals && _account is not null && _deals.Count > 0)
            {
                var projected = ProjectTrades();
                _trades.Clear();
                foreach (var trade in projected)
                {
                    _trades[trade.PositionId] = trade;
                    await TryPersistLiveAsync(
                        () => _database.UpsertTradeAsync(trade, _cancellation.Token),
                        "保存完整交易");
                }
                await RecalculateDailyAsync(isRecovery: true);
            }
        }
        if (timeContextChanged && _worker is not null)
        {
            await _worker.RefreshAsync(
                heartbeat.ServerUtcOffsetSeconds,
                heartbeat.ServerDate,
                _cancellation.Token);
        }
        await EnsureReviewHistorySyncAsync();

        await OnUiAsync(() =>
        {
            _viewModel.BridgeText = "桥接插件已连接";
            _viewModel.ServerDateText = _serverDate.ToString("yyyy-MM-dd");
        });
        if (dateChanged && previousDateWasAuthoritative && previousServerDate < _serverDate)
        {
            await OfferDailyTradingReportAsync(previousServerDate);
        }
    }

    private async Task PersistServerTimeSegmentAsync(ReviewTimeBasis basis, string sourceVersion)
    {
        if (_account is null || _terminal is null)
        {
            return;
        }
        var segment = new ServerTimeSegment(
            _account.Scope.AccountKey,
            _terminal.TerminalId,
            _timeProvider.GetUtcNow(),
            null,
            _serverUtcOffsetSeconds,
            basis,
            sourceVersion);
        await TryPersistLiveAsync(
            () => _database.SaveServerTimeSegmentAsync(segment, _cancellation.Token),
            "保存服务器时间来源");
    }

    private async Task HandleChartObjectAsync(ChartObjectSnapshot chartObject)
    {
        _chartObjects[chartObject.ObjectKey] = chartObject;
        if (_account is null || !_persistenceAvailable)
        {
            return;
        }

        var isTracked = _planItems.ContainsKey(chartObject.ObjectKey);
        var isNewRecordedObject = _viewModel.IsPlanRecording &&
                                  !_planBaseline.Contains(chartObject.ObjectKey) &&
                                  !chartObject.IsDeleted;
        if (!isTracked && !isNewRecordedObject)
        {
            return;
        }

        await _database.UpsertChartObjectAsync(
            chartObject, BridgePayloadMapper.ComputeContentHash(chartObject), _cancellation.Token);
        if (isTracked)
        {
            await CreateOrUpdatePlanItemAsync(chartObject, createIfMissing: false);
        }
        else
        {
            await CreateOrUpdatePlanItemAsync(chartObject, createIfMissing: true);
            _planBaseline.Add(chartObject.ObjectKey);
            await ShowSpeechAsync("新画了一个计划对象。", "到交易计划页面选择它的用途。", TimeSpan.FromSeconds(7));
        }
    }

    private async Task HandleChartSnapshotAsync(BridgeChartSnapshot snapshot)
    {
        _hostChartId = snapshot.HostChartId;
        var nextKeys = snapshot.Objects.Select(item => item.ObjectKey).ToHashSet(StringComparer.Ordinal);
        var removed = _chartObjects.Values
            .Where(item => item.TerminalId == snapshot.TerminalId &&
                           !item.IsDeleted &&
                           !nextKeys.Contains(item.ObjectKey) &&
                           _planItems.ContainsKey(item.ObjectKey))
            .Select(item => item with { IsDeleted = true, CapturedAtUtc = _timeProvider.GetUtcNow() })
            .ToArray();

        foreach (var key in _chartObjects
                     .Where(pair => pair.Value.TerminalId == snapshot.TerminalId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _chartObjects.Remove(key);
        }

        foreach (var chartObject in snapshot.Objects)
        {
            _chartObjects[chartObject.ObjectKey] = chartObject;
            if (_planItems.ContainsKey(chartObject.ObjectKey))
            {
                await HandleChartObjectAsync(chartObject);
            }
        }

        foreach (var chartObject in removed)
        {
            await HandleChartObjectAsync(chartObject);
        }
    }

    private async Task HandleEconomicCalendarAsync(BridgeEconomicCalendarSnapshot snapshot)
    {
        if (!_macroCalendarSnapshotReceived)
        {
            AppLog.Write($"MT5 economic calendar snapshot received: {snapshot.Events.Count} events.");
            _macroCalendarSnapshotReceived = true;
        }
        var previous = _economicCalendarEvents.ToDictionary(item => item.ValueId);
        _economicCalendarEvents = snapshot.Events
            .Where(item => item.Importance is EconomicEventImportance.Moderate or EconomicEventImportance.High)
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenByDescending(item => item.Importance)
            .ToArray();

        var newlyPublished = _economicCalendarEvents
            .Where(item => item.Importance == EconomicEventImportance.High &&
                           item.ActualValue is not null &&
                           (!previous.TryGetValue(item.ValueId, out var old) || old.ActualValue is null) &&
                           item.ScheduledAtUtc >= _timeProvider.GetUtcNow().AddMinutes(-15))
            .OrderByDescending(item => item.ScheduledAtUtc)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToArray();

        await OnUiAsync(() => _macroCalendarWindow?.ApplyEvents(
            _economicCalendarEvents, snapshot.ServerUtcOffsetSeconds));
        if (newlyPublished.Length > 0)
        {
            await ShowSpeechAsync(
                newlyPublished.Length == 1 ? "重要宏观数据已公布" : $"{newlyPublished.Length} 项高重要度数据已公布",
                FormatMacroReminderDetails(newlyPublished, includeValues: true),
                TimeSpan.FromSeconds(newlyPublished.Length == 1 ? 12 : 18));
        }
    }

    public void ShowMacroCalendar()
    {
        if (_macroCalendarWindow is { IsVisible: true })
        {
            _macroCalendarWindow.Activate();
            return;
        }

        var window = new MacroCalendarWindow(_economicCalendarEvents, _serverUtcOffsetSeconds);
        _macroCalendarWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_macroCalendarWindow, window))
            {
                _macroCalendarWindow = null;
            }
        };
        window.Show();
        window.Activate();
    }

    private async Task EnsureScopeLoadedAsync()
    {
        if (_account is null)
        {
            return;
        }

        var scope = $"{_account.Scope.AccountKey}|{_serverDate:yyyy-MM-dd}";
        if (_loadedScope == scope)
        {
            return;
        }

        _loadedScope = scope;
        _missingStopLossAlerted.Clear();
        if (!_persistenceAvailable)
        {
            await ResetScopeToLiveDataAsync();
            return;
        }
        _lastReviewUiAtUtc = default;
        _planItems.Clear();
        _structuredPlans.Clear();
        _reviewMetadata.Clear();
        _trades.Clear();
        _equitySamples.Clear();
        _cashFlows.Clear();
        _lastEquitySampleAtUtc = default;
        _lastDailyStatePersistedAtUtc = default;
        _excursions.Clear();
        _excursionPersistedAt.Clear();
        _lossZones.Clear();
        _lossZoneAttempts.Clear();
        foreach (var specification in await _database.LoadSymbolSpecificationsAsync(
                     _account.Scope.AccountKey, _cancellation.Token))
        {
            _symbolSpecifications[specification.Key] = specification.Value;
        }
        await EnsureLossZoneHistoryProjectionAsync();
        if (_deals.Count == 0)
        {
            foreach (var deal in await _database.LoadDealsAsync(_account.Scope.AccountKey, _cancellation.Token))
            {
                _deals[deal.Ticket] = deal;
            }
        }
        var storedDay = await _database.LoadTradingDayAsync(_account.Scope.AccountKey, _serverDate, _cancellation.Token);
        _settings = storedDay?.Settings ?? DailyPlanSettings.BalancedDefault;
        _dailyState = storedDay?.State;
        _equitySamples.AddRange(await _database.LoadEquitySamplesAsync(
            _account.Scope.AccountKey, _serverDate, _cancellation.Token));
        var (serverDayStartUtc, serverDayEndUtc) = ResolveServerDayUtcRange(_serverDate);
        foreach (var cashFlow in await _database.LoadAccountCashFlowsAsync(
                     _account.Scope.AccountKey, serverDayStartUtc, serverDayEndUtc, _cancellation.Token))
        {
            _cashFlows[cashFlow.Ticket] = cashFlow;
        }
        var storedBehaviorPolicies = await _database.LoadSettingAsync<BehaviorPolicySet>(
            $"account:{_account.Scope.AccountKey}", BehaviorSettingKey, _cancellation.Token);
        if (storedBehaviorPolicies is null)
        {
            var migratedBalanced = BehaviorPolicy.Balanced with
            {
                ConsecutiveLossThreshold = Math.Max(1, _settings.ConsecutiveLossThreshold),
                RapidReentrySeconds = Math.Max(1, _settings.RapidReentrySeconds),
                CooldownSeconds = Math.Max(1, _settings.RapidReentrySeconds),
                LotEscalationMultiplier = Math.Max(1m, _settings.LotEscalationMultiplier),
            };
            _behaviorPolicies = BehaviorPolicySet.CreateDefault(_account.Scope.AccountKey) with
            {
                Balanced = migratedBalanced,
            };
            await _database.SaveSettingAsync($"account:{_account.Scope.AccountKey}", BehaviorSettingKey, _behaviorPolicies, _cancellation.Token);
        }
        else
        {
            _behaviorPolicies = storedBehaviorPolicies;
        }
        var storedFloatingLossPolicy = await _database.LoadSettingAsync<FloatingLossAlertPolicy>(
            $"account:{_account.Scope.AccountKey}", FloatingLossSettingKey, _cancellation.Token);
        _floatingLossPolicy = (storedFloatingLossPolicy
            ?? await LoadFloatingLossFallbackAsync(_account.Scope.AccountKey)
            ?? FloatingLossAlertPolicy.BalancedDefault).Normalize();
        foreach (var plan in await _database.LoadStructuredTradePlansAsync(
                     _account.Scope.AccountKey, _serverDate.AddDays(-30), _serverDate, _cancellation.Token))
        {
            _structuredPlans[plan.Id] = plan;
        }
        foreach (var metadata in await _database.LoadTradeReviewMetadataAsync(_account.Scope.AccountKey, cancellationToken: _cancellation.Token))
        {
            _reviewMetadata[metadata.Key] = metadata.Value;
        }
        foreach (var trade in await _database.LoadTradesAsync(
                     _account.Scope.AccountKey, _serverDate.AddDays(-30), _serverDate, _cancellation.Token))
        {
            _trades[trade.PositionId] = trade;
        }
        var storedExcursions = await _database.LoadTradeExcursionsAsync(
            _account.Scope.AccountKey, _trades.Keys.ToArray(), _cancellation.Token);
        foreach (var excursion in storedExcursions)
        {
            _excursions[excursion.Key] = excursion.Value;
            _excursionPersistedAt[excursion.Key] = excursion.Value.LastSampleAtUtc;
        }
        foreach (var planItem in await _database.LoadPlanItemsAsync(_account.Scope.AccountKey, _serverDate, _cancellation.Token))
        {
            _planItems[planItem.ObjectKey] = planItem;
        }
        _lossZones.AddRange(await _database.LoadLossZonesAsync(_account.Scope.AccountKey, _serverDate, _cancellation.Token));
        _lossZoneAttempts.AddRange(await _database.LoadLossZoneAttemptsAsync(_lossZones.Select(item => item.Id).ToArray(), _cancellation.Token));
        await RebuildCurrentLossZoneProjectionAsync(updateUi: false);
        var timeline = await _database.LoadTimelineAsync(_account.Scope.AccountKey, _serverDate, cancellationToken: _cancellation.Token);

        await OnUiAsync(() =>
        {
            _viewModel.AccountLabel = $"交易账户 · ****{Math.Abs(_account.Scope.Login % 10000):0000}";
            _viewModel.ServerDateText = _serverDateAuthoritative ? _serverDate.ToString("yyyy-MM-dd") : $"{_serverDate:yyyy-MM-dd}（等待桥接插件）";
            _viewModel.DailyTarget = _settings.DailyTarget;
            _viewModel.DailyLoss = _settings.DailyLoss;
            _viewModel.MaximumTrades = _settings.MaximumTrades;
            _viewModel.MaximumLot = _settings.MaximumLot;
            _viewModel.StopLossReminderEnabled = _settings.StopLossReminderEnabled;
            _viewModel.StopLossReminderSeconds = _settings.StopLossReminderSeconds;
            _viewModel.GivebackValue = _settings.GivebackValue;
            _viewModel.BehaviorPresetText = _behaviorPolicies.SelectedPreset switch
            {
                BehaviorPreset.Conservative => "保守",
                BehaviorPreset.Loose => "宽松",
                _ => "平衡",
            };
            _viewModel.SelectedBehaviorPreset = _viewModel.BehaviorPresetText;
            _viewModel.SetBehaviorSettings(_behaviorPolicies.Selected);
            _viewModel.SetFloatingLossPolicy(_floatingLossPolicy);
            _viewModel.PlanItems.Clear();
            foreach (var item in _planItems.Values.OrderBy(item => item.UpdatedAtUtc))
            {
                _viewModel.PlanItems.Add(new PlanItemRowViewModel(item, SavePlanItemAsync));
            }
            _viewModel.StructuredPlans.Clear();
            foreach (var plan in _structuredPlans.Values.OrderByDescending(item => item.UpdatedAtUtc))
            {
                _viewModel.StructuredPlans.Add(ToStructuredPlanRow(plan));
            }
            _viewModel.Timeline.Clear();
            foreach (var item in timeline)
            {
                _viewModel.Timeline.Add(new TimelineRowViewModel(item));
            }
        });
        await UpdateLossZoneUiAsync();
        if (_dailyState is not null)
        {
            await UpdateDailyUiAsync(_dailyState);
        }
        await RefreshReviewUiAsync();
    }

    private async Task EnsureScopeLoadedForLiveAsync()
    {
        try
        {
            await EnsureScopeLoadedAsync();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _persistenceAvailable = false;
            if (_account is not null)
            {
                await ResetScopeToLiveDataAsync();
            }

            await ReportPersistenceFailureAsync("加载账户交易日数据", exception);
        }
    }

    private async Task ResetScopeToLiveDataAsync()
    {
        if (_account is null)
        {
            return;
        }

        _deals.Clear();
        _trades.Clear();
        _equitySamples.Clear();
        _cashFlows.Clear();
        _exactCloseServerDates.Clear();
        _excursions.Clear();
        _excursionPersistedAt.Clear();
        _planItems.Clear();
        _structuredPlans.Clear();
        _reviewMetadata.Clear();
        _lossZones.Clear();
        _lossZoneAttempts.Clear();
        _dailyState = null;
        _workerSession.ResetDealsBaseline();
        _settings = DailyPlanSettings.BalancedDefault;
        _behaviorPolicies = BehaviorPolicySet.CreateDefault(_account.Scope.AccountKey);
        _floatingLossPolicy = (await LoadFloatingLossFallbackAsync(_account.Scope.AccountKey)
            ?? FloatingLossAlertPolicy.BalancedDefault).Normalize();

        await OnUiAsync(() =>
        {
            _viewModel.AccountLabel = $"交易账户 · ****{Math.Abs(_account.Scope.Login % 10000):0000}";
            _viewModel.ServerDateText = _serverDateAuthoritative
                ? _serverDate.ToString("yyyy-MM-dd")
                : $"{_serverDate:yyyy-MM-dd}（等待桥接插件）";
            _viewModel.BehaviorPresetText = "平衡";
            _viewModel.SelectedBehaviorPreset = "平衡";
            _viewModel.SetBehaviorSettings(_behaviorPolicies.Selected);
            _viewModel.SetFloatingLossPolicy(_floatingLossPolicy);
            _viewModel.RealizedPnl = 0m;
            _viewModel.TradeCount = 0;
            _viewModel.WinCount = 0;
            _viewModel.LossCount = 0;
            _viewModel.ConsecutiveLosses = 0;
            _viewModel.HighWaterPnl = 0m;
            _viewModel.Giveback = 0m;
            _viewModel.PlanItems.Clear();
            _viewModel.StructuredPlans.Clear();
            _viewModel.LossZones.Clear();
            _viewModel.Timeline.Clear();
            _viewModel.ReviewSyncText = "本地记录异常，正在从交易终端重建本次运行数据";
        });
        if (_serverDateAuthoritative && _worker is not null)
        {
            await _worker.RefreshAsync(_serverUtcOffsetSeconds, _serverDate, _cancellation.Token);
        }
    }

    private async Task CreateOrUpdatePlanItemAsync(ChartObjectSnapshot chartObject, bool createIfMissing)
    {
        if (_account is null)
        {
            return;
        }

        if (!_planItems.TryGetValue(chartObject.ObjectKey, out var item) && !createIfMissing)
        {
            return;
        }

        var prices = chartObject.Anchors.Where(anchor => anchor.Price is not null).Select(anchor => anchor.Price!.Value).ToArray();
        decimal? low = prices.Length == 0 ? null : prices.Min();
        decimal? high = prices.Length == 0 ? null : prices.Max();
        item ??= new PlanItem(
            CreatePlanItemId(_account.Scope.AccountKey, _serverDate, chartObject.ObjectKey),
            _account.Scope.AccountKey,
            _serverDate,
            chartObject.ObjectKey,
            chartObject.Kind is ChartObjectKind.Text or ChartObjectKind.Label ? PlanCategory.Note : PlanCategory.Unclassified,
            chartObject.Symbol,
            low,
            high,
            chartObject.Text,
            !chartObject.IsDeleted,
            chartObject.CapturedAtUtc);
        item = item with
        {
            PriceLow = low,
            PriceHigh = high,
            Text = chartObject.Text,
            IsActive = !chartObject.IsDeleted,
            UpdatedAtUtc = chartObject.CapturedAtUtc,
        };
        _planItems[chartObject.ObjectKey] = item;
        await _database.UpsertPlanItemAsync(item, _cancellation.Token);
        await OnUiAsync(() =>
        {
            var existing = _viewModel.PlanItems.FirstOrDefault(row => row.ObjectKey == item.ObjectKey);
            if (existing is null)
            {
                _viewModel.PlanItems.Add(new PlanItemRowViewModel(item, SavePlanItemAsync));
            }
            else
            {
                existing.Update(item);
            }
        });
    }

    private Task SavePlanItemAsync(PlanItem item) =>
        WithStateGateAsync(() => SavePlanItemCoreAsync(item));

    private async Task SavePlanItemCoreAsync(PlanItem item)
    {
        _planItems[item.ObjectKey] = item;
        await TryPersistLiveAsync(
            () => _database.UpsertPlanItemAsync(item, _cancellation.Token),
            "保存计划对象分类");
    }

    private async Task ApplyAutomaticPlanMetadataAsync(IReadOnlyCollection<TradeRecord> trades)
    {
        foreach (var trade in trades)
        {
            if (_reviewMetadata.ContainsKey(trade.PositionId))
            {
                continue;
            }

            var metadata = _tradePlanMatcher.CreateAutomaticMetadata(
                trade,
                _structuredPlans.Values,
                _timeProvider.GetUtcNow());
            _reviewMetadata[trade.PositionId] = metadata;
            await TryPersistLiveAsync(
                () => _database.UpsertTradeReviewMetadataAsync(metadata, _cancellation.Token),
                "保存计划匹配");
        }
    }

    public Task CreateStructuredPlanAsync() => WithStateGateAsync(CreateStructuredPlanCoreAsync);

    private async Task CreateStructuredPlanCoreAsync()
    {
        if (_account is null)
        {
            await ShowSpeechAsync("还没连接账户。", "连接后再保存交易计划。", TimeSpan.FromSeconds(5));
            return;
        }

        if (!TryParsePlanDecimal(_viewModel.NewPlanReferenceEntry, out var reference) ||
            !TryParsePlanDecimal(_viewModel.NewPlanEntryLow, out var low) ||
            !TryParsePlanDecimal(_viewModel.NewPlanEntryHigh, out var high) ||
            !TryParsePlanDecimal(_viewModel.NewPlanStop, out var stop) ||
            !TryParsePlanDecimal(_viewModel.NewPlanTarget, out var target) ||
            string.IsNullOrWhiteSpace(_viewModel.NewPlanSymbol))
        {
            await ShowSpeechAsync("计划还没填完整。", "请填写品种、入场区、止损和目标。", TimeSpan.FromSeconds(5));
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var plan = new StructuredTradePlan(
            $"structured-{Guid.NewGuid():N}",
            _account.Scope.AccountKey,
            _serverDate,
            _viewModel.NewPlanSymbol.Trim(),
            _viewModel.NewPlanSide,
            reference,
            Math.Min(low, high),
            Math.Max(low, high),
            stop,
            target,
            _viewModel.NewPlanStrategy.Trim(),
            _viewModel.NewPlanSetup.Trim(),
            _viewModel.NewPlanTags.Split(['，', ',', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _viewModel.NewPlanNotes.Trim(),
            true,
            now,
            now);
        var validation = _tradePlanMatcher.Validate(plan);
        if (!validation.IsValid)
        {
            await ShowSpeechAsync("计划方向不成立。", validation.Error ?? "请检查入场、止损和目标。", TimeSpan.FromSeconds(6));
            return;
        }
        _structuredPlans[plan.Id] = plan;
        await _database.UpsertStructuredTradePlanAsync(plan, _cancellation.Token);
        await OnUiAsync(() => _viewModel.StructuredPlans.Insert(0, ToStructuredPlanRow(plan)));
        await ShowSpeechAsync("结构化计划已保存。", $"{plan.Symbol} {FormatSide(plan.Side)}，计划风险倍数 {plan.PlannedRiskMultiple:0.##}", TimeSpan.FromSeconds(5));
    }

    public Task ApplyBehaviorPresetAsync() => WithStateGateAsync(ApplyBehaviorPresetCoreAsync);

    private async Task ApplyBehaviorPresetCoreAsync()
    {
        if (_account is null || _behaviorPolicies is null)
        {
            return;
        }

        var selected = _viewModel.SelectedBehaviorPreset switch
        {
            "保守" => BehaviorPreset.Conservative,
            "宽松" => BehaviorPreset.Loose,
            _ => BehaviorPreset.Balanced,
        };
        _behaviorPolicies = _behaviorPolicies with { SelectedPreset = selected };
        await _database.SaveSettingAsync($"account:{_account.Scope.AccountKey}", BehaviorSettingKey, _behaviorPolicies, _cancellation.Token);
        await OnUiAsync(() =>
        {
            _viewModel.BehaviorPresetText = selected switch
            {
                BehaviorPreset.Conservative => "保守",
                BehaviorPreset.Loose => "宽松",
                _ => "平衡",
            };
            _viewModel.SetBehaviorSettings(_behaviorPolicies.Selected);
        });
        await RefreshReviewUiAsync(force: true);
        await UpdateTodayBehaviorUiAsync();
    }

    public Task SaveBehaviorSettingsAsync() => WithStateGateAsync(SaveBehaviorSettingsCoreAsync);

    private async Task SaveBehaviorSettingsCoreAsync()
    {
        if (_account is null || _behaviorPolicies is null)
        {
            return;
        }

        var policy = _behaviorPolicies.Selected;
        var values = _viewModel.BehaviorSettings.ToDictionary(item => item.Rule, item => item.Value);
        var enabled = _viewModel.BehaviorSettings.ToDictionary(item => item.Rule, item => item.Enabled);
        if (!TryParseInt(values, BehaviorRuleKind.ReentryCount, out var reentry) ||
            !TryParseInt(values, BehaviorRuleKind.LossZonePersistence, out var persistence) ||
            !TryParseInt(values, BehaviorRuleKind.CooldownViolation, out var cooldown) ||
            !TryParseDecimal(values, BehaviorRuleKind.RevengeScore, out var revenge) ||
            !TryParseDecimal(values, BehaviorRuleKind.OvertradeBurst, out var burst) ||
            !TryParseDecimal(values, BehaviorRuleKind.PlanDeviationRate, out var deviation) ||
            !TryParseDecimal(values, BehaviorRuleKind.ProfitGiveback, out var giveback) ||
            !TryParseDecimal(values, BehaviorRuleKind.SizeEscalationAfterLoss, out var size) ||
            !TryParseDecimal(values, BehaviorRuleKind.PriceFixationScore, out var fixation))
        {
            await ShowSpeechAsync("行为阈值没保存。", "请检查输入的数字。", TimeSpan.FromSeconds(4));
            return;
        }

        policy = policy with
        {
            ReentryThreshold = Math.Max(1, reentry),
            LossZonePersistenceThreshold = Math.Max(1, persistence),
            CooldownSeconds = Math.Max(1, cooldown),
            RapidReentrySeconds = Math.Max(1, cooldown),
            RevengeScoreThreshold = Math.Clamp(revenge, 0m, 100m),
            OvertradeBaselineMultiplier = Math.Max(1m, burst),
            PlanDeviationRateThreshold = Math.Clamp(deviation, 0m, 100m),
            ProfitGivebackThreshold = Math.Clamp(giveback, 0m, 100m),
            LotEscalationMultiplier = Math.Max(1m, size),
            PriceFixationScoreThreshold = Math.Clamp(fixation, 0m, 100m),
            EnabledRules = new BehaviorRuleSwitches(
                enabled.GetValueOrDefault(BehaviorRuleKind.ReentryCount, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.LossZonePersistence, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.RevengeScore, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.OvertradeBurst, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.PlanDeviationRate, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.ProfitGiveback, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.SizeEscalationAfterLoss, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.CooldownViolation, true),
                enabled.GetValueOrDefault(BehaviorRuleKind.PriceFixationScore, true)),
        };
        _behaviorPolicies = _behaviorPolicies with
        {
            Conservative = policy.Preset == BehaviorPreset.Conservative ? policy : _behaviorPolicies.Conservative,
            Balanced = policy.Preset == BehaviorPreset.Balanced ? policy : _behaviorPolicies.Balanced,
            Loose = policy.Preset == BehaviorPreset.Loose ? policy : _behaviorPolicies.Loose,
        };
        await _database.SaveSettingAsync($"account:{_account.Scope.AccountKey}", BehaviorSettingKey, _behaviorPolicies, _cancellation.Token);
        await RefreshReviewUiAsync(force: true);
        await UpdateTodayBehaviorUiAsync();
        await ShowSpeechAsync("行为阈值已保存。", string.Empty, TimeSpan.FromSeconds(4));
    }

    private static bool TryParseInt(IReadOnlyDictionary<BehaviorRuleKind, string> values, BehaviorRuleKind key, out int value)
    {
        value = default;
        return values.TryGetValue(key, out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
    }

    private static bool TryParseDecimal(IReadOnlyDictionary<BehaviorRuleKind, string> values, BehaviorRuleKind key, out decimal value)
    {
        value = default;
        if (!values.TryGetValue(key, out var text))
        {
            return false;
        }

        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
               decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParsePlanDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private StructuredTradePlanRow ToStructuredPlanRow(StructuredTradePlan plan) => new(
        plan.Id,
        plan.Symbol,
        FormatSide(plan.Side),
        $"{plan.EntryLow:0.#####} – {plan.EntryHigh:0.#####}",
        $"止损 {plan.StopPrice:0.#####} / 目标 {plan.TargetPrice:0.#####}",
        string.IsNullOrWhiteSpace(plan.Strategy) ? "未填写" : plan.Strategy,
        string.IsNullOrWhiteSpace(plan.Setup) ? "未填写" : plan.Setup,
        plan.Tags.Count == 0 ? "—" : string.Join("、", plan.Tags),
        plan.PlannedRiskMultiple?.ToString("0.##") ?? "暂无",
        plan.IsActive ? "启用" : "已停用",
        plan.IsActive ? "停用" : "复制为新版本",
        new AsyncRelayCommand(() => ToggleStructuredPlanAsync(plan.Id)),
        new AsyncRelayCommand(() => LoadPlanForRevisionAsync(plan.Id)));

    private Task ToggleStructuredPlanAsync(string planId) =>
        WithStateGateAsync(() => ToggleStructuredPlanCoreAsync(planId));

    private async Task ToggleStructuredPlanCoreAsync(string planId)
    {
        if (!_structuredPlans.TryGetValue(planId, out var plan))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var updated = plan.IsActive
            ? plan with { IsActive = false, UpdatedAtUtc = now }
            : plan with
            {
                Id = $"structured-{Guid.NewGuid():N}",
                IsActive = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
        _structuredPlans[planId] = updated;
        if (!plan.IsActive)
        {
            _structuredPlans[planId] = plan;
            _structuredPlans[updated.Id] = updated;
        }
        await TryPersistLiveAsync(
            () => _database.UpsertStructuredTradePlanAsync(updated, _cancellation.Token),
            "更新结构化计划状态");
        await OnUiAsync(() =>
        {
            _viewModel.StructuredPlans.Clear();
            foreach (var item in _structuredPlans.Values.OrderByDescending(item => item.UpdatedAtUtc))
            {
                _viewModel.StructuredPlans.Add(ToStructuredPlanRow(item));
            }
        });
    }

    private Task LoadPlanForRevisionAsync(string planId) =>
        WithStateGateAsync(() => LoadPlanForRevisionCoreAsync(planId));

    private async Task LoadPlanForRevisionCoreAsync(string planId)
    {
        if (!_structuredPlans.TryGetValue(planId, out var plan))
        {
            return;
        }

        await OnUiAsync(() =>
        {
            _viewModel.NewPlanSymbol = plan.Symbol;
            _viewModel.NewPlanSide = plan.Side;
            _viewModel.NewPlanReferenceEntry = plan.ReferenceEntryPrice?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            _viewModel.NewPlanEntryLow = plan.EntryLow?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            _viewModel.NewPlanEntryHigh = plan.EntryHigh?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            _viewModel.NewPlanStop = plan.StopPrice?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            _viewModel.NewPlanTarget = plan.TargetPrice?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            _viewModel.NewPlanStrategy = plan.Strategy;
            _viewModel.NewPlanSetup = plan.Setup;
            _viewModel.NewPlanTags = string.Join("、", plan.Tags);
            _viewModel.NewPlanNotes = plan.Notes;
        });
        await ShowSpeechAsync("计划已载入。", "修改后保存会建立新版本，旧交易仍绑定原计划。", TimeSpan.FromSeconds(6));
    }

    public Task RefreshReviewAsync() => WithMaintenanceOperationAsync(
        MaintenanceOperationKind.Read,
        () => WithStateGateOnlyAsync(() => RefreshReviewUiAsync(force: true)));

    private Task RefreshReviewDuringMaintenanceAsync() =>
        WithStateGateOnlyAsync(() => RefreshReviewUiAsync(force: true));

    private Task SaveReviewMetadataAsync(TradeReviewMetadata metadata) =>
        WithStateGateAsync(() => SaveReviewMetadataCoreAsync(metadata));

    private async Task SaveReviewMetadataCoreAsync(TradeReviewMetadata metadata)
    {
        if (_account is null || metadata.AccountKey != _account.Scope.AccountKey)
        {
            return;
        }

        _reviewMetadata[metadata.PositionId] = metadata;
        await TryPersistLiveAsync(
            () => _database.UpsertTradeReviewMetadataAsync(metadata, _cancellation.Token),
            "保存人工复盘归类");
        await RefreshReviewUiAsync(force: true);
        await UpdateTodayBehaviorUiAsync();
        await ShowSpeechAsync("这笔交易的复盘归类已保存。", string.Empty, TimeSpan.FromSeconds(4));
    }

    private async Task RefreshReviewUiAsync(bool force = false)
    {
        if (!_persistenceAvailable)
        {
            await ApplyInMemoryReviewFallbackAsync();
            return;
        }

        try
        {
            await RefreshReviewUiCoreAsync(force);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Application shutdown should not surface through an async WPF command.
        }
        catch (Exception exception)
        {
            await ApplyInMemoryReviewFallbackAsync(exception);
        }
    }

    private async Task RefreshReviewUiCoreAsync(bool force)
    {
        if (_account is null || _behaviorPolicies is null)
        {
            return;
        }

        if (!SupportsCompleteTradeProjection)
        {
            await OnUiAsync(() =>
                _viewModel.ClearReview("当前账户不是对冲模式，完整交易复盘已停用，避免生成错误统计。"));
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (!force && now - _lastReviewUiAtUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }
        _lastReviewUiAtUtc = now;

        var (from, to) = ResolveReviewRangeFromMemory();

        var side = _viewModel.SelectedReviewSideFilter switch
        {
            "买入" => TradeSide.Buy,
            "卖出" => TradeSide.Sell,
            _ => (TradeSide?)null,
        };
        var symbol = string.IsNullOrWhiteSpace(_viewModel.ReviewSymbolFilter)
            ? null
            : _viewModel.ReviewSymbolFilter.Trim();
        var accountKey = _account.Scope.AccountKey;
        var session = _accountSessions.Current;
        if (session is null || !string.Equals(session.AccountKey, accountKey, StringComparison.Ordinal))
        {
            return;
        }
        var generation = session.Generation;
        var filter = new ReviewFilter(accountKey, from, to, symbol, side,
            ServerUtcOffsetSeconds: _serverUtcOffsetSeconds);
        var trades = _trades.Values.ToArray();
        var metadata = new Dictionary<long, TradeReviewMetadata>(_reviewMetadata);
        var excursions = new Dictionary<long, TradeExcursion>(_excursions);
        var selection = _reviewQueryEngine.Select(filter, trades, metadata, excursions);
        var reviewZones = _lossZones.ToArray();
        var reviewAttempts = _lossZoneAttempts.ToArray();
        IReadOnlyDictionary<DateOnly, DailyState> dailyStates = _dailyState is null
            ? new Dictionary<DateOnly, DailyState>()
            : new Dictionary<DateOnly, DailyState> { [_dailyState.ServerDate] = _dailyState };
        var policy = _behaviorPolicies.Selected;
        var symbols = new Dictionary<string, SymbolSpecification>(_symbolSpecifications, StringComparer.OrdinalIgnoreCase);
        var snapshot = _reviewQueryEngine.Complete(
            selection,
            reviewZones,
            reviewAttempts,
            metadata,
            policy,
            dailyStates,
            now,
            symbols);
        await OnUiAsync(() =>
        {
            _viewModel.ReviewFromDateText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _viewModel.ReviewToDateText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _viewModel.ReviewRangeText = snapshot.Trades.Count == 0
                ? $"{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd} · 没有符合条件的完整交易"
                : $"{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd} · {snapshot.Trades.Count} 笔完整交易";
            _viewModel.ApplyReviewSnapshot(snapshot);
            _viewModel.ApplyReviewTrades(snapshot.Trades, metadata, SaveReviewMetadataAsync);
            _viewModel.ReviewWorkspace.StatusText = "正在读取复盘工作区快照…";
        });

        var review = _viewModel.ReviewWorkspace;
        var workspaceFilter = new ReviewWorkspaceFilter(
            accountKey,
            from,
            to,
            symbol,
            side,
            EmptyToNull(review.Strategy),
            EmptyToNull(review.Setup),
            SplitValues(review.Tags),
            review.TagMode == "全部" ? ReviewTagMatchMode.All : ReviewTagMatchMode.Any,
            ParseReviewStatus(review.StatusFilter),
            ParseAssessmentStatus(review.AssessmentFilter),
            EmptyToNull(review.CampaignFilter),
            Search: EmptyToNull(review.Search),
            Sort: ParseReviewSort(review.Sort),
            Page: review.Page,
            PageSize: 100,
            ServerUtcOffsetSeconds: _serverUtcOffsetSeconds,
            ComparisonMode: ParseReviewComparisonMode(review.ComparisonMode));
        var context = new ReviewQueryContext(Guid.NewGuid().ToString("N"), generation, accountKey);
        Volatile.Write(ref _latestReviewQueryId, context.QueryId);
        var task = RunReviewWorkspaceQueryAsync(workspaceFilter, context, force);
        Volatile.Write(ref _reviewRefreshTask, task);
    }

    private async Task RunReviewWorkspaceQueryAsync(
        ReviewWorkspaceFilter filter,
        ReviewQueryContext context,
        bool persistDrawdowns)
    {
        await Task.Yield();
        await using var operationLease = await _maintenance.EnterOperationAsync(
            MaintenanceOperationKind.Read, _cancellation.Token);
        if (!string.Equals(Volatile.Read(ref _latestReviewQueryId), context.QueryId, StringComparison.Ordinal))
        {
            return;
        }
        var session = _accountSessions.Current;
        if (session is null ||
            session.Generation != context.SessionGeneration ||
            !string.Equals(session.AccountKey, context.ExpectedAccountKey, StringComparison.Ordinal))
        {
            return;
        }
        var replacement = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellation.Token,
            session.CancellationToken);
        var previous = Interlocked.Exchange(ref _reviewQueryCancellation, replacement);
        previous?.Cancel();
        previous?.Dispose();
        var review = _viewModel.ReviewWorkspace;
        ReviewQueryResult result;
        try
        {
            result = await _workspaceQueryService.QueryAsync(
                context,
                filter,
                () => _accountSessions.Current?.Generation ?? -1,
                replacement.Token);
        }
        catch (OperationCanceledException) when (replacement.IsCancellationRequested && !_cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Review workspace query failed: {exception}");
            await OnUiAsync(() => review.StatusText = "复盘查询失败；实时监控继续运行。" );
            return;
        }
        if (persistDrawdowns)
        {
            foreach (var episode in ReviewAnalyticsCalculator.CalculateRealizedDrawdowns(result.Snapshot.Analytics.Trades))
            {
                await TryPersistLiveAsync(
                    () => _database.UpsertDrawdownEpisodeAsync(episode, replacement.Token),
                    "保存回撤区间");
            }
        }
        await _stateGate.WaitAsync(replacement.Token);
        try
        {
            if (!result.IsCurrentSession || !ReferenceEquals(replacement, _reviewQueryCancellation) ||
                !string.Equals(Volatile.Read(ref _latestReviewQueryId), context.QueryId, StringComparison.Ordinal) ||
                _account?.Scope.AccountKey != context.ExpectedAccountKey)
            {
                return;
            }
            _lastWorkspaceQuery = result;
            _activeImprovementGoals.Clear();
            _activeImprovementGoals.AddRange(result.Data.Goals.Where(item => item.Status == ImprovementGoalStatus.Active));
            await OnUiAsync(() =>
            {
                _viewModel.ReviewFromDateText = filter.FromServerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _viewModel.ReviewToDateText = filter.ToServerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _viewModel.ReviewRangeText = result.Snapshot.TotalCount == 0
                    ? $"{filter.FromServerDate:yyyy-MM-dd} 至 {filter.ToServerDate:yyyy-MM-dd} · 没有符合条件的完整交易"
                    : $"{filter.FromServerDate:yyyy-MM-dd} 至 {filter.ToServerDate:yyyy-MM-dd} · {result.Snapshot.TotalCount} 笔完整交易";
                _viewModel.ApplyReviewSnapshot(result.Snapshot.Analytics);
                _viewModel.ApplyReviewTrades(result.Snapshot.Trades, result.Data.Metadata, SaveReviewMetadataAsync);
                review.Apply(result.Snapshot, result.Data, result.Context.SessionGeneration);
            });
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static ReviewCompletionStatus? ParseReviewStatus(string value) => value switch
    {
        "待复盘" => ReviewCompletionStatus.Pending,
        "草稿" => ReviewCompletionStatus.Draft,
        "已复盘" => ReviewCompletionStatus.Reviewed,
        "需重审" => ReviewCompletionStatus.NeedsReview,
        _ => null,
    };

    private static RuleAssessmentStatus? ParseAssessmentStatus(string value) => value switch
    {
        "通过" => RuleAssessmentStatus.Passed,
        "未通过" => RuleAssessmentStatus.Failed,
        "未知" => RuleAssessmentStatus.Unknown,
        "不适用" => RuleAssessmentStatus.NotApplicable,
        _ => null,
    };

    private static ReviewSortOrder ParseReviewSort(string value) => value switch
    {
        "最早平仓" => ReviewSortOrder.ClosedAscending,
        "最大亏损" => ReviewSortOrder.LargestLoss,
        "最大回吐" => ReviewSortOrder.LargestGiveback,
        "行为优先" => ReviewSortOrder.BehaviorFirst,
        "最早待复盘" => ReviewSortOrder.OldestPending,
        _ => ReviewSortOrder.ClosedDescending,
    };

    private static ReviewComparisonMode ParseReviewComparisonMode(string value) => value switch
    {
        "合规与违规" => ReviewComparisonMode.Compliance,
        "买入与卖出" => ReviewComparisonMode.Side,
        _ => ReviewComparisonMode.PeriodHalves,
    };

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> SplitValues(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task OpenTradeReviewAsync(long positionId)
    {
        var accountKey = _account?.Scope.AccountKey;
        var session = _accountSessions.Current;
        if (string.IsNullOrWhiteSpace(accountKey) || session is null ||
            !string.Equals(session.AccountKey, accountKey, StringComparison.Ordinal))
        {
            return;
        }
        var review = _viewModel.ReviewWorkspace;
        if (review.TradeEditorKey is { } selectedKey &&
            selectedKey != new TradeKey(accountKey, positionId) && review.HasUnsavedReviewChanges)
        {
            await WithReviewWriteGateAsync(FlushDirtyTradeReviewAsync);
            if (review.HasUnsavedReviewChanges)
            {
                review.StatusText = $"草稿未能保存，仍停留在 #{selectedKey.PositionId}，请处理保存错误后再切换。";
                return;
            }
        }

        await using var operationLease = await EnterRuntimeOperationAsync(
            MaintenanceOperationKind.Read, _cancellation.Token);
        if (!_accountSessions.IsCurrent(accountKey, session.Generation) ||
            _account?.Scope.AccountKey != accountKey || !_persistenceAvailable)
        {
            return;
        }

        var key = new TradeKey(accountKey, positionId);
        var requestId = Guid.NewGuid().ToString("N");
        var requestContext = new TradeDetailRequestContext(requestId, session.Generation, key);
        Volatile.Write(ref _latestTradeDetailRequestId, requestId);
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellation.Token,
            session.CancellationToken);
        var previous = Interlocked.Exchange(ref _tradeDetailCancellation, requestCancellation);
        previous?.Cancel();
        try
        {
            var basis = ResolveTradeReviewBasis(key);
            if (basis is not null)
            {
                await _journalService.MarkNeedsReviewIfChangedAsync(
                    key, basis.SourceVersion, basis.RuleVersion,
                    requestCancellation.Token);
            }
            var detail = await _reviewRepository.LoadTradeDetailAsync(key, requestCancellation.Token);
            if (detail is null ||
                new TradeKey(detail.Trade.AccountKey, detail.Trade.PositionId) != key ||
                !IsCurrentTradeDetailRequest(requestContext,
                    new TradeKey(detail.Trade.AccountKey, detail.Trade.PositionId)))
            {
                return;
            }
            _lastTradeDetail = detail;
            var tagSuggestions = _reviewWorkspaceCalculator.BuildTagSuggestions(
                key, detail.Behaviors, detail.Metadata?.Tags ?? []);
            var workspace = _lastWorkspaceQuery is { } query &&
                            string.Equals(query.Context.ExpectedAccountKey, accountKey, StringComparison.Ordinal)
                ? query.Data
                : null;
            var fallbackPlaybook = workspace?.Playbooks
                .Where(item => item.IsActive && item.EffectiveFromUtc <= detail.Trade.OpenedAtUtc)
                .OrderByDescending(item => item.EffectiveFromUtc)
                .FirstOrDefault()
                ?? workspace?.Playbooks.Where(item => item.IsActive)
                    .OrderByDescending(item => item.EffectiveFromUtc).FirstOrDefault();
            await OnUiAsync(() =>
            {
                if (!IsCurrentTradeDetailRequest(requestContext,
                        new TradeKey(detail.Trade.AccountKey, detail.Trade.PositionId)))
                {
                    return;
                }
                _viewModel.ReviewWorkspace.ApplyDetail(
                    detail,
                    session.Generation,
                    fallbackPlaybook,
                    workspace?.Currency ?? _account?.Currency ?? string.Empty,
                    tagSuggestions);
            });
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _tradeDetailCancellation, null, requestCancellation);
            requestCancellation.Dispose();
        }
    }

    private async Task SaveTradeReviewDraftAsync(bool refresh)
    {
        var review = _viewModel.ReviewWorkspace;
        if (review.TradeEditorKey is not { } key || review.TradeEditorIdentity is not { } identity)
        {
            review.StatusText = "请先选择一笔交易。";
            return;
        }
        var basis = ResolveTradeReviewBasis(key)
            ?? new TradeReviewBasis("trade-v1:unknown", "assessment-v1:unknown");
        var submission = review.CaptureTradeReviewEdit(basis);
        if (submission is null)
        {
            review.StatusText = "当前交易编辑身份无效，请重新打开后再保存。";
            return;
        }

        var receipt = await _journalService.SaveTradeReviewAsync(submission, _cancellation.Token);
        var applied = false;
        await OnUiAsync(() =>
        {
            applied = review.ApplyTradeReviewSaveReceipt(receipt);
            if (applied)
            {
                review.StatusText = "草稿已保存。";
            }
            else if (review.IsCurrentTradeEdit(identity))
            {
                review.StatusText = receipt.IsSaved
                    ? "较早版本已保存，当前还有新修改等待保存。"
                    : receipt.Message;
            }
        });
        if (receipt.IsSaved && _lastTradeDetail is not null &&
            new TradeKey(_lastTradeDetail.Trade.AccountKey, _lastTradeDetail.Trade.PositionId) == key)
        {
            _lastTradeDetail = _lastTradeDetail with { Document = receipt.Value };
        }
        if (receipt.IsSaved && refresh &&
            _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await RefreshReviewAsync();
        }
    }

    private bool IsCurrentTradeDetailRequest(
        TradeDetailRequestContext request,
        TradeKey returnedTradeKey)
    {
        var currentSession = _accountSessions.Current;
        return request.CanApply(
            Volatile.Read(ref _latestTradeDetailRequestId),
            _account?.Scope.AccountKey,
            currentSession?.Generation ?? -1,
            returnedTradeKey) &&
            string.Equals(currentSession?.AccountKey, request.TradeKey.AccountKey, StringComparison.Ordinal);
    }

    private async Task FlushDirtyWorkspaceEditorsAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        review.CancelPendingReviewAutoSave();
        review.CancelPendingWorkspaceAutoSave();
        foreach (var kind in review.GetDirtyEditorKinds())
        {
            if (kind != EditEntityKind.TradeReview && !review.IsEditorDirty(kind))
            {
                continue;
            }
            switch (kind)
            {
                case EditEntityKind.TradeReview:
                    await SaveTradeReviewDraftAsync(refresh: false);
                    break;
                case EditEntityKind.DailyJournal:
                    await SaveDailyJournalAsync();
                    break;
                case EditEntityKind.PeriodReview:
                    await SavePeriodReviewAsync();
                    break;
                case EditEntityKind.RuleAssessment:
                    await SaveRuleAssessmentsAsync();
                    break;
                case EditEntityKind.Opportunity:
                    await SaveOpportunityAsync();
                    break;
                case EditEntityKind.ImprovementGoal:
                    await SaveImprovementGoalAsync();
                    break;
                case EditEntityKind.Annotation:
                    await SaveBehaviorReviewAsync();
                    break;
            }
        }
    }

    private async Task ReloadWorkspaceEditorAsync(EditEntityKind kind)
    {
        var review = _viewModel.ReviewWorkspace;
        var identity = review.GetEditorIdentity(kind);
        if (identity is null)
        {
            return;
        }
        switch (kind)
        {
            case EditEntityKind.DailyJournal when DateOnly.TryParseExact(
                identity.EntityId, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date):
                await OpenDailyReviewAsync(date);
                return;
            case EditEntityKind.RuleAssessment when long.TryParse(
                identity.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var positionId):
                await OpenTradeReviewAsync(positionId);
                return;
        }

        await RefreshReviewAsync();
        if (!_accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            return;
        }
        if (kind == EditEntityKind.ImprovementGoal)
        {
            var goal = _lastWorkspaceQuery?.Data.Goals.FirstOrDefault(item => item.Id == identity.EntityId);
            await OnUiAsync(() =>
            {
                if (goal is not null)
                {
                    review.ApplySavedGoal(goal);
                }
                else
                {
                    review.NewGoalCommand.Execute(null);
                }
            });
        }
        else if (kind == EditEntityKind.Opportunity)
        {
            var opportunity = _lastWorkspaceQuery?.Data.Opportunities.FirstOrDefault(item => item.Id == identity.EntityId);
            await OnUiAsync(() =>
            {
                if (opportunity is not null)
                {
                    review.ApplyOpportunityForReload(
                        opportunity,
                        _lastWorkspaceQuery?.Data.OpportunityAttachments ?? []);
                }
                else
                {
                    review.NewOpportunityCommand.Execute(null);
                }
            });
        }
    }

    private static EditSaveReceipt<TValue> ToEditReceipt<TContent, TValue>(
        EditSnapshot<TContent> snapshot,
        ReviewSaveResult<TValue> result)
    {
        var status = result.Status switch
        {
            ReviewSaveStatus.Saved => EditSaveStatus.Saved,
            ReviewSaveStatus.ValidationFailed => EditSaveStatus.ValidationFailed,
            ReviewSaveStatus.Conflict => EditSaveStatus.Conflict,
            ReviewSaveStatus.NotFound => EditSaveStatus.NotFound,
            _ => EditSaveStatus.StorageFailed,
        };
        return new EditSaveReceipt<TValue>(
            snapshot.Identity,
            snapshot.ContentSequence,
            status,
            result.Value,
            result.Message);
    }

    private async Task<bool> FlushDirtyTradeReviewAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        if (!review.HasUnsavedReviewChanges)
        {
            return true;
        }
        review.CancelPendingReviewAutoSave();
        await SaveTradeReviewDraftAsync(refresh: false);
        return !review.HasUnsavedReviewChanges;
    }

    private async Task MarkTradeReviewedAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        if (review.TradeEditorKey is not { } key || review.TradeEditorIdentity is not { } identity)
        {
            review.StatusText = "请先选择一笔交易。";
            return;
        }
        if (!await FlushDirtyTradeReviewAsync())
        {
            return;
        }
        var contentSequence = review.TradeEditSequence;
        var expectedRevision = review.DocumentRevision;
        var basis = ResolveTradeReviewBasis(key)
            ?? new TradeReviewBasis("trade-v1:unknown", "assessment-v1:unknown");
        var result = await _journalService.MarkReviewedAsync(
            key,
            basis.SourceVersion, basis.RuleVersion,
            expectedRevision, _cancellation.Token);
        var applied = false;
        await OnUiAsync(() =>
        {
            if (!review.IsCurrentTradeEdit(identity, contentSequence))
            {
                return;
            }
            applied = true;
            review.StatusText = result.IsSaved ? "复盘已完成并冻结当前数据与规则版本。" : result.Message;
            if (result.IsSaved)
            {
                review.DocumentRevision = result.Value!.Revision;
                review.DocumentStatus = "已复盘";
            }
        });
        if (result.IsSaved && applied)
        {
            if (_lastTradeDetail is not null &&
                new TradeKey(_lastTradeDetail.Trade.AccountKey, _lastTradeDetail.Trade.PositionId) == key)
            {
                _lastTradeDetail = _lastTradeDetail with { Document = result.Value };
            }
            if (_accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
            {
                await RefreshReviewAsync();
            }
        }
    }

    private TradeReviewBasis? ResolveTradeReviewBasis(TradeKey key)
    {
        var data = _lastWorkspaceQuery?.Data;
        var trade = data?.Trades.FirstOrDefault(item =>
            item.AccountKey == key.AccountKey && item.PositionId == key.PositionId);
        if (trade is not null)
        {
            data!.Excursions.TryGetValue(key.PositionId, out var excursion);
            return _reviewWorkspaceCalculator.BuildTradeReviewBasis(
                trade, data.Deals, data.Assessments, excursion);
        }
        var detail = _lastTradeDetail;
        if (detail is not null && detail.Trade.AccountKey == key.AccountKey &&
            detail.Trade.PositionId == key.PositionId)
        {
            return _reviewWorkspaceCalculator.BuildTradeReviewBasis(
                detail.Trade, detail.Deals, detail.Assessments, detail.Excursion);
        }
        return null;
    }

    private async Task SaveRuleAssessmentsAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var identity = review.GetEditorIdentity(EditEntityKind.RuleAssessment);
        if (identity is null ||
            !long.TryParse(identity.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var positionId) ||
            review.RuleAssessments.Count == 0)
        {
            review.StatusText = "当前交易没有可评价的策略规则。";
            return;
        }
        if (!await FlushDirtyTradeReviewAsync())
        {
            return;
        }
        var key = new TradeKey(identity.AccountKey, positionId);
        var now = _timeProvider.GetUtcNow();
        var assessments = review.RuleAssessments.Select(item => new TradeRuleAssessment(
            key, item.PlaybookVersionId, item.RuleId, item.Status, ReviewEvidenceSource.UserBackfill,
            $"manual:{now:O}", item.Notes, item.Revision + 1, now)).ToArray();
        var snapshot = review.CaptureWorkspaceEdit(EditEntityKind.RuleAssessment, assessments);
        if (snapshot is null)
        {
            return;
        }
        var result = await _database.SaveRuleAssessmentsAsync(key, snapshot.Content, _cancellation.Token);
        var receipt = ToEditReceipt<TradeRuleAssessment[], IReadOnlyList<TradeRuleAssessment>>(snapshot, result);
        var acknowledged = false;
        await OnUiAsync(() =>
        {
            if (review.GetEditorIdentity(EditEntityKind.RuleAssessment) != identity)
            {
                return;
            }
            acknowledged = review.ApplyWorkspaceSaveReceipt(EditEntityKind.RuleAssessment, receipt);
            review.StatusText = acknowledged ? "执行评价已保存。" : review.EditorSaveStatus;
        });
        if (acknowledged &&
            _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await OpenTradeReviewAsync(key.PositionId);
            await RefreshReviewAsync();
        }
    }

    private async Task ApplyTradeTagSuggestionAsync(string tag, bool accept)
    {
        var review = _viewModel.ReviewWorkspace;
        if (review.TradeEditorKey is not { } key || review.TradeEditorIdentity is not { } identity)
        {
            review.StatusText = "请先选择一笔交易。";
            return;
        }
        if (!await FlushDirtyTradeReviewAsync())
        {
            return;
        }
        var result = await _reviewTagSuggestionService.ApplyAsync(key, tag, accept, _cancellation.Token);
        await OnUiAsync(() =>
        {
            if (review.IsCurrentTradeEdit(identity))
            {
                review.StatusText = result.IsSaved
                    ? accept ? $"已采用候选标签“{tag}”。" : $"已撤销候选标签“{tag}”。"
                    : result.Message;
            }
        });
        if (!result.IsSaved || !review.IsCurrentTradeEdit(identity) ||
            !_accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            return;
        }
        var metadata = await _database.LoadTradeReviewMetadataAsync(
            key.AccountKey, [key.PositionId], _cancellation.Token);
        if (metadata.TryGetValue(key.PositionId, out var saved))
        {
            _reviewMetadata[key.PositionId] = saved;
        }
        await OpenTradeReviewAsync(key.PositionId);
        await RefreshReviewAsync();
    }

    private async Task ImportReviewAttachmentAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        if (review.TradeEditorKey is not { } key || review.TradeEditorIdentity is not { } identity)
        {
            review.StatusText = "请先选择一笔交易。";
            return;
        }
        if (!await FlushDirtyTradeReviewAsync())
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入复盘附件",
            Filter = "复盘附件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.pdf;*.txt;*.md;*.csv",
            Multiselect = false,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var now = _timeProvider.GetUtcNow();
        var stamp = new ReviewEvidenceStamp(
            ReviewEvidenceSource.UserBackfill, now, now, ReviewTimeBasis.Local, "manual-attachment-v1");
        var attachment = await _reviewAttachmentStore.ImportAsync(
            key.AccountKey, "trade", key.PositionId.ToString(CultureInfo.InvariantCulture),
            dialog.FileName,
            string.IsNullOrWhiteSpace(review.AttachmentTitle)
                ? Path.GetFileNameWithoutExtension(dialog.FileName)
                : review.AttachmentTitle,
            stamp, _cancellation.Token, review.AttachmentEventReference);
        if (review.IsCurrentTradeEdit(identity) &&
            _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await OnUiAsync(() => review.StatusText = $"附件“{attachment.Title}”已导入受控目录。" );
            await OpenTradeReviewAsync(key.PositionId);
        }
    }

    private async Task PasteReviewAttachmentAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        if (review.TradeEditorKey is not { } key || review.TradeEditorIdentity is not { } identity)
        {
            review.StatusText = "请先选择一笔交易。";
            return;
        }
        if (!await FlushDirtyTradeReviewAsync())
        {
            return;
        }
        if (!System.Windows.Clipboard.ContainsImage())
        {
            review.StatusText = "剪贴板中没有可粘贴的图片。";
            return;
        }
        var image = System.Windows.Clipboard.GetImage();
        if (image is null)
        {
            review.StatusText = "无法读取剪贴板图片。";
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"TradePet-clipboard-{Guid.NewGuid():N}.png");
        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }
            var now = _timeProvider.GetUtcNow();
            var stamp = new ReviewEvidenceStamp(
                ReviewEvidenceSource.UserBackfill, now, now, ReviewTimeBasis.Local, "clipboard-image-v1");
            var attachment = await _reviewAttachmentStore.ImportAsync(
                key.AccountKey, "trade", key.PositionId.ToString(CultureInfo.InvariantCulture), temp,
                string.IsNullOrWhiteSpace(review.AttachmentTitle) ? "剪贴板截图" : review.AttachmentTitle,
                stamp, _cancellation.Token, review.AttachmentEventReference);
            if (review.IsCurrentTradeEdit(identity) &&
                _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
            {
                await OnUiAsync(() => review.StatusText = $"剪贴板图片“{attachment.Title}”已导入受控目录。" );
                await OpenTradeReviewAsync(key.PositionId);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string? ResolveReviewAttachmentPath(ReviewAttachment attachment)
    {
        if (_account?.Scope.AccountKey != attachment.AccountKey)
        {
            return null;
        }
        try
        {
            var path = _reviewAttachmentStore.ResolvePath(attachment);
            return File.Exists(path) ? path : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private Task OpenReviewAttachmentAsync(ReviewAttachment attachment)
    {
        var path = ResolveReviewAttachmentPath(attachment);
        if (path is null)
        {
            _viewModel.ReviewWorkspace.StatusText = "附件不存在或不属于当前账户。";
            return Task.CompletedTask;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task DeleteReviewAttachmentAsync(ReviewAttachment attachment)
    {
        var review = _viewModel.ReviewWorkspace;
        var accountKey = _account?.Scope.AccountKey;
        var isCurrentOwner = attachment.OwnerKind switch
        {
            "trade" => review.SelectedPositionId?.ToString(CultureInfo.InvariantCulture) == attachment.OwnerId,
            "opportunity" => review.OpportunityId == attachment.OwnerId,
            _ => false,
        };
        if (accountKey is null || attachment.AccountKey != accountKey || !isCurrentOwner)
        {
            review.StatusText = "附件链接已变化，请刷新后重试。";
            return;
        }
        if (attachment.OwnerKind == "trade" && !await FlushDirtyTradeReviewAsync())
        {
            return;
        }
        var answer = System.Windows.MessageBox.Show(
            $"删除当前记录中的附件“{attachment.Title}”？同一文件的其他记录链接会保留。",
            "删除复盘附件", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }
        await _reviewAttachmentStore.DeleteAsync(
            attachment.Id, accountKey, attachment.OwnerKind, attachment.OwnerId, _cancellation.Token);
        review.StatusText = "附件链接已删除。";
        if (attachment.OwnerKind == "trade" && long.TryParse(attachment.OwnerId, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var positionId))
        {
            await OpenTradeReviewAsync(positionId);
        }
        else
        {
            await RefreshReviewAsync();
        }
    }

    private async Task SaveDailyJournalAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var identity = review.GetEditorIdentity(EditEntityKind.DailyJournal);
        if (identity is null || !DateOnly.TryParseExact(identity.EntityId, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            review.StatusText = "请先打开一个有效日期的日记。";
            return;
        }
        DailyJournal? current = null;
        if (_lastWorkspaceQuery?.Context.ExpectedAccountKey == identity.AccountKey)
        {
            _lastWorkspaceQuery.Data.DailyJournals.TryGetValue(date, out current);
        }
        var journal = BuildDailyJournal(identity.AccountKey, date, current, ReviewCompletionStatus.Draft);
        var snapshot = review.CaptureWorkspaceEdit(EditEntityKind.DailyJournal, journal);
        if (snapshot is null)
        {
            return;
        }
        var result = await _journalService.SaveDailyJournalAsync(
            snapshot.Content, snapshot.Content.Revision, _cancellation.Token);
        var receipt = ToEditReceipt(snapshot, result);
        var acknowledged = false;
        await OnUiAsync(() =>
        {
            if (review.GetEditorIdentity(EditEntityKind.DailyJournal) != identity)
            {
                return;
            }
            acknowledged = review.ApplyWorkspaceSaveReceipt(
                EditEntityKind.DailyJournal,
                receipt,
                saved =>
                {
                    review.DailyRevision = saved.Revision;
                    review.DailyStatus = "草稿";
                });
            review.StatusText = acknowledged ? $"{date:yyyy-MM-dd} 日记已保存。" : review.EditorSaveStatus;
        });
        if (acknowledged && _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await RefreshReviewAsync();
        }
    }

    private async Task CompleteDailyJournalAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var identity = review.GetEditorIdentity(EditEntityKind.DailyJournal);
        if (identity is null || !DateOnly.TryParseExact(identity.EntityId, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            review.StatusText = "请先打开一个有效日期的日记。";
            return;
        }

        DailyJournal? current = null;
        if (_lastWorkspaceQuery?.Context.ExpectedAccountKey == identity.AccountKey)
        {
            _lastWorkspaceQuery.Data.DailyJournals.TryGetValue(date, out current);
        }
        var journal = BuildDailyJournal(identity.AccountKey, date, current, ReviewCompletionStatus.Reviewed);
        var snapshot = review.CaptureWorkspaceEdit(EditEntityKind.DailyJournal, journal);
        if (snapshot is null)
        {
            return;
        }
        var dailySourceVersion = _lastWorkspaceQuery?.Context.ExpectedAccountKey == identity.AccountKey
            ? _lastWorkspaceQuery.Snapshot.DailyFacts?.GetValueOrDefault(date)?.SourceVersion
            : null;
        dailySourceVersion ??= string.Empty;
        var result = await _journalService.CompleteDailyJournalAsync(
            snapshot.Content, snapshot.Content.Revision, dailySourceVersion, _cancellation.Token);
        var receipt = ToEditReceipt(snapshot, result);
        var acknowledged = false;
        await OnUiAsync(() =>
        {
            if (review.GetEditorIdentity(EditEntityKind.DailyJournal) != identity)
            {
                return;
            }
            acknowledged = review.ApplyWorkspaceSaveReceipt(
                EditEntityKind.DailyJournal,
                receipt,
                saved =>
                {
                    review.DailyRevision = saved.Revision;
                    review.DailyStatus = $"已完成 · {saved.ReviewedAtUtc:yyyy-MM-dd HH:mm}";
                });
            review.StatusText = acknowledged
                ? $"{date:yyyy-MM-dd} 日总结已完成并冻结当前数据版本。"
                : review.EditorSaveStatus;
        });
        if (acknowledged && _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await RefreshReviewAsync();
        }
    }

    private async Task OpenDailyReviewAsync(DateOnly date)
    {
        var query = _lastWorkspaceQuery;
        var accountKey = _account?.Scope.AccountKey;
        if (query is null || accountKey is null || query.Data.AccountKey != accountKey)
        {
            return;
        }

        query.Data.DailyJournals.TryGetValue(date, out var journal);
        var facts = query.Snapshot.DailyFacts?.GetValueOrDefault(date);
        var staleResult = journal is null
            ? null
            : await _journalService.MarkDailyNeedsReviewIfChangedAsync(
                journal, facts?.SourceVersion ?? string.Empty, _cancellation.Token);
        if (staleResult is { IsSaved: true })
        {
            journal = staleResult.Value;
        }
        await OnUiAsync(() =>
        {
            reviewApply();
            if (staleResult is { IsSaved: true })
            {
                _viewModel.ReviewWorkspace.StatusText = $"{date:yyyy-MM-dd} 的成交或费用已更新，日总结需要重审。";
            }
            else if (staleResult is { IsSaved: false })
            {
                _viewModel.ReviewWorkspace.StatusText = staleResult.Message;
            }
        });
        if (staleResult is { IsSaved: true })
        {
            await RefreshReviewAsync();
        }

        void reviewApply() => _viewModel.ReviewWorkspace.ApplyDaily(
            date, journal, facts, query.Data.Version.SourceVersion.ToString(CultureInfo.InvariantCulture));
    }

    private DailyJournal BuildDailyJournal(
        string accountKey,
        DateOnly date,
        DailyJournal? current,
        ReviewCompletionStatus status)
    {
        var review = _viewModel.ReviewWorkspace;
        var now = _timeProvider.GetUtcNow();
        var sourceVersion = _lastWorkspaceQuery?.Context.ExpectedAccountKey == accountKey
            ? _lastWorkspaceQuery.Snapshot.DailyFacts?.GetValueOrDefault(date)?.SourceVersion ?? "0"
            : "0";
        return new DailyJournal(
            accountKey, date, review.PreMarketPlan, review.IntradayNotes, review.PostMarketSummary,
            review.DailyDidWell, review.DailyToImprove, review.DailyNextAction, status,
            review.DailyRevision,
            sourceVersion,
            current?.CreatedAtUtc ?? now, now, current?.ReviewedAtUtc,
            current?.PreMarketRecordedAtUtc ?? (string.IsNullOrWhiteSpace(review.PreMarketPlan) ? null : now),
            current?.IntradayRecordedAtUtc ?? (string.IsNullOrWhiteSpace(review.IntradayNotes) ? null : now),
            current?.PostMarketRecordedAtUtc ?? (string.IsNullOrWhiteSpace(review.PostMarketSummary) ? null : now),
            current?.ReviewedSourceVersion);
    }

    private async Task SavePlaybookVersionAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var accountKey = _account?.Scope.AccountKey;
        if (accountKey is null)
        {
            return;
        }
        var parsedRules = new List<PlaybookRule>();
        var order = 0;
        foreach (var line in review.PlaybookRules.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                review.StatusText = "策略规则格式应为：分区|规则名|说明|关键/普通。";
                return;
            }
            var section = parts[0] switch
            {
                "入场" or "Entry" => PlaybookRuleSection.Entry,
                "风险" or "Risk" => PlaybookRuleSection.Risk,
                "管理" or "Management" => PlaybookRuleSection.Management,
                "退出" or "Exit" => PlaybookRuleSection.Exit,
                _ => (PlaybookRuleSection?)null,
            };
            if (section is null)
            {
                review.StatusText = $"未知规则分区：{parts[0]}。";
                return;
            }
            parsedRules.Add(new PlaybookRule(
                $"rule-{Guid.NewGuid():N}", section.Value, parts[1], parts.Length > 2 ? parts[2] : string.Empty,
                parts.Length > 3 && parts[3].Equals("关键", StringComparison.OrdinalIgnoreCase), order++));
        }
        var existing = _lastWorkspaceQuery?.Data.Playbooks
            .Where(item => item.Name.Equals(review.PlaybookName.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Version).FirstOrDefault();
        var playbookId = existing?.PlaybookId ?? $"playbook-{Guid.NewGuid():N}";
        var version = (existing?.Version ?? 0) + 1;
        var now = _timeProvider.GetUtcNow();
        var playbook = new PlaybookVersion(
            $"{playbookId}:v{version}", playbookId, accountKey, version, review.PlaybookName,
            review.PlaybookSymbols, review.PlaybookConditions, review.PlaybookInvalidWhen, parsedRules,
            now, now, true);
        var result = await _playbookService.SaveVersionAsync(playbook, _cancellation.Token);
        await OnUiAsync(() => review.StatusText = result.IsSaved ? $"策略“{playbook.Name}”v{version} 已保存。" : result.Message);
        if (result.IsSaved)
        {
            await RefreshReviewAsync();
        }
    }

    private async Task SaveCampaignAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var accountKey = _account?.Scope.AccountKey;
        if (accountKey is null)
        {
            return;
        }
        var ids = SplitValues(review.CampaignMembers)
            .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null)
            .Where(value => value is not null).Select(value => value!.Value).Distinct().ToArray();
        if (ids.Length == 0)
        {
            review.StatusText = "交易想法至少需要一个有效 position ID。";
            return;
        }
        var data = _lastWorkspaceQuery?.Data;
        var symbol = data?.Trades.FirstOrDefault(item => item.PositionId == ids[0])?.Symbol
            ?? _lastTradeDetail?.Trade.Symbol ?? string.Empty;
        var current = _lastTradeDetail?.Campaign;
        var now = _timeProvider.GetUtcNow();
        var campaign = new TradeCampaign(
            current?.Id ?? $"campaign-{Guid.NewGuid():N}", accountKey, symbol, review.CampaignName,
            review.CampaignThesis, ids, current?.Revision ?? 0, current?.CreatedAtUtc ?? now, now);
        var result = await _campaignService.SaveAsync(campaign, current?.Revision ?? 0, _cancellation.Token);
        await OnUiAsync(() => review.StatusText = result.IsSaved ? "交易想法分组已保存。" : result.Message);
        if (result.IsSaved)
        {
            await RefreshReviewAsync();
        }
    }

    private async Task SaveImprovementGoalAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var identity = review.GetEditorIdentity(EditEntityKind.ImprovementGoal);
        if (identity is null)
        {
            review.StatusText = "请先新建或选择一个改进目标。";
            return;
        }
        BehaviorRuleKind? rule = Enum.TryParse<BehaviorRuleKind>(review.GoalRule, out var parsedRule) ? parsedRule : null;
        decimal? target = decimal.TryParse(review.GoalTarget, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsedTarget)
            ? parsedTarget : null;
        if (!int.TryParse(review.GoalObservationWindow, NumberStyles.Integer, CultureInfo.CurrentCulture, out var observationWindow) ||
            observationWindow <= 0)
        {
            review.StatusText = "观察窗口必须是至少 1 天的整数。";
            return;
        }
        var query = _lastWorkspaceQuery;
        var existing = query?.Context.ExpectedAccountKey != identity.AccountKey
            ? null
            : query.Data.Goals.FirstOrDefault(item => item.Id == identity.EntityId);
        var now = _timeProvider.GetUtcNow();
        var ruleVersion = query?.Context.ExpectedAccountKey == identity.AccountKey
            ? query.Data.Version.RuleVersion
            : string.Empty;
        var goal = new ImprovementGoal(
            existing is null ? identity.EntityId : $"goal-{Guid.NewGuid():N}", identity.AccountKey, review.GoalName, rule,
            ruleVersion, _serverDate, null, target, null,
            review.GoalMeasurement, review.GoalSymbols, review.GoalNotificationEnabled,
            ImprovementGoalStatus.Active, 0, now, now,
            ObservationWindowDays: observationWindow, EndCondition: review.GoalEndCondition);
        var snapshot = review.CaptureWorkspaceEdit(EditEntityKind.ImprovementGoal, goal);
        if (snapshot is null)
        {
            return;
        }
        var result = existing is null
            ? await _improvementService.SaveAsync(snapshot.Content, 0, _cancellation.Token)
            : existing.Status != ImprovementGoalStatus.Active
                ? ReviewSaveResult<ImprovementGoal>.Validation("历史目标只读；请新建目标或选择活动版本。")
                : await _improvementService.CreateVersionAsync(
                    existing, snapshot.Content, query?.Data.GoalObservations ?? [], _cancellation.Token);
        var receipt = ToEditReceipt(snapshot, result);
        var acknowledged = false;
        await OnUiAsync(() =>
        {
            if (review.GetEditorIdentity(EditEntityKind.ImprovementGoal) != identity)
            {
                return;
            }
            acknowledged = review.ApplyWorkspaceSaveReceipt(
                EditEntityKind.ImprovementGoal,
                receipt,
                review.ApplySavedGoal);
            review.StatusText = acknowledged
                ? existing is null
                    ? "改进目标已保存，并冻结当前规则版本、观察窗口和基线状态。"
                    : $"目标新版本 v{result.Value!.Version} 已保存；旧版本已归档，历史观察保留。"
                : review.EditorSaveStatus;
        });
        if (acknowledged && _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await RefreshReviewAsync();
        }
    }

    private async Task ArchiveImprovementGoalAsync(ImprovementGoal goal)
    {
        var review = _viewModel.ReviewWorkspace;
        if (_account?.Scope.AccountKey != goal.AccountKey)
        {
            review.StatusText = "目标所属账户已变化，请刷新后重试。";
            return;
        }
        var result = await _improvementService.ArchiveAsync(goal, _serverDate, _cancellation.Token);
        await OnUiAsync(() => review.StatusText = result.IsSaved
            ? "改进目标已归档，历史观察已保留，桌宠提醒已停止。"
            : result.Message);
        if (result.IsSaved)
        {
            await RefreshReviewAsync();
        }
    }

    private async Task SaveBehaviorReviewAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var identity = review.GetEditorIdentity(EditEntityKind.Annotation);
        if (identity is null)
        {
            review.BehaviorReviewStatus = "请先选择一条行为事件。";
            return;
        }

        var snapshot = review.CaptureWorkspaceEdit(
            EditEntityKind.Annotation,
            new BehaviorReviewEditCommand(
                identity.AccountKey,
                identity.EntityId,
                review.BehaviorExplanation,
                review.BehaviorEvidenceInsufficient,
                review.BehaviorRevision));
        if (snapshot is null)
        {
            return;
        }
        var result = await _behaviorReviewService.SaveReviewAsync(
            snapshot.Content.AccountKey,
            snapshot.Content.OccurrenceId,
            snapshot.Content.Explanation,
            snapshot.Content.EvidenceInsufficient,
            snapshot.Content.ExpectedRevision,
            _cancellation.Token);
        var receipt = ToEditReceipt(snapshot, result);
        var acknowledged = false;
        await OnUiAsync(() =>
        {
            if (review.GetEditorIdentity(EditEntityKind.Annotation) != identity)
            {
                return;
            }
            acknowledged = review.ApplyWorkspaceSaveReceipt(
                EditEntityKind.Annotation,
                receipt,
                review.ApplySavedBehaviorReview);
            review.BehaviorReviewStatus = acknowledged
                ? "人工解释已保存；原始规则事实、时间、角色和提醒处置保持不变。"
                : review.EditorSaveStatus;
        });
        if (acknowledged && _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await RefreshReviewAsync();
        }
    }

    private async Task SaveOpportunityAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var identity = review.GetEditorIdentity(EditEntityKind.Opportunity);
        if (identity is null)
        {
            review.StatusText = "请先新建或选择一条机会记录。";
            return;
        }
        var kind = review.OpportunityKind switch
        {
            "事后发现" => OpportunityRecordKind.DiscoveredAfterMove,
            "主动放弃" => OpportunityRecordKind.DeliberatelySkipped,
            _ => OpportunityRecordKind.ObservedBeforeMove,
        };
        var now = _timeProvider.GetUtcNow();
        var opportunitySide = review.OpportunitySide switch
        {
            "买入" => TradeSide.Buy,
            "卖出" => TradeSide.Sell,
            _ => (TradeSide?)null,
        };
        decimal? entry = decimal.TryParse(review.OpportunityEntry, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsedEntry) ? parsedEntry : null;
        decimal? stop = decimal.TryParse(review.OpportunityStop, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsedStop) ? parsedStop : null;
        decimal? target = decimal.TryParse(review.OpportunityTarget, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsedTarget) ? parsedTarget : null;
        var existing = _lastWorkspaceQuery?.Context.ExpectedAccountKey != identity.AccountKey
            ? null
            : _lastWorkspaceQuery.Data.Opportunities.FirstOrDefault(item => item.Id == identity.EntityId);
        var opportunity = new OpportunityRecord(
            existing?.Id ?? identity.EntityId, identity.AccountKey, kind,
            existing?.ObservedAtUtc ?? now, now, existing?.ServerDate ?? _serverDate,
            review.OpportunitySymbol, opportunitySide, EmptyToNull(review.OpportunityPlaybook), entry, stop, target, review.OpportunityReason,
            review.OpportunityNotes, EmptyToNull(review.OpportunityLinkedTrade), review.OpportunityRevision,
            review.OpportunityConditions);
        var snapshot = review.CaptureWorkspaceEdit(EditEntityKind.Opportunity, opportunity);
        if (snapshot is null)
        {
            return;
        }
        var result = await _opportunityService.SaveAsync(
            snapshot.Content, snapshot.Content.Revision, _cancellation.Token);
        var receipt = ToEditReceipt(snapshot, result);
        var acknowledged = false;
        await OnUiAsync(() =>
        {
            if (review.GetEditorIdentity(EditEntityKind.Opportunity) != identity)
            {
                return;
            }
            acknowledged = review.ApplyWorkspaceSaveReceipt(
                EditEntityKind.Opportunity,
                receipt,
                review.ApplySavedOpportunity);
            review.StatusText = acknowledged
                ? "机会记录已保存；不会进入真实交易绩效分母。"
                : review.EditorSaveStatus;
        });
        if (acknowledged && _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await RefreshReviewAsync();
        }
    }

    private async Task ApplyBulkReviewEditAsync(ReviewBulkEditKind kind, string value)
    {
        var review = _viewModel.ReviewWorkspace;
        var query = _lastWorkspaceQuery;
        var accountKey = _account?.Scope.AccountKey;
        var positionIds = review.SelectedTradeIds;
        if (query is null || accountKey is null || query.Context.ExpectedAccountKey != accountKey || positionIds.Count == 0)
        {
            review.StatusText = "请先在当前复盘页勾选要批量修改的交易。";
            return;
        }
        var requestValue = kind == ReviewBulkEditKind.Status
            ? value switch
            {
                "待复盘" => ReviewCompletionStatus.Pending.ToString(),
                "草稿" => ReviewCompletionStatus.Draft.ToString(),
                "已复盘" => ReviewCompletionStatus.Reviewed.ToString(),
                "需重审" => ReviewCompletionStatus.NeedsReview.ToString(),
                _ => string.Empty,
            }
            : value;
        var action = kind switch
        {
            ReviewBulkEditKind.Strategy => $"把策略设为“{value.Trim()}”",
            ReviewBulkEditKind.Tags => $"把标签替换为“{value.Trim()}”",
            _ => $"把复盘状态设为“{value}”",
        };
        var answer = System.Windows.MessageBox.Show(
            $"将对当前账户已选择的 {positionIds.Count} 笔交易{action}。\n任一交易校验或修订冲突时整批不写入。",
            "确认批量复盘操作", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            review.StatusText = "已取消批量操作。";
            return;
        }

        var result = await _bulkEditService.ApplyAsync(
            new ReviewBulkEditRequest(accountKey, positionIds, kind, requestValue), _cancellation.Token);
        if (_account?.Scope.AccountKey != accountKey)
        {
            return;
        }
        await OnUiAsync(() => review.StatusText = result.IsSaved
            ? $"已完成 {result.Value!.AffectedCount} 笔批量修改。"
            : result.Message);
        if (result.IsSaved)
        {
            if (kind is ReviewBulkEditKind.Strategy or ReviewBulkEditKind.Tags)
            {
                var metadata = await _database.LoadTradeReviewMetadataAsync(accountKey, positionIds, _cancellation.Token);
                await _stateGate.WaitAsync(_cancellation.Token);
                try
                {
                    if (_account?.Scope.AccountKey == accountKey)
                    {
                        foreach (var item in metadata)
                        {
                            _reviewMetadata[item.Key] = item.Value;
                        }
                    }
                }
                finally
                {
                    _stateGate.Release();
                }
            }
            await RefreshReviewAsync();
        }
    }

    private async Task ImportOpportunityAttachmentAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var accountKey = _account?.Scope.AccountKey;
        if (accountKey is null || string.IsNullOrWhiteSpace(review.OpportunityId))
        {
            review.StatusText = "请先保存或选择一条机会记录。";
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入机会截图或证据",
            Filter = "机会附件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.pdf;*.txt;*.md;*.csv",
            Multiselect = false,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var now = _timeProvider.GetUtcNow();
        var stamp = new ReviewEvidenceStamp(
            ReviewEvidenceSource.UserBackfill, now, now, ReviewTimeBasis.Local, "manual-opportunity-attachment-v1");
        var attachment = await _reviewAttachmentStore.ImportAsync(
            accountKey, "opportunity", review.OpportunityId, dialog.FileName,
            Path.GetFileNameWithoutExtension(dialog.FileName), stamp, _cancellation.Token);
        await OnUiAsync(() => review.StatusText = $"机会证据“{attachment.Title}”已导入受控目录。" );
        await RefreshReviewAsync();
    }

    private async Task SavePeriodReviewAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var query = _lastWorkspaceQuery;
        var identity = review.GetEditorIdentity(EditEntityKind.PeriodReview);
        if (query is null || identity is null || query.Context.ExpectedAccountKey != identity.AccountKey)
        {
            review.StatusText = "请先查询当前账户的复盘周期。";
            return;
        }
        var existing = query.Data.PeriodReviews?.FirstOrDefault(item =>
            item.FromServerDate == query.Snapshot.Filter.FromServerDate &&
            item.ToServerDate == query.Snapshot.Filter.ToServerDate);
        var now = _timeProvider.GetUtcNow();
        var period = new PeriodReview(
            existing?.Id ?? identity.EntityId, identity.AccountKey,
            query.Snapshot.Filter.FromServerDate, query.Snapshot.Filter.ToServerDate,
            review.PeriodFacts, review.PeriodDidWell, review.PeriodToImprove, review.PeriodNextAction,
            query.Snapshot.PeriodFacts?.Trades ??
                query.Snapshot.Trades.Select(item => new TradeKey(item.AccountKey, item.PositionId)).ToArray(),
            existing?.Revision ?? review.PeriodRevision,
            query.Snapshot.Version.Token, existing?.CreatedAtUtc ?? now, now,
            query.Snapshot.PeriodFacts?.BehaviorOccurrenceIds);
        var snapshot = review.CaptureWorkspaceEdit(EditEntityKind.PeriodReview, period);
        if (snapshot is null)
        {
            return;
        }
        var expectedRevision = existing?.Revision ?? snapshot.Content.Revision;
        var result = await _journalService.SavePeriodReviewAsync(
            snapshot.Content, expectedRevision, _cancellation.Token);
        var receipt = ToEditReceipt(snapshot, result);
        var acknowledged = false;
        await OnUiAsync(() =>
        {
            if (review.GetEditorIdentity(EditEntityKind.PeriodReview) != identity)
            {
                return;
            }
            acknowledged = review.ApplyWorkspaceSaveReceipt(
                EditEntityKind.PeriodReview,
                receipt,
                saved =>
                {
                    review.PeriodRevision = saved.Revision;
                    review.PeriodReviewId = saved.Id;
                });
            review.StatusText = acknowledged ? "周期总结已保存，并保留当前样本引用。" : review.EditorSaveStatus;
        });
        if (acknowledged && _accountSessions.IsCurrent(identity.AccountKey, identity.SessionGeneration))
        {
            await RefreshReviewAsync();
        }
    }

    private async Task SaveReviewFilterAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var query = _lastWorkspaceQuery;
        var accountKey = _account?.Scope.AccountKey;
        if (query is null || accountKey is null || query.Context.ExpectedAccountKey != accountKey)
        {
            review.StatusText = "请先应用一次筛选。";
            return;
        }
        var current = query.Data.SavedFilters?.FirstOrDefault(item =>
            item.Name.Equals(review.FilterName.Trim(), StringComparison.OrdinalIgnoreCase));
        var now = _timeProvider.GetUtcNow();
        var filter = new ReviewSavedFilter(
            current?.Id ?? $"filter-{Guid.NewGuid():N}", accountKey, review.FilterName,
            JsonSerializer.Serialize(query.Snapshot.Filter with { Page = 1 }, ProtocolJson.Options),
            current?.Revision ?? 0, current?.CreatedAtUtc ?? now, now);
        var result = await _reviewFilterService.SaveAsync(filter, current?.Revision ?? 0, _cancellation.Token);
        await OnUiAsync(() => review.StatusText = result.IsSaved ? $"筛选“{result.Value!.Name}”已保存。" : result.Message);
        if (result.IsSaved)
        {
            await RefreshReviewAsync();
        }
    }

    private async Task SaveTradingSessionAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var query = _lastWorkspaceQuery;
        var accountKey = _account?.Scope.AccountKey;
        if (query is null || accountKey is null || query.Context.ExpectedAccountKey != accountKey)
        {
            review.StatusText = "请先查询当前账户的复盘数据。";
            return;
        }
        if (!TimeOnly.TryParse(review.TradingSessionStart, CultureInfo.CurrentCulture, out var start) ||
            !TimeOnly.TryParse(review.TradingSessionEnd, CultureInfo.CurrentCulture, out var end))
        {
            review.StatusText = "交易时段时间格式应为 HH:mm。";
            return;
        }
        if (!TryParseTradingSessionDays(review.TradingSessionDays, out var startDays))
        {
            review.StatusText = "交易日请填写每天，或用顿号/逗号分隔周一至周日。";
            return;
        }
        if (!int.TryParse(review.TradingSessionOrder, NumberStyles.Integer, CultureInfo.CurrentCulture, out var sortOrder))
        {
            review.StatusText = "时段顺序必须是整数；重叠时按较小顺序优先归类。";
            return;
        }
        var existing = (query.Data.TradingSessions ?? []).FirstOrDefault(item => item.Id == review.TradingSessionId);
        var now = _timeProvider.GetUtcNow();
        var session = new TradingSessionDefinition(
            existing?.Id ?? $"session-{Guid.NewGuid():N}",
            accountKey,
            review.TradingSessionName,
            review.TradingSessionTimeZone,
            start,
            end,
            startDays,
            sortOrder,
            review.TradingSessionActive,
            existing?.Revision ?? review.TradingSessionRevision,
            existing?.CreatedAtUtc ?? now,
            now);
        var expectedRevision = existing?.Revision ?? review.TradingSessionRevision;
        var result = await _tradingSessionService.SaveAsync(session, expectedRevision, _cancellation.Token);
        await OnUiAsync(() =>
        {
            review.StatusText = result.IsSaved
                ? $"交易时段“{result.Value!.Name}”已保存，分析将按具名时区重新归类。"
                : result.Message;
            if (result.IsSaved)
            {
                review.ApplyTradingSessionSave(result.Value!);
            }
        });
        if (result.IsSaved)
        {
            await RefreshReviewAsync();
        }
    }

    private static bool TryParseTradingSessionDays(string text, out IReadOnlyList<DayOfWeek> days)
    {
        days = [];
        var normalized = text.Trim();
        if (normalized.Length == 0 || normalized.Equals("每天", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var map = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = DayOfWeek.Monday, ["周一"] = DayOfWeek.Monday, ["星期一"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
            ["2"] = DayOfWeek.Tuesday, ["周二"] = DayOfWeek.Tuesday, ["星期二"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
            ["3"] = DayOfWeek.Wednesday, ["周三"] = DayOfWeek.Wednesday, ["星期三"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
            ["4"] = DayOfWeek.Thursday, ["周四"] = DayOfWeek.Thursday, ["星期四"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
            ["5"] = DayOfWeek.Friday, ["周五"] = DayOfWeek.Friday, ["星期五"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
            ["6"] = DayOfWeek.Saturday, ["周六"] = DayOfWeek.Saturday, ["星期六"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
            ["7"] = DayOfWeek.Sunday, ["周日"] = DayOfWeek.Sunday, ["周天"] = DayOfWeek.Sunday, ["星期日"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
        };
        var result = new List<DayOfWeek>();
        foreach (var token in normalized.Split(['、', ',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!map.TryGetValue(token, out var day))
            {
                return false;
            }
            result.Add(day);
        }
        days = result.Distinct().Order().ToArray();
        return days.Count > 0;
    }

    private async Task ApplySavedReviewFilterAsync(ReviewSavedFilter saved)
    {
        var accountKey = _account?.Scope.AccountKey;
        if (accountKey is null || saved.AccountKey != accountKey)
        {
            return;
        }
        var filter = JsonSerializer.Deserialize<ReviewWorkspaceFilter>(saved.FilterJson, ProtocolJson.Options)
            ?? throw new InvalidDataException("保存的筛选内容无效。");
        if (filter.AccountKey != accountKey)
        {
            throw new InvalidDataException("保存的筛选属于其他账户。");
        }
        await OnUiAsync(() =>
        {
            _viewModel.SelectedReviewPeriod = "自定义";
            _viewModel.ReviewFromDateText = filter.FromServerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _viewModel.ReviewToDateText = filter.ToServerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _viewModel.ReviewSymbolFilter = filter.Symbol ?? string.Empty;
            _viewModel.SelectedReviewSideFilter = filter.Side switch { TradeSide.Buy => "买入", TradeSide.Sell => "卖出", _ => "全部" };
            var review = _viewModel.ReviewWorkspace;
            review.Strategy = filter.Strategy ?? string.Empty;
            review.Setup = filter.Setup ?? string.Empty;
            review.Tags = string.Join("、", filter.Tags ?? []);
            review.TagMode = filter.TagMatchMode == ReviewTagMatchMode.All ? "全部" : "任一";
            review.StatusFilter = filter.Status switch
            {
                ReviewCompletionStatus.Pending => "待复盘",
                ReviewCompletionStatus.Draft => "草稿",
                ReviewCompletionStatus.Reviewed => "已复盘",
                ReviewCompletionStatus.NeedsReview => "需重审",
                _ => "全部",
            };
            review.AssessmentFilter = filter.Assessment switch
            {
                RuleAssessmentStatus.Passed => "通过",
                RuleAssessmentStatus.Failed => "未通过",
                RuleAssessmentStatus.Unknown => "未知",
                RuleAssessmentStatus.NotApplicable => "不适用",
                _ => "全部",
            };
            review.CampaignFilter = filter.CampaignId ?? string.Empty;
            review.Search = filter.Search ?? string.Empty;
            review.Sort = filter.Sort switch
            {
                ReviewSortOrder.ClosedAscending => "最早平仓",
                ReviewSortOrder.LargestLoss => "最大亏损",
                ReviewSortOrder.LargestGiveback => "最大回吐",
                ReviewSortOrder.BehaviorFirst => "行为优先",
                ReviewSortOrder.OldestPending => "最早待复盘",
                _ => "最新平仓",
            };
            review.ComparisonMode = filter.ComparisonMode switch
            {
                ReviewComparisonMode.Compliance => "合规与违规",
                ReviewComparisonMode.Side => "买入与卖出",
                _ => "周期前后",
            };
            review.Page = 1;
        });
        await RefreshReviewAsync();
    }

    private async Task LoadTradeReplayAsync()
    {
        await using var operationLease = await EnterRuntimeOperationAsync(
            MaintenanceOperationKind.Read, _cancellation.Token);
        var review = _viewModel.ReviewWorkspace;
        var detail = _lastTradeDetail;
        var accountKey = _account?.Scope.AccountKey;
        if (detail is null || accountKey is null || _terminal is null || detail.Trade.AccountKey != accountKey)
        {
            review.ReplayStatus = "请先选择当前账户的一笔交易。";
            return;
        }
        var paths = RuntimePaths.Resolve();
        if (!File.Exists(paths.HistoryWorkerScript))
        {
            review.ReplayStatus = "历史行情脚本缺失，请重新构建或安装应用。";
            return;
        }
        review.ReplayStatus = "正在从指定 MT5 终端读取历史行情…";
        var from = detail.Trade.OpenedAtUtc.AddHours(-1);
        var to = (detail.Trade.ClosedAtUtc ?? detail.Trade.OpenedAtUtc.AddHours(4)).AddHours(1);
        var precision = review.ReplayPrecision == "Tick" ? MarketDataPrecision.Ticks : MarketDataPrecision.Bars;
        var request = new MarketHistoryRequest(
            Guid.NewGuid().ToString("N"), _terminal.TerminalId, accountKey, detail.Trade.Symbol, "M5",
            from, to, precision);
        var client = new Mt5HistoryClient(new Mt5HistoryOptions(
            paths.PythonExecutable, paths.HistoryWorkerScript, _terminal.TerminalPath, _terminal.TerminalId));
        var service = new TradeReplayService(_database, client);
        var history = await service.LoadAsync(request, _cancellation.Token);
        _lastReplayHistory = history;
        var cursor = history.Range.ActualFromUtc ?? from;
        var notes = detail.Document is null
            ? Array.Empty<ReviewEvidenceStamp>()
            : [new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, detail.Trade.OpenedAtUtc,
                detail.Document.UpdatedAtUtc, ReviewTimeBasis.Local, detail.Document.SourceVersion)];
        var frame = service.CreateFrame(cursor, history, detail, notes, review.ShowFullReplayReview);
        await OnUiAsync(() => review.ApplyReplay(history, frame, 0d));
    }

    private async Task SeekTradeReplayAsync(double progress)
    {
        var review = _viewModel.ReviewWorkspace;
        var history = _lastReplayHistory;
        var detail = _lastTradeDetail;
        if (history is null || detail is null || detail.Trade.AccountKey != _account?.Scope.AccountKey)
        {
            return;
        }
        var start = history.Range.ActualFromUtc ?? history.Range.RequestedFromUtc;
        var end = history.Range.RequestedToUtc;
        var cursor = start + TimeSpan.FromTicks((long)((end - start).Ticks * Math.Clamp(progress, 0d, 100d) / 100d));
        var notes = detail.Document is null
            ? Array.Empty<ReviewEvidenceStamp>()
            : [new ReviewEvidenceStamp(ReviewEvidenceSource.UserBackfill, detail.Trade.OpenedAtUtc,
                detail.Document.UpdatedAtUtc, ReviewTimeBasis.Local, detail.Document.SourceVersion)];
        var frame = _reviewWorkspaceCalculator.BuildReplayFrame(
            cursor, history.Range, history.Bars, history.Ticks, detail.Deals, detail.PnlSamples,
            detail.Behaviors, notes, detail.Trade, detail.Plan, detail.Document,
            review.ShowFullReplayReview);
        await OnUiAsync(() => review.ApplyReplay(history, frame, progress));
    }

    private async Task ClearReviewMarketDataCacheAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var accountKey = _account?.Scope.AccountKey;
        if (accountKey is null)
        {
            review.CacheStatus = "尚未连接账户，无法确定缓存作用域。";
            return;
        }
        if (!_persistenceAvailable)
        {
            review.CacheStatus = "本地数据库不可写，缓存没有清理。";
            return;
        }

        try
        {
            review.CacheStatus = "正在清理当前账户可重建的 bars、ticks 与请求范围…";
            var result = await _reviewCacheService.ClearMarketDataAsync(accountKey, _cancellation.Token);
            _lastReplayHistory = null;
            await OnUiAsync(() => review.CacheStatus =
                $"已清理 {result.RangeCount} 个范围、{result.BarCount} 根 K 线、{result.TickCount} 条 tick；人工复盘与附件未改变。");
            await RefreshReviewAsync();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            review.CacheStatus = "行情缓存清理失败，现有数据保持不变。";
            await ReportPersistenceFailureAsync("清理复盘行情缓存", exception);
        }
    }

    private async Task ExportReviewAsync()
    {
        await using var operationLease = await EnterRuntimeOperationAsync(
            MaintenanceOperationKind.Read, _cancellation.Token);
        var review = _viewModel.ReviewWorkspace;
        review.ClearExportPreview();
        _preparedReviewExport = null;
        var query = _lastWorkspaceQuery;
        if (query is null || !query.IsCurrentSession || _account?.Scope.AccountKey != query.Context.ExpectedAccountKey)
        {
            review.ExportStatus = "请先查询当前账户的复盘数据。";
            return;
        }
        var scope = review.ExportScope == "当前页选中交易"
            ? ReviewExportScope.SelectedTrades : ReviewExportScope.AllFiltered;
        var selectedIds = review.SelectedTradeIds.ToHashSet();
        var exportTrades = (scope == ReviewExportScope.AllFiltered
                ? query.Snapshot.AllFilteredTrades ?? query.Snapshot.Trades
                : query.Snapshot.Trades.Where(trade => selectedIds.Contains(trade.PositionId)).ToArray())
            .ToArray();
        if (scope == ReviewExportScope.SelectedTrades && exportTrades.Length == 0)
        {
            review.ExportStatus = "当前页没有勾选交易；请先在交易档案页选择。";
            return;
        }
        review.ExportStatus = "正在冻结所选范围并读取详情…";
        var exportKeys = exportTrades
            .Select(trade => new TradeKey(trade.AccountKey, trade.PositionId))
            .ToArray();
        var loadedDetails = await _reviewRepository.LoadTradeDetailsAsync(exportKeys, _cancellation.Token);
        var detailsByKey = loadedDetails.ToDictionary(
            item => new TradeKey(item.Trade.AccountKey, item.Trade.PositionId));
        if (detailsByKey.Count != exportKeys.Distinct().Count() ||
            loadedDetails.Any(item => item.Version.Token != query.Snapshot.Version.Token))
        {
            review.ExportStatus = "导出期间数据已变化或交易详情缺失；请刷新后重新导出，未生成混合版本包。";
            return;
        }
        var detailData = exportKeys.Select(key => detailsByKey[key]).ToArray();
        var opportunityIds = query.Data.Opportunities.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var availableAttachments = detailData.SelectMany(item => item.Attachments)
            .Concat(scope == ReviewExportScope.AllFiltered
                ? (query.Data.OpportunityAttachments ?? []).Where(item => opportunityIds.Contains(item.OwnerId))
                : [])
            .DistinctBy(item => item.Id).ToArray();
        var frozenSnapshot = query.Snapshot with
        {
            AllFilteredTrades = exportTrades,
            TotalCount = exportTrades.Length,
        };
        _preparedReviewExport = new PreparedReviewExport(query, frozenSnapshot, detailData, availableAttachments, scope);
        review.ExportAttachmentOptions.ReplaceWith(availableAttachments.Select(item =>
            new ReviewExportAttachmentOption(item.Id, item.Title, item.FileName, item.SizeBytes)));
        var opportunityCount = scope == ReviewExportScope.AllFiltered ? query.Data.Opportunities.Count : 0;
        review.ExportPreview =
            $"范围：{frozenSnapshot.Filter.FromServerDate:yyyy-MM-dd} 至 {frozenSnapshot.Filter.ToServerDate:yyyy-MM-dd}；" +
            $"交易 {exportTrades.Length} 笔，净盈亏 {exportTrades.Sum(item => item.NetPnl):0.##} {query.Data.Currency}；" +
            $"机会 {opportunityCount} 条；可选择原始附件 {availableAttachments.Length} 件（默认 0 件）。";
        var textSamples = detailData.Select(item => item.Document?.Summary)
            .Concat(scope == ReviewExportScope.AllFiltered
                ? query.Data.Opportunities.Select(item => item.Notes)
                : [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(3)
            .Select(item => ReviewExportService.PreviewPublicText(item!, frozenSnapshot.Filter.AccountKey))
            .Select(item => item.Length > 100 ? item[..100] + "…" : item)
            .ToArray();
        if (textSamples.Length > 0)
        {
            review.ExportPreview += "\n脱敏文本示例（最多 3 条）：\n" + string.Join("\n", textSamples);
        }
        review.CanConfirmExport = true;
        review.ExportStatus = "预览已生成。检查范围及附件勾选后，再确认写出。";
    }

    private async Task ConfirmReviewExportAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var prepared = _preparedReviewExport;
        if (prepared is null || !review.CanConfirmExport || !ReferenceEquals(_lastWorkspaceQuery, prepared.Query) ||
            _account?.Scope.AccountKey != prepared.Snapshot.Filter.AccountKey)
        {
            review.ClearExportPreview();
            review.ExportStatus = "预览已过期，请重新生成。";
            return;
        }
        var selectedOptions = review.ExportAttachmentOptions.Where(item => item.IsIncluded)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var selectedIds = selectedOptions.Keys.ToHashSet(StringComparer.Ordinal);
        var selectedAttachments = prepared.AvailableAttachments
            .Where(item => selectedIds.Contains(item.Id))
            .Select(item => item with { Title = selectedOptions[item.Id].PublicNote.Trim() })
            .ToArray();
        var confirmation = System.Windows.MessageBox.Show(
            $"{review.ExportPreview}\n\n本次包含原始附件 {selectedAttachments.Length} 件。" +
            "公开包会隐藏内部 ID、账户和已识别路径，但自由文本及主动选入的图片可能仍含私人信息，请自行检查。确认写出？",
            "确认公开分享范围", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            review.ExportStatus = "已取消写出；预览和附件选择仍保留。";
            return;
        }
        try
        {
            review.ExportStatus = "正在校验所选附件并写入公开分享包…";
            await using var operationLease = await _maintenance.EnterOperationAsync(
                MaintenanceOperationKind.File, _cancellation.Token);
            var latestVersion = await _reviewRepository.LoadReviewDataVersionAsync(
                prepared.Snapshot.Filter.AccountKey, _cancellation.Token);
            if (latestVersion.Token != prepared.Snapshot.Version.Token ||
                !ReferenceEquals(_lastWorkspaceQuery, prepared.Query))
            {
                review.ClearExportPreview();
                _preparedReviewExport = null;
                review.ExportStatus = "预览后数据已变化，请刷新并重新生成预览。";
                return;
            }
            var detailSnapshots = prepared.Details.Select(detail => new TradeDetailSnapshot(
                detail.Trade, detail.Deals, detail.Metadata, detail.Document, detail.Excursion,
                detail.PnlSamples, detail.Plan, detail.Playbook, detail.Assessments, detail.Behaviors,
                detail.Attachments, detail.Campaign, detail.Version.Token)).ToArray();
            var package = _reviewExportService.Build(prepared.Snapshot, detailSnapshots, prepared.Query.Data,
                ReviewExportMode.PublicShare, prepared.Scope, selectedAttachments);
            var entries = package.Entries.ToList();
            for (var index = 0; index < selectedAttachments.Length; index++)
            {
                var attachment = selectedAttachments[index];
                entries.Add(new ReviewExportEntry(
                    ReviewExportService.PublicAttachmentPath(index + 1, attachment),
                    attachment.MediaType, [], _reviewAttachmentStore.ResolvePath(attachment),
                    attachment.Sha256, attachment.SizeBytes));
            }
            var destination = await _reviewPackageWriter.WriteAsync(
                package with { Entries = entries }, cancellationToken: _cancellation.Token);
            review.ClearExportPreview();
            _preparedReviewExport = null;
            await OnUiAsync(() => review.ExportStatus = $"公开分享包已写出：{destination}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            review.ExportStatus = $"公开分享导出失败，未写出新包：{exception.Message}";
            await ReportPersistenceFailureAsync("导出公开分享包", exception);
        }
    }

    private sealed record PreparedReviewExport(
        ReviewQueryResult Query,
        ReviewWorkspaceSnapshot Snapshot,
        IReadOnlyList<TradeDetailData> Details,
        IReadOnlyList<ReviewAttachment> AvailableAttachments,
        ReviewExportScope Scope);

    private async Task BackupReviewAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        review.ExportStatus = "正在创建 SQLite 一致快照与附件备份…";
        await using var operationLease = await _maintenance.EnterOperationAsync(
            MaintenanceOperationKind.File,
            _cancellation.Token);
        await _reviewSaveGate.WaitAsync(_cancellation.Token);
        try
        {
            var destination = await _reviewBackupService.CreateAsync(cancellationToken: _cancellation.Token);
            await OnUiAsync(() => review.ExportStatus = $"完整备份已创建并写入哈希清单：{destination}" );
        }
        finally
        {
            _reviewSaveGate.Release();
        }
    }

    private async Task RestoreReviewAsync()
    {
        var review = _viewModel.ReviewWorkspace;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 TradePet 完整备份",
            Filter = "TradePet 备份|TradePet-backup-*.zip|ZIP 文件|*.zip",
            Multiselect = false,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            review.ExportStatus = "正在验证备份路径、哈希、数据库和账户身份…";
            var manifest = await _reviewBackupService.ValidateAsync(dialog.FileName, _cancellation.Token);
            var answer = System.Windows.MessageBox.Show(
                $"备份已通过校验，包含 {manifest.AccountKeys.Count} 个账户。\n\n恢复会替换当前本地数据，并先自动创建一份当前数据的完整备份。是否继续？",
                "恢复 TradePet 备份",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                review.ExportStatus = "已取消恢复，当前数据没有变化。";
                return;
            }

            await using var maintenanceLease = await _maintenance.EnterMaintenanceAsync(_cancellation.Token);
            await _reviewSaveGate.WaitAsync(_cancellation.Token);
            try
            {
                await _stateGate.WaitAsync(_cancellation.Token);
                try
                {
                    _reviewQueryCancellation?.Cancel();
                    _tradeDetailCancellation?.Cancel();
                    Volatile.Write(ref _latestTradeDetailRequestId, null);
                    var result = await _reviewBackupService.RestoreAsync(
                        dialog.FileName, cancellationToken: _cancellation.Token,
                        preserveUnreadableCurrent: _databaseRecoveryRequired);
                    if (_account is not null)
                    {
                        await _accountSessions.SwitchAsync(_account.Scope.AccountKey, _cancellation.Token);
                    }
                    _lastWorkspaceQuery = null;
                    _lastTradeDetail = null;
                    _lastReplayHistory = null;
                    _activeImprovementGoals.Clear();
                    _loadedScope = null;
                    _persistenceAvailable = true;
                    _databaseRecoveryRequired = false;
                    await OnUiAsync(() => review.ResetAccountState(
                        "备份已恢复，正在重新载入当前账户。",
                        preserveTradeDraft: false));
                    if (_account is not null)
                    {
                        await EnsureScopeLoadedForLiveAsync();
                    }
                    await OnUiAsync(() => review.ExportStatus =
                        $"恢复完成；切换前备份：{result.SafetyBackupPath}" );
                }
                finally
                {
                    _stateGate.Release();
                }
            }
            finally
            {
                _reviewSaveGate.Release();
            }
            await RefreshReviewDuringMaintenanceAsync();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Review restore failed: {exception}");
            var unresolved = File.Exists(TradePetPaths.GetDatabasePath() + ".restore-state.json");
            if (unresolved)
            {
                _persistenceAvailable = false;
            }
            await OnUiAsync(() => review.ExportStatus = unresolved
                ? $"恢复未能安全收束；已停止后台采集并保留旧集、安全备份及操作记录，请勿继续操作数据：{exception.Message}"
                : $"恢复失败；当前数据未切换或已回滚并校验，请核对：{exception.Message}");
            if (unresolved)
            {
                _cancellation.Cancel();
            }
            else
            {
                await RefreshReviewAsync();
            }
        }
    }

    private async Task ApplyInMemoryReviewFallbackAsync(Exception? exception = null)
    {
        if (exception is not null)
        {
            AppLog.Write($"Review database query failed; using current-session data: {exception}");
        }
        if (_account is null || _behaviorPolicies is null)
        {
            await OnUiAsync(() =>
            {
                _viewModel.ReviewRangeText = "复盘暂时不可用：尚未连接交易账户。";
                _viewModel.ReviewSyncText = "查询失败，应用仍在运行";
                _viewModel.DiagnosticText = "复盘查询失败，但桌宠和实时提醒仍在工作。";
            });
            return;
        }

        try
        {
            var (from, to) = ResolveReviewRangeFromMemory();
            var side = _viewModel.SelectedReviewSideFilter switch
            {
                "买入" => TradeSide.Buy,
                "卖出" => TradeSide.Sell,
                _ => (TradeSide?)null,
            };
            var symbol = string.IsNullOrWhiteSpace(_viewModel.ReviewSymbolFilter)
                ? null
                : _viewModel.ReviewSymbolFilter.Trim();
            var filter = new ReviewFilter(_account.Scope.AccountKey, from, to, symbol, side,
                ServerUtcOffsetSeconds: _serverUtcOffsetSeconds);
            var trades = _trades.Values.ToArray();
            var metadata = new Dictionary<long, TradeReviewMetadata>(_reviewMetadata);
            var excursions = new Dictionary<long, TradeExcursion>(_excursions);
            var zones = _lossZones.ToArray();
            var attempts = _lossZoneAttempts.ToArray();
            IReadOnlyDictionary<DateOnly, DailyState> dailyStates = _dailyState is null
                ? new Dictionary<DateOnly, DailyState>()
                : new Dictionary<DateOnly, DailyState> { [_dailyState.ServerDate] = _dailyState };

            var selection = _reviewQueryEngine.Select(filter, trades, metadata, excursions);
            var snapshot = _reviewQueryEngine.Complete(
                selection,
                zones,
                attempts,
                metadata,
                _behaviorPolicies.Selected,
                dailyStates,
                _timeProvider.GetUtcNow(),
                _symbolSpecifications);

            await OnUiAsync(() =>
            {
                _viewModel.ReviewFromDateText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _viewModel.ReviewToDateText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _viewModel.ReviewRangeText = snapshot.Trades.Count == 0
                    ? $"{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd} · 本次运行尚未收到符合条件的完整交易"
                    : $"{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd} · {snapshot.Trades.Count} 笔完整交易 · 临时数据";
                _viewModel.ReviewSyncText = "本地历史库异常 · 当前显示本次运行已同步数据";
                _viewModel.DiagnosticText = "本地历史记录暂时不可用，复盘已切换为本次运行数据。";
                _viewModel.ApplyReviewSnapshot(snapshot);
                _viewModel.ApplyReviewTrades(snapshot.Trades, metadata, SaveReviewMetadataAsync);
            });
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Application shutdown should not surface through an async WPF command.
        }
        catch (Exception fallbackException)
        {
            AppLog.Write($"Review in-memory fallback failed but was contained: {fallbackException}");
            await OnUiAsync(() =>
            {
                _viewModel.ReviewRangeText = "复盘数据暂时无法读取，请稍后重试。";
                _viewModel.ReviewSyncText = "查询失败，应用仍在运行";
                _viewModel.DiagnosticText = "复盘查询失败，但桌宠和实时提醒仍在工作。";
            });
        }
    }

    private ReviewDateRange ResolveReviewRangeFromMemory()
    {
        var preset = ResolveSelectedReviewPeriod();
        DateOnly? availableFrom = null;
        DateOnly? availableTo = null;
        if (preset == ReviewPeriodPreset.AllHistory)
        {
            var dates = _trades.Values
                .Where(trade =>
                    trade.AccountKey == _account!.Scope.AccountKey &&
                    trade.IsComplete &&
                    trade.CloseServerDate is not null)
                .Select(trade => trade.CloseServerDate!.Value)
                .ToArray();
            if (dates.Length > 0)
            {
                availableFrom = dates.Min();
                availableTo = dates.Max();
            }
        }

        return _reviewRangeResolver.Resolve(
            preset,
            _serverDate,
            availableFrom,
            availableTo,
            ParseReviewDate(_viewModel.ReviewFromDateText),
            ParseReviewDate(_viewModel.ReviewToDateText));
    }

    private async Task<ReviewDateRange> ResolveReviewRangeAsync()
    {
        var preset = ResolveSelectedReviewPeriod();
        DateOnly? availableFrom = null;
        DateOnly? availableTo = null;
        if (preset == ReviewPeriodPreset.AllHistory)
        {
            var range = await _database.LoadTradeDateRangeAsync(
                _account!.Scope.AccountKey,
                _cancellation.Token);
            availableFrom = range.FromServerDate;
            availableTo = range.ToServerDate;
        }

        return _reviewRangeResolver.Resolve(
            preset,
            _serverDate,
            availableFrom,
            availableTo,
            ParseReviewDate(_viewModel.ReviewFromDateText),
            ParseReviewDate(_viewModel.ReviewToDateText));
    }

    private ReviewPeriodPreset ResolveSelectedReviewPeriod() =>
        _viewModel.SelectedReviewPeriod switch
        {
            "今天" => ReviewPeriodPreset.Today,
            "本周" => ReviewPeriodPreset.ThisWeek,
            "本月" => ReviewPeriodPreset.ThisMonth,
            "近90天" => ReviewPeriodPreset.LastNinetyDays,
            "今年" => ReviewPeriodPreset.ThisYear,
            "全部历史" => ReviewPeriodPreset.AllHistory,
            "自定义" => ReviewPeriodPreset.Custom,
            _ => ReviewPeriodPreset.LastThirtyDays,
        };

    private static DateOnly? ParseReviewDate(string text) =>
        DateOnly.TryParseExact(
            text,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value
            : null;

    private async Task EnsureReviewHistorySyncAsync()
    {
        if (_worker is null || !_workerConnected || !_serverDateAuthoritative || _account is null)
        {
            return;
        }
        if (!_persistenceAvailable)
        {
            await OnUiAsync(() => _viewModel.ReviewSyncText = "本地历史库异常 · 当前显示本次运行数据");
            return;
        }

        var scope = _account.Scope.AccountKey;
        if (!_historySyncTracker.ShouldRequest(scope))
        {
            return;
        }
        var states = await _database.LoadHistorySyncStatesAsync(scope, _cancellation.Token);
        var plan = _historySyncTracker.BeginRequest(scope, _serverDate.Year, states);
        if (plan is null)
        {
            return;
        }
        if (plan.RequestedYears.Count == 0)
        {
            await OnUiAsync(() =>
                _viewModel.ReviewSyncText = $"历史已覆盖 {plan.EarliestYear}—{plan.CurrentYear} 年");
            return;
        }

        await OnUiAsync(() =>
            _viewModel.ReviewSyncText = $"正在同步历史 · {plan.RequestedYears[0]} 年开始");
        await _worker.RefreshHistoryAsync(
            plan.RequestedYears,
            _serverUtcOffsetSeconds,
            _serverDate,
            _cancellation.Token);
    }

    private async Task PersistLossZoneOpenResultAsync(LossZoneOpenResult result)
    {
        if (result.Attempt is not null)
        {
            UpsertAttemptInMemory(result.Attempt);
            await TryPersistLiveAsync(
                () => _database.UpsertLossZoneAttemptAsync(result.Attempt, _cancellation.Token),
                "保存亏损区域尝试");
        }
        if (result.Zone is not null)
        {
            var reconciled = _lossZoneEngine.Reconcile(result.Zone, _lossZoneAttempts);
            UpsertZoneInMemory(reconciled);
            await TryPersistLiveAsync(
                () => _database.UpsertLossZoneAsync(reconciled, _cancellation.Token),
                "保存亏损区域");
        }
        await UpdateLossZoneUiAsync();
    }

    private void UpsertZoneInMemory(LossZoneState zone)
    {
        var index = _lossZones.FindIndex(item => item.Id == zone.Id);
        if (index >= 0) _lossZones[index] = zone; else _lossZones.Add(zone);
    }

    private void UpsertAttemptInMemory(LossZoneAttempt attempt)
    {
        _lossZoneAttempts.RemoveAll(item => item.PositionId == attempt.PositionId && item.Id != attempt.Id);
        var index = _lossZoneAttempts.FindIndex(item => item.Id == attempt.Id);
        if (index >= 0) _lossZoneAttempts[index] = attempt; else _lossZoneAttempts.Add(attempt);
    }

    private async Task EnsureLossZoneHistoryProjectionAsync()
    {
        if (_account is null)
        {
            return;
        }

        var settingScope = $"account:{_account.Scope.AccountKey}";
        var storedVersion = await _database.LoadSettingAsync<int?>(
            settingScope, LossZoneProjectionSettingKey, _cancellation.Token);
        if (storedVersion >= LossZoneProjectionVersion)
        {
            return;
        }

        var dates = await _database.LoadLossZoneServerDatesAsync(_account.Scope.AccountKey, _cancellation.Token);
        foreach (var date in dates)
        {
            var trades = await _database.LoadTradesForLossZoneDateAsync(
                _account.Scope.AccountKey, date, _cancellation.Token);
            var projection = _lossZoneEngine.Rebuild(
                _account.Scope.AccountKey,
                date,
                0m,
                trades,
                toleranceResolver: ResolveLossZoneTolerance);
            await _database.ReplaceLossZoneProjectionAsync(
                _account.Scope.AccountKey, date, projection, _cancellation.Token);
        }

        await _database.SaveSettingAsync(
            settingScope, LossZoneProjectionSettingKey, LossZoneProjectionVersion, _cancellation.Token);
        AppLog.Write($"Loss-zone history projection repaired for {dates.Count} server dates.");
    }

    private async Task RebuildCurrentLossZoneProjectionAsync(bool updateUi)
    {
        if (_account is null)
        {
            return;
        }

        var projection = _lossZoneEngine.Rebuild(
            _account.Scope.AccountKey,
            _serverDate,
            0m,
            _trades.Values.ToArray(),
            _lossZones,
            ResolveLossZoneTolerance);
        var changed = !ProjectionMatches(projection);
        _lossZones.Clear();
        _lossZones.AddRange(projection.Zones);
        _lossZoneAttempts.Clear();
        _lossZoneAttempts.AddRange(projection.Attempts);
        if (changed)
        {
            await TryPersistLiveAsync(
                () => _database.ReplaceLossZoneProjectionAsync(
                    _account.Scope.AccountKey, _serverDate, projection, _cancellation.Token),
                "校准亏损区域投影");
        }

        if (updateUi)
        {
            await UpdateLossZoneUiAsync();
        }
    }

    private bool ProjectionMatches(LossZoneProjection projection)
    {
        var currentZones = _lossZones.OrderBy(zone => zone.Id).ToArray();
        var projectedZones = projection.Zones.OrderBy(zone => zone.Id).ToArray();
        var currentAttempts = _lossZoneAttempts.OrderBy(attempt => attempt.Id).ToArray();
        var projectedAttempts = projection.Attempts.OrderBy(attempt => attempt.Id).ToArray();
        return currentZones.SequenceEqual(projectedZones) && currentAttempts.SequenceEqual(projectedAttempts);
    }

    private async Task AddTimelineAsync(TimelineKind kind, string summary)
    {
        if (_account is null)
        {
            return;
        }

        var item = new TimelineEvent(
            Guid.NewGuid().ToString("N"),
            _account.Scope.AccountKey,
            _serverDate,
            _timeProvider.GetUtcNow(),
            kind,
            summary,
            "{}");
        await OnUiAsync(() => _viewModel.Timeline.Insert(0, new TimelineRowViewModel(item)));
        await TryPersistLiveAsync(
            () => _database.AddTimelineEventAsync(item, _cancellation.Token),
            "保存时间线");
    }

    private async Task UpdatePositionUiAsync(IReadOnlyCollection<PositionSnapshot> positions)
    {
        await OnUiAsync(() =>
        {
            _viewModel.Positions.Clear();
            foreach (var position in positions.OrderBy(item => item.OpenedAtUtc))
            {
                _viewModel.Positions.Add(new PositionRowViewModel(position));
            }
            _viewModel.HasPositions = positions.Count > 0;
            _viewModel.OpenPositionCount = positions.Count;
            _viewModel.CurrentExposure = positions.Sum(position => position.Volume);
            var firstPosition = positions.OrderBy(position => position.OpenedAtUtc).FirstOrDefault();
            _viewModel.PositionCard = firstPosition is null
                ? "当前无持仓"
                : $"{firstPosition.Symbol} {FormatSide(firstPosition.Side)} {firstPosition.Volume:0.##} 手  " +
                  $"{(firstPosition.Profit >= 0m ? "+" : string.Empty)}{firstPosition.Profit:0.##}" +
                  (positions.Count > 1 ? $"  ·  另有 {positions.Count - 1} 笔" : string.Empty);
        });
    }

    private async Task UpdateExcursionSamplingAsync(
        IReadOnlyCollection<PositionSnapshot> previous,
        IReadOnlyCollection<PositionSnapshot> current,
        DateTimeOffset capturedAtUtc)
    {
        if (_account is null)
        {
            return;
        }

        var currentByPosition = current.ToDictionary(item => item.PositionId);
        foreach (var position in current)
        {
            var pnl = position.Profit + position.Swap;
            if (!_excursions.TryGetValue(position.PositionId, out var existing) ||
                existing.AlgorithmVersion != PositionPnlAlgorithmVersion)
            {
                var holdingMilliseconds = Math.Max(0L, (long)(capturedAtUtc - position.OpenedAtUtc).TotalMilliseconds);
                var plannedRisk = _tradePlanMatcher
                    .FindBest(ProvisionalTrade(position), _structuredPlans.Values)
                    ?.PlannedRiskMultiple;
                existing = new TradeExcursion(
                    _account.Scope.AccountKey,
                    position.PositionId,
                    pnl,
                    pnl,
                    position.InitialRiskAmount is > 0m ? position.InitialRiskAmount : null,
                    plannedRisk,
                    null,
                    capturedAtUtc,
                    capturedAtUtc,
                    0,
                    holdingMilliseconds,
                    capturedAtUtc - position.OpenedAtUtc <= TimeSpan.FromSeconds(2),
                    false,
                    0,
                    PositionPnlAlgorithmVersion);
            }
            else
            {
                var delta = Math.Max(0L, (long)(capturedAtUtc - existing.LastSampleAtUtc).TotalMilliseconds);
                existing = existing with
                {
                    MinimumPnl = Math.Min(existing.MinimumPnl, pnl),
                    MaximumPnl = Math.Max(existing.MaximumPnl, pnl),
                    LastSampleAtUtc = capturedAtUtc,
                    CoveredMilliseconds = existing.CoveredMilliseconds + Math.Min(delta, 1000L),
                    HoldingMilliseconds = Math.Max(existing.HoldingMilliseconds,
                        Math.Max(0L, (long)(capturedAtUtc - position.OpenedAtUtc).TotalMilliseconds)),
                    MaximumGapMilliseconds = Math.Max(existing.MaximumGapMilliseconds, delta),
                    PlannedRiskMultiple = existing.PlannedRiskMultiple ?? _tradePlanMatcher
                        .FindBest(ProvisionalTrade(position), _structuredPlans.Values)
                        ?.PlannedRiskMultiple,
                };
            }

            _excursions[position.PositionId] = existing;
            if (!_pnlSamplePersistedAt.TryGetValue(position.PositionId, out var lastPnlSample) ||
                capturedAtUtc - lastPnlSample >= TimeSpan.FromSeconds(1))
            {
                var gap = lastPnlSample == default
                    ? 0L
                    : Math.Max(0L, (long)(capturedAtUtc - lastPnlSample).TotalMilliseconds);
                var sample = new PositionPnlSample(
                    new TradeKey(_account.Scope.AccountKey, position.PositionId), capturedAtUtc,
                    0m, pnl, pnl, position.Volume,
                    position.StopLoss == 0m ? null : position.StopLoss,
                    position.TakeProfit == 0m ? null : position.TakeProfit,
                    gap, PositionPnlAlgorithmVersion);
                await TryPersistLiveAsync(
                    () => _database.AddPositionPnlSampleAsync(sample, _cancellation.Token),
                    "保存持仓盈亏曲线采样");
                _pnlSamplePersistedAt[position.PositionId] = capturedAtUtc;
            }
            if (!_excursionPersistedAt.TryGetValue(position.PositionId, out var lastPersisted) ||
                capturedAtUtc - lastPersisted >= TimeSpan.FromSeconds(5))
            {
                await TryPersistLiveAsync(
                    () => _database.UpsertTradeExcursionAsync(existing, _cancellation.Token),
                    "保存持仓波动采样");
                _excursionPersistedAt[position.PositionId] = capturedAtUtc;
            }
        }

        foreach (var previousPosition in previous)
        {
            if (currentByPosition.ContainsKey(previousPosition.PositionId) ||
                !_excursions.TryGetValue(previousPosition.PositionId, out var existing))
            {
                continue;
            }

            var delta = Math.Max(0L, (long)(capturedAtUtc - existing.LastSampleAtUtc).TotalMilliseconds);
            var completed = existing with
            {
                LastSampleAtUtc = capturedAtUtc,
                CoveredMilliseconds = existing.CoveredMilliseconds + Math.Min(delta, 1000L),
                HoldingMilliseconds = Math.Max(existing.HoldingMilliseconds,
                    Math.Max(0L, (long)(capturedAtUtc - previousPosition.OpenedAtUtc).TotalMilliseconds)),
                MaximumGapMilliseconds = Math.Max(existing.MaximumGapMilliseconds, delta),
                IsComplete = true,
            };
            _excursions[previousPosition.PositionId] = completed;
            await TryPersistLiveAsync(
                () => _database.UpsertTradeExcursionAsync(completed, _cancellation.Token),
                "完成持仓波动采样");
            _excursionPersistedAt[previousPosition.PositionId] = capturedAtUtc;
        }
    }

    private async Task CompleteTradeExcursionAsync(TradeRecord trade)
    {
        if (!_excursions.TryGetValue(trade.PositionId, out var existing))
        {
            return;
        }

        var completed = existing with
        {
            ActualRiskMultiple = existing.InitialRiskAmount is > 0m
                ? trade.NetPnl / existing.InitialRiskAmount.Value
                : null,
            IsComplete = true,
        };
        _excursions[trade.PositionId] = completed;
        await TryPersistLiveAsync(
            () => _database.UpsertTradeExcursionAsync(completed, _cancellation.Token),
            "保存交易风险倍数");
        _excursionPersistedAt[trade.PositionId] = completed.LastSampleAtUtc;
    }

    private async Task SampleEquityAsync(AccountSnapshot account, bool hasOpenPosition, bool force)
    {
        var interval = hasOpenPosition ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60);
        if (!force && _lastEquitySampleAtUtc != default && account.CapturedAtUtc - _lastEquitySampleAtUtc < interval)
        {
            return;
        }

        var sample = new EquitySample(
            account.Scope.AccountKey,
            ResolveServerDate(account.CapturedAtUtc),
            account.CapturedAtUtc,
            account.Balance,
            account.Equity,
            account.FloatingPnl,
            hasOpenPosition);
        await TryPersistLiveAsync(
            () => _database.AddEquitySampleAsync(sample, _cancellation.Token),
            "保存净值采样");
        var existingSampleIndex = _equitySamples.FindIndex(item => item.CapturedAtUtc == sample.CapturedAtUtc);
        if (existingSampleIndex >= 0)
        {
            _equitySamples[existingSampleIndex] = sample;
        }
        else
        {
            _equitySamples.Add(sample);
        }
        _lastEquitySampleAtUtc = account.CapturedAtUtc;
        if (force)
        {
            await TryPersistLiveAsync(
                () => _database.DeleteEquitySamplesBeforeAsync(account.CapturedAtUtc.AddDays(-90), _cancellation.Token),
                "清理过期净值采样");
        }
    }

    private async Task UpdateDailyUiAsync(DailyState state)
    {
        await OnUiAsync(() =>
        {
            _viewModel.RealizedPnl = state.RealizedPnl;
            _viewModel.FloatingPnl = state.FloatingPnl;
            _viewModel.HighWaterPnl = state.HighWaterPnl;
            _viewModel.Giveback = state.Giveback;
            _viewModel.TradeCount = state.TradeCount;
            _viewModel.WinCount = state.WinCount;
            _viewModel.LossCount = state.LossCount;
            _viewModel.ConsecutiveLosses = state.ConsecutiveLosses;
            _viewModel.MaximumExposure = state.MaximumExposure;
            _viewModel.RiskText = FormatRiskText(state);
        });
    }

    private async Task UpdateLossZoneUiAsync()
    {
        var visibleZones = _lossZones
            .Where(item =>
                _account is not null &&
                item.AccountKey == _account.Scope.AccountKey &&
                item.ServerDate == _serverDate &&
                LossZoneEngine.IsConfirmedLossZone(item))
            .OrderByDescending(item => item.LastAttemptAtUtc)
            .ToArray();
        await OnUiAsync(() =>
        {
            _viewModel.LossZones.Clear();
            foreach (var zone in visibleZones)
            {
                _viewModel.LossZones.Add(new LossZoneRowViewModel(zone));
            }
        });
        if (_account is not null)
        {
            _bridge.SetLossZones(
                _terminal?.TerminalPath ?? string.Empty,
                _account.Scope.AccountKey,
                _serverDate,
                visibleZones.Select(zone => new BridgeLossZone(
                    zone.Id,
                    zone.Symbol,
                    zone.CenterPrice - zone.Tolerance,
                    zone.CenterPrice,
                    zone.CenterPrice + zone.Tolerance,
                    zone.AttemptCount,
                    zone.LossCount,
                    zone.CumulativeLoss)).ToArray());
        }
    }

    private async Task<bool> ShowAlertAsync(
        CombinedAlert alert,
        PetSignal signal,
        LossZoneState? lossZone = null,
        decimal? currentPrice = null,
        bool persistDelivery = true,
        bool deduplicateInMemory = true)
    {
        var deliveryAccountKey = _account?.Scope.AccountKey ?? "session";
        var tradeIdentity = AlertTradeIdentity.TryExtract(alert.Id);
        var isolationKey = tradeIdentity is null ? null : $"{deliveryAccountKey}|{tradeIdentity}";
        if (isolationKey is not null && _mutedTradeAlerts.Contains(isolationKey)) return false;
        var alertDate = ResolveServerDate(alert.OccurredAtUtc);
        if (!_alertDeliveryGate.CanAttempt(
                deliveryAccountKey,
                alertDate,
                alert.Id,
                alert.Priority,
                _petState.Current.ActiveAlertPriority,
                deduplicateInMemory))
        {
            return false;
        }

        if (persistDelivery && _account is not null)
        {
            try
            {
                if (_persistenceAvailable && !await _database.TryMarkAlertDeliveredAsync(
                        _account.Scope.AccountKey, alertDate,
                        alert with { Id = $"{alert.Id}:priority-{(int)alert.Priority}" }, _cancellation.Token))
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await ReportPersistenceFailureAsync("保存提醒去重", exception);
            }
        }

        if (!_alertDeliveryGate.Commit(
                deliveryAccountKey,
                alertDate,
                alert.Id,
                deduplicateInMemory,
                alert.Priority))
        {
            return false;
        }

        var version = Interlocked.Increment(ref _alertVersion);
        _activeAlertId = alert.Id;
        _activeAlertPriority = alert.Priority;
        _petState = _petBehavior.ApplySignal(_petState, signal, alert.OccurredAtUtc);
        var hasLossZoneCard = lossZone is not null && alert.Facts.Any(fact => fact.Kind == RuleFactKind.LossZoneHistory);
        var bubbleHeadline = hasLossZoneCard && alert.Headline == "又是这里。"
            ? "又进亏损区了。别急着证明自己。"
            : alert.Headline;
        var bubbleDetails = string.Join("\n", alert.Facts
            .Where(fact => !hasLossZoneCard || fact.Kind is not (
                RuleFactKind.LossZoneHistory or RuleFactKind.BehaviorLossZonePersistence))
            .Select(fact => fact.Detail));
        if (hasLossZoneCard && string.IsNullOrWhiteSpace(bubbleDetails))
        {
            bubbleDetails = "先离开鼠标，把进场理由重新说一遍。";
        }
        var zoneAttempts = lossZone is null
            ? null
            : _lossZoneAttempts.Where(attempt => attempt.ZoneId == lossZone.Id).ToArray();
        await OnUiAsync(() =>
        {
            _viewModel.BubbleHeadline = bubbleHeadline;
            _viewModel.BubbleDetails = bubbleDetails;
            _viewModel.SetBubbleLossZone(lossZone, currentPrice, zoneAttempts);
            _viewModel.IsBubbleVisible = true;
            _viewModel.IsAlertActionVisible = true;
            _viewModel.CanMuteCurrentAlert = tradeIdentity is not null;
            _viewModel.RiskText = bubbleHeadline;
            _viewModel.PetActivity = _petState.Current.Activity;
        });
        _ = HideBubbleLaterAsync(version, TimeSpan.FromSeconds(alert.Priority >= AlertPriority.Important ? 12 : 8));
        return true;
    }

    private void AcknowledgeAlert() => DismissActiveAlert();

    private void SnoozeAlert()
    {
        var headline = _viewModel.BubbleHeadline;
        var details = _viewModel.BubbleDetails;
        DismissActiveAlert();
        _ = Task.Run(async () =>
        {
            try { await _scheduler.DelayAsync(TimeSpan.FromMinutes(5), _cancellation.Token); await ShowSpeechAsync(headline, $"稍后提醒：{details}", TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        });
    }

    private void MuteCurrentTradeAlert()
    {
        if (_activeAlertId is not null)
        {
            var tradeIdentity = AlertTradeIdentity.TryExtract(_activeAlertId);
            if (tradeIdentity is not null)
            {
                var account = _account?.Scope.AccountKey ?? "session";
                _mutedTradeAlerts.Add($"{account}|{tradeIdentity}");
            }
        }
        DismissActiveAlert();
    }

    private void DismissActiveAlert()
    {
        Interlocked.Increment(ref _alertVersion);
        _activeAlertId = null;
        _petState = _petBehavior.CompleteInterruption(_petState, _timeProvider.GetUtcNow());
        _ = OnUiAsync(() => { _viewModel.IsBubbleVisible = false; _viewModel.IsAlertActionVisible = false; _viewModel.CanMuteCurrentAlert = false; _viewModel.SetBubbleLossZone(null); });
    }

    private async Task ShowSpeechAsync(
        string headline,
        string details,
        TimeSpan duration,
        LossZoneState? lossZone = null,
        decimal? currentPrice = null)
    {
        if (_petState.Current.ActiveAlertPriority is not null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _alertVersion);
        _activeAlertId = null;
        var zoneAttempts = lossZone is null
            ? null
            : _lossZoneAttempts.Where(attempt => attempt.ZoneId == lossZone.Id).ToArray();
        await OnUiAsync(() =>
        {
            _viewModel.BubbleHeadline = headline;
            _viewModel.BubbleDetails = details;
            _viewModel.SetBubbleLossZone(lossZone, currentPrice, zoneAttempts);
            _viewModel.IsBubbleVisible = true;
            _viewModel.IsAlertActionVisible = false;
            _viewModel.CanMuteCurrentAlert = false;
            _viewModel.PetActivity = _petState.Current.Activity;
        });
        _ = HideBubbleLaterAsync(version, duration);
    }

    private async Task HideBubbleLaterAsync(long version, TimeSpan delay)
    {
        try
        {
            await _scheduler.DelayAsync(delay, _cancellation.Token);
            if (version != Interlocked.Read(ref _alertVersion))
            {
                return;
            }
            _petState = _petBehavior.CompleteInterruption(_petState, _timeProvider.GetUtcNow());
            _activeAlertId = null;
            await OnUiAsync(() =>
            {
                _viewModel.IsBubbleVisible = false;
                _viewModel.IsAlertActionVisible = false;
                _viewModel.CanMuteCurrentAlert = false;
                _viewModel.SetBubbleLossZone(null);
                _viewModel.RiskText = _workerConnected ? FormatRiskText(_dailyState) : "交易终端已断开";
                _viewModel.PetActivity = _petState.Current.Activity;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write($"Delayed bubble cleanup failed: {exception}");
        }
    }

    private async Task RunPetLifeAsync(CancellationToken cancellationToken)
    {
        var nextSpeech = _timeProvider.GetUtcNow().AddMinutes(Random.Shared.Next(18, 46));
        while (!cancellationToken.IsCancellationRequested)
        {
            await _scheduler.DelayAsync(TimeSpan.FromSeconds(8), cancellationToken);
            if (_petState.Current.ActiveAlertPriority is null)
            {
                _petState = _petBehavior.Advance(_petState, _timeProvider.GetUtcNow());
                await OnUiAsync(() => _viewModel.PetActivity = _petState.Current.Activity);
            }
            if (!_viewModel.IsFocusMode && _timeProvider.GetUtcNow() >= nextSpeech && !_viewModel.IsBubbleVisible)
            {
                var consecutiveWins = PetAdviceCatalog.CountConsecutiveWins(_trades.Values, _serverDate);
                var situations = ResolvePetAdviceSituations(consecutiveWins);
                var advice = _petAdviceCatalog.Select(
                    ResolvePetAdviceContext(),
                    Random.Shared.Next(),
                    _recentPetAdviceKeys,
                    situations);
                _recentPetAdviceKeys.Enqueue(advice.Key);
                while (_recentPetAdviceKeys.Count > 8)
                {
                    _recentPetAdviceKeys.Dequeue();
                }
                await ShowSpeechAsync(
                    advice.Headline,
                    FormatPetAdviceDetails(advice, consecutiveWins),
                    TimeSpan.FromSeconds(9));
                nextSpeech = _timeProvider.GetUtcNow().AddMinutes(Random.Shared.Next(18, 46));
            }
        }
    }

    private async Task RunDailyReportScheduleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _scheduler.DelayAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (!_viewModel.DailyReportEnabled || !_serverDateAuthoritative || _account is null ||
                !TryParseDailyReportTime(_viewModel.DailyReportTimeText, out var reportTime))
            {
                continue;
            }

            var serverNow = _timeProvider.GetUtcNow().ToOffset(TimeSpan.FromSeconds(_serverUtcOffsetSeconds));
            if (DateOnly.FromDateTime(serverNow.DateTime) != _serverDate ||
                TimeOnly.FromDateTime(serverNow.DateTime) < reportTime)
            {
                continue;
            }

            await OfferDailyTradingReportAsync(_serverDate);
        }
    }

    private async Task RunMacroCalendarMonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _scheduler.DelayAsync(TimeSpan.FromSeconds(15), cancellationToken);
            var now = _timeProvider.GetUtcNow();
            var upcoming = _economicCalendarEvents
                .Where(item => item.Importance == EconomicEventImportance.High &&
                               item.ActualValue is null && item.ScheduledAtUtc >= now &&
                               item.ScheduledAtUtc <= now.AddMinutes(30))
                .OrderBy(item => item.ScheduledAtUtc)
                .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                .ToArray();
            if (upcoming.Length == 0)
            {
                continue;
            }

            var fiveMinuteEvents = upcoming
                .Where(item => item.ScheduledAtUtc - now <= TimeSpan.FromMinutes(5))
                .Where(item => _macroFiveMinuteReminders.Add(item.ValueId))
                .ToArray();
            if (fiveMinuteEvents.Length > 0)
            {
                await ShowSpeechAsync(
                    fiveMinuteEvents.Length == 1
                        ? "5 分钟后有高重要度事件"
                        : $"5 分钟内有 {fiveMinuteEvents.Length} 项高重要度事件",
                    FormatMacroReminderDetails(fiveMinuteEvents, includeValues: true),
                    TimeSpan.FromSeconds(fiveMinuteEvents.Length == 1 ? 12 : 18));
            }
            else
            {
                var thirtyMinuteEvents = upcoming
                    .Where(item => item.ScheduledAtUtc - now > TimeSpan.FromMinutes(5))
                    .Where(item => _macroThirtyMinuteReminders.Add(item.ValueId))
                    .ToArray();
                if (thirtyMinuteEvents.Length > 0)
                {
                    await ShowSpeechAsync(
                        thirtyMinuteEvents.Length == 1
                            ? "30 分钟内有高重要度事件"
                            : $"30 分钟内有 {thirtyMinuteEvents.Length} 项高重要度事件",
                        FormatMacroReminderDetails(thirtyMinuteEvents, includeValues: false),
                        TimeSpan.FromSeconds(thirtyMinuteEvents.Length == 1 ? 10 : 16));
                }
            }
        }
    }

    public Task ShowDailyTradingReportAsync() =>
        ShowDailyTradingReportAsync(_serverDate, automatic: false);

    private Task OfferDailyTradingReportAsync(DateOnly date) =>
        _viewModel.DailyReportEnabled
            ? ShowDailyTradingReportAsync(date, automatic: true)
            : Task.CompletedTask;

    private async Task ShowDailyTradingReportAsync(DateOnly date, bool automatic)
    {
        if (_account is null || !_serverDateAuthoritative)
        {
            await ShowSpeechAsync("日报还不能生成。", "等待 MT5 账户和服务器时间连接完成。", TimeSpan.FromSeconds(7));
            return;
        }

        await _dailyReportGate.WaitAsync(_cancellation.Token);
        try
        {
            var accountKey = _account.Scope.AccountKey;
            var runKey = $"{accountKey}|{date:yyyy-MM-dd}";
            if (automatic && _dailyReportsShownThisRun.Contains(runKey))
            {
                return;
            }

            if (automatic && _persistenceAvailable)
            {
                try
                {
                    var storedDate = await _database.LoadSettingAsync<string>(
                        $"account:{accountKey}", DailyReportLastShownSettingKey, _cancellation.Token);
                    if (DateOnly.TryParseExact(storedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var lastShownDate) && lastShownDate >= date)
                    {
                        _dailyReportsShownThisRun.Add(runKey);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    AppLog.Write($"Daily report marker could not be loaded: {exception}");
                }
            }

            var data = await _reviewRepository.LoadWorkspaceAsync(
                accountKey, date, date, _cancellation.Token);
            if (_account?.Scope.AccountKey != accountKey)
            {
                return;
            }

            var facts = _reviewWorkspaceCalculator.BuildDailyFacts(
                accountKey,
                date,
                date,
                data.Trades,
                data.Deals,
                data.Documents,
                data.Behaviors,
                data.DailyStates ?? new Dictionary<DateOnly, DailyState>(),
                _serverUtcOffsetSeconds)[date];
            var completed = data.Trades
                .Where(item => item.AccountKey == accountKey && item.IsComplete && item.CloseServerDate == date)
                .OrderBy(item => item.ClosedAtUtc)
                .ThenBy(item => item.PositionId)
                .ToArray();
            var winCount = completed.Count(item => item.NetPnl > 0.01m);
            var lossCount = completed.Count(item => item.NetPnl < -0.01m);
            var breakevenCount = completed.Length - winCount - lossCount;
            var insidePlanCount = completed.Count(item =>
                data.Metadata.TryGetValue(item.PositionId, out var metadata) &&
                metadata.ComplianceStatus is PlanComplianceStatus.Matched or PlanComplianceStatus.ManualInside);
            var outsidePlanCount = completed.Count(item =>
                data.Metadata.TryGetValue(item.PositionId, out var metadata) &&
                metadata.ComplianceStatus is PlanComplianceStatus.OutsidePlan or PlanComplianceStatus.ManualOutside);
            var reviewedCount = completed.Count(item =>
                data.Documents.TryGetValue(item.PositionId, out var document) &&
                document.Status == ReviewCompletionStatus.Reviewed);
            var behaviorAlerts = data.Behaviors.Count(item =>
                item.AccountKey == accountKey && item.ServerDate == date &&
                item.Level is BehaviorRiskLevel.Attention or BehaviorRiskLevel.Critical);
            var report = new DailyTradingReport(
                accountKey,
                string.IsNullOrWhiteSpace(data.Currency) ? _account.Currency : data.Currency,
                date,
                date >= _serverDate,
                facts.RealizedCashPnl,
                facts.Fees,
                completed.Length,
                winCount,
                lossCount,
                breakevenCount,
                completed.Length == 0 ? null : winCount * 100m / completed.Length,
                completed.OrderByDescending(item => item.NetPnl).FirstOrDefault(),
                completed.OrderBy(item => item.NetPnl).FirstOrDefault(),
                insidePlanCount,
                outsidePlanCount,
                completed.Length - insidePlanCount - outsidePlanCount,
                reviewedCount,
                completed.Length - reviewedCount,
                behaviorAlerts,
                facts.CooldownViolationCount,
                string.Empty);
            var markdown = BuildDailyReportMarkdown(data, facts, report);
            var archivePath = await ArchiveDailyReportAsync(report, markdown);
            report = report with { Markdown = markdown, ArchivePath = archivePath };

            await OnUiAsync(() => ShowDailyReportWindow(report));
            _dailyReportsShownThisRun.Add(runKey);
            if (automatic && _persistenceAvailable)
            {
                await TryPersistLiveAsync(
                    () => _database.SaveSettingAsync(
                        $"account:{accountKey}",
                        DailyReportLastShownSettingKey,
                        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        _cancellation.Token),
                    "记录交易日报弹出日期");
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Daily report generation failed: {exception}");
            await OnUiAsync(() => _viewModel.DiagnosticText = "交易日报生成失败，详细信息已写入日志。");
        }
        finally
        {
            _dailyReportGate.Release();
        }
    }

    private void ShowDailyReportWindow(DailyTradingReport report)
    {
        var previous = _dailyReportWindow;
        _dailyReportWindow = null;
        previous?.Close();

        var window = new DailyTradingReportWindow(report);
        _dailyReportWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dailyReportWindow, window))
            {
                _dailyReportWindow = null;
            }
        };
        window.OpenReviewRequested += (_, _) => _ = OpenDailyReportReviewAsync(report.ServerDate);
        window.Show();
        window.Activate();
    }

    private async Task OpenDailyReportReviewAsync(DateOnly date)
    {
        try
        {
            await RefreshReviewAsync();
            await WithReviewWriteGateAsync(() => OpenDailyReviewAsync(date));
            await OnUiAsync(() => _viewModel.ShowConsolePage?.Invoke(3));
        }
        catch (Exception exception)
        {
            AppLog.Write($"Daily report review could not be opened: {exception}");
        }
    }

    private async Task<string?> ArchiveDailyReportAsync(DailyTradingReport report, string markdown)
    {
        try
        {
            var accountFolder = SanitizePathPart(report.AccountKey);
            var directory = Path.Combine(TradePetPaths.GetDataDirectory(), "reports", accountFolder);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{report.ServerDate:yyyy-MM-dd}-trading-report.md");
            await File.WriteAllTextAsync(path, markdown, Encoding.UTF8, _cancellation.Token);
            return path;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Daily report archive failed: {exception}");
            return null;
        }
    }

    private string BuildDailyReportMarkdown(
        ReviewWorkspaceData data,
        DailyReviewFacts facts,
        DailyTradingReport report)
    {
        var offset = TimeSpan.FromSeconds(facts.ServerUtcOffsetSeconds);
        var date = report.ServerDate;
        var completed = data.Trades
            .Where(item => item.AccountKey == report.AccountKey && item.IsComplete && item.CloseServerDate == date)
            .OrderBy(item => item.ClosedAtUtc)
            .ThenBy(item => item.PositionId)
            .ToArray();
        var opened = data.Trades
            .Where(item => item.AccountKey == report.AccountKey && item.OpenServerDate == date)
            .OrderBy(item => item.OpenedAtUtc)
            .ThenBy(item => item.PositionId)
            .ToArray();
        var deals = data.Deals
            .Where(item => DateOnly.FromDateTime(item.OccurredAtUtc.ToOffset(offset).DateTime) == date)
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Ticket)
            .ToArray();
        var behaviors = data.Behaviors
            .Where(item => item.AccountKey == report.AccountKey && item.ServerDate == date)
            .OrderBy(item => item.EventAtUtc)
            .ToArray();
        var cashFlows = (data.CashFlows ?? [])
            .Where(item => item.AccountKey == report.AccountKey &&
                           DateOnly.FromDateTime(item.OccurredAtUtc.ToOffset(offset).DateTime) == date)
            .OrderBy(item => item.OccurredAtUtc)
            .ToArray();
        var equity = (data.EquitySamples ?? [])
            .Where(item => item.AccountKey == report.AccountKey && item.ServerDate == date)
            .OrderBy(item => item.CapturedAtUtc)
            .ToArray();
        var macroEvents = _economicCalendarEvents
            .Where(item => DateOnly.FromDateTime(item.ScheduledAtUtc.ToOffset(offset).DateTime) == date)
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenByDescending(item => item.Importance)
            .ToArray();
        data.DailyJournals.TryGetValue(date, out var journal);
        DailyState? dailyState = null;
        data.DailyStates?.TryGetValue(date, out dailyState);

        var builder = new StringBuilder();
        builder.AppendLine($"# {date:yyyy-MM-dd} 交易日报");
        builder.AppendLine();
        builder.AppendLine($"> 账户：{report.AccountKey}  ");
        builder.AppendLine($"> 币种：{report.Currency}  ");
        builder.AppendLine($"> 状态：{(report.IsLive ? "当日实时快照" : "交易日已结束")}  ");
        builder.AppendLine($"> 生成时间（服务器）：{FormatServerTime(_timeProvider.GetUtcNow(), offset)}  ");
        builder.AppendLine($"> 数据版本：{facts.SourceVersion}");
        builder.AppendLine();
        builder.AppendLine("## 一、核心结果");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 数值 |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| 已实现现金盈亏（含费用） | {FormatReportMoney(facts.RealizedCashPnl, report.Currency)} |");
        builder.AppendLine($"| 完整交易净盈亏 | {FormatReportMoney(facts.CompleteTradeNetPnl, report.Currency)} |");
        builder.AppendLine($"| 费用（佣金 + 隔夜费 + 其他费） | {FormatReportMoney(facts.Fees, report.Currency)} |");
        builder.AppendLine($"| 开仓交易数 | {facts.OpeningTradeCount} |");
        builder.AppendLine($"| 完整平仓交易数 | {report.TradeCount} |");
        builder.AppendLine($"| 胜 / 负 / 平 | {report.WinCount} / {report.LossCount} / {report.BreakevenCount} |");
        builder.AppendLine($"| 胜率 | {(report.WinRate is null ? "—" : $"{report.WinRate:0.##}%")} |");
        builder.AppendLine($"| 复盘完成率 | {facts.ReviewCompletionPercentage:0.##}% |");
        builder.AppendLine($"| 连续亏损 | {facts.ConsecutiveLosses} |");
        builder.AppendLine($"| 冷静期违规 | {facts.CooldownViolationCount} |");
        builder.AppendLine($"| 行为风险提醒 | {report.BehaviorAlertCount} |");
        builder.AppendLine();

        builder.AppendLine("## 二、计划执行与风险状态");
        builder.AppendLine();
        builder.AppendLine($"- 计划内：{report.InsidePlanCount} 笔");
        builder.AppendLine($"- 计划外：{report.OutsidePlanCount} 笔");
        builder.AppendLine($"- 未分类：{report.UnclassifiedPlanCount} 笔");
        builder.AppendLine($"- 已复盘：{report.ReviewedCount} 笔；待复盘：{report.PendingReviewCount} 笔");
        builder.AppendLine($"- 达标时刻：{(facts.TargetReachedAtUtc is null ? "未记录" : FormatServerTime(facts.TargetReachedAtUtc.Value, offset))}");
        builder.AppendLine($"- 达标金额：{(facts.TargetAmount is null ? "未记录" : FormatReportMoney(facts.TargetAmount.Value, report.Currency))}");
        builder.AppendLine($"- 达标后新交易净盈亏：{(facts.AfterTargetNewTradeNetPnl is null ? "未记录" : FormatReportMoney(facts.AfterTargetNewTradeNetPnl.Value, report.Currency))}");
        if (dailyState is not null)
        {
            builder.AppendLine($"- 日内浮动盈亏快照：{FormatReportMoney(dailyState.FloatingPnl, report.Currency)}");
            builder.AppendLine($"- 日内高水位：{FormatReportMoney(dailyState.HighWaterPnl, report.Currency)}；回吐：{FormatReportMoney(dailyState.Giveback, report.Currency)}");
            builder.AppendLine($"- 最大敞口：{dailyState.MaximumExposure:0.####}");
            builder.AppendLine($"- 提醒状态：目标={YesNo(dailyState.TargetAlerted)}，亏损线={YesNo(dailyState.LossAlerted)}，回吐={YesNo(dailyState.GivebackAlerted)}，交易数上限={YesNo(dailyState.TradeLimitAlerted)}，手数上限={YesNo(dailyState.LotLimitAlerted)}");
        }
        builder.AppendLine();

        builder.AppendLine("## 三、完整平仓交易");
        builder.AppendLine();
        if (completed.Length == 0)
        {
            builder.AppendLine("_无完整平仓交易。_");
        }
        else
        {
            builder.AppendLine("| Position | 品种 | 方向 | 开仓（服务器） | 平仓（服务器） | 入场 | 出场 | 最大手数 | 净盈亏 | 计划执行 | 策略 / 形态 | 标签 | 复盘状态 |");
            builder.AppendLine("|---:|---|---|---|---|---:|---:|---:|---:|---|---|---|---|");
            foreach (var trade in completed)
            {
                data.Metadata.TryGetValue(trade.PositionId, out var metadata);
                data.Documents.TryGetValue(trade.PositionId, out var document);
                builder.AppendLine($"| {trade.PositionId} | {MdCell(trade.Symbol)} | {FormatSide(trade.Side)} | {FormatServerTime(trade.OpenedAtUtc, offset)} | {FormatServerTime(trade.ClosedAtUtc, offset)} | {trade.EntryPrice:0.#####} | {FormatNullable(trade.ExitPrice)} | {trade.MaximumVolume:0.####} | {trade.NetPnl:+0.##;-0.##;0} | {FormatCompliance(metadata?.ComplianceStatus)} | {MdCell(JoinNonEmpty(metadata?.Strategy, metadata?.Setup))} | {MdCell(metadata is null ? null : string.Join(", ", metadata.Tags))} | {FormatReviewStatus(document?.Status)} |");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 四、逐笔复盘内容");
        builder.AppendLine();
        foreach (var trade in completed)
        {
            data.Documents.TryGetValue(trade.PositionId, out var document);
            builder.AppendLine($"### {trade.Symbol} · Position {trade.PositionId} · {trade.NetPnl:+0.##;-0.##;0} {report.Currency}");
            builder.AppendLine();
            if (document is null)
            {
                builder.AppendLine("_尚未填写复盘。_");
            }
            else
            {
                AppendMarkdownField(builder, "入场原因", document.EntryReason);
                AppendMarkdownField(builder, "退出原因", document.ExitReason);
                AppendMarkdownField(builder, "做得好", document.DidWell);
                AppendMarkdownField(builder, "待改进", document.ToImprove);
                AppendMarkdownField(builder, "下次行动", document.NextAction);
                AppendMarkdownField(builder, "总结", document.Summary);
                AppendMarkdownField(builder, "情绪", document.Emotion);
                AppendMarkdownField(builder, "市场状态", document.MarketCondition);
            }
            builder.AppendLine();
        }

        builder.AppendLine("## 五、当日全部成交明细");
        builder.AppendLine();
        if (deals.Length == 0)
        {
            builder.AppendLine("_无成交记录。_");
        }
        else
        {
            builder.AppendLine("| 时间（服务器） | Deal | Order | Position | 品种 | 方向 | 类型 | 手数 | 价格 | 毛盈亏 | 佣金 | 隔夜费 | 其他费 | 净额 |");
            builder.AppendLine("|---|---:|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var deal in deals)
            {
                builder.AppendLine($"| {FormatServerTime(deal.OccurredAtUtc, offset)} | {deal.Ticket} | {deal.OrderTicket} | {deal.PositionId} | {MdCell(deal.Symbol)} | {FormatSide(deal.Side)} | {FormatDealEntry(deal.EntryKind)} | {deal.Volume:0.####} | {deal.Price:0.#####} | {deal.Profit:+0.##;-0.##;0} | {deal.Commission:+0.##;-0.##;0} | {deal.Swap:+0.##;-0.##;0} | {deal.Fee:+0.##;-0.##;0} | {deal.NetPnl:+0.##;-0.##;0} |");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 六、当日开仓清单");
        builder.AppendLine();
        if (opened.Length == 0)
        {
            builder.AppendLine("_无新开仓交易。_");
        }
        else
        {
            builder.AppendLine("| Position | 品种 | 方向 | 时间（服务器） | 入场价 | 开仓手数 | 最大手数 | 当日是否已平仓 |");
            builder.AppendLine("|---:|---|---|---|---:|---:|---:|---|");
            foreach (var trade in opened)
            {
                builder.AppendLine($"| {trade.PositionId} | {MdCell(trade.Symbol)} | {FormatSide(trade.Side)} | {FormatServerTime(trade.OpenedAtUtc, offset)} | {trade.EntryPrice:0.#####} | {trade.OpeningVolume:0.####} | {trade.MaximumVolume:0.####} | {YesNo(trade.IsComplete && trade.CloseServerDate == date)} |");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 七、行为提醒与证据");
        builder.AppendLine();
        if (behaviors.Length == 0)
        {
            builder.AppendLine("_无行为规则记录。_");
        }
        else
        {
            builder.AppendLine("| 时间（服务器） | 风险 | 规则 | 摘要 | 数值 / 阈值 | 说明 | 关联 Position |");
            builder.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var behavior in behaviors)
            {
                var positions = string.Join(", ", behavior.TradeLinks.Select(item => item.TradeKey.PositionId).Distinct());
                var explanation = JoinNonEmpty(behavior.UserExplanation, behavior.MissingData,
                    behavior.EvidenceInsufficient ? "证据不足" : null);
                builder.AppendLine($"| {FormatServerTime(behavior.EventAtUtc, offset)} | {FormatBehaviorRisk(behavior.Level)} | {FormatBehaviorRule(behavior.Rule)} | {MdCell(behavior.Summary)} | {behavior.Value:0.####} / {behavior.Threshold:0.####} | {MdCell(explanation)} | {MdCell(positions)} |");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 八、规则评估");
        builder.AppendLine();
        var assessments = data.Assessments
            .Where(item => completed.Any(trade => trade.PositionId == item.TradeKey.PositionId))
            .OrderBy(item => item.TradeKey.PositionId)
            .ThenBy(item => item.RuleId)
            .ToArray();
        if (assessments.Length == 0)
        {
            builder.AppendLine("_无规则评估。_");
        }
        else
        {
            builder.AppendLine("| Position | 规则 | 结果 | 证据 | 备注 |");
            builder.AppendLine("|---:|---|---|---|---|");
            foreach (var assessment in assessments)
            {
                builder.AppendLine($"| {assessment.TradeKey.PositionId} | {MdCell(assessment.RuleId)} | {FormatAssessment(assessment.Status)} | {MdCell(assessment.EvidenceReference)} | {MdCell(assessment.Notes)} |");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 九、日记");
        builder.AppendLine();
        if (journal is null)
        {
            builder.AppendLine("_当日尚未建立日记。_");
        }
        else
        {
            AppendMarkdownField(builder, "盘前计划", journal.PreMarketPlan);
            AppendMarkdownField(builder, "盘中记录", journal.IntradayNotes);
            AppendMarkdownField(builder, "盘后总结", journal.PostMarketSummary);
            AppendMarkdownField(builder, "做得好", journal.DidWell);
            AppendMarkdownField(builder, "待改进", journal.ToImprove);
            AppendMarkdownField(builder, "下一步行动", journal.NextAction);
            builder.AppendLine($"- 日记状态：{FormatReviewStatus(journal.Status)}");
        }
        builder.AppendLine();

        builder.AppendLine("## 十、资金、权益与改进观察");
        builder.AppendLine();
        if (cashFlows.Length == 0)
        {
            builder.AppendLine("- 非交易资金变动：无");
        }
        else
        {
            foreach (var cashFlow in cashFlows)
            {
                builder.AppendLine($"- {FormatServerTime(cashFlow.OccurredAtUtc, offset)} · {cashFlow.Type} · {FormatReportMoney(cashFlow.Amount, report.Currency)} · ticket {cashFlow.Ticket}");
            }
        }
        if (equity.Length == 0)
        {
            builder.AppendLine("- 权益采样：无");
        }
        else
        {
            builder.AppendLine($"- 权益采样：{equity.Length} 个；起始权益 {equity[0].Equity:0.##}，结束权益 {equity[^1].Equity:0.##}，最高 {equity.Max(item => item.Equity):0.##}，最低 {equity.Min(item => item.Equity):0.##}");
        }
        foreach (var observation in data.GoalObservations.Where(item => item.ServerDate == date))
        {
            var goalName = data.Goals.FirstOrDefault(item => item.Id == observation.GoalId)?.Name ?? observation.GoalId;
            builder.AppendLine($"- 改进目标「{goalName}」：{observation.Status}；机会 {observation.OpportunityCount}，通过 {observation.PassCount}，失败 {observation.FailCount}；证据：{MdCell(observation.Evidence)}");
        }
        foreach (var opportunity in data.Opportunities.Where(item => item.ServerDate == date))
        {
            builder.AppendLine($"- 机会记录：{opportunity.Symbol} · {opportunity.Kind} · {MdCell(opportunity.Reason)} · {MdCell(opportunity.Notes)}");
        }
        builder.AppendLine();

        builder.AppendLine("## 十一、当日事实时间线");
        builder.AppendLine();
        if (facts.Timeline.Count == 0)
        {
            builder.AppendLine("_无时间线事件。_");
        }
        else
        {
            foreach (var item in facts.Timeline)
            {
                builder.AppendLine($"- {FormatServerTime(item.AtUtc, offset)} · {item.Kind} · {item.Summary}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 十二、当日宏观事件");
        builder.AppendLine();
        if (macroEvents.Length == 0)
        {
            builder.AppendLine("_当前未收到该日的 MT5 宏观事件数据。_");
        }
        else
        {
            builder.AppendLine("| 时间（服务器） | 重要度 | 国家 / 货币 | 事件 | 前值 | 预期 | 公布 | 影响 |");
            builder.AppendLine("|---|---|---|---|---:|---:|---:|---|");
            foreach (var item in macroEvents)
            {
                builder.AppendLine($"| {FormatServerTime(item.ScheduledAtUtc, offset)} | {FormatMacroImportance(item.Importance)} | {MdCell(JoinNonEmpty(item.CountryCode, item.Currency))} | {MdCell(item.Name)} | {FormatMacroValue(item.RevisedPreviousValue ?? item.PreviousValue, item)} | {FormatMacroValue(item.ForecastValue, item)} | {FormatMacroValue(item.ActualValue, item)} | {MdCell(item.Impact)} |");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 十三、数据完整性说明");
        builder.AppendLine();
        builder.AppendLine($"- 服务器 UTC 偏移：{facts.ServerUtcOffsetSeconds} 秒");
        builder.AppendLine($"- 当日是否标记数据缺口：{YesNo(data.DataGapDates.Contains(date))}");
        builder.AppendLine($"- 工作区版本：来源 {data.Version.SourceVersion}，元数据 {data.Version.MetadataVersion}，观察 {data.Version.ObservationVersion}，规则 {data.Version.RuleVersion}，时间 {data.Version.TimeVersion}");
        builder.AppendLine("- 金额以 MT5 成交和费用记录为准；胜率只统计当日完整平仓的 position；尚未平仓的持仓不会计入胜率。\n");

        builder.AppendLine("## 给 AI 的分析任务");
        builder.AppendLine();
        builder.AppendLine("请只依据本报告中的事实进行分析，不要补造行情或交易数据。请输出：");
        builder.AppendLine("1. 当日表现结论与最主要的盈利/亏损来源；");
        builder.AppendLine("2. 计划执行、仓位、费用、退出和行为纪律中的关键问题；");
        builder.AppendLine("3. 做得最好的 3 点、最需要改进的 3 点；");
        builder.AppendLine("4. 明日可执行的 3 条具体动作，并说明每条动作对应的报告证据；");
        builder.AppendLine("5. 明确区分“数据已证明”“合理推测”“数据不足”。");
        return builder.ToString();
    }

    private static void AppendMarkdownField(StringBuilder builder, string label, string? value)
    {
        builder.AppendLine($"**{label}**");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "_未记录_" : value.Trim());
        builder.AppendLine();
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "account" : sanitized;
    }

    private static bool TryParseDailyReportTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string NormalizeDailyReportTime(string? value) =>
        TryParseDailyReportTime(value, out var time)
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "23:55";

    private static string FormatServerTime(DateTimeOffset? value, TimeSpan offset) => value is null
        ? "—"
        : value.Value.ToOffset(offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatReportMoney(decimal value, string currency) =>
        $"{value:+0.##;-0.##;0} {currency}";

    private static string FormatNullable(decimal? value) => value?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "—";

    private static string MdCell(string? value) => string.IsNullOrWhiteSpace(value)
        ? "—"
        : value.Trim().Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>");

    private static string JoinNonEmpty(params string?[] values) =>
        string.Join(" / ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string FormatCompliance(PlanComplianceStatus? status) => status switch
    {
        PlanComplianceStatus.Matched => "自动匹配计划内",
        PlanComplianceStatus.ManualInside => "人工标记计划内",
        PlanComplianceStatus.OutsidePlan => "自动判断计划外",
        PlanComplianceStatus.ManualOutside => "人工标记计划外",
        _ => "未分类",
    };

    private static string FormatReviewStatus(ReviewCompletionStatus? status) => status switch
    {
        ReviewCompletionStatus.Reviewed => "已复盘",
        ReviewCompletionStatus.Draft => "草稿",
        ReviewCompletionStatus.NeedsReview => "需要重审",
        _ => "待复盘",
    };

    private static string FormatDealEntry(DealEntryKind entry) => entry switch
    {
        DealEntryKind.In => "入场",
        DealEntryKind.Out => "出场",
        DealEntryKind.InOut => "反向成交",
        DealEntryKind.OutBy => "对冲平仓",
        _ => entry.ToString(),
    };

    private static string FormatBehaviorRisk(BehaviorRiskLevel level) => level switch
    {
        BehaviorRiskLevel.Critical => "严重",
        BehaviorRiskLevel.Attention => "需注意",
        BehaviorRiskLevel.Normal => "正常",
        _ => "观察中",
    };

    private static string FormatBehaviorRule(BehaviorRuleKind rule) => rule switch
    {
        BehaviorRuleKind.ReentryCount => "重复进场",
        BehaviorRuleKind.LossZonePersistence => "亏损区域执着",
        BehaviorRuleKind.RevengeScore => "报复性交易",
        BehaviorRuleKind.OvertradeBurst => "短时过度交易",
        BehaviorRuleKind.PlanDeviationRate => "计划偏离",
        BehaviorRuleKind.ProfitGiveback => "利润回吐",
        BehaviorRuleKind.SizeEscalationAfterLoss => "亏后放大仓位",
        BehaviorRuleKind.CooldownViolation => "冷静期违规",
        BehaviorRuleKind.PriceFixationScore => "价格执着",
        _ => rule.ToString(),
    };

    private static string FormatAssessment(RuleAssessmentStatus status) => status switch
    {
        RuleAssessmentStatus.Passed => "通过",
        RuleAssessmentStatus.Failed => "未通过",
        RuleAssessmentStatus.NotApplicable => "不适用",
        _ => "未知",
    };

    private static string FormatMacroEventName(EconomicCalendarEvent item) =>
        string.IsNullOrWhiteSpace(item.Currency) ? item.Name : $"{item.Currency} · {item.Name}";

    private string FormatMacroReminderDetails(
        IReadOnlyList<EconomicCalendarEvent> events,
        bool includeValues)
    {
        const int visibleEventLimit = 8;
        var lines = events
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenByDescending(item => item.Importance)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .Take(visibleEventLimit)
            .Select(item =>
            {
                var time = item.ScheduledAtUtc
                    .ToOffset(TimeSpan.FromSeconds(_serverUtcOffsetSeconds))
                    .ToString("HH:mm", CultureInfo.InvariantCulture);
                if (!includeValues)
                {
                    return $"• {time}  {FormatMacroEventName(item)}";
                }

                return $"• {time}  {FormatMacroEventName(item)}  前 {FormatMacroValue(item.RevisedPreviousValue ?? item.PreviousValue, item)} / 预 {FormatMacroValue(item.ForecastValue, item)}" +
                       (item.ActualValue is null ? string.Empty : $" / 实 {FormatMacroValue(item.ActualValue, item)}");
            })
            .ToList();
        if (events.Count > visibleEventLimit)
        {
            lines.Add($"… 另有 {events.Count - visibleEventLimit} 项，右键宠物打开“宏观日历”查看全部");
        }

        return string.Join("\n", lines);
    }

    private static string FormatMacroImportance(EconomicEventImportance importance) => importance switch
    {
        EconomicEventImportance.High => "高",
        EconomicEventImportance.Moderate => "中",
        EconomicEventImportance.Low => "低",
        _ => "未评级",
    };

    private static string FormatMacroValue(decimal? value, EconomicCalendarEvent item)
    {
        if (value is null)
        {
            return "—";
        }
        var digits = Math.Clamp(item.Digits, 0, 6);
        var format = digits == 0 ? "0" : $"0.{new string('#', digits)}";
        var text = value.Value.ToString(format, CultureInfo.InvariantCulture);
        return item.Unit.EndsWith("PERCENT", StringComparison.OrdinalIgnoreCase) ? text + "%" : text;
    }

    private PetAdviceContext ResolvePetAdviceContext()
    {
        if (_dailyState is not null &&
            _dailyState.ConsecutiveLosses >= EffectiveAlertSettings().ConsecutiveLossThreshold)
        {
            return PetAdviceContext.CoolingDown;
        }

        if (_positions.Count > 0)
        {
            return PetAdviceContext.Holding;
        }

        return _dailyState?.RealizedPnl > 0.01m
            ? PetAdviceContext.ProtectingProfit
            : PetAdviceContext.Flat;
    }

    private PetAdviceSituation ResolvePetAdviceSituations(int consecutiveWins)
    {
        var situations = PetAdviceSituation.None;
        var floatingPnl = CurrentFloatingPnl();
        if (_positions.Count > 0 && floatingPnl > 0.01m)
        {
            situations |= PetAdviceSituation.HoldingProfit;
        }
        else if (_positions.Count > 0 && floatingPnl < -0.01m)
        {
            situations |= PetAdviceSituation.HoldingLoss;
        }

        if (consecutiveWins >= 2)
        {
            situations |= PetAdviceSituation.WinningStreak;
        }
        if (_dailyState?.TargetAlerted == true)
        {
            situations |= PetAdviceSituation.DailyTargetReached;
        }
        if (_dailyState is { HighWaterPnl: > 0m } state &&
            (state.GivebackAlerted || state.Giveback / state.HighWaterPnl >= 0.10m))
        {
            situations |= PetAdviceSituation.ProfitGiveback;
        }

        return situations;
    }

    private string FormatPetAdviceDetails(PetAdvice advice, int consecutiveWins)
    {
        var fact = advice.RequiredSituation switch
        {
            PetAdviceSituation.HoldingProfit => $"当前持仓浮盈 {FormatSignedValue(CurrentFloatingPnl())}。",
            PetAdviceSituation.HoldingLoss => $"当前持仓浮亏 {FormatSignedValue(CurrentFloatingPnl())}。",
            PetAdviceSituation.WinningStreak => $"当前连续盈利 {consecutiveWins} 笔。",
            PetAdviceSituation.DailyTargetReached => $"今日曾达到目标，当前合计 {FormatSignedValue((_dailyState?.RealizedPnl ?? 0m) + CurrentFloatingPnl())}。",
            PetAdviceSituation.ProfitGiveback => $"今日已从高点回吐 {_dailyState?.Giveback ?? 0m:0.##}。",
            _ => string.Empty,
        };
        return $"{fact}{advice.Details}";
    }

    private decimal CurrentFloatingPnl() =>
        _dailyState?.FloatingPnl ?? _positions.Values.Sum(position => position.Profit + position.Swap);

    private static string FormatSignedValue(decimal value) =>
        $"{(value >= 0m ? "+" : string.Empty)}{value:0.##}";

    private async Task LoadDesktopSettingsAsync()
    {
        var settings = await _database.LoadSettingAsync<DesktopSettings>(GlobalScope, DesktopSettingKey, _cancellation.Token)
            ?? new DesktopSettings(false, true, false, false, 1.0, 0.70, 200m, null, 1);
        var tolerancePoints = settings.PriceDistanceVersion >= 1
            ? settings.LossZoneTolerance
            : settings.LossZoneTolerance * 100m;
        await OnUiAsync(() =>
        {
            _viewModel.IsFocusMode = settings.FocusMode;
            _viewModel.IsTopmost = settings.Topmost;
            _viewModel.IsPositionLocked = settings.PositionLocked;
            _viewModel.IsMouseThrough = settings.MouseThrough;
            _viewModel.PetOpacity = settings.Opacity;
            _viewModel.PetScale = settings.Scale;
            _viewModel.LossZoneTolerance = tolerancePoints;
            _viewModel.SelectedTerminalPath = settings.TerminalPath;
            _viewModel.ExpandCardOnHover = settings.InteractionVersion >= 1
                ? settings.ExpandCardOnHover
                : true;
            _viewModel.MiniPositionVisible = settings.MiniPositionVisible;
            _viewModel.MiniPositionPinned = settings.MiniPositionPinned;
            _viewModel.DailyReportEnabled = settings.DailyReportEnabled;
            _viewModel.DailyReportTimeText = NormalizeDailyReportTime(settings.DailyReportTime);
            _viewModel.StartWithWindows = StartupRegistration.IsEnabled();
        });
    }

    private void UpdateWorkerDiagnostic(string message, string setupScript)
    {
        AppLog.Write(message);
        if (message.Contains("No module named", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("MetaTrader5", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("ModuleNotFound", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Write($"Worker setup script: {setupScript}");
            _ = OnUiAsync(() => _viewModel.DiagnosticText = "数据采集环境未就绪，请运行发布包中的环境修复脚本。");
            return;
        }

        _ = OnUiAsync(() => _viewModel.DiagnosticText = "数据采集组件发来诊断信息，详细内容已写入日志。");
    }

    private void UpdateBridgeDiagnostic(string message)
    {
        AppLog.Write(message);
        var display = message.Contains("connected", StringComparison.OrdinalIgnoreCase) &&
                      !message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
            ? "桥接插件已连接。"
            : message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
                ? "桥接插件已断开，正在等待重连。"
                : "桥接插件发来诊断信息，详细内容已写入日志。";
        _ = OnUiAsync(() => _viewModel.DiagnosticText = display);
    }

    private DateOnly ResolveServerDate(DateTimeOffset occurredAtUtc) =>
        _serverClock.Resolve(occurredAtUtc);

    private (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ResolveServerDayUtcRange(DateOnly serverDate) =>
        _serverClock.ResolveDayUtcRange(serverDate);

    private decimal CalculateObservedHighWater()
    {
        var currentCombined = _trades.Values
            .Where(trade => trade.IsComplete && trade.CloseServerDate == _serverDate)
            .Sum(trade => trade.NetPnl) +
            _positions.Values.Sum(position => position.Profit + position.Swap);
        if (!_hasInitialDeals)
        {
            return Math.Max(0m, currentCombined);
        }

        var dailyDeals = _deals.Values
            .Where(deal => ResolveServerDate(deal.OccurredAtUtc) == _serverDate)
            .ToArray();
        return _dailyHighWaterCalculator.Calculate(
            dailyDeals,
            _equitySamples,
            _cashFlows.Values.ToArray(),
            currentCombined);
    }

    private IReadOnlyList<TradeRecord> ProjectTrades() =>
        !SupportsCompleteTradeProjection
            ? []
            : _tradeProjector.Project(_account!.Scope.AccountKey, _deals.Values, ResolveServerDate)
            .Select(trade => trade.IsComplete && _exactCloseServerDates.TryGetValue(trade.PositionId, out var serverDate)
                ? trade with { CloseServerDate = serverDate }
                : trade)
            .ToArray();

    private decimal CalculateDailyRealizedPnl() => _deals.Values
        .Where(deal => ResolveServerDate(deal.OccurredAtUtc) == _serverDate)
        .Sum(deal => deal.NetPnl);

    private bool SupportsCompleteTradeProjection => _account?.MarginMode == 2;

    private decimal ResolveLossZoneTolerance(TradeRecord trade)
    {
        _symbolSpecifications.TryGetValue(trade.Symbol, out var specification);
        return PriceDistancePolicy.ToPriceDistance(
            _viewModel.LossZoneTolerance,
            specification,
            Math.Max(0.01m, _viewModel.LossZoneTolerance / 100m));
    }

    private async Task UpdateSymbolSpecificationsAsync(
        string accountKey,
        IReadOnlyCollection<SymbolSpecification> specifications)
    {
        if (specifications.Count == 0)
        {
            return;
        }

        var changed = specifications
            .Where(specification =>
                !_symbolSpecifications.TryGetValue(specification.Symbol, out var current) ||
                current != specification)
            .ToArray();
        if (changed.Length == 0)
        {
            return;
        }
        foreach (var specification in changed)
        {
            _symbolSpecifications[specification.Symbol] = specification;
        }
        await TryPersistLiveAsync(
            () => _database.UpsertSymbolSpecificationsAsync(accountKey, changed, _cancellation.Token),
            "保存品种规格");
    }

    private TradeRecord ProvisionalTrade(PositionSnapshot position) => new(
        _account!.Scope.AccountKey,
        position.PositionId,
        position.Symbol,
        position.Side,
        position.OpenedAtUtc,
        null,
        ResolveServerDate(position.OpenedAtUtc),
        null,
        position.EntryPrice,
        null,
        position.Volume,
        position.Volume,
        position.Volume,
        0m,
        false);

    private static string CreatePlanItemId(string accountKey, DateOnly serverDate, string objectKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{accountKey}|{serverDate:yyyy-MM-dd}|{objectKey}"));
        return $"plan-{Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private void UpdateDiagnostic(string message)
    {
        AppLog.Write(message);
        _ = OnUiAsync(() => _viewModel.DiagnosticText = message);
    }

    private static string FloatingLossFallbackPath =>
        Path.Combine(TradePetPaths.GetDataDirectory(), FloatingLossFallbackFileName);

    private async Task<FloatingLossAlertPolicy?> LoadFloatingLossFallbackAsync(string accountKey)
    {
        try
        {
            if (!File.Exists(FloatingLossFallbackPath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(FloatingLossFallbackPath, _cancellation.Token);
            var policies = JsonSerializer.Deserialize<Dictionary<string, FloatingLossAlertPolicy>>(json);
            return policies is not null && policies.TryGetValue(accountKey, out var policy)
                ? policy.Normalize()
                : null;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Floating-loss fallback settings could not be loaded: {exception}");
            return null;
        }
    }

    private async Task<bool> SaveFloatingLossFallbackAsync(string accountKey, FloatingLossAlertPolicy policy)
    {
        try
        {
            Dictionary<string, FloatingLossAlertPolicy> policies;
            if (File.Exists(FloatingLossFallbackPath))
            {
                var json = await File.ReadAllTextAsync(FloatingLossFallbackPath, _cancellation.Token);
                policies = JsonSerializer.Deserialize<Dictionary<string, FloatingLossAlertPolicy>>(json)
                    ?? new Dictionary<string, FloatingLossAlertPolicy>(StringComparer.Ordinal);
            }
            else
            {
                policies = new Dictionary<string, FloatingLossAlertPolicy>(StringComparer.Ordinal);
            }

            policies[accountKey] = policy.Normalize();
            Directory.CreateDirectory(TradePetPaths.GetDataDirectory());
            var temporaryPath = FloatingLossFallbackPath + ".tmp";
            var output = JsonSerializer.Serialize(policies, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporaryPath, output, _cancellation.Token);
            File.Move(temporaryPath, FloatingLossFallbackPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Floating-loss fallback settings could not be saved: {exception}");
            return false;
        }
    }

    private async Task<bool> TryPersistLiveAsync(Func<Task> persistenceAction, string operation)
    {
        if (!_persistenceAvailable)
        {
            return false;
        }

        try
        {
            await persistenceAction();
            return true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportPersistenceFailureAsync(operation, exception);
            return false;
        }
    }

    private async Task ReportPersistenceFailureAsync(string operation, Exception exception)
    {
        var now = _timeProvider.GetUtcNow();
        if (_lastPersistenceWarningAtUtc != default &&
            now - _lastPersistenceWarningAtUtc < TimeSpan.FromSeconds(30))
        {
            _suppressedPersistenceWarnings++;
            return;
        }

        var suppressed = _suppressedPersistenceWarnings > 0
            ? $"；期间另有 {_suppressedPersistenceWarnings} 次同类失败"
            : string.Empty;
        _lastPersistenceWarningAtUtc = now;
        _suppressedPersistenceWarnings = 0;
        AppLog.Write($"Persistence operation '{operation}' failed; live feedback continued{suppressed}: {exception}");
        await OnUiAsync(() =>
        {
            _viewModel.DiagnosticText = "本地记录暂时不可用，但实时下单提醒仍在工作。";
            _viewModel.ReviewWorkspace.StorageStatus =
                $"本地 SQLite 写入失败（{operation}）；本次更改未标为已保存。";
        });
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes:00}:{duration.Seconds:00}";

    private static string FormatSide(TradeSide side) => side == TradeSide.Buy ? "买入" : "卖出";

    private static string FormatTradeChange(TradeDomainEventKind kind) => kind switch
    {
        TradeDomainEventKind.StopLossAdded => "新增止损",
        TradeDomainEventKind.StopLossModified => "修改止损",
        TradeDomainEventKind.StopLossRemoved => "移除止损",
        TradeDomainEventKind.TakeProfitAdded => "新增止盈",
        TradeDomainEventKind.TakeProfitModified => "修改止盈",
        TradeDomainEventKind.TakeProfitRemoved => "移除止盈",
        _ => "交易状态变化",
    };

    private string FormatRiskText(DailyState? state)
    {
        if (!_workerConnected)
        {
            return "等待交易终端";
        }
        if (state?.LossAlerted == true)
        {
            return "已触及每日亏损线";
        }
        if (state?.GivebackAlerted == true)
        {
            return "盈利回吐提醒";
        }
        if (state is not null && state.ConsecutiveLosses >= EffectiveAlertSettings().ConsecutiveLossThreshold)
        {
            return $"连续亏损 {state.ConsecutiveLosses} 单";
        }

        return "正常";
    }

    private static TradeRecord ReplayTrade(
        string account, long positionId, TradeSide side, decimal entry, decimal volume, decimal pnl,
        DateTimeOffset opened, DateTimeOffset? closed, DateOnly date) =>
        new(account, positionId, "XAUUSD.s", side, opened, closed, date, closed is null ? null : date,
            entry, closed is null ? null : entry, volume, volume, closed is null ? volume : 0m, pnl, closed is not null);

    private static DateTimeOffset ReplayAt(int hour, int minute) =>
        new(2026, 8, 30, hour, minute, 0, TimeSpan.Zero);

    private static async Task OnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            await dispatcher.InvokeAsync(action);
        }
    }

    private sealed record DesktopSettings(
        bool FocusMode,
        bool Topmost,
        bool PositionLocked,
        bool MouseThrough,
        double Opacity,
        double Scale,
        decimal LossZoneTolerance,
        string? TerminalPath,
        int PriceDistanceVersion = 0,
        bool ExpandCardOnHover = false,
        bool MiniPositionVisible = true,
        bool MiniPositionPinned = false,
        int InteractionVersion = 0,
        bool DailyReportEnabled = true,
        string DailyReportTime = "23:55");

    private sealed record BehaviorReviewEditCommand(
        string AccountKey,
        string OccurrenceId,
        string Explanation,
        bool EvidenceInsufficient,
        int ExpectedRevision);

}

public sealed record ShutdownPreparationResult(
    bool CanExit,
    string Message,
    string? DraftPath);
