using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using TradePet.Core.Protocol;
using TradePet.Infrastructure.Mt5;

namespace TradePet.Infrastructure.Mt4;

public sealed record Mt4Frame(string InstanceId, long Sequence, DateTimeOffset CapturedAtUtc,
    bool Connected, string AccountKey, JsonElement Payload);

public sealed class Mt4FileClient(string terminalPath, string dataDirectory) : ITradingWorkerClient
{
    public const string SnapshotFileName = "TradePet\\snapshot.json";
    private readonly Channel<ProtocolEnvelope> _events = Channel.CreateBounded<ProtocolEnvelope>(32);
    private readonly string _source = $"mt4-{Guid.NewGuid():N}";
    private long _sequence;
    public ChannelReader<ProtocolEnvelope> Events => _events.Reader;

    public static Mt4Frame ReadFrame(string json, string expectedTerminalPath, DateTimeOffset now)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty MT4 snapshot.");
        if (root["platform"]?.GetValue<string>() != "mt4" || root["version"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Unsupported MT4 snapshot format.");
        var installation = root["terminalPath"]?.GetValue<string>() ?? string.Empty;
        if (!string.Equals(Mt5TerminalDiscovery.GetInstallationDirectory(installation),
                Mt5TerminalDiscovery.GetInstallationDirectory(expectedTerminalPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MT4 snapshot belongs to another terminal.");
        var captured = root["capturedAtUtc"]!.GetValue<DateTimeOffset>();
        if (now - captured > TimeSpan.FromSeconds(10) || captured - now > TimeSpan.FromSeconds(5))
            throw new InvalidDataException("MT4 snapshot has expired.");
        var instance = root["sourceInstanceId"]!.GetValue<string>();
        var sequence = root["sequence"]!.GetValue<long>();
        if (string.IsNullOrWhiteSpace(instance) || sequence < 1) throw new InvalidDataException("Invalid MT4 sequence.");
        var connected = root["connected"]!.GetValue<bool>();
        var account = root["account"]!.AsObject();
        var server = account["server"]!.GetValue<string>();
        var login = account["login"]!.GetValue<long>();
        if (connected && (string.IsNullOrWhiteSpace(server) || login <= 0)) throw new InvalidDataException("Invalid MT4 account.");
        // Keep MT4 records isolated even when the same server name and login also exist in MT5.
        account["server"] = "MT4:" + server.Trim();
        account["marginMode"] = -1; // Monitoring only: never synthesize MT5 deals from MT4 order tickets.
        var payload = JsonSerializer.SerializeToElement(root, ProtocolJson.Options);
        var accountKey = $"MT4:{server.Trim()}|{login.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (connected)
        {
            var batch = Mt5PayloadMapper.MapSnapshot(ProtocolEnvelope.Create(instance, sequence, "snapshot", payload, accountKey));
            if (batch.ServerUtcOffsetSeconds is < -50400 or > 50400 ||
                batch.Positions.Select(item => item.Ticket).Distinct().Count() != batch.Positions.Count ||
                batch.Orders.Select(item => item.Ticket).Distinct().Count() != batch.Orders.Count)
                throw new InvalidDataException("Inconsistent MT4 snapshot.");
        }
        return new Mt4Frame(instance, sequence, captured, connected, accountKey, payload);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string? instance = null;
        long lastSequence = -1;
        string? connectedAccount = null;
        var path = Path.Combine(dataDirectory, "MQL4", "Files", SnapshotFileName);
        while (!cancellationToken.IsCancellationRequested)
        {
            Mt4Frame? frame = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("MT4 snapshot exceeds size limit.");
                using var reader = new StreamReader(stream);
                frame = ReadFrame(await reader.ReadToEndAsync(cancellationToken), terminalPath, DateTimeOffset.UtcNow);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or ArgumentException or FormatException or NullReferenceException)
            {
                // Incomplete writes, removed EAs and stale files must never masquerade as live data.
            }

            if (frame is not { Connected: true })
            {
                if (connectedAccount is not null)
                    await EmitAsync("connection", new { connected = false }, connectedAccount, cancellationToken);
                connectedAccount = null;
            }
            else if (frame.InstanceId != instance || frame.Sequence > lastSequence)
            {
                if (connectedAccount != frame.AccountKey || instance != frame.InstanceId)
                    await EmitAsync("connection", new { connected = true }, frame.AccountKey, cancellationToken);
                connectedAccount = frame.AccountKey;
                instance = frame.InstanceId;
                lastSequence = frame.Sequence;
                await EmitAsync("snapshot", frame.Payload, frame.AccountKey, cancellationToken, frame.CapturedAtUtc);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private ValueTask EmitAsync<T>(string kind, T payload, string account, CancellationToken token, DateTimeOffset? captured = null) =>
        _events.Writer.WriteAsync(ProtocolEnvelope.Create(_source, ++_sequence, kind, payload, account,
            occurredAtUtc: captured), token);

    public Task RefreshAsync(int serverUtcOffsetSeconds, DateOnly serverDate, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RefreshHistoryAsync(IReadOnlyCollection<int> historyYears, int serverUtcOffsetSeconds,
        DateOnly serverDate, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
