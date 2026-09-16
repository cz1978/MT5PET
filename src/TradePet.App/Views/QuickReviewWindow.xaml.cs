using System.Windows;
using System.Windows.Controls;
using TradePet.Application.Review;
using TradePet.Core.Domain;

namespace TradePet.App.Views;

public partial class QuickReviewWindow : Window
{
    private bool _completed;
    public QuickReviewWindow(TradeDetailData detail)
    {
        InitializeComponent();
        var trade = detail.Trade;
        TradeText.Text = $"{trade.Symbol} · {trade.NetPnl:+0.##;-0.##;0} · {trade.ClosedAtUtc:yyyy-MM-dd HH:mm}";
        PlanBox.SelectedIndex = detail.Metadata?.ComplianceStatus switch
        {
            PlanComplianceStatus.Matched or PlanComplianceStatus.ManualInside => 0,
            PlanComplianceStatus.OutsidePlan or PlanComplianceStatus.ManualOutside => 1,
            _ => 2,
        };
        var analysis = AnalyzeExitReason(detail);
        ExitReasonBox.Text = analysis.Reason;
        AnalysisText.Text = analysis.Explanation;
    }

    public bool SaveRequested { get; private set; }
    public bool RemindLater { get; private set; }
    public event Action<QuickReviewWindow>? Completed;
    public string PlanCompliance => (PlanBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "不确定";
    public string ExitReason => ExitReasonBox.Text.Trim();
    public string Improvement => ImproveBox.Text.Trim();

    private void ReasonPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string reason }) return;
        ExitReasonBox.Text = reason;
        ExitReasonBox.CaretIndex = ExitReasonBox.Text.Length;
        ExitReasonBox.Focus();
    }

    private static ExitReasonAnalysis AnalyzeExitReason(TradeDetailData detail)
    {
        var trade = detail.Trade;
        var exitPrice = detail.Deals
            .Where(deal => deal.EntryKind is DealEntryKind.Out or DealEntryKind.InOut or DealEntryKind.OutBy)
            .OrderByDescending(deal => deal.OccurredAtUtc)
            .ThenByDescending(deal => deal.Ticket)
            .Select(deal => (decimal?)deal.Price)
            .FirstOrDefault() ?? trade.ExitPrice;

        if (exitPrice is not null && detail.Plan is { } plan)
        {
            var validTarget = plan.TargetPrice is { } target && trade.Side switch
            {
                TradeSide.Buy => target > trade.EntryPrice && exitPrice.Value >= target,
                TradeSide.Sell => target < trade.EntryPrice && exitPrice.Value <= target,
                _ => false,
            };
            if (validTarget)
            {
                return new ExitReasonAnalysis("止盈离场", $"自动判断：最终平仓价 {exitPrice:0.#####} 已达到计划目标 {plan.TargetPrice:0.#####}。", true);
            }

            var validStop = plan.StopPrice is { } stop && trade.Side switch
            {
                TradeSide.Buy => stop < trade.EntryPrice && exitPrice.Value <= stop,
                TradeSide.Sell => stop > trade.EntryPrice && exitPrice.Value >= stop,
                _ => false,
            };
            if (validStop)
            {
                return new ExitReasonAnalysis("止损离场", $"自动判断：最终平仓价 {exitPrice:0.#####} 已触及计划止损 {plan.StopPrice:0.#####}。", true);
            }
        }

        var reason = trade.NetPnl switch
        {
            > 0.01m => "盈利离场（待确认）",
            < -0.01m => "亏损离场（待确认）",
            _ => "保本离场（待确认）",
        };
        var explanation = detail.Plan is null
            ? "自动建议：没有绑定止损/目标计划，只能按盈亏判断；可直接保存或从下拉框纠正。"
            : "自动建议：平仓价未触及计划止损或目标，只能按盈亏判断；可从下拉框纠正。";
        return new ExitReasonAnalysis(reason, explanation, false);
    }

    private sealed record ExitReasonAnalysis(string Reason, string Explanation, bool IsExact);
    private void Save_Click(object sender, RoutedEventArgs e) { SaveRequested = true; Finish(); }
    private void Later_Click(object sender, RoutedEventArgs e) { RemindLater = true; Finish(); }
    private void Skip_Click(object sender, RoutedEventArgs e) => Finish();
    private void Finish()
    {
        if (_completed) return;
        _completed = true;
        Completed?.Invoke(this);
        Close();
    }
}
