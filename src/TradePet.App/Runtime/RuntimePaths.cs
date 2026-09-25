using System.IO;

namespace TradePet.App.Runtime;

internal sealed record RuntimePaths(
    string PythonExecutable,
    string WorkerScript,
    string HistoryWorkerScript,
    string BridgeCompiled,
    string BridgeSource,
    string PythonSetupScript)
{
    public static RuntimePaths Resolve()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var packagedWorker = Path.Combine(baseDirectory, "Runtime", "python", "tradepet_mt5_worker.py");
        var packagedHistoryWorker = Path.Combine(baseDirectory, "Runtime", "python", "tradepet_mt5_history_worker.py");
        var packagedBridge = Path.Combine(baseDirectory, "Runtime", "TradePetBridge.ex5");
        var packagedSource = Path.Combine(baseDirectory, "Runtime", "TradePetBridge.mq5");
        var packagedSetup = Path.Combine(baseDirectory, "Runtime", "setup-python.ps1");
        var repoRoot = FindRepoRoot(baseDirectory);
        var worker = File.Exists(packagedWorker)
            ? packagedWorker
            : Path.Combine(repoRoot ?? baseDirectory, "python", "tradepet_mt5_worker.py");
        var historyWorker = File.Exists(packagedHistoryWorker)
            ? packagedHistoryWorker
            : Path.Combine(repoRoot ?? baseDirectory, "python", "tradepet_mt5_history_worker.py");
        var bridge = File.Exists(packagedBridge)
            ? packagedBridge
            : Path.Combine(repoRoot ?? baseDirectory, "src", "TradePet.App", "Runtime", "TradePetBridge.ex5");
        var source = File.Exists(packagedSource)
            ? packagedSource
            : Path.Combine(repoRoot ?? baseDirectory, "mt5", "TradePetBridge.mq5");
        var virtualEnvironmentPython = repoRoot is null
            ? string.Empty
            : Path.Combine(repoRoot, ".venv", "Scripts", "python.exe");
        var userEnvironmentPython = Path.Combine(
            TradePet.Infrastructure.Persistence.TradePetPaths.GetDataDirectory(),
            "python",
            "venv",
            "Scripts",
            "python.exe");
        var systemPython = @"C:\Program Files\Python313\python.exe";
        var python = File.Exists(userEnvironmentPython)
            ? userEnvironmentPython
            : File.Exists(virtualEnvironmentPython) ? virtualEnvironmentPython
            : File.Exists(systemPython) ? systemPython : "python";
        var setup = File.Exists(packagedSetup) ? packagedSetup : Path.Combine(repoRoot ?? baseDirectory, "scripts", "setup-python.ps1");
        return new RuntimePaths(python, worker, historyWorker, bridge, source, setup);
    }

    private static string? FindRepoRoot(string startingPath)
    {
        var current = new DirectoryInfo(startingPath);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TradePet.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
