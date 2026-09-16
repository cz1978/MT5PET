using System.ComponentModel;
using System.Windows;
using TradePet.App.ViewModels;

namespace TradePet.App.Views;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void ShowPage(int pageIndex)
    {
        MainTabs.SelectedIndex = Math.Clamp(pageIndex, 0, MainTabs.Items.Count - 1);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void HideWindow_Click(object sender, RoutedEventArgs e) => Hide();
}
