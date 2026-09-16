using System.IO;
using System.Text.Json;
using System.Windows;
using TradePet.Infrastructure.Persistence;

namespace TradePet.App.Views;

internal static class PetWindowPlacement
{
    private static string PathName => Path.Combine(TradePetPaths.GetDataDirectory(), "window-placement.json");

    public static void Save(string key, double left, double top)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            var values = File.Exists(PathName)
                ? JsonSerializer.Deserialize<Dictionary<string, PlacementPoint>>(File.ReadAllText(PathName)) ?? []
                : [];
            var width = Math.Max(1d, SystemParameters.VirtualScreenWidth);
            var height = Math.Max(1d, SystemParameters.VirtualScreenHeight);
            values[key] = new PlacementPoint((left - SystemParameters.VirtualScreenLeft) / width, (top - SystemParameters.VirtualScreenTop) / height);
            File.WriteAllText(PathName, JsonSerializer.Serialize(values));
        }
        catch { }
    }

    public static System.Windows.Point? Load(string key)
    {
        try
        {
            if (!File.Exists(PathName)) return null;
            var values = JsonSerializer.Deserialize<Dictionary<string, PlacementPoint>>(File.ReadAllText(PathName));
            if (values is null || !values.TryGetValue(key, out var normalized)) return null;
            return new System.Windows.Point(SystemParameters.VirtualScreenLeft + normalized.X * SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenTop + normalized.Y * SystemParameters.VirtualScreenHeight);
        }
        catch { return null; }
    }

    private sealed record PlacementPoint(double X, double Y);
}
