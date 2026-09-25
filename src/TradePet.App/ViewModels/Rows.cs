using TradePet.Core.Domain;

namespace TradePet.App.ViewModels;

public static class FinancialPalette
{
    public const string Profit = "#F06C75";
    public const string Loss = "#4BC49D";
    public const string Neutral = "#EAF0ED";
    public const string ProfitBackground = "#25171B";
    public const string LossBackground = "#12231F";
    public const string NeutralBackground = "#131A25";

    public static string For(decimal value) =>
        value > 0.01m ? Profit : value < -0.01m ? Loss : Neutral;

    public static string BackgroundFor(decimal value) =>
        value > 0.01m ? ProfitBackground : value < -0.01m ? LossBackground : NeutralBackground;
}

public sealed class PositionRowViewModel(PositionSnapshot position, bool showDuration = true)
{
    public long Ticket { get; } = position.Ticket;
    public string Symbol { get; } = position.Symbol;
    public string Side { get; } = position.Side == TradeSide.Buy ? "买入" : "卖出";
    public string Volume { get; } = position.Volume.ToString("0.##");
    public string EntryPrice { get; } = position.EntryPrice.ToString("0.#####");
    public string Profit { get; } = $"{(position.Profit >= 0m ? "+" : string.Empty)}{position.Profit:0.##}";
    public string ProfitColor { get; } = FinancialPalette.For(position.Profit);
    public string Duration { get; } = showDuration ? FormatDuration(DateTimeOffset.UtcNow - position.OpenedAtUtc) : "—";
    public string StopLossStatus { get; } = position.StopLoss > 0m ? $"止损 {position.StopLoss:0.#####}" : "未设止损";

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes:00}:{duration.Seconds:00}";
}

public sealed record PlanCategoryOption(PlanCategory Value, string Label);

public sealed record TerminalOption(string Path, string Label);
public sealed record PlatformOption(TradePet.Infrastructure.Mt5.TradingPlatform Value, string Label);

public sealed class PlanItemRowViewModel : ObservableObject
{
    private readonly Func<PlanItem, Task> _save;
    private PlanCategory _category;

    public PlanItemRowViewModel(PlanItem model, Func<PlanItem, Task> save)
    {
        Model = model;
        _category = model.Category;
        _save = save;
    }

    public PlanItem Model { get; private set; }

    public string Id => Model.Id;
    public string ObjectKey => Model.ObjectKey;
    public string Symbol => Model.Symbol;
    public string KindText => Model.PriceLow is not null && Model.PriceHigh is not null && Model.PriceLow != Model.PriceHigh
        ? $"区域 {Model.PriceLow:0.#####}–{Model.PriceHigh:0.#####}"
        : Model.PriceLow is not null ? $"价格 {Model.PriceLow:0.#####}" : "文字";
    public string Text => Model.Text ?? string.Empty;
    public string ActiveText => Model.IsActive ? "有效" : "已删除";

    public PlanCategory Category
    {
        get => _category;
        set
        {
            if (!SetProperty(ref _category, value))
            {
                return;
            }

            Model = Model with { Category = value, UpdatedAtUtc = DateTimeOffset.UtcNow };
            _ = _save(Model);
        }
    }

    public void Update(PlanItem model)
    {
        Model = model;
        _category = model.Category;
        RaisePropertyChanged(nameof(Category));
        RaisePropertyChanged(nameof(KindText));
        RaisePropertyChanged(nameof(Text));
        RaisePropertyChanged(nameof(ActiveText));
    }
}

public sealed class LossZoneRowViewModel(LossZoneState zone)
{
    private readonly decimal _low = zone.CenterPrice - zone.Tolerance;
    private readonly decimal _high = zone.CenterPrice + zone.Tolerance;

