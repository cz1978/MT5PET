using System.Windows;
using System.Threading;
using TradePet.App.Runtime;
using TradePet.App.ViewModels;
using TradePet.App.Views;

namespace TradePet.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\TradePet.App.v1";

    private TradePetRuntime? _runtime;
    private PetWindow? _petWindow;
    private MainWindow? _mainWindow;
    private MiniPositionWindow? _miniPositionWindow;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _shutdownStarted;
    private bool _runtimeDisposed;
    private bool _globallyHidden;
    private bool _petWasVisibleBeforeFullscreen;
    private bool _miniWasVisibleBeforeFullscreen;
    private bool _mainWasVisibleBeforeGlobalHide;
    private bool _petWasVisibleBeforeGlobalHide;
    private bool _miniWasVisibleBeforeGlobalHide;
    private bool _fullscreenSuppressed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        var dependencies = TradePetRuntimeDependencies.CreateDefault();
        var viewModel = new MainViewModel(dependencies.Scheduler, dependencies.TimeProvider);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.MiniPositionVisible) && _miniPositionWindow is not null && !_globallyHidden)
            {
                if (viewModel.MiniPositionVisible && !_fullscreenSuppressed) _miniPositionWindow.Show(); else _miniPositionWindow.Hide();
            }
        };
        _mainWindow = new MainWindow(viewModel);
        _petWindow = new PetWindow(viewModel);
        _miniPositionWindow = new MiniPositionWindow(viewModel);
        _petWindow.GlobalToggleRequested += ToggleGlobalVisibility;
        _petWindow.FullscreenSuppressionChanged += SetFullscreenSuppression;
        viewModel.ShowMainWindow = () =>
        {
            _mainWindow.Show();
            _mainWindow.Activate();
        };
        viewModel.ShowConsolePage = pageIndex =>
        {
            _mainWindow.ShowPage(pageIndex);
            _mainWindow.Show();
            _mainWindow.Activate();
        };
        viewModel.HidePet = () => _petWindow.Hide();
        viewModel.ExitApplicationAsync = ShutdownAsync;
        _runtime = new TradePetRuntime(viewModel, dependencies);
        viewModel.TogglePlanRecordingAsync = _runtime.TogglePlanRecordingAsync;
        viewModel.ImportPlanAsync = _runtime.ImportCurrentChartAsync;
        viewModel.SaveSettingsAsync = _runtime.SaveSettingsAsync;
        viewModel.InstallBridgeAsync = _runtime.InstallBridgeAsync;
        viewModel.ReplayScenarioAsync = _runtime.ReplayCoreScenarioAsync;
        viewModel.CreateStructuredPlanAsync = _runtime.CreateStructuredPlanAsync;
        viewModel.ApplyBehaviorPresetAsync = _runtime.ApplyBehaviorPresetAsync;
        viewModel.SaveBehaviorSettingsAsync = _runtime.SaveBehaviorSettingsAsync;
        viewModel.RefreshReviewAsync = _runtime.RefreshReviewAsync;
        viewModel.ShowDailyTradingReportAsync = _runtime.ShowDailyTradingReportAsync;
        viewModel.ShowMacroCalendar = _runtime.ShowMacroCalendar;

        _petWindow.Show();
        _miniPositionWindow.RestorePlacement();
        _miniPositionWindow.Show();
#if DEBUG
        _mainWindow.Show();
#endif
        await _runtime.StartAsync();
    }

    private void ToggleGlobalVisibility()
    {
        _globallyHidden = !_globallyHidden;
        if (_globallyHidden)
        {
            _mainWasVisibleBeforeGlobalHide = _mainWindow?.IsVisible == true;
            _petWasVisibleBeforeGlobalHide = _petWindow?.IsVisible == true;
            _miniWasVisibleBeforeGlobalHide = _miniPositionWindow?.IsVisible == true;
            _petWindow?.Hide();
            _miniPositionWindow?.Hide();
            _mainWindow?.Hide();
        }
        else
        {
            if (_fullscreenSuppressed) return;
            if (_petWasVisibleBeforeGlobalHide) _petWindow?.Show();
            if (_miniWasVisibleBeforeGlobalHide && _miniPositionWindow?.DataContext is MainViewModel { MiniPositionVisible: true }) _miniPositionWindow.Show();
            if (_mainWasVisibleBeforeGlobalHide) { _mainWindow?.Show(); _mainWindow?.Activate(); }
        }
    }

    private void SetFullscreenSuppression(bool suppress)
    {
        _fullscreenSuppressed = suppress;
        if (_globallyHidden) return;
        if (suppress)
        {
            _petWasVisibleBeforeFullscreen = _petWindow?.IsVisible == true;
            _miniWasVisibleBeforeFullscreen = _miniPositionWindow?.IsVisible == true;
            _petWindow?.Hide();
            _miniPositionWindow?.Hide();
        }
        else
        {
            if (_petWasVisibleBeforeFullscreen || _petWasVisibleBeforeGlobalHide) _petWindow?.Show();
            if ((_miniWasVisibleBeforeFullscreen || _miniWasVisibleBeforeGlobalHide) &&
                _miniPositionWindow?.DataContext is MainViewModel { MiniPositionVisible: true }) _miniPositionWindow.Show();
            if (_mainWasVisibleBeforeGlobalHide) { _mainWindow?.Show(); _mainWindow?.Activate(); }
        }
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        var closeApproved = false;
        if (_mainWindow is not null)
        {
            _mainWindow.IsEnabled = false;
        }
        try
        {
            if (_runtime is not null)
            {
                while (true)
                {
                    var preparation = await _runtime.PrepareForShutdownAsync();
                    if (preparation.CanExit)
                    {
                        break;
                    }

                    if (preparation.DraftPath is null)
                    {
                        var retry = System.Windows.MessageBox.Show(
                            $"{preparation.Message}\n\n恢复草稿也未能写入。选择“是”重试保存；选择“否”返回程序。",
                            "仍有内容未保存",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning,
                            MessageBoxResult.No);
                        if (retry == MessageBoxResult.Yes)
                        {
                            continue;
                        }
                        return;
                    }

                    var choice = System.Windows.MessageBox.Show(
                        $"{preparation.Message}\n\n恢复草稿已写入：\n{preparation.DraftPath}\n\n“是”重试保存；“否”保留该文件并退出；“取消”返回程序。",
                        "仍有内容未保存",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Warning,
                        MessageBoxResult.Cancel);
                    if (choice == MessageBoxResult.Yes)
                    {
                        continue;
                    }
                    if (choice == MessageBoxResult.Cancel)
                    {
                        return;
                    }
                    break;
                }
                closeApproved = true;
                await _runtime.DisposeAsync();
                _runtimeDisposed = true;
            }
            closeApproved = true;
        }
        finally
        {
            if (closeApproved)
            {
                if (_mainWindow is not null)
                {
                    _mainWindow.AllowClose = true;
                    _mainWindow.Close();
                }
                _petWindow?.Close();
                _miniPositionWindow?.Close();
                Shutdown();
            }
            else
            {
                try
                {
                    if (_runtime is not null)
                    {
                        await _runtime.ResumeAfterCancelledShutdownAsync();
                    }
                }
                finally
                {
                    _shutdownStarted = false;
                    if (_mainWindow is not null)
                    {
                        _mainWindow.IsEnabled = true;
                    }
                }
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _petWindow?.DisposeTrayIcon();
        if (!_runtimeDisposed)
        {
            _runtime?.RequestStop();
        }
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
