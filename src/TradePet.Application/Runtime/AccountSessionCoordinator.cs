namespace TradePet.Application.Runtime;

public sealed record AccountSessionContext(
    string AccountKey,
    long Generation,
    CancellationToken CancellationToken);

public interface IAccountSessionCoordinator : IDisposable
{
    AccountSessionContext? Current { get; }
    Task<AccountSessionContext> SwitchAsync(string accountKey, CancellationToken cancellationToken = default);
    bool IsCurrent(string accountKey, long generation);
}

public sealed class AccountSessionCoordinator : IAccountSessionCoordinator
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly CancellationToken _lifetimeToken;
    private AccountSessionContext? _current;
    private CancellationTokenSource? _currentCancellation;
    private long _generation;
    private bool _disposed;

    public AccountSessionCoordinator(CancellationToken lifetimeToken = default) =>
        _lifetimeToken = lifetimeToken;

    public AccountSessionContext? Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    public async Task<AccountSessionContext> SwitchAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new ArgumentException("账户会话必须包含账户。", nameof(accountKey));
        }

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? previous;
            long generation;
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                previous = _currentCancellation;
                _currentCancellation = null;
                _current = null;
                generation = ++_generation;
            }

            if (previous is not null)
            {
                await previous.CancelAsync().ConfigureAwait(false);
                previous.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var nextCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            var next = new AccountSessionContext(accountKey.Trim(), generation, nextCancellation.Token);
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _currentCancellation = nextCancellation;
                _current = next;
            }
            return next;
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public bool IsCurrent(string accountKey, long generation)
    {
        lock (_stateGate)
        {
            return _current is { } current &&
                   current.Generation == generation &&
                   string.Equals(current.AccountKey, accountKey, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? current;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            current = _currentCancellation;
            _currentCancellation = null;
            _current = null;
        }
        current?.Cancel();
        current?.Dispose();
        _switchGate.Dispose();
    }
}
