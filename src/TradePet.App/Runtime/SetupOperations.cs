using System.Diagnostics;
using System.IO;
using TradePet.Infrastructure.Mt5;

namespace TradePet.App.Runtime;

internal static class SetupOperations
{
    public static Mt5TerminalInstallation RequireTerminal(string? path, TradingPlatform platform) =>
        new Mt5TerminalDiscovery().FindPreferred(path, platform)
        ?? throw new InvalidOperationException("请先选择终端；未找到时，点击“浏览终端”。");

    public static string InstallBridge(Mt5TerminalInstallation terminal)
    {
        if (terminal.Platform == TradingPlatform.Mt5)
        {
            var paths = RuntimePaths.Resolve();
            return new BridgeInstaller().Install(terminal, paths.BridgeCompiled, paths.BridgeSource).ExpertDirectory;
        }
        if (terminal.DataDirectory is null)
            throw new InvalidOperationException("请先启动一次 MT4，使其创建数据目录，再重新检测。");
        var folder = Path.Combine(terminal.DataDirectory, "MQL4", "Experts", "TradePet");
        var source = Path.Combine(AppContext.BaseDirectory, "Runtime", "mt4", "TradePetBridge.mq4");
        var compiled = Path.ChangeExtension(source, ".ex4");
        if (!File.Exists(source) || !File.Exists(compiled))
            throw new FileNotFoundException("MT4 插件文件不完整，请使用包含 Runtime/mt4 的完整发布包。");
        Directory.CreateDirectory(folder);
        File.Copy(source, Path.Combine(folder, "TradePetBridge.mq4"), true);
        File.Copy(compiled, Path.Combine(folder, "TradePetBridge.ex4"), true);
        return folder;
    }

    public static async Task<bool> CheckPythonAsync(CancellationToken token)
    {
        try
        {
            var result = await RunAsync(RuntimePaths.Resolve().PythonExecutable,
                ["-c", "import MetaTrader5; print('ready')"], TimeSpan.FromSeconds(15), token);
            return result == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static Task<int> RepairPythonAsync(string? pythonPath, CancellationToken token)
    {
        var script = RuntimePaths.Resolve().PythonSetupScript;
        if (!File.Exists(script)) throw new FileNotFoundException("找不到环境修复脚本，请重新解压完整发布包。");
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };
        if (pythonPath is not null) args.AddRange(["-PythonPath", pythonPath]);
        return RunAsync("powershell.exe", args, TimeSpan.FromMinutes(5), token);
    }

    private static async Task<int> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动环境检测。");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            AppLog.Write($"Setup exit {process.ExitCode}: {await stdout}\n{await stderr}");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("环境操作超时，请检查网络或 Python 安装后重试。");
        }
    }
}
