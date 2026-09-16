namespace TradePet.Core.Trading;

public static class AlertTradeIdentity
{
    public static string? TryExtract(string alertId)
    {
        if (string.IsNullOrWhiteSpace(alertId)) return null;
        var parts = alertId.Split(':');
        if (parts.Length == 2 && parts[0] == "add-loss" && long.TryParse(parts[1], out _)) return parts[1];
        if (parts.Length >= 3 && parts[0] == "open" && long.TryParse(parts[^1], out _)) return parts[^1];
        return null;
    }
}
