using System.Collections.ObjectModel;
using System.Windows.Input;
using TradePet.Application.Runtime;
using TradePet.App.ViewModels.Review;
using TradePet.Core.Domain;

namespace TradePet.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private string _connectionText = "正在寻找交易终端…";
    private string _bridgeText = "桥接插件未连接";
    private string _accountLabel = "未连接账户";
    private string _serverDateText = "--";
    private decimal _realizedPnl;
    private decimal _floatingPnl;
    private decimal _currentFloatingLossPercentage;
    private decimal _highWaterPnl;
    private decimal _giveback;
    private int _tradeCount;
    private int _winCount;
    private int _lossCount;
    private int _consecutiveLosses;
    private decimal _maximumExposure;
    private int _openPositionCount;
    private decimal _currentExposure;
    private decimal? _dailyTarget;
    private decimal? _dailyLoss;
    private int? _maximumTrades;
    private decimal? _maximumLot;
    private bool _stopLossReminderEnabled;
    private int _stopLossReminderSeconds = 60;
    private decimal _lossZoneTolerance = 200m;
    private decimal _givebackValue = 50m;
    private bool _isPlanRecording;
    private bool _isFocusMode;
    private bool _isTopmost = true;
    private bool _isPositionLocked;
    private bool _isMouseThrough;
    private bool _startWithWindows;
    private bool _dailyReportEnabled = true;
    private string _dailyReportTimeText = "23:55";
    private double _petOpacity = 1.0;
    private double _petScale = 0.70;
    private PetActivity _petActivity = PetActivity.Idle;
    private bool _isBubbleVisible;
    private string _bubbleHeadline = string.Empty;
    private string _bubbleDetails = string.Empty;
    private bool _isBubbleLossZoneVisible;
    private string _bubbleZoneSymbol = string.Empty;
    private string _bubbleZoneRangeText = string.Empty;
    private string _bubbleZoneLowText = string.Empty;
    private string _bubbleZoneCenterText = string.Empty;
    private string _bubbleZoneHighText = string.Empty;
    private string _bubbleZoneCurrentText = string.Empty;
    private string _bubbleZoneStatsText = string.Empty;
    private string _bubbleZonePnlText = string.Empty;
    private double _bubbleZonePositionPercent = 50d;
    private bool _hasPositions;
    private string _positionCard = "当前无持仓";
    private bool _positionDataStale = true;
    private bool _miniPositionVisible = true;
    private bool _miniPositionPinned;
    private bool _expandCardOnHover;
    private bool _isAlertActionVisible;
    private bool _canMuteCurrentAlert;
    private string _diagnosticText = "天禄正在启动";
    private string? _selectedTerminalPath;
    private string _riskText = "等待交易终端";
    private string _reviewSyncText = "历史数据待同步";
    private string _behaviorRiskText = "观察中";
    private string _todayBehaviorRiskText = "观察中";
    private string _behaviorPresetText = "平衡";
    private string _behaviorTriggeredText = "当前没有触发规则";
    private string _todayBehaviorTriggeredText = "当前没有触发规则";
    private string _selectedBehaviorPreset = "平衡";
    private string _newPlanSymbol = "XAUUSD.s";
    private string _newPlanReferenceEntry = string.Empty;
    private string _newPlanEntryLow = string.Empty;
    private string _newPlanEntryHigh = string.Empty;
    private string _newPlanStop = string.Empty;
    private string _newPlanTarget = string.Empty;
    private string _newPlanStrategy = string.Empty;
    private string _newPlanSetup = string.Empty;
    private string _newPlanTags = string.Empty;
    private string _newPlanNotes = string.Empty;
    private string _selectedReviewPeriod = "近30天";
    private string _reviewSymbolFilter = string.Empty;
    private string _selectedReviewSideFilter = "全部";
    private string _reviewFromDateText = string.Empty;
    private string _reviewToDateText = string.Empty;
    private string _reviewRangeText = "等待历史查询";
    private bool _hasReviewTrades;
    private ReviewCategoryTab? _selectedReviewCategory;
    private TradeSide _newPlanSide = TradeSide.Buy;

    public MainViewModel(IAsyncScheduler? scheduler = null, TimeProvider? timeProvider = null)
    {
        ReviewWorkspace = new ReviewWorkspaceViewModel(scheduler, timeProvider);
        TogglePlanRecordingCommand = AsyncCommand(() => TogglePlanRecordingAsync?.Invoke() ?? Task.CompletedTask);
        ImportPlanCommand = AsyncCommand(() => ImportPlanAsync?.Invoke() ?? Task.CompletedTask);
        SaveSettingsCommand = AsyncCommand(() => SaveSettingsAsync?.Invoke() ?? Task.CompletedTask);
        InstallBridgeCommand = AsyncCommand(() => InstallBridgeAsync?.Invoke() ?? Task.CompletedTask);
        ReplayScenarioCommand = AsyncCommand(() => ReplayScenarioAsync?.Invoke() ?? Task.CompletedTask);
        CreateStructuredPlanCommand = AsyncCommand(() => CreateStructuredPlanAsync?.Invoke() ?? Task.CompletedTask);
        ApplyBehaviorPresetCommand = AsyncCommand(() => ApplyBehaviorPresetAsync?.Invoke() ?? Task.CompletedTask);
        SaveBehaviorSettingsCommand = AsyncCommand(() => SaveBehaviorSettingsAsync?.Invoke() ?? Task.CompletedTask);
        RefreshReviewCommand = AsyncCommand(() => RefreshReviewAsync?.Invoke() ?? Task.CompletedTask);
        ToggleFocusCommand = new RelayCommand(() => IsFocusMode = !IsFocusMode);
        ShowMainWindowCommand = new RelayCommand(() => ShowMainWindow?.Invoke());
        ShowPlanPageCommand = new RelayCommand(() => ShowConsolePage?.Invoke(1));
        ShowLossZonesPageCommand = new RelayCommand(() => ShowConsolePage?.Invoke(2));
        ShowReviewPageCommand = new RelayCommand(() => ShowConsolePage?.Invoke(3));
        ShowDailyTradingReportCommand = AsyncCommand(() => ShowDailyTradingReportAsync?.Invoke() ?? Task.CompletedTask);
        ShowMacroCalendarCommand = new RelayCommand(() => ShowMacroCalendar?.Invoke());
        ShowTimelinePageCommand = new RelayCommand(() => ShowConsolePage?.Invoke(4));
        ShowSettingsPageCommand = new RelayCommand(() => ShowConsolePage?.Invoke(5));
        HidePetCommand = new RelayCommand(() => HidePet?.Invoke());
        ExitCommand = AsyncCommand(() => ExitApplicationAsync?.Invoke() ?? Task.CompletedTask);
    }

    public Func<Task>? TogglePlanRecordingAsync { get; set; }
    public Func<Task>? ImportPlanAsync { get; set; }
    public Func<Task>? SaveSettingsAsync { get; set; }
    public Func<Task>? InstallBridgeAsync { get; set; }
    public Func<Task>? ReplayScenarioAsync { get; set; }
    public Func<Task>? CreateStructuredPlanAsync { get; set; }
    public Func<Task>? ApplyBehaviorPresetAsync { get; set; }
    public Func<Task>? SaveBehaviorSettingsAsync { get; set; }
    public Func<Task>? RefreshReviewAsync { get; set; }
    public Func<Task>? ShowDailyTradingReportAsync { get; set; }
    public Action? ShowMacroCalendar { get; set; }
    public Action? ShowMainWindow { get; set; }
    public Action<int>? ShowConsolePage { get; set; }
    public Action? HidePet { get; set; }
    public Func<Task>? ExitApplicationAsync { get; set; }

    public ICommand TogglePlanRecordingCommand { get; }
    public ICommand ImportPlanCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand InstallBridgeCommand { get; }
    public ICommand ReplayScenarioCommand { get; }
    public ICommand CreateStructuredPlanCommand { get; }
    public ICommand ApplyBehaviorPresetCommand { get; }
    public ICommand SaveBehaviorSettingsCommand { get; }
    public ICommand RefreshReviewCommand { get; }
    public ICommand ToggleFocusCommand { get; }
    public ICommand ShowMainWindowCommand { get; }
    public ICommand ShowPlanPageCommand { get; }
    public ICommand ShowLossZonesPageCommand { get; }
    public ICommand ShowReviewPageCommand { get; }
    public ICommand ShowDailyTradingReportCommand { get; }
    public ICommand ShowMacroCalendarCommand { get; }
    public ICommand ShowTimelinePageCommand { get; }
    public ICommand ShowSettingsPageCommand { get; }
    public ICommand HidePetCommand { get; }
    public ICommand ExitCommand { get; }
    public Action? AcknowledgeAlert { get; set; }
    public Action? SnoozeAlert { get; set; }
    public Action? MuteCurrentTradeAlert { get; set; }

    private AsyncRelayCommand AsyncCommand(Func<Task> execute) =>
        new(execute, onError: exception =>
        {
            System.Diagnostics.Trace.TraceError(exception.ToString());
            DiagnosticText = "操作执行失败，详细信息已写入诊断输出。";
        });

    public ObservableCollection<PositionRowViewModel> Positions { get; } = [];
    public ObservableCollection<PlanItemRowViewModel> PlanItems { get; } = [];
    public ObservableCollection<LossZoneRowViewModel> LossZones { get; } = [];
    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = [];
    public ObservableCollection<TerminalOption> TerminalOptions { get; } = [];
    public ObservableCollection<ReviewMetricRow> ReviewMetrics { get; } = [];
    public ObservableCollection<ReviewGroupRow> ReviewGroups { get; } = [];
    public ObservableCollection<ReviewCategoryTab> ReviewCategories { get; } = [];
    public ObservableCollection<ReviewGroupRow> VisibleReviewGroups { get; } = [];
    public ObservableCollection<BehaviorMetricRow> BehaviorMetrics { get; } = [];
    public ObservableCollection<ReviewTradeRowViewModel> ReviewTrades { get; } = [];
    public ObservableCollection<BehaviorSettingRow> BehaviorSettings { get; } = [];
    public ObservableCollection<FloatingLossLevelRow> FloatingLossLevels { get; } = [];
    public ObservableCollection<StructuredTradePlanRow> StructuredPlans { get; } = [];
    public ReviewWorkspaceViewModel ReviewWorkspace { get; }
    public IReadOnlyList<TradeSideOption> TradeSideOptions { get; } =
    [
        new(TradeSide.Buy, "买入"),
        new(TradeSide.Sell, "卖出"),
    ];
    public IReadOnlyList<PlanCategoryOption> PlanCategoryOptions { get; } =
    [
        new(PlanCategory.Unclassified, "待分类"),
        new(PlanCategory.Support, "支撑线"),
        new(PlanCategory.Resistance, "阻力线"),
        new(PlanCategory.BreakoutWatch, "突破观察"),
        new(PlanCategory.EntryWatch, "入场观察"),
        new(PlanCategory.StopReference, "止损参考"),
        new(PlanCategory.MarkOnly, "仅作标记"),
        new(PlanCategory.LongWatchZone, "做多观察区"),
        new(PlanCategory.ShortWatchZone, "做空观察区"),
        new(PlanCategory.NoTradeZone, "不交易区"),
        new(PlanCategory.KeyZone, "关键区域"),
        new(PlanCategory.Note, "计划备注"),
    ];

    public string ConnectionText { get => _connectionText; set => SetProperty(ref _connectionText, value); }
    public string GreetingText => DateTime.Now.Hour switch
    {
        < 6 => "夜深了，交易者",
        < 12 => "早上好，交易者",
        < 18 => "下午好，交易者",
        _ => "晚上好，交易者",
    };
    public string BridgeText { get => _bridgeText; set => SetProperty(ref _bridgeText, value); }
    public string AccountLabel { get => _accountLabel; set => SetProperty(ref _accountLabel, value); }
    public string ServerDateText { get => _serverDateText; set => SetProperty(ref _serverDateText, value); }
    public decimal RealizedPnl { get => _realizedPnl; set { if (SetProperty(ref _realizedPnl, value)) { RaisePropertyChanged(nameof(RealizedPnlColor)); NotifyTotalPnlChanged(); } } }
    public decimal FloatingPnl { get => _floatingPnl; set { if (SetProperty(ref _floatingPnl, value)) { RaisePropertyChanged(nameof(FloatingPnlColor)); NotifyTotalPnlChanged(); } } }
    public decimal TotalPnl => RealizedPnl + FloatingPnl;
    public bool IsTotalPnlNegative => TotalPnl < 0m;
    public string TotalPnlColor => FinancialPalette.For(TotalPnl);
    public string TotalPnlBackground => FinancialPalette.BackgroundFor(TotalPnl);
    public string RealizedPnlColor => FinancialPalette.For(RealizedPnl);
    public string FloatingPnlColor => FinancialPalette.For(FloatingPnl);
    public decimal CurrentFloatingLossPercentage
    {
        get => _currentFloatingLossPercentage;
        set
        {
            if (SetProperty(ref _currentFloatingLossPercentage, Math.Max(0m, value)))
            {
                RaisePropertyChanged(nameof(CurrentFloatingLossPercentageColor));
            }
        }
    }
    public string CurrentFloatingLossPercentageColor => CurrentFloatingLossPercentage > 0.01m
        ? FinancialPalette.Loss
        : FinancialPalette.Neutral;
    public decimal HighWaterPnl { get => _highWaterPnl; set { if (SetProperty(ref _highWaterPnl, value)) RaisePropertyChanged(nameof(HighWaterPnlColor)); } }
    public string HighWaterPnlColor => FinancialPalette.For(HighWaterPnl);
    public decimal Giveback { get => _giveback; set { if (SetProperty(ref _giveback, value)) RaisePropertyChanged(nameof(GivebackColor)); } }
    public string GivebackColor => Giveback > 0.01m ? FinancialPalette.Loss : FinancialPalette.Neutral;
    public int TradeCount { get => _tradeCount; set => SetProperty(ref _tradeCount, value); }
    public int WinCount { get => _winCount; set { if (SetProperty(ref _winCount, value)) RaisePropertyChanged(nameof(WinCountColor)); } }
    public string WinCountColor => WinCount > 0 ? FinancialPalette.Profit : FinancialPalette.Neutral;
    public int LossCount { get => _lossCount; set { if (SetProperty(ref _lossCount, value)) RaisePropertyChanged(nameof(LossCountColor)); } }
    public string LossCountColor => LossCount > 0 ? FinancialPalette.Loss : FinancialPalette.Neutral;
    public int ConsecutiveLosses { get => _consecutiveLosses; set { if (SetProperty(ref _consecutiveLosses, value)) RaisePropertyChanged(nameof(ConsecutiveLossesColor)); } }
    public string ConsecutiveLossesColor => ConsecutiveLosses > 0 ? FinancialPalette.Loss : FinancialPalette.Neutral;
    public decimal MaximumExposure { get => _maximumExposure; set => SetProperty(ref _maximumExposure, value); }
    public int OpenPositionCount { get => _openPositionCount; set => SetProperty(ref _openPositionCount, value); }
    public decimal CurrentExposure { get => _currentExposure; set => SetProperty(ref _currentExposure, value); }
    public decimal? DailyTarget { get => _dailyTarget; set => SetProperty(ref _dailyTarget, value); }
    public decimal? DailyLoss { get => _dailyLoss; set => SetProperty(ref _dailyLoss, value); }
    public int? MaximumTrades { get => _maximumTrades; set => SetProperty(ref _maximumTrades, value); }
    public decimal? MaximumLot { get => _maximumLot; set => SetProperty(ref _maximumLot, value); }
    public bool StopLossReminderEnabled { get => _stopLossReminderEnabled; set => SetProperty(ref _stopLossReminderEnabled, value); }
    public int StopLossReminderSeconds { get => _stopLossReminderSeconds; set => SetProperty(ref _stopLossReminderSeconds, Math.Max(1, value)); }
    public decimal LossZoneTolerance { get => _lossZoneTolerance; set => SetProperty(ref _lossZoneTolerance, Math.Max(1m, value)); }
    public decimal GivebackValue { get => _givebackValue; set => SetProperty(ref _givebackValue, Math.Clamp(value, 1m, 100m)); }
    public bool IsPlanRecording { get => _isPlanRecording; set => SetProperty(ref _isPlanRecording, value); }
    public string PlanRecordingText => IsPlanRecording ? "结束记录今日计划" : "开始记录今日计划";
    public bool IsFocusMode { get => _isFocusMode; set => SetProperty(ref _isFocusMode, value); }
    public bool IsTopmost { get => _isTopmost; set => SetProperty(ref _isTopmost, value); }
    public bool IsPositionLocked { get => _isPositionLocked; set => SetProperty(ref _isPositionLocked, value); }
    public bool IsMouseThrough { get => _isMouseThrough; set => SetProperty(ref _isMouseThrough, value); }
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool DailyReportEnabled { get => _dailyReportEnabled; set => SetProperty(ref _dailyReportEnabled, value); }
    public string DailyReportTimeText { get => _dailyReportTimeText; set => SetProperty(ref _dailyReportTimeText, value); }
    public double PetOpacity { get => _petOpacity; set => SetProperty(ref _petOpacity, Math.Clamp(value, 0.25, 1.0)); }
    public double PetScale { get => _petScale; set => SetProperty(ref _petScale, Math.Clamp(value, 0.4, 1.5)); }
    public PetActivity PetActivity { get => _petActivity; set => SetProperty(ref _petActivity, value); }
    public bool IsBubbleVisible { get => _isBubbleVisible; set => SetProperty(ref _isBubbleVisible, value); }
    public string BubbleHeadline { get => _bubbleHeadline; set => SetProperty(ref _bubbleHeadline, value); }
    public string BubbleDetails { get => _bubbleDetails; set => SetProperty(ref _bubbleDetails, value); }
    public bool IsBubbleLossZoneVisible { get => _isBubbleLossZoneVisible; set => SetProperty(ref _isBubbleLossZoneVisible, value); }
    public string BubbleZoneSymbol { get => _bubbleZoneSymbol; set => SetProperty(ref _bubbleZoneSymbol, value); }
    public string BubbleZoneRangeText { get => _bubbleZoneRangeText; set => SetProperty(ref _bubbleZoneRangeText, value); }
    public string BubbleZoneLowText { get => _bubbleZoneLowText; set => SetProperty(ref _bubbleZoneLowText, value); }
    public string BubbleZoneCenterText { get => _bubbleZoneCenterText; set => SetProperty(ref _bubbleZoneCenterText, value); }
    public string BubbleZoneHighText { get => _bubbleZoneHighText; set => SetProperty(ref _bubbleZoneHighText, value); }
    public string BubbleZoneCurrentText { get => _bubbleZoneCurrentText; set => SetProperty(ref _bubbleZoneCurrentText, value); }
    public string BubbleZoneStatsText { get => _bubbleZoneStatsText; set => SetProperty(ref _bubbleZoneStatsText, value); }
    public string BubbleZonePnlText { get => _bubbleZonePnlText; set => SetProperty(ref _bubbleZonePnlText, value); }
    public double BubbleZonePositionPercent { get => _bubbleZonePositionPercent; set => SetProperty(ref _bubbleZonePositionPercent, Math.Clamp(value, 0d, 100d)); }
    public bool HasPositions
    {
        get => _hasPositions;
        set
        {
            if (SetProperty(ref _hasPositions, value))
            {
                RaisePropertyChanged(nameof(HasNoPositions));
            }
        }
    }
    public bool HasNoPositions => !HasPositions;
    public string PositionCard { get => _positionCard; set => SetProperty(ref _positionCard, value); }
    public bool PositionDataStale { get => _positionDataStale; set { if (SetProperty(ref _positionDataStale, value)) RaisePropertyChanged(nameof(PositionDataStatus)); } }
    public string PositionDataStatus => PositionDataStale ? "数据已过期" : "实时";
    public bool MiniPositionVisible { get => _miniPositionVisible; set => SetProperty(ref _miniPositionVisible, value); }
    public bool MiniPositionPinned { get => _miniPositionPinned; set => SetProperty(ref _miniPositionPinned, value); }
    public bool ExpandCardOnHover { get => _expandCardOnHover; set => SetProperty(ref _expandCardOnHover, value); }
    public bool IsAlertActionVisible { get => _isAlertActionVisible; set => SetProperty(ref _isAlertActionVisible, value); }
    public bool CanMuteCurrentAlert { get => _canMuteCurrentAlert; set => SetProperty(ref _canMuteCurrentAlert, value); }
    public string DiagnosticText { get => _diagnosticText; set => SetProperty(ref _diagnosticText, value); }
    public string? SelectedTerminalPath { get => _selectedTerminalPath; set => SetProperty(ref _selectedTerminalPath, value); }
    public string RiskText { get => _riskText; set => SetProperty(ref _riskText, value); }
    public string ReviewSyncText { get => _reviewSyncText; set => SetProperty(ref _reviewSyncText, value); }
    public string BehaviorRiskText { get => _behaviorRiskText; set => SetProperty(ref _behaviorRiskText, value); }
    public string TodayBehaviorRiskText { get => _todayBehaviorRiskText; set => SetProperty(ref _todayBehaviorRiskText, value); }
    public string BehaviorPresetText { get => _behaviorPresetText; set => SetProperty(ref _behaviorPresetText, value); }
    public string BehaviorTriggeredText { get => _behaviorTriggeredText; set => SetProperty(ref _behaviorTriggeredText, value); }
    public string TodayBehaviorTriggeredText { get => _todayBehaviorTriggeredText; set => SetProperty(ref _todayBehaviorTriggeredText, value); }
    public string SelectedBehaviorPreset { get => _selectedBehaviorPreset; set => SetProperty(ref _selectedBehaviorPreset, value); }
    public IReadOnlyList<string> BehaviorPresetOptions { get; } = ["保守", "平衡", "宽松"];
    public IReadOnlyList<string> ReviewPeriodOptions { get; } =
        ["今天", "本周", "本月", "近30天", "近90天", "今年", "全部历史", "自定义"];
    public IReadOnlyList<string> ReviewSideFilterOptions { get; } = ["全部", "买入", "卖出"];
    public IReadOnlyList<PlanComplianceOption> PlanComplianceOptions { get; } =
    [
        new(PlanComplianceStatus.Unclassified, "未归类"),
        new(PlanComplianceStatus.Matched, "自动：计划内"),
        new(PlanComplianceStatus.OutsidePlan, "自动：计划外"),
        new(PlanComplianceStatus.ManualInside, "人工：计划内"),
        new(PlanComplianceStatus.ManualOutside, "人工：计划外"),
    ];
    public string SelectedReviewPeriod { get => _selectedReviewPeriod; set => SetProperty(ref _selectedReviewPeriod, value); }
    public string ReviewSymbolFilter { get => _reviewSymbolFilter; set => SetProperty(ref _reviewSymbolFilter, value); }
    public string SelectedReviewSideFilter { get => _selectedReviewSideFilter; set => SetProperty(ref _selectedReviewSideFilter, value); }
    public string ReviewFromDateText { get => _reviewFromDateText; set => SetProperty(ref _reviewFromDateText, value); }
    public string ReviewToDateText { get => _reviewToDateText; set => SetProperty(ref _reviewToDateText, value); }
    public string ReviewRangeText { get => _reviewRangeText; set => SetProperty(ref _reviewRangeText, value); }
    public ReviewCategoryTab? SelectedReviewCategory
    {
        get => _selectedReviewCategory;
        set
        {
            if (SetProperty(ref _selectedReviewCategory, value))
            {
                RefreshVisibleReviewGroups();
            }
        }
    }
    public bool HasReviewTrades
    {
        get => _hasReviewTrades;
        set
        {
            if (SetProperty(ref _hasReviewTrades, value))
            {
                RaisePropertyChanged(nameof(HasNoReviewTrades));
            }
        }
    }
    public bool HasNoReviewTrades => !HasReviewTrades;
    public string NewPlanSymbol { get => _newPlanSymbol; set => SetProperty(ref _newPlanSymbol, value); }
    public TradeSide NewPlanSide { get => _newPlanSide; set => SetProperty(ref _newPlanSide, value); }
    public string NewPlanReferenceEntry { get => _newPlanReferenceEntry; set => SetProperty(ref _newPlanReferenceEntry, value); }
    public string NewPlanEntryLow { get => _newPlanEntryLow; set => SetProperty(ref _newPlanEntryLow, value); }
    public string NewPlanEntryHigh { get => _newPlanEntryHigh; set => SetProperty(ref _newPlanEntryHigh, value); }
    public string NewPlanStop { get => _newPlanStop; set => SetProperty(ref _newPlanStop, value); }
    public string NewPlanTarget { get => _newPlanTarget; set => SetProperty(ref _newPlanTarget, value); }
    public string NewPlanStrategy { get => _newPlanStrategy; set => SetProperty(ref _newPlanStrategy, value); }
    public string NewPlanSetup { get => _newPlanSetup; set => SetProperty(ref _newPlanSetup, value); }
    public string NewPlanTags { get => _newPlanTags; set => SetProperty(ref _newPlanTags, value); }
    public string NewPlanNotes { get => _newPlanNotes; set => SetProperty(ref _newPlanNotes, value); }

    public void ApplyReviewSnapshot(ReviewSnapshot snapshot)
    {
        HasReviewTrades = snapshot.Trades.Count > 0;
        ReviewMetrics.Clear();
        var p = snapshot.Performance;
        ReviewMetrics.Add(new("净盈亏", FormatSigned(p.NetPnl), "含手续费、隔夜费和其他费用", FinancialPalette.For(p.NetPnl)));
        ReviewMetrics.Add(new("胜率", $"{p.WinRate:0.##}%", $"盈利 {p.WinCount} / 亏损 {p.LossCount} / 保本 {p.BreakevenCount}"));
        ReviewMetrics.Add(new("盈亏因子", p.ProfitFactor is null ? "无亏损样本" : p.ProfitFactor.Value.ToString("0.##"), "总盈利 ÷ 总亏损绝对值"));
        ReviewMetrics.Add(new("期望值", FormatSigned(p.Expectancy), "每笔完整交易平均净盈亏", FinancialPalette.For(p.Expectancy)));
        ReviewMetrics.Add(new("平均盈亏比", p.AverageWinLossRatio?.ToString("0.##") ?? "暂无", "平均盈利 ÷ 平均亏损"));
        ReviewMetrics.Add(new("最大回撤", $"{p.MaximumDrawdown:0.##}{(p.MaximumDrawdownPercentage is null ? string.Empty : $"（{p.MaximumDrawdownPercentage:0.##}%）")}", "已实现净盈亏曲线", p.MaximumDrawdown > 0.01m ? FinancialPalette.Loss : FinancialPalette.Neutral));
        ReviewMetrics.Add(new("平均回撤", p.AverageDrawdown.ToString("0.##"), "回撤区间平均值", p.AverageDrawdown > 0.01m ? FinancialPalette.Loss : FinancialPalette.Neutral));
        ReviewMetrics.Add(new("恢复因子", p.RecoveryFactor?.ToString("0.##") ?? "暂无", "净利润 ÷ 最大回撤", p.RecoveryFactor is null ? FinancialPalette.Neutral : FinancialPalette.For(p.RecoveryFactor.Value)));
        ReviewMetrics.Add(new("连胜 / 连亏", $"{p.MaximumWinStreak} / {p.MaximumLossStreak}", "历史最大连续次数"));
        ReviewMetrics.Add(new("平均持仓", FormatDuration(p.AverageHoldingTime), "完整交易首入场至最终平仓"));
        ReviewMetrics.Add(new("总首开量", $"{p.TotalOpeningVolume:0.##} 手", $"每笔首开：平均 {p.AverageOpeningVolume:0.##}，最大 {p.MaximumOpeningVolume:0.##}"));
        ReviewMetrics.Add(new("每日胜率", $"{p.DailyWinRate:0.##}%", $"保本日 {p.BreakevenDayCount} 天"));
        ReviewMetrics.Add(new("计划 / 实际风险倍数", $"{p.AveragePlannedRiskMultiple?.ToString("0.##") ?? "暂无"} / {p.AverageActualRiskMultiple?.ToString("0.##") ?? "暂无"}", "实际 R 仅统计开仓即采到止损且 MT5 可估值的交易"));
        var mae = p.AverageAdverseExcursion?.ToString("0.##") ?? "暂无";
        var mfe = p.AverageFavorableExcursion?.ToString("0.##") ?? "暂无";
        var maeR = p.AverageAdverseExcursionRiskMultiple?.ToString("0.##") ?? "暂无";
        var mfeR = p.AverageFavorableExcursionRiskMultiple?.ToString("0.##") ?? "暂无";
        ReviewMetrics.Add(new("最大不利 / 有利波动", $"{mae} / {mfe}", $"货币；R 倍数 {maeR} / {mfeR}，覆盖 {p.AdvancedDataCoverage:0.##}%"));

        var selectedCategoryKey = SelectedReviewCategory?.Key ?? "symbol";
        SelectedReviewCategory = null;
        ReviewGroups.Clear();
        ReviewCategories.Clear();
        AddCategory("side", "多空", "⇅", "买入与卖出的整体表现", snapshot.SidePerformance);
        AddCategory("symbol", "品种", "◈", "不同交易品种的贡献与稳定性", snapshot.SymbolPerformance);
        AddCategory("weekday", "星期", "▦", "按服务器交易日观察星期效应", snapshot.WeekdayPerformance);
        AddCategory("hour", "入场时段", "◷", "找出更适合自己的入场时间", snapshot.HourPerformance);
        AddCategory("duration", "持仓时长", "⌛", "比较不同持仓周期的结果", snapshot.DurationPerformance);
        AddCategory("strategy", "策略", "◎", "按自己填写的策略名称归类", snapshot.StrategyPerformance);
        AddCategory("setup", "形态", "◇", "按交易形态与触发条件归类", snapshot.SetupPerformance);
        AddCategory("tag", "标签", "#", "一笔交易可以进入多个标签分组", snapshot.TagPerformance);
        SelectedReviewCategory = ReviewCategories.FirstOrDefault(item => item.Key == selectedCategoryKey)
            ?? ReviewCategories.FirstOrDefault(item => item.Key == "symbol")
            ?? ReviewCategories.FirstOrDefault();

        void AddCategory(string key, string label, string icon, string hint, IEnumerable<GroupMetricRow> groups)
        {
            var rows = groups.Select(group => new ReviewGroupRow(
                    key,
                    group.Group,
                    group.TradeCount.ToString(),
                    $"{group.WinRate:0.##}%",
                    FormatSigned(group.NetPnl),
                    group.ProfitFactor?.ToString("0.##") ?? "—",
                    FormatSigned(group.Expectancy),
                    FinancialPalette.For(group.NetPnl),
                    FinancialPalette.For(group.Expectancy),
                    group.TradeCount,
                    group.NetPnl))
                .ToArray();
            foreach (var row in rows)
            {
                ReviewGroups.Add(row);
            }

            var best = rows.OrderByDescending(row => row.NetPnlValue).FirstOrDefault();
            var mostActive = rows.OrderByDescending(row => row.TradeCountValue).ThenBy(row => row.Group).FirstOrDefault();
            ReviewCategories.Add(new ReviewCategoryTab(
                key,
                label,
                icon,
                hint,
                rows.Length,
                best?.Group ?? "暂无数据",
                best?.NetPnl ?? "—",
                best?.NetPnlColor ?? FinancialPalette.Neutral,
                mostActive?.Group ?? "暂无数据",
                mostActive is null ? "—" : $"{mostActive.Trades} 笔"));
        }

        BehaviorMetrics.Clear();
        foreach (var item in snapshot.Behavior.Evaluations)
        {
            BehaviorMetrics.Add(new(
                FormatBehaviorName(item.Rule),
                item.Value.ToString("0.##"),
                item.Threshold.ToString("0.##"),
                item.Triggered ? item.Level == BehaviorRiskLevel.Critical ? "严重" : "注意" : "正常",
                item.Summary));
        }
        BehaviorRiskText = snapshot.Behavior.RiskLevel switch
        {
            BehaviorRiskLevel.Critical => "需要停一停",
            BehaviorRiskLevel.Attention => "需要留意",
            BehaviorRiskLevel.Observing => "观察中",
            _ => "正常",
        };
        var triggeredRules = snapshot.Behavior.Evaluations
            .Where(item => item.Triggered)
            .Select(item => FormatBehaviorName(item.Rule))
            .Take(2)
            .ToArray();
        BehaviorTriggeredText = triggeredRules.Length == 0
            ? "当前没有触发规则"
            : $"触发：{string.Join("、", triggeredRules)}";
    }

    public void ApplyReviewTrades(
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyDictionary<long, TradeReviewMetadata> metadata,
        Func<TradeReviewMetadata, Task> save)
    {
        ReviewTrades.Clear();
        foreach (var trade in trades.OrderByDescending(item => item.OpenedAtUtc))
        {
            metadata.TryGetValue(trade.PositionId, out var item);
            ReviewTrades.Add(new ReviewTradeRowViewModel(trade, item, save));
        }
    }

    public void ApplyTodayBehavior(BehaviorSummary summary)
    {
        TodayBehaviorRiskText = summary.RiskLevel switch
        {
            BehaviorRiskLevel.Critical => "需要停一停",
            BehaviorRiskLevel.Attention => "需要留意",
            BehaviorRiskLevel.Observing => "观察中",
            _ => "正常",
        };
        var triggeredRules = summary.Evaluations
            .Where(item => item.Triggered)
            .Select(item => FormatBehaviorName(item.Rule))
            .Take(2)
            .ToArray();
        TodayBehaviorTriggeredText = triggeredRules.Length == 0
            ? "当前没有触发规则"
            : $"触发：{string.Join("、", triggeredRules)}";
    }

    public void ClearReview(string message)
    {
        ClearReviewCollections();
        ReviewWorkspace.ResetAccountState(message);
        ReviewRangeText = message;
        BehaviorRiskText = "当前账户模式不支持完整复盘";
        BehaviorTriggeredText = "仍保留持仓和账户级浮亏监控";
        TodayBehaviorRiskText = "仅监控持仓";
        TodayBehaviorTriggeredText = "保留账户级浮亏监控";
    }

    public void ResetReviewForAccountChange(string message)
    {
        ClearReviewCollections();
        ReviewWorkspace.ResetAccountState(message);
        ReviewRangeText = message;
        BehaviorRiskText = "正在读取新账户行为数据";
        BehaviorTriggeredText = "旧账户结果已清除";
        TodayBehaviorRiskText = "正在同步";
        TodayBehaviorTriggeredText = "等待新账户数据";
    }

    private void ClearReviewCollections()
    {
        HasReviewTrades = false;
        ReviewMetrics.Clear();
        ReviewGroups.Clear();
        VisibleReviewGroups.Clear();
        ReviewCategories.Clear();
        ReviewTrades.Clear();
        BehaviorMetrics.Clear();
        SelectedReviewCategory = null;
        ReviewRangeText = string.Empty;
    }

    private void RefreshVisibleReviewGroups()
    {
        VisibleReviewGroups.Clear();
        if (SelectedReviewCategory is null)
        {
            return;
        }

        foreach (var row in ReviewGroups
                     .Where(row => row.CategoryKey == SelectedReviewCategory.Key)
                     .OrderByDescending(row => row.TradeCountValue)
                     .ThenBy(row => row.Group, StringComparer.OrdinalIgnoreCase))
        {
            VisibleReviewGroups.Add(row);
        }
    }

    public void SetBehaviorSettings(BehaviorPolicy policy)
    {
        BehaviorSettings.Clear();
        BehaviorSettings.Add(new(BehaviorRuleKind.ReentryCount, "重进次数阈值", policy.ReentryThreshold.ToString(), policy.EnabledRules.ReentryCount));
        BehaviorSettings.Add(new(BehaviorRuleKind.LossZonePersistence, "亏损区攻击阈值", policy.LossZonePersistenceThreshold.ToString(), policy.EnabledRules.LossZonePersistence));
        BehaviorSettings.Add(new(BehaviorRuleKind.RevengeScore, "报复性评分阈值", policy.RevengeScoreThreshold.ToString("0.##"), policy.EnabledRules.RevengeScore));
        BehaviorSettings.Add(new(BehaviorRuleKind.OvertradeBurst, "过度交易基线倍数", policy.OvertradeBaselineMultiplier.ToString("0.##"), policy.EnabledRules.OvertradeBurst));
        BehaviorSettings.Add(new(BehaviorRuleKind.PlanDeviationRate, "计划偏离率阈值（%）", policy.PlanDeviationRateThreshold.ToString("0.##"), policy.EnabledRules.PlanDeviationRate));
        BehaviorSettings.Add(new(BehaviorRuleKind.ProfitGiveback, "盈利回吐阈值（%）", policy.ProfitGivebackThreshold.ToString("0.##"), policy.EnabledRules.ProfitGiveback));
        BehaviorSettings.Add(new(BehaviorRuleKind.SizeEscalationAfterLoss, "亏损后仓位倍数", policy.LotEscalationMultiplier.ToString("0.##"), policy.EnabledRules.SizeEscalationAfterLoss));
        BehaviorSettings.Add(new(BehaviorRuleKind.CooldownViolation, "冷静期（秒）", policy.CooldownSeconds.ToString(), policy.EnabledRules.CooldownViolation));
        BehaviorSettings.Add(new(BehaviorRuleKind.PriceFixationScore, "价格执着评分阈值", policy.PriceFixationScoreThreshold.ToString("0.##"), policy.EnabledRules.PriceFixationScore));
    }

    public void SetFloatingLossPolicy(FloatingLossAlertPolicy policy)
    {
        FloatingLossLevels.Clear();
        foreach (var level in policy.Normalize().Levels.OrderBy(item => item.Stage))
        {
            FloatingLossLevels.Add(new FloatingLossLevelRow(level));
        }
    }

    public FloatingLossAlertPolicy GetFloatingLossPolicy() =>
        new FloatingLossAlertPolicy(FloatingLossLevels.Select(level => level.ToModel()).ToArray()).Normalize();

    public void NotifyPlanRecordingChanged()
    {
        RaisePropertyChanged(nameof(IsPlanRecording));
        RaisePropertyChanged(nameof(PlanRecordingText));
    }

    public void SetBubbleLossZone(
        LossZoneState? zone,
        decimal? currentPrice = null,
        IReadOnlyCollection<LossZoneAttempt>? attempts = null)
    {
        IsBubbleLossZoneVisible = zone is not null;
        if (zone is null)
        {
            BubbleZoneSymbol = string.Empty;
            BubbleZoneRangeText = string.Empty;
            BubbleZoneLowText = string.Empty;
            BubbleZoneCenterText = string.Empty;
            BubbleZoneHighText = string.Empty;
            BubbleZoneCurrentText = string.Empty;
            BubbleZoneStatsText = string.Empty;
            BubbleZonePnlText = string.Empty;
            BubbleZonePositionPercent = 50d;
            return;
        }

        var low = zone.CenterPrice - zone.Tolerance;
        var high = zone.CenterPrice + zone.Tolerance;
        var price = currentPrice ?? zone.CenterPrice;
        var width = high - low;
        BubbleZoneSymbol = zone.Symbol;
        BubbleZoneRangeText = $"{low:0.#####} — {high:0.#####}";
        BubbleZoneLowText = $"低 {low:0.#####}";
        BubbleZoneCenterText = $"中心 {zone.CenterPrice:0.#####}";
        BubbleZoneHighText = $"高 {high:0.#####}";
        BubbleZoneCurrentText = $"本次 {price:0.#####}";
        var closedAttempts = attempts?
            .Where(attempt => attempt.ZoneId == zone.Id && attempt.NetPnl is not null)
            .ToArray() ?? [];
        if (closedAttempts.Length > 0)
        {
            var wins = closedAttempts.Count(attempt => attempt.NetPnl > 0.01m);
            var losses = closedAttempts.Count(attempt => attempt.NetPnl < -0.01m);
            BubbleZoneStatsText = $"第 {zone.AttemptCount} 次进入 · 历史 {wins} 盈 {losses} 亏";
            BubbleZonePnlText = $"已平净额 {FormatSigned(closedAttempts.Sum(attempt => attempt.NetPnl ?? 0m))} · 亏损合计 {FormatSigned(zone.CumulativeLoss)}";
        }
        else
        {
            BubbleZoneStatsText = $"第 {zone.AttemptCount} 次进入 · 历史亏损 {zone.LossCount} 次";
            BubbleZonePnlText = $"亏损合计 {FormatSigned(zone.CumulativeLoss)}";
        }
        BubbleZonePositionPercent = width <= 0m
            ? 50d
            : (double)Math.Clamp((price - low) * 100m / width, 0m, 100m);
    }

    private void NotifyTotalPnlChanged()
    {
        RaisePropertyChanged(nameof(TotalPnl));
        RaisePropertyChanged(nameof(IsTotalPnlNegative));
        RaisePropertyChanged(nameof(TotalPnlColor));
        RaisePropertyChanged(nameof(TotalPnlBackground));
    }

    private static string FormatSigned(decimal value) => $"{(value >= 0m ? "+" : string.Empty)}{value:0.##}";
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}"
        : $"{duration.Minutes:00}:{duration.Seconds:00}";
    private static string FormatBehaviorName(BehaviorRuleKind rule) => rule switch
    {
        BehaviorRuleKind.ReentryCount => "重进次数",
        BehaviorRuleKind.LossZonePersistence => "亏损区持续攻击",
        BehaviorRuleKind.RevengeScore => "报复性交易评分",
        BehaviorRuleKind.OvertradeBurst => "过度交易突增",
        BehaviorRuleKind.PlanDeviationRate => "计划偏离率",
        BehaviorRuleKind.ProfitGiveback => "盈利回吐",
        BehaviorRuleKind.SizeEscalationAfterLoss => "亏损后仓位放大",
        BehaviorRuleKind.CooldownViolation => "冷静期违规",
        BehaviorRuleKind.PriceFixationScore => "价格执着评分",
        _ => "行为规则",
    };
}
