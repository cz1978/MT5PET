using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public static class AlertDeliveryIdentity
{
    public static string Create(
        string accountKey,
        DateOnly serverDate,
        string alertId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        return FormattableString.Invariant(
            $"{accountKey.Length}:{accountKey}|{serverDate:yyyyMMdd}|{alertId.Length}:{alertId}");
    }
}

public sealed class AlertDeliveryGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AlertPriority> _delivered = new(StringComparer.Ordinal);

    public bool CanAttempt(
        string accountKey,
        DateOnly serverDate,
        string alertId,
        AlertPriority incomingPriority,
        AlertPriority? activePriority,
        bool deduplicate = true)
    {
        if (activePriority is { } active && active > incomingPriority)
        {
            return false;
        }
        if (!deduplicate)
        {
            return true;
        }

        var key = AlertDeliveryIdentity.Create(accountKey, serverDate, alertId);
        lock (_sync)
        {
            return !_delivered.TryGetValue(key, out var deliveredPriority) || incomingPriority > deliveredPriority;
        }
    }

    public bool Commit(
        string accountKey,
        DateOnly serverDate,
        string alertId,
        bool deduplicate = true,
        AlertPriority priority = AlertPriority.Important)
    {
        if (!deduplicate)
        {
            return true;
        }

        var key = AlertDeliveryIdentity.Create(accountKey, serverDate, alertId);
        lock (_sync)
        {
            if (_delivered.TryGetValue(key, out var delivered) && delivered >= priority) return false;
            _delivered[key] = priority;
            return true;
        }
    }
}
