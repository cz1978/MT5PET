using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TradePet.Infrastructure.Mt5;

public sealed record Mt5TerminalInstallation(
    string TerminalPath,
    string TerminalId,
    string? DataDirectory,
    bool IsRunning);

public sealed class Mt5TerminalDiscovery
{
    public IReadOnlyList<Mt5TerminalInstallation> Discover()
    {
        var runningPaths = DiscoverRunningPaths();
        var candidates = new HashSet<string>(runningPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var knownPath in KnownTerminalPaths())
        {
            if (File.Exists(knownPath))
            {
                candidates.Add(knownPath);
            }
        }

        var metaQuotesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MetaQuotes",
            "Terminal");
        return candidates
            .Select(path => new Mt5TerminalInstallation(
                path,
                CreateTerminalId(path),
                ResolveDataDirectory(path, metaQuotesRoot),
                runningPaths.Contains(path)))
            .OrderByDescending(item => item.IsRunning)
            .ThenByDescending(item => item.TerminalPath.Contains("WeTrade", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.TerminalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Mt5TerminalInstallation? FindPreferred(string? previouslySelectedPath = null)
    {
        var terminals = Discover();
        if (!string.IsNullOrWhiteSpace(previouslySelectedPath))
        {
            var previous = terminals.FirstOrDefault(item =>
                string.Equals(item.TerminalPath, previouslySelectedPath, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                return previous;
            }
        }

        return terminals.FirstOrDefault(item => item.IsRunning) ?? terminals.FirstOrDefault();
    }

    public static string? ResolveDataDirectory(string terminalPath, string metaQuotesRoot)
    {
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
            catch (IOException)
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

    private static HashSet<string> DiscoverRunningPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("terminal64"))
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
