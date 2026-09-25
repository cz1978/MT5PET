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
        var analysis = QuickReviewAnalyzer.Analyze(detail);
        ExitReasonBox.Text = analysis.ExitReason;
        AnalysisText.Text = analysis.Explanation;
        ImproveBox.Text = analysis.Improvement;
    }

    public bool SaveRequested { get; private set; }
    public bool RemindLater { get; private set; }
    public event Action<QuickReviewWindow>? Completed;
    public string PlanCompliance => (PlanBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "不确定";
    public string ExitReason => ExitReasonBox.Text.Trim();
    public string Improvement => ImproveBox.Text.Trim();
    public string AnalysisSummary => AnalysisText.Text;

    private void ReasonPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string reason }) return;
        ExitReasonBox.Text = reason;
        ExitReasonBox.CaretIndex = ExitReasonBox.Text.Length;
        ExitReasonBox.Focus();
    }

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
