using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TradePet.App.ViewModels;
using TradePet.Core.Domain;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TradePet.App.Views;

public partial class PetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WmHotKey = 0x0312;
    private const int HotKeyId = 0x5450;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _movementTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _fullScreenTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Drawing.Font _trayMenuFont;
    private readonly Drawing.Icon _trayIconImage;
    private bool _trayResourcesDisposed;
    private bool _quickCardVisible;
    private bool _quickCardPinned;
    private bool _isDragging;
    private double _homeLeft;
    private bool _fullscreenActive;
    public event Action? GlobalToggleRequested;
    public event Action<bool>? FullscreenSuppressionChanged;

    public PetWindow(MainViewModel viewModel)
    {
        InitializeComponent();
#if DEBUG
        Title = "天禄桌宠";
        ShowInTaskbar = true;
#endif
        _viewModel = viewModel;
        DataContext = viewModel;
        QuickCardPopup.DataContext = viewModel;
        QuickCardPopup.PlacementTarget = PetSprite;
        SpeechBubblePopup.DataContext = viewModel;
        SpeechBubblePopup.PlacementTarget = PetSprite;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        SourceInitialized += (_, _) =>
        {
            // Keep the small, layered pet window off the hardware composition path.
            if (PresentationSource.FromVisual(this)?.CompositionTarget is HwndTarget target)
            {
                target.RenderMode = RenderMode.SoftwareOnly;
            }
            ApplyMouseThrough();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowProc);
            RegisterHotKey(new WindowInteropHelper(this).Handle, HotKeyId, ModControl | ModShift, (uint)KeyInterop.VirtualKeyFromKey(Key.H));
        };
        Loaded += (_, _) =>
        {
            ResizeForScale();
            PlaceAtSafeDefault();
        };

        _movementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _movementTimer.Tick += (_, _) => MoveWithActivity();
        _movementTimer.Start();

        _fullScreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullScreenTimer.Tick += (_, _) => CheckFullScreen();
        _fullScreenTimer.Start();

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (IsVisible && !_isDragging && ContextMenu?.IsOpen != true &&
                PetSprite.IsMouseOver && !_viewModel.IsMouseThrough)
            {
                SetQuickCardVisible(true);
            }
        };
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_quickCardPinned && !PetSprite.IsMouseOver && !QuickCardHost.IsMouseOver)
            {
                SetQuickCardVisible(false);
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _hoverTimer.Stop();
                _hideTimer.Stop();
                _quickCardPinned = false;
                SetQuickCardVisible(false);
            }
            UpdateSpeechBubble();
        };

        _trayIconImage = LoadTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "天禄交易助手",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() =>
        {
            Show();
            _viewModel.ShowMainWindowCommand.Execute(null);
        });
        _trayMenuFont = new Drawing.Font("Microsoft YaHei UI", 10F, Drawing.FontStyle.Regular,
            Drawing.GraphicsUnit.Point);
        _trayMenu = new Forms.ContextMenuStrip
        {
            BackColor = Drawing.Color.FromArgb(16, 23, 33),
            ForeColor = Drawing.Color.FromArgb(242, 245, 244),
            Font = _trayMenuFont,
            Padding = new Forms.Padding(5),
            ShowImageMargin = false,
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColorTable()),
        };
        _trayMenu.Items.Add("显示桌宠", null, (_, _) => Dispatcher.Invoke(Show));
        _trayMenu.Items.Add("今日状态", null, (_, _) => Dispatcher.Invoke(() =>
        {
            Show();
            _quickCardPinned = true;
            SetQuickCardVisible(true);
        }));
        _trayMenu.Items.Add("交易日报", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowDailyTradingReportCommand.Execute(null)));
        _trayMenu.Items.Add("宏观日历", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowMacroCalendarCommand.Execute(null)));
        _trayMenu.Items.Add("显示/隐藏迷你持仓", null, (_, _) => Dispatcher.Invoke(() => _viewModel.MiniPositionVisible = !_viewModel.MiniPositionVisible));
        _trayMenu.Items.Add("交易计划", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowPlanPageCommand.Execute(null)));
        _trayMenu.Items.Add("亏损区域", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowLossZonesPageCommand.Execute(null)));
        _trayMenu.Items.Add("复盘分析", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowReviewPageCommand.Execute(null)));
        _trayMenu.Items.Add("快速复盘", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowQuickReviewCommand.Execute(null)));
        _trayMenu.Items.Add("时间线", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowTimelinePageCommand.Execute(null)));
        _trayMenu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ShowSettingsPageCommand.Execute(null)));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("关闭鼠标穿透", null, (_, _) => Dispatcher.Invoke(() => _viewModel.IsMouseThrough = false));
        _trayMenu.Items.Add("退出天禄交易助手", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ExitCommand.Execute(null)));
        _trayIcon.ContextMenuStrip = _trayMenu;
    }

    public void DisposeTrayIcon()
    {
        if (_trayResourcesDisposed)
        {
            return;
        }
        _trayResourcesDisposed = true;

        _movementTimer.Stop();
        _fullScreenTimer.Stop();
        _hoverTimer.Stop();
        _hideTimer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        QuickCardPopup.IsOpen = false;
        SpeechBubblePopup.IsOpen = false;
        PetSprite.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip = null;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _trayMenuFont.Dispose();
        _trayIconImage.Dispose();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotKeyId);
    }

    private Drawing.Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri(
            "pack://application:,,,/TradePet;component/Assets/tradepet-icon.ico",
            UriKind.Absolute));
        if (resource is null)
        {
            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }

        using var resourceStream = resource.Stream;
        using var sourceIcon = new Drawing.Icon(resourceStream);
        return (Drawing.Icon)sourceIcon.Clone();
    }

    private sealed class DarkMenuColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(16, 23, 33);
        private static readonly Drawing.Color SurfaceHover = Drawing.Color.FromArgb(27, 102, 85);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(58, 75, 96);

        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => SurfaceHover;
        public override Drawing.Color MenuItemSelected => SurfaceHover;
        public override Drawing.Color MenuItemSelectedGradientBegin => SurfaceHover;
        public override Drawing.Color MenuItemSelectedGradientEnd => SurfaceHover;
        public override Drawing.Color ToolStripDropDownBackground => Surface;
        public override Drawing.Color ImageMarginGradientBegin => Surface;
        public override Drawing.Color ImageMarginGradientMiddle => Surface;
        public override Drawing.Color ImageMarginGradientEnd => Surface;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Border;
    }

    private void PetSprite_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _quickCardPinned = false;
            SetQuickCardVisible(false);
            _viewModel.ShowMainWindowCommand.Execute(null);
            return;
        }
        if (_viewModel.IsMouseThrough || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        if (_viewModel.IsPositionLocked)
        {
            ToggleQuickCard();
            return;
        }

        _isDragging = true;
        _hoverTimer.Stop();
        _hideTimer.Stop();
        _movementTimer.Stop();
        SetQuickCardVisible(false);
        UpdateSpeechBubble();
        PetSprite.SetAnimationPaused(true);
        var start = Forms.Cursor.Position;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        finally
        {
            _isDragging = false;
            if (!_trayResourcesDisposed)
            {
                ResizeForScale();
                _homeLeft = Left;
                PetSprite.SetAnimationPaused(false);
                _movementTimer.Start();
                UpdateSpeechBubble();
            }
        }
        var end = Forms.Cursor.Position;
        _homeLeft = Left;
        var moved = Math.Abs(end.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(end.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!moved)
        {
            ToggleQuickCard();
        }
        else
        {
            _quickCardPinned = false;
        }
    }

    private void PetSprite_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hideTimer.Stop();
        if (_viewModel.ExpandCardOnHover && !_isDragging && !_quickCardVisible)
        {
            _hoverTimer.Start();
        }
    }

    private void PetSprite_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverTimer.Stop();
        ScheduleQuickCardHide();
    }

    private void QuickCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _hideTimer.Stop();

    private void QuickCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ScheduleQuickCardHide();

    private void ShowQuickCard_Click(object sender, RoutedEventArgs e)
    {
        _quickCardPinned = true;
        SetQuickCardVisible(true);
    }

    private void ToggleMiniPosition_Click(object sender, RoutedEventArgs e) =>
        _viewModel.MiniPositionVisible = !_viewModel.MiniPositionVisible;

    private void OpenPlan_Click(object sender, RoutedEventArgs e)
    {
        _quickCardPinned = false;
        SetQuickCardVisible(false);
        _viewModel.ShowPlanPageCommand.Execute(null);
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        _quickCardPinned = false;
        SetQuickCardVisible(false);
        _viewModel.ShowMainWindowCommand.Execute(null);
    }

    private void AcknowledgeAlert_Click(object sender, RoutedEventArgs e) => _viewModel.AcknowledgeAlert?.Invoke();
    private void SnoozeAlert_Click(object sender, RoutedEventArgs e) => _viewModel.SnoozeAlert?.Invoke();
    private void MuteAlert_Click(object sender, RoutedEventArgs e) => _viewModel.MuteCurrentTradeAlert?.Invoke();

    private void ToggleQuickCard()
    {
        _quickCardPinned = !_quickCardPinned;
        SetQuickCardVisible(_quickCardPinned);
    }

    private void ScheduleQuickCardHide()
    {
        if (!_isDragging && !_quickCardPinned)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }

    private void SetQuickCardVisible(bool visible)
    {
        if (visible && (_isDragging || !IsVisible))
        {
            return;
        }
        if (_quickCardVisible == visible)
        {
            return;
        }

        _quickCardVisible = visible;
        QuickCardPopup.IsOpen = visible;
    }

    private CustomPopupPlacement[] PlaceQuickCard(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset) =>
    [
        new CustomPopupPlacement(
            new System.Windows.Point(-popupSize.Width, targetSize.Height - popupSize.Height),
            PopupPrimaryAxis.Vertical),
    ];

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMouseThrough))
        {
            ApplyMouseThrough();
            if (_viewModel.IsMouseThrough)
            {
                _quickCardPinned = false;
                SetQuickCardVisible(false);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.PetScale) && !_isDragging)
        {
            ResizeForScale();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsBubbleVisible))
        {
            UpdateSpeechBubble();
        }
    }

    private void UpdateSpeechBubble() =>
        SpeechBubblePopup.IsOpen = !_trayResourcesDisposed && IsVisible &&
                                   !_isDragging && _viewModel.IsBubbleVisible;

    private void ResizeForScale(bool clamp = true)
    {
        PetSprite.Width = 192 * _viewModel.PetScale;
        PetSprite.Height = 208 * _viewModel.PetScale;
        Width = PetSprite.Width + 16;
        Height = Math.Max(230, PetSprite.Height + 28);
        if (clamp)
        {
            ClampToCurrentMonitor();
        }
    }

    private void PlaceAtSafeDefault()
    {
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;

        ClampToCurrentMonitor();
        _homeLeft = Left;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            GlobalToggleRequested?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void CheckFullScreen()
    {
        var foreground = GetForegroundWindow();
        var own = new WindowInteropHelper(this).Handle;
        GetWindowThreadProcessId(foreground, out var processId);
        var full = foreground != IntPtr.Zero && foreground != own &&
                   processId != (uint)Process.GetCurrentProcess().Id &&
                   GetWindowRect(foreground, out var rect) &&
                   Forms.Screen.FromHandle(foreground).Bounds is var bounds &&
                   rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
        if (full == _fullscreenActive) return;
        _fullscreenActive = full;
        FullscreenSuppressionChanged?.Invoke(full);
    }

    private void MoveWithActivity()
    {
        if (_isDragging || _viewModel.IsPositionLocked || !IsVisible || _quickCardVisible ||
            _viewModel.IsBubbleVisible || ContextMenu?.IsOpen == true || PetSprite.IsMouseOver)
        {
            return;
        }

        var delta = _viewModel.PetActivity switch
        {
            PetActivity.RunningRight => 1.5,
            PetActivity.RunningLeft => -1.5,
            _ => 0.0,
        };
        if (delta == 0.0)
        {
            return;
        }

        Left += delta;
        if (Left > _homeLeft + 100 || Left < _homeLeft - 100)
        {
            Left -= delta;
        }
        ClampToCurrentMonitor();
    }

    private void ClampToCurrentMonitor()
    {
        if (!IsLoaded)
        {
            return;
        }

        ClampToWorkingArea(GetCurrentWorkingArea());
    }

    private Rect GetCurrentWorkingArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } compositionTarget)
        {
            return SystemParameters.WorkArea;
        }

        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var topLeft = compositionTarget.TransformFromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = compositionTarget.TransformFromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void ClampToWorkingArea(Rect workingArea)
    {
        Left = Math.Clamp(Left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width));
        Top = Math.Clamp(Top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height));
    }

    private void ApplyMouseThrough()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = _viewModel.IsMouseThrough
            ? style | WsExTransparent | WsExNoActivate
            : style & ~(WsExTransparent | WsExNoActivate);
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newLong);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
