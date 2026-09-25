using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TradePet.App.Runtime;
using TradePet.App.ViewModels;
using TradePet.Infrastructure.Mt4;
using TradePet.Infrastructure.Mt5;

namespace TradePet.App.Views;

public partial class SetupWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<Task<bool>> _save;
    private readonly CancellationTokenSource _closed = new();
    private int _step;
    private bool _ready;
    private bool _busy;
    private bool _saved;

    public SetupWindow(MainViewModel viewModel, Func<Task<bool>> save)
    {
        _viewModel = viewModel;
        _save = save;
        InitializeComponent();
        DataContext = viewModel;
        _ready = true;
        RefreshTerminals();
        UpdateStep();
        Closed += (_, _) => _closed.Cancel();
    }

    private void RefreshTerminals()
    {
        var selected = _viewModel.SelectedTerminalPath;
        var terminals = new Mt5TerminalDiscovery().Discover(selected, _viewModel.SelectedPlatform);
        _viewModel.TerminalOptions.Clear();
        foreach (var terminal in terminals) _viewModel.TerminalOptions.Add(new TerminalOption(terminal.TerminalPath, terminal.Label));
        if (selected is not null && !terminals.Any(item => string.Equals(item.TerminalPath, selected, StringComparison.OrdinalIgnoreCase)))
            _viewModel.TerminalOptions.Add(new TerminalOption(selected, "已保存的终端 · 未找到，请重新选择"));
        _viewModel.SelectedTerminalPath = selected ?? terminals.FirstOrDefault()?.TerminalPath;
        var mt4 = _viewModel.SelectedPlatform == TradingPlatform.Mt4;
        PlatformDescription.Text = mt4
            ? "MT4 · 只读监控：账户、持仓、挂单和浮亏提醒。成交历史、完整复盘、图表计划、日历与日报暂不支持。无需 Python。"
            : "MT5 · 账户与持仓监控；对冲账户支持完整复盘。图表计划和经济日历需要桥接插件。";
        PythonPanel.Visibility = mt4 ? Visibility.Collapsed : Visibility.Visible;
        BridgeInstructions.Text = mt4
            ? "安装后，在 MT4 导航器的 EA 列表右键刷新，将 TradePet / TradePetBridge 拖到一个图表，并保持该图表打开。\n\n不需要 DLL 权限，不需要允许实盘交易。只在一个图表上运行此插件。"
            : "安装后，在 MT5 导航器的 EA 列表右键刷新，将 TradePet / TradePetBridge 拖到一个图表。\n\n保持终端和图表打开，看到“桥接插件已连接”即完成。";
    }

    private void UpdateStep()
    {
        var panels = new[] { PlatformStep, PrepareStep, VerifyStep, PreferencesStep };
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Text = $"第 {_step + 1} 步，共 4 步";
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == 3 ? "保存并完成设置" : "下一步";
        StepScroll.ScrollToTop();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        StepScroll.IsEnabled = BackButton.IsEnabled = NextButton.IsEnabled = LaterButton.IsEnabled = false;
        Status.Text = "正在处理…";
        try { await action(); }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception exception) { AppLog.Write($"Setup failed: {exception}"); Status.Text = exception.Message; }
        finally
        {
            _busy = false;
            StepScroll.IsEnabled = !_saved;
            BackButton.IsEnabled = !_saved && _step > 0;
            NextButton.IsEnabled = !_saved;
            LaterButton.IsEnabled = true;
        }
    }

    private void Platform_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (PlatformPicker.SelectedValue is TradingPlatform platform) _viewModel.SelectedPlatform = platform;
        RefreshTerminals();
        InstallStatus.Text = ConnectionCheck.Text = Status.Text = string.Empty;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshTerminals();
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var mt4 = _viewModel.SelectedPlatform == TradingPlatform.Mt4;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = mt4 ? "MT4 终端|terminal.exe" : "MT5 终端|terminal64.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        if (!Mt5TerminalDiscovery.IsTerminalPath(dialog.FileName, _viewModel.SelectedPlatform)) { Status.Text = "请选择对应平台的终端程序。"; return; }
        _viewModel.SelectedTerminalPath = dialog.FileName;
        RefreshTerminals();
    }
    private void Back_Click(object sender, RoutedEventArgs e) { _step--; Status.Text = string.Empty; UpdateStep(); }
    private void Later_Click(object sender, RoutedEventArgs e) => Close();
    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3) { _step++; Status.Text = string.Empty; UpdateStep(); return; }
        await RunAsync(async () =>
        {
            if (!await _save()) { Status.Text = "设置未能保存，请检查数据目录权限后重试。下次启动仍会显示向导。"; return; }
            _saved = true;
            Status.Text = "设置已保存。若切换了平台 / 终端或修复了环境，请从桌宠右键菜单退出，再重新启动 TradePet。";
            LaterButton.Content = "关闭向导";
        });
    }
    private async void Install_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var terminal = SetupOperations.RequireTerminal(_viewModel.SelectedTerminalPath, _viewModel.SelectedPlatform);
        InstallStatus.Text = "已安装到：" + SetupOperations.InstallBridge(terminal);
        Status.Text = "插件文件已就绪；请按上面的说明挂到图表。";
        return Task.CompletedTask;
    });
    private async void OpenData_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var terminal = SetupOperations.RequireTerminal(_viewModel.SelectedTerminalPath, _viewModel.SelectedPlatform);
        var directory = terminal.DataDirectory ?? throw new InvalidOperationException("尚未找到数据目录，请先启动一次交易终端。");
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        Status.Text = "已打开终端数据目录。";
        return Task.CompletedTask;
    });
    private async void CheckPython_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        PythonStatus.Text = await SetupOperations.CheckPythonAsync(_closed.Token) ? "Python 和 MetaTrader5 依赖已就绪。" : "依赖未就绪，请点击修复。";
        Status.Text = PythonStatus.Text;
    });
    private async void RepairPython_Click(object sender, RoutedEventArgs e) => await RepairAsync(null);
    private async void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Python 解释器|python.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await RepairAsync(dialog.FileName);
    }
    private Task RepairAsync(string? path) => RunAsync(async () =>
    {
        Status.Text = "正在准备 Python 依赖，可能需要几分钟…";
        var code = await SetupOperations.RepairPythonAsync(path, _closed.Token);
        if (code != 0) throw new InvalidOperationException("依赖修复失败。请检查网络、选择已安装的 64 位 Python 3.13 后重试；详细原因已写入日志。");
        PythonStatus.Text = await SetupOperations.CheckPythonAsync(_closed.Token) ? "环境已就绪；请在完成设置后重启 TradePet。" : "修复后检测未通过，请检查 Python 版本。";
        Status.Text = PythonStatus.Text;
    });
    private async void CheckConnection_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var terminal = SetupOperations.RequireTerminal(_viewModel.SelectedTerminalPath, _viewModel.SelectedPlatform);
        if (terminal.Platform == TradingPlatform.Mt4)
        {
            if (terminal.DataDirectory is null) throw new InvalidOperationException("请先启动 MT4 并安装插件。");
            var path = Path.Combine(terminal.DataDirectory, "MQL4", "Files", Mt4FileClient.SnapshotFileName);
            var frame = Mt4FileClient.ReadFrame(await File.ReadAllTextAsync(path, _closed.Token), terminal.TerminalPath, DateTimeOffset.UtcNow);
            ConnectionCheck.Text = frame.Connected ? "所选 MT4 插件正在输出有效账户数据。保存并重启后开始监控。" : "插件已运行，但 MT4 尚未连接账户服务器。";
        }
        else
        {
            ConnectionCheck.Text = terminal.IsRunning ? "所选 MT5 已运行。请结合账户和桥接状态确认连接；切换终端后需重启。" : "所选 MT5 尚未运行，请先启动并登录。";
        }
        Status.Text = "检查完成。";
    });
}
