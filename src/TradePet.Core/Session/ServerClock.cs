namespace TradePet.Core.Session;

public enum ServerClockSource
{
    LocalFallback,
    WorkerOffset,
    BridgeHeartbeat,
}

public sealed record ServerClockUpdate(
    bool Applied,
    bool ContextChanged,
    bool DateChanged);

public sealed class ServerClock
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _bridgeFreshness;
    private ServerClockState _state;

    public ServerClock(
        TimeProvider? timeProvider = null,
        TimeSpan? bridgeFreshness = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bridgeFreshness = bridgeFreshness ?? TimeSpan.FromSeconds(5);
        if (_bridgeFreshness < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bridgeFreshness));
        }

        _state = new ServerClockState(
            DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime),
            0,
            ServerClockSource.LocalFallback,
            null);
    }

    public DateOnly ServerDate
    {
        get { lock (_sync) return _state.ServerDate; }
    }

    public int UtcOffsetSeconds
    {
        get { lock (_sync) return _state.UtcOffsetSeconds; }
    }

    public ServerClockSource Source
    {
        get { lock (_sync) return _state.Source; }
    }

    public bool IsAuthoritative
    {
        get { lock (_sync) return _state.Source != ServerClockSource.LocalFallback; }
    }

    public ServerClockUpdate ApplyWorkerOffset(int utcOffsetSeconds)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (HasFreshBridge(now))
            {
                return new ServerClockUpdate(false, false, false);
            }

            var serverDate = Resolve(now, utcOffsetSeconds);
            return Apply(serverDate, utcOffsetSeconds, ServerClockSource.WorkerOffset, null);
        }
    }

    public ServerClockUpdate ApplyBridgeHeartbeat(
        DateOnly serverDate,
        int utcOffsetSeconds)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            return Apply(serverDate, utcOffsetSeconds, ServerClockSource.BridgeHeartbeat, now);
        }
    }

    public void MarkBridgeDisconnected()
    {
        lock (_sync)
        {
            _state = _state with { LastBridgeHeartbeatUtc = null };
        }
    }

    public DateOnly Resolve(DateTimeOffset occurredAtUtc)
    {
        lock (_sync)
        {
            return Resolve(occurredAtUtc, _state.UtcOffsetSeconds);
        }
    }

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ResolveDayUtcRange(DateOnly serverDate)
    {
        lock (_sync)
        {
            var offset = TimeSpan.FromSeconds(_state.UtcOffsetSeconds);
            var start = new DateTimeOffset(serverDate.ToDateTime(TimeOnly.MinValue), offset).ToUniversalTime();
            return (start, start.AddDays(1));
        }
    }

    private ServerClockUpdate Apply(
        DateOnly serverDate,
        int utcOffsetSeconds,
        ServerClockSource source,
        DateTimeOffset? lastBridgeHeartbeatUtc)
    {
        var wasAuthoritative = _state.Source != ServerClockSource.LocalFallback;
        var contextChanged = !wasAuthoritative || _state.UtcOffsetSeconds != utcOffsetSeconds || _state.Source != source;
        var dateChanged = !wasAuthoritative || _state.ServerDate != serverDate;
        _state = new ServerClockState(
            serverDate,
            utcOffsetSeconds,
            source,
            lastBridgeHeartbeatUtc);
        return new ServerClockUpdate(true, contextChanged, dateChanged);
    }

    private bool HasFreshBridge(DateTimeOffset now) =>
        _state.LastBridgeHeartbeatUtc is { } heartbeat &&
        now <= heartbeat + _bridgeFreshness;

    private static DateOnly Resolve(DateTimeOffset occurredAtUtc, int utcOffsetSeconds) =>
        DateOnly.FromDateTime(occurredAtUtc.UtcDateTime.AddSeconds(utcOffsetSeconds));

    private sealed record ServerClockState(
        DateOnly ServerDate,
        int UtcOffsetSeconds,
        ServerClockSource Source,
        DateTimeOffset? LastBridgeHeartbeatUtc);
}
