namespace TradePet.Application.Runtime;

public enum MaintenanceOperationKind
{
    Read,
    Write,
    File,
}

public interface IMaintenanceCoordinator : IDisposable
{
    int ActiveOperationCount { get; }
    bool IsMaintenancePending { get; }

    ValueTask<IAsyncDisposable> EnterOperationAsync(
        MaintenanceOperationKind kind,
        CancellationToken cancellationToken = default);

    ValueTask<IAsyncDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken = default);
}

public sealed class MaintenanceCoordinator : IMaintenanceCoordinator
{
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _maintenanceSerial = new(1, 1);
    private int _activeOperations;
    private bool _maintenancePending;
    private bool _disposed;
    private TaskCompletionSource _operationsDrained = CompletedSource();
    private TaskCompletionSource _operationsResumed = CompletedSource();

    public int ActiveOperationCount => Volatile.Read(ref _activeOperations);
    public bool IsMaintenancePending => Volatile.Read(ref _maintenancePending);

    public async ValueTask<IAsyncDisposable> EnterOperationAsync(
        MaintenanceOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        _ = kind;
        while (true)
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task? waitForResume = null;
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_maintenancePending)
                {
                    _activeOperations++;
                    if (_activeOperations == 1)
                    {
                        _operationsDrained = NewSource();
                    }
                    return new AsyncLease(ReleaseOperationAsync);
                }
                waitForResume = _operationsResumed.Task;
            }
            finally
            {
                _stateGate.Release();
            }

            await waitForResume.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<IAsyncDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await _maintenanceSerial.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ownsSerial = true;
        try
        {
            Task waitForDrain;
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _maintenancePending = true;
                _operationsResumed = NewSource();
                waitForDrain = _operationsDrained.Task;
            }
            finally
            {
                _stateGate.Release();
            }

            try
            {
                await waitForDrain.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await ResumeOperationsAsync().ConfigureAwait(false);
                throw;
            }

            ownsSerial = false;
            return new AsyncLease(ReleaseMaintenanceAsync);
        }
        finally
        {
            if (ownsSerial)
            {
                _maintenanceSerial.Release();
            }
        }
    }

    public void Dispose()
    {
        _stateGate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }
            if (_activeOperations != 0 || _maintenancePending)
            {
                throw new InvalidOperationException("仍有活动操作或维护租约，不能释放协调器。");
            }
            _disposed = true;
        }
        finally
        {
            _stateGate.Release();
        }
        _stateGate.Dispose();
        _maintenanceSerial.Dispose();
    }

    private async ValueTask ReleaseOperationAsync()
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeOperations <= 0)
            {
                throw new InvalidOperationException("维护操作租约释放次数不匹配。");
            }
            _activeOperations--;
            if (_activeOperations == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async ValueTask ReleaseMaintenanceAsync()
    {
        await ResumeOperationsAsync().ConfigureAwait(false);
        _maintenanceSerial.Release();
    }

    private async ValueTask ResumeOperationsAsync()
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _maintenancePending = false;
            _operationsResumed.TrySetResult();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSource()
    {
        var source = NewSource();
        source.SetResult();
        return source;
    }

    private sealed class AsyncLease(Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? _release = release;

        public async ValueTask DisposeAsync()
        {
            var callback = Interlocked.Exchange(ref _release, null);
            if (callback is not null)
            {
                await callback().ConfigureAwait(false);
            }
        }
    }
}
