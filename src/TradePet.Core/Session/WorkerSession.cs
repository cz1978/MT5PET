namespace TradePet.Core.Session;

public enum WorkerSessionPhase
{
    Disconnected,
    Recovering,
    Live,
}

public sealed class WorkerSession
{
    private readonly object _sync = new();
    private WorkerSessionState _state = WorkerSessionState.Disconnected;

    public bool IsConnected
    {
        get { lock (_sync) return _state.IsConnected; }
    }

    public string? AccountKey
    {
        get { lock (_sync) return _state.AccountKey; }
    }

    public bool HasInitialSnapshot
    {
        get { lock (_sync) return _state.HasInitialSnapshot; }
    }

    public bool HasInitialDeals
    {
        get { lock (_sync) return _state.HasInitialDeals; }
    }

    public WorkerSessionPhase Phase
    {
        get { lock (_sync) return _state.Phase; }
    }

    public bool IsRecovery => Phase != WorkerSessionPhase.Live;

    public void Connect(string? accountKey)
    {
        lock (_sync)
        {
            _state = new WorkerSessionState(
                true,
                accountKey,
                false,
                false,
                WorkerSessionPhase.Recovering);
        }
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            _state = WorkerSessionState.Disconnected;
        }
    }

    public void ResetForAccount(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        lock (_sync)
        {
            _state = new WorkerSessionState(
                _state.IsConnected,
                accountKey,
                false,
                false,
                _state.IsConnected ? WorkerSessionPhase.Recovering : WorkerSessionPhase.Disconnected);
        }
    }

    public void ResetDealsBaseline()
    {
        lock (_sync)
        {
            _state = _state with
            {
                HasInitialDeals = false,
                Phase = _state.IsConnected
                    ? WorkerSessionPhase.Recovering
                    : WorkerSessionPhase.Disconnected,
            };
        }
    }

    public void MarkSnapshotReceived(bool requiresDealHistory = true)
    {
        lock (_sync)
        {
            var next = _state with { HasInitialSnapshot = true };
            _state = !requiresDealHistory && next.IsConnected
                ? next with { Phase = WorkerSessionPhase.Live }
                : Advance(next);
        }
    }

    public void MarkDealsReceived()
    {
        lock (_sync)
        {
            _state = Advance(_state with { HasInitialDeals = true });
        }
    }

    private static WorkerSessionState Advance(WorkerSessionState state) => state with
    {
        Phase = state.IsConnected && state.HasInitialSnapshot && state.HasInitialDeals
            ? WorkerSessionPhase.Live
            : state.IsConnected
                ? WorkerSessionPhase.Recovering
                : WorkerSessionPhase.Disconnected,
    };

    private sealed record WorkerSessionState(
        bool IsConnected,
        string? AccountKey,
        bool HasInitialSnapshot,
        bool HasInitialDeals,
        WorkerSessionPhase Phase)
    {
        public static WorkerSessionState Disconnected { get; } =
            new(false, null, false, false, WorkerSessionPhase.Disconnected);
    }
}
