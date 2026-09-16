using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Mt5;

public sealed class BridgePipeServer : IAsyncDisposable
{
    public const string DefaultPipeName = "TradePetBridge.v1";
    public const string CommandPipeSuffix = ".commands";

    private readonly string _pipeName;
    private readonly Channel<ProtocolEnvelope> _events = Channel.CreateBounded<ProtocolEnvelope>(
        new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly string _commandSourceInstanceId = $"tradepet-app-{Guid.NewGuid():N}";
    private string _latestCommandLine;
    private long _commandSequence;
    private bool _disposed;
    private volatile bool _connected;

    public BridgePipeServer(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
        _latestCommandLine = SerializeCommand(ProtocolEnvelope.Create(
            _commandSourceInstanceId,
            0,
            "loss_zone_snapshot",
            new LossZoneChartSnapshot(0, string.Empty, [])));
    }

    public ChannelReader<ProtocolEnvelope> Events => _events.Reader;

    public bool IsConnected => _connected;

    public event Action<string>? DiagnosticReceived;
    public event Action<bool>? ConnectionChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            RunEventPipeAsync(cancellationToken),
            RunCommandPipeAsync(cancellationToken));
    }

    public void SetLossZones(
        string terminalPath,
        string accountKey,
        DateOnly serverDate,
        IReadOnlyCollection<BridgeLossZone> zones)
    {
        var sequence = Interlocked.Increment(ref _commandSequence);
        var snapshot = new LossZoneChartSnapshot(
            sequence,
            Mt5TerminalDiscovery.GetInstallationDirectory(terminalPath),
            zones.OrderBy(zone => zone.Id).ToArray());
        var envelope = ProtocolEnvelope.Create(
            _commandSourceInstanceId,
            sequence,
            "loss_zone_snapshot",
            snapshot,
            accountKey,
            serverDate);
        Volatile.Write(ref _latestCommandLine, SerializeCommand(envelope));
    }

    private async Task RunEventPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                _connected = true;
                ConnectionChanged?.Invoke(true);
                DiagnosticReceived?.Invoke("TradePet Bridge connected.");
                await ReadConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                DiagnosticReceived?.Invoke($"TradePet Bridge disconnected: {exception.Message}");
            }
            finally
            {
                var wasConnected = _connected;
                _connected = false;
                if (wasConnected)
                {
                    ConnectionChanged?.Invoke(false);
                }
            }
        }
    }

    private async Task RunCommandPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName + CommandPipeSuffix,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                var bytes = Encoding.UTF8.GetBytes(Volatile.Read(ref _latestCommandLine));
                await pipe.WriteAsync(bytes, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                DiagnosticReceived?.Invoke($"TradePet Bridge command pipe disconnected: {exception.Message}");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private async Task ReadConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (ProtocolLineParser.TryParse(line, out var envelope, out var error))
            {
                await _events.Writer.WriteAsync(envelope!, cancellationToken);
            }
            else
            {
                DiagnosticReceived?.Invoke($"Invalid Bridge message: {error}");
            }
        }
    }

    private static string SerializeCommand(ProtocolEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, ProtocolJson.Options) + "\n";
}
