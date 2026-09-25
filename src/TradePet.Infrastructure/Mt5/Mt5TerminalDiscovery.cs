using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TradePet.Infrastructure.Mt5;

public enum TradingPlatform { Mt5, Mt4 }

public sealed record Mt5TerminalInstallation(
    string TerminalPath,
    string TerminalId,
    string? DataDirectory,
    bool IsRunning,
    TradingPlatform Platform = TradingPlatform.Mt5)
{
    public string Label => $"{Path.GetFileName(Path.GetDirectoryName(TerminalPath))} · {(IsRunning ? "运行中" : "未启动")}";
}

public sealed class Mt5TerminalDiscovery
{
    public IReadOnlyList<Mt5TerminalInstallation> Discover(string? selectedPath = null, TradingPlatform platform = TradingPlatform.Mt5)
    {
        var runningPaths = DiscoverRunningPaths(platform);
        var candidates = new HashSet<string>(runningPaths, StringComparer.OrdinalIgnoreCase);
        var metaQuotesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal");
        foreach (var knownPath in KnownTerminalPaths().Concat(DiscoverRegisteredPaths(metaQuotesRoot, platform)).Append(selectedPath ?? string.Empty))
        {
            if (IsTerminalPath(knownPath, platform) && File.Exists(knownPath))
            {
                candidates.Add(Path.GetFullPath(knownPath));
            }
        }

        return candidates
            .Select(path => new Mt5TerminalInstallation(
                path,
                CreateTerminalId(path),
                ResolveDataDirectory(path, metaQuotesRoot),
                runningPaths.Contains(path), platform))
            .OrderByDescending(item => item.IsRunning)
            .ThenBy(item => item.TerminalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Mt5TerminalInstallation? FindPreferred(string? previouslySelectedPath = null, TradingPlatform platform = TradingPlatform.Mt5)
    {
        var terminals = Discover(previouslySelectedPath, platform);
        if (!string.IsNullOrWhiteSpace(previouslySelectedPath))
        {
            var previous = terminals.FirstOrDefault(item =>
                string.Equals(item.TerminalPath, previouslySelectedPath, StringComparison.OrdinalIgnoreCase));
            // Never silently switch a saved account source to a different installation.
            return previous;
        }

        return terminals.FirstOrDefault(item => item.IsRunning) ?? terminals.FirstOrDefault();
    }

    public static string? ResolveDataDirectory(string terminalPath, string metaQuotesRoot)
    {
        var installation = GetInstallationDirectory(terminalPath);
        var mqlDirectory = IsTerminalPath(terminalPath, TradingPlatform.Mt4) ? "MQL4" : "MQL5";
        if (Directory.Exists(Path.Combine(installation, mqlDirectory)))
        {
            return installation;
        }
        if (!Directory.Exists(metaQuotesRoot))
        {
            return null;
        }

        var normalizedTerminal = NormalizePath(terminalPath);
        foreach (var directory in Directory.EnumerateDirectories(metaQuotesRoot))
        {
            var originPath = Path.Combine(directory, "origin.txt");
            if (!File.Exists(originPath))
            {
                continue;
            }

            try
            {
                var origin = NormalizePath(File.ReadAllText(originPath).Trim());
                var terminalDirectory = NormalizePath(Path.GetDirectoryName(normalizedTerminal) ?? string.Empty);
                if (string.Equals(origin, terminalDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
            {
                // A terminal may be updating its files while discovery is running.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore inaccessible installations and continue discovery.
            }
        }

        return null;
    }

    public static string CreateTerminalId(string terminalPath)
    {
        var normalized = GetInstallationDirectory(terminalPath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static string GetInstallationDirectory(string terminalPath)
    {
        var normalized = NormalizePath(terminalPath);
        return string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(normalized) ?? normalized
            : normalized;
    }

    public static bool IsTerminalPath(string path, TradingPlatform platform) =>
        string.Equals(Path.GetFileName(path), platform == TradingPlatform.Mt4 ? "terminal.exe" : "terminal64.exe", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DiscoverRegisteredPaths(string metaQuotesRoot, TradingPlatform platform)
    {
        var paths = new List<string>();
        if (!Directory.Exists(metaQuotesRoot)) return paths;
        foreach (var directory in Directory.EnumerateDirectories(metaQuotesRoot))
        {
            try
            {
                var origin = Path.Combine(directory, "origin.txt");
                if (!File.Exists(origin)) continue;
                var install = File.ReadAllText(origin).Trim();
                if (!Path.IsPathFullyQualified(install)) continue;
                var terminal = Path.Combine(install, platform == TradingPlatform.Mt4 ? "terminal.exe" : "terminal64.exe");
                if (File.Exists(terminal)) paths.Add(Path.GetFullPath(terminal));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // One stale or inaccessible registration must not hide the other terminals.
            }
        }
        return paths;
    }

    private static HashSet<string> DiscoverRunningPaths(TradingPlatform platform)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName(platform == TradingPlatform.Mt4 ? "terminal" : "terminal64"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { Length: > 0 } path)
                    {
                        paths.Add(Path.GetFullPath(path));
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return paths;
    }

    private static IEnumerable<string> KnownTerminalPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(programFiles, "WeTrade MetaTrader 5 Terminal", "terminal64.exe");
        yield return Path.Combine(programFiles, "MetaTrader 5", "terminal64.exe");
        yield return Path.Combine(programFilesX86, "WeTrade MetaTrader 5 Terminal", "terminal64.exe");
    }

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar);
}
