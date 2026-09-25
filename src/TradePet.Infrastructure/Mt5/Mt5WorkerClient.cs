using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Mt5;

public sealed record Mt5WorkerOptions(
    string PythonExecutable,
    string WorkerScriptPath,
    string TerminalPath);

public sealed class Mt5WorkerClient : ITradingWorkerClient
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);
    private readonly Mt5WorkerOptions _options;
    private readonly Channel<ProtocolEnvelope> _events = Channel.CreateBounded<ProtocolEnvelope>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _sourceInstanceId = $"app-{Guid.NewGuid():N}";
    private Process? _process;
    private long _sequence;
    private long _lastMessageAtUtcTicks;
    private bool _disposed;

    public Mt5WorkerClient(Mt5WorkerOptions options)
    {
        _options = options;
    }

    public ChannelReader<ProtocolEnvelope> Events => _events.Reader;

    public event Action<string>? DiagnosticReceived;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retry = TimeSpan.FromMilliseconds(500);
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                await RunProcessOnceAsync(cancellationToken);
                retry = TimeSpan.FromMilliseconds(500);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                DiagnosticReceived?.Invoke($"MT5 worker stopped: {exception.Message}");
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(retry, cancellationToken);
                retry = TimeSpan.FromSeconds(Math.Min(10, retry.TotalSeconds * 2));
            }
        }
    }

    public Task RefreshAsync(
        int serverUtcOffsetSeconds,
        DateOnly serverDate,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync("refresh", new { serverUtcOffsetSeconds, serverDate }, cancellationToken);

    public Task RefreshHistoryAsync(
        IReadOnlyCollection<int> historyYears,
        int serverUtcOffsetSeconds,
        DateOnly serverDate,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync("refresh", new
        {
            historyYears = historyYears.Distinct().OrderDescending().ToArray(),
            serverUtcOffsetSeconds,
            serverDate,
        }, cancellationToken);

    public Task RequestSnapshotAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("request_snapshot", new { }, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        await SendCommandAsync("shutdown", new { }, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync();
        }
        finally
        {
            _events.Writer.TryComplete();
            _writeLock.Dispose();
            _process?.Dispose();
        }
    }

    private async Task RunProcessOnceAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(_options.WorkerScriptPath);
        startInfo.ArgumentList.Add("--terminal-path");
        startInfo.ArgumentList.Add(_options.TerminalPath);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the MT5 worker.");
        }

        _process = process;
        Interlocked.Exchange(ref _lastMessageAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputTask = ReadOutputAsync(process, cancellationToken);
        var errorTask = ReadErrorsAsync(process, cancellationToken);
        var watchdogTask = WatchWorkerAsync(process, watchdogCancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        finally
        {
            watchdogCancellation.Cancel();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try
            {
                await watchdogTask;
            }
            catch (OperationCanceledException) when (watchdogCancellation.IsCancellationRequested)
            {
            }

            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }

            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var envelope = ProtocolEnvelope.Create(
                    _sourceInstanceId,
                    Interlocked.Increment(ref _sequence),
                    "connection",
                    new { connected = false, reason = "worker_exit" });
                await _events.Writer.WriteAsync(envelope, cancellationToken);
            }
        }
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (ProtocolLineParser.TryParse(line, out var envelope, out var error))
            {
                Interlocked.Exchange(ref _lastMessageAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                await _events.Writer.WriteAsync(envelope!, cancellationToken);
            }
            else
            {
                DiagnosticReceived?.Invoke($"Invalid MT5 worker message: {error}");
            }
        }
    }

    private async Task WatchWorkerAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(WatchdogInterval, cancellationToken);
            var lastMessage = new DateTimeOffset(
                Interlocked.Read(ref _lastMessageAtUtcTicks),
                TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - lastMessage <= MessageTimeout)
            {
                continue;
            }

            DiagnosticReceived?.Invoke("MT5 worker stopped producing data; restarting read-only collector.");
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            return;
        }
    }

    private async Task ReadErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            DiagnosticReceived?.Invoke(line);
        }
    }

    private async Task SendCommandAsync<T>(string kind, T payload, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return;
        }

        var envelope = ProtocolEnvelope.Create(_sourceInstanceId, Interlocked.Increment(ref _sequence), kind, payload);
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
