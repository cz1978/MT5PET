using System.Windows;
using System.Windows.Input;
using TradePet.App.ViewModels;

namespace TradePet.App.Views;

public partial class MiniPositionWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _restoringPlacement;

    public MiniPositionWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += (_, _) => ClampToDesktop();
        LocationChanged += (_, _) => { if (IsLoaded && !_restoringPlacement) PetWindowPlacement.Save("mini", Left, Top); };
    }

    public void RestorePlacement()
    {
        _restoringPlacement = true;
        try
        {
            var point = PetWindowPlacement.Load("mini");
            Left = point?.X ?? SystemParameters.WorkArea.Left + 18;
            Top = point?.Y ?? SystemParameters.WorkArea.Top + 18;
            ClampToDesktop();
        }
        finally { _restoringPlacement = false; }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.MiniPositionPinned && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => _viewModel.MiniPositionPinned = !_viewModel.MiniPositionPinned;

    private void Hide_Click(object sender, RoutedEventArgs e) => _viewModel.MiniPositionVisible = false;

    private void ClampToDesktop()
    {
        var area = SystemParameters.VirtualScreenWidth > 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Math.Max(80, Height);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - height));
    }
}