    public string Id { get; } = zone.Id;
    public string Symbol { get; } = zone.Symbol;
    public string Price { get; } = $"{zone.CenterPrice:0.#####} 附近";
    public string Range => $"{_low:0.#####} — {_high:0.#####}";
    public string Center { get; } = $"中心 {zone.CenterPrice:0.#####}";
    public string Attempts { get; } = $"尝试 {zone.AttemptCount} 次 / 亏损 {zone.LossCount} 次";
    public string Loss { get; } = $"累计 {zone.CumulativeLoss:0.##}";
    public string LossColor { get; } = FinancialPalette.Loss;
    public string LastTime { get; } = zone.LastAttemptAtUtc.ToLocalTime().ToString("HH:mm:ss");
}

public sealed class TimelineRowViewModel(TimelineEvent timelineEvent)
{
    public string Time { get; } = timelineEvent.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Kind { get; } = timelineEvent.Kind switch
    {
        TimelineKind.Connected => "已连接",
        TimelineKind.Disconnected => "已断开",
        TimelineKind.TradeOpened => "开仓",
        TimelineKind.TradeIncreased => "加仓",
        TimelineKind.TradeReduced => "减仓",
        TimelineKind.TradeClosed => "平仓",
        TimelineKind.StopLossChanged => "止损变化",
        TimelineKind.TakeProfitChanged => "止盈变化",
        TimelineKind.LossZoneReentry => "重回亏损区",
        TimelineKind.DirectionFlip => "方向切换",
        TimelineKind.LotEscalation => "仓位放大",
        TimelineKind.AddingToLoss => "浮亏加仓",
        TimelineKind.DailyTargetReached => "达到目标",
        TimelineKind.DailyLossReached => "触及亏损线",
        TimelineKind.ProfitGiveback => "盈利回吐",
        TimelineKind.PlanObjectAdded => "新增计划对象",
        TimelineKind.PlanObjectChanged => "计划对象变化",
        TimelineKind.PlanObjectDeleted => "删除计划对象",
        TimelineKind.Alert => "风险提醒",
        _ => "交易事件",
    };
    public string Summary { get; } = LocalizeSummary(timelineEvent.Summary);

    private static string LocalizeSummary(string summary) => summary
        .Replace(" BUY ", " 买入 ", StringComparison.OrdinalIgnoreCase)
        .Replace(" SELL ", " 卖出 ", StringComparison.OrdinalIgnoreCase)
        .Replace(" @ ", "，入场价 ", StringComparison.Ordinal)
        .Replace("Exposure", "总仓位", StringComparison.OrdinalIgnoreCase)
        .Replace(nameof(TradeDomainEventKind.StopLossAdded), "新增止损", StringComparison.Ordinal)
        .Replace(nameof(TradeDomainEventKind.StopLossModified), "修改止损", StringComparison.Ordinal)
        .Replace(nameof(TradeDomainEventKind.StopLossRemoved), "移除止损", StringComparison.Ordinal)
        .Replace(nameof(TradeDomainEventKind.TakeProfitAdded), "新增止盈", StringComparison.Ordinal)
        .Replace(nameof(TradeDomainEventKind.TakeProfitModified), "修改止盈", StringComparison.Ordinal)
        .Replace(nameof(TradeDomainEventKind.TakeProfitRemoved), "移除止盈", StringComparison.Ordinal);
}

public sealed record ReviewMetricRow(
    string Label,
    string Value,
    string Hint,
    string ValueColor = FinancialPalette.Neutral);

public sealed record ReviewCategoryTab(
    string Key,
    string Label,
    string Icon,
    string Hint,
    int GroupCount,
    string BestGroup,
    string BestNetPnl,
    string BestNetPnlColor,
    string MostActiveGroup,
    string MostActiveTrades)
{
    public string CountText => $"{GroupCount} 组";
    public bool HasGroups => GroupCount > 0;
    public bool HasNoGroups => !HasGroups;
}

public sealed record ReviewGroupRow(
    string CategoryKey,
    string Group,
    string Trades,
    string WinRate,
    string NetPnl,
    string ProfitFactor,
    string Expectancy,
    string NetPnlColor,
    string ExpectancyColor,
    int TradeCountValue,
    decimal NetPnlValue);

public sealed record BehaviorMetricRow(string Name, string Value, string Threshold, string Status, string Detail);

public sealed record ReviewTagSuggestionRow(
    string Tag,
    string Status,
    string Level,
    string Reason,
    string Evidence,
    System.Windows.Input.ICommand ActionCommand,
    string ActionText);

