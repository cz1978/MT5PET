using System.IO;
using System.Windows;
using System.Windows.Media;
using TradePet.Core.Domain;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace TradePet.App.Views;

public sealed record DailyTradingReport(
    string AccountKey,
    string Currency,
    DateOnly ServerDate,
    bool IsLive,
    decimal RealizedCashPnl,
    decimal Fees,
    int TradeCount,
    int WinCount,
    int LossCount,
    int BreakevenCount,
    decimal? WinRate,
    TradeRecord? BestTrade,
    TradeRecord? WorstTrade,
    int InsidePlanCount,
    int OutsidePlanCount,
    int UnclassifiedPlanCount,
    int ReviewedCount,
    int PendingReviewCount,
    int BehaviorAlertCount,
    int CooldownViolationCount,
    string Markdown,
    string? ArchivePath = null);

public partial class DailyTradingReportWindow : Window
{
    private static readonly WpfBrush ProfitBrush = new SolidColorBrush(WpfColor.FromRgb(77, 198, 163));
    private static readonly WpfBrush LossBrush = new SolidColorBrush(WpfColor.FromRgb(240, 107, 120));
    private static readonly WpfBrush FlatBrush = new SolidColorBrush(WpfColor.FromRgb(242, 245, 244));
    private readonly DailyTradingReport _report;

    public DailyTradingReportWindow(DailyTradingReport report)
    {
        InitializeComponent();
        _report = report;
        TitleText.Text = $"{report.ServerDate:yyyy-MM-dd} 交易日报";
        AccountText.Text = $"{report.AccountKey} · {report.Currency}";
        StatusText.Text = report.IsLive ? "今日实时" : "已结束";
        NetPnlText.Text = FormatMoney(report.RealizedCashPnl, report.Currency);
        NetPnlText.Foreground = PnlBrush(report.RealizedCashPnl);
        SummaryText.Text = BuildSummary(report);
        TradeCountText.Text = $"{report.TradeCount} 笔";
        WinRateText.Text = report.WinRate is null ? "—" : $"{report.WinRate:0.#}%";
        WinLossText.Text = $"{report.WinCount} / {report.LossCount}";
        FeesText.Text = FormatMoney(report.Fees, report.Currency);
        BestTradeText.Text = FormatTrade(report.BestTrade, report.Currency);
        WorstTradeText.Text = FormatTrade(report.WorstTrade, report.Currency);
        PlanText.Text = $"计划内 {report.InsidePlanCount} · 计划外 {report.OutsidePlanCount} · 未分类 {report.UnclassifiedPlanCount}";
        ReviewText.Text = $"已完成 {report.ReviewedCount} · 待复盘 {report.PendingReviewCount}";
        BehaviorText.Text = report.BehaviorAlertCount == 0
            ? "未发现高风险行为"
            : $"{report.BehaviorAlertCount} 条风险提醒，其中冷静期违规 {report.CooldownViolationCount} 条";
        ArchivePathText.Text = string.IsNullOrWhiteSpace(report.ArchivePath)
            ? "Markdown 尚未归档。"
            : $"已归档：{report.ArchivePath}";
        ArchivePathText.ToolTip = report.ArchivePath;
    }

    public DateOnly ServerDate => _report.ServerDate;
    public event EventHandler? OpenReviewRequested;

    private static string BuildSummary(DailyTradingReport report)
    {
        if (report.TradeCount == 0)
        {
            return report.IsLive ? "今天还没有完整平仓交易。" : "该交易日没有完整平仓交易。";
        }

        var direction = report.RealizedCashPnl switch
        {
            > 0.01m => "当日盈利",
            < -0.01m => "当日亏损",
            _ => "当日基本持平",
        };
        var review = report.PendingReviewCount > 0 ? $"，还有 {report.PendingReviewCount} 笔待复盘" : "，复盘已完成";
        return $"{direction}；{report.WinCount} 胜、{report.LossCount} 负、{report.BreakevenCount} 平{review}。";
    }

    private static string FormatMoney(decimal value, string currency) =>
        $"{value:+0.##;-0.##;0} {currency}";

    private static string FormatTrade(TradeRecord? trade, string currency) => trade is null
        ? "—"
        : $"{trade.Symbol} · {(trade.Side == TradeSide.Buy ? "买入" : "卖出")} · {FormatMoney(trade.NetPnl, currency)}";

    private static WpfBrush PnlBrush(decimal value) => value switch
    {
        > 0.01m => ProfitBrush,
        < -0.01m => LossBrush,
        _ => FlatBrush,
    };

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_report.Markdown);
            ArchivePathText.Text = "完整 Markdown 已复制，可直接粘贴给 AI。";
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "复制 Markdown 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveMarkdown_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfSaveFileDialog
        {
            Title = "保存交易日报",
            Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = $"{_report.ServerDate:yyyy-MM-dd}-交易日报.md",
            InitialDirectory = string.IsNullOrWhiteSpace(_report.ArchivePath)
                ? null
                : Path.GetDirectoryName(_report.ArchivePath),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _report.Markdown);
            ArchivePathText.Text = $"已保存：{dialog.FileName}";
            ArchivePathText.ToolTip = dialog.FileName;
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "保存 Markdown 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenReview_Click(object sender, RoutedEventArgs e) => OpenReviewRequested?.Invoke(this, EventArgs.Empty);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
