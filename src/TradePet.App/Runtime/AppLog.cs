using System.IO;
using System.Text.RegularExpressions;
using TradePet.Infrastructure.Persistence;

namespace TradePet.App.Runtime;

internal static partial class AppLog
{
    private const long MaximumLogLength = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var directory = TradePetPaths.GetLogDirectory();
                Directory.CreateDirectory(directory);
                DeleteExpiredLogs(directory);
                var path = Path.Combine(directory, $"tradepet-{DateTime.UtcNow:yyyyMMdd}.log");
                RotateIfNeeded(path);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {Redact(message)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never interrupt trading-state observation or the pet UI.
        }
    }

    private static string Redact(string message) => LongNumber().Replace(message, match =>
        $"****{match.Value[^Math.Min(4, match.Value.Length)..]}");

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumLogLength)
        {
            return;
        }

        var archive = path + ".1";
        if (File.Exists(archive))
        {
            File.Delete(archive);
        }
        File.Move(path, archive);
    }

    private static void DeleteExpiredLogs(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-14);
        foreach (var path in Directory.EnumerateFiles(directory, "tradepet-*.log*"))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                File.Delete(path);
            }
        }
    }

    [GeneratedRegex(@"(?<!\d)\d{5,}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();
}