public sealed record TradeSideOption(TradeSide Value, string Label);

public sealed record StructuredTradePlanRow(
    string Id,
    string Symbol,
    string Side,
    string EntryRange,
    string StopTarget,
    string Strategy,
    string Setup,
    string Tags,
    string RiskMultiple,
    string Status,
    string ToggleText,
    System.Windows.Input.ICommand ToggleActiveCommand,
    System.Windows.Input.ICommand ReviseCommand);

public sealed record PlanComplianceOption(PlanComplianceStatus Value, string Label);

public sealed class ReviewTradeRowViewModel : ObservableObject
{
    private readonly Func<TradeReviewMetadata, Task> _save;
    private PlanComplianceStatus _complianceStatus;
    private string _strategy;
    private string _setup;
    private string _tags;

    public ReviewTradeRowViewModel(
        TradeRecord trade,
        TradeReviewMetadata? metadata,
        Func<TradeReviewMetadata, Task> save)
    {
        Trade = trade;
        Metadata = metadata;
        _save = save;
        _complianceStatus = metadata?.ComplianceStatus ?? PlanComplianceStatus.Unclassified;
        _strategy = metadata?.Strategy ?? string.Empty;
        _setup = metadata?.Setup ?? string.Empty;
        _tags = metadata is null ? string.Empty : string.Join("、", metadata.Tags);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public TradeRecord Trade { get; }
    public TradeReviewMetadata? Metadata { get; private set; }
    public long PositionId => Trade.PositionId;
    public string Time => Trade.OpenedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string Symbol => Trade.Symbol;
    public string Side => Trade.Side == TradeSide.Buy ? "买入" : "卖出";
    public string Pnl => $"{(Trade.NetPnl >= 0m ? "+" : string.Empty)}{Trade.NetPnl:0.##}";
    public PlanComplianceStatus ComplianceStatus { get => _complianceStatus; set => SetProperty(ref _complianceStatus, value); }
    public string Strategy { get => _strategy; set => SetProperty(ref _strategy, value); }
    public string Setup { get => _setup; set => SetProperty(ref _setup, value); }
    public string Tags { get => _tags; set => SetProperty(ref _tags, value); }
    public System.Windows.Input.ICommand SaveCommand { get; }

    private async Task SaveAsync()
    {
        var updated = new TradeReviewMetadata(
            Trade.AccountKey,
            Trade.PositionId,
            ComplianceStatus == PlanComplianceStatus.Unclassified ? null : Metadata?.PlanId,
            ComplianceStatus,
            Strategy.Trim(),
            Setup.Trim(),
            Tags.Split(['，', ',','、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            true,
            DateTimeOffset.UtcNow);
        await _save(updated);
        Metadata = updated;
    }
}

public sealed class BehaviorSettingRow : ObservableObject
{
    private string _value;
    private bool _enabled;

    public BehaviorSettingRow(BehaviorRuleKind rule, string name, string value, bool enabled = true)
    {
        Rule = rule;
        Name = name;
        _value = value;
        _enabled = enabled;
    }

    public BehaviorRuleKind Rule { get; }
    public string Name { get; }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
}

public sealed class FloatingLossLevelRow : ObservableObject
{
    private bool _enabled;
    private decimal _lossPercentage;

    public FloatingLossLevelRow(FloatingLossAlertLevel level)
    {
        Stage = level.Stage;
        _enabled = level.Enabled;
        _lossPercentage = level.LossPercentage;
    }

    public int Stage { get; }
    public string Name => $"第 {Stage} 段";
    public string Hint => Stage switch
    {
        1 => "先停手检查持仓理由",
        2 => "禁止冲动补仓和扛单",
        _ => "强提醒离开鼠标冷静",
    };
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public decimal LossPercentage
    {
        get => _lossPercentage;
        set => SetProperty(ref _lossPercentage, Math.Clamp(value, 0.01m, 100m));
    }

    public FloatingLossAlertLevel ToModel() => new(Stage, Enabled, LossPercentage);
}
