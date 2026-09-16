namespace TradePet.Application.Runtime;

public interface IAsyncScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemAsyncScheduler : IAsyncScheduler
{
    private readonly TimeProvider _timeProvider;

    public SystemAsyncScheduler(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
}
