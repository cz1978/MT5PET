using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;
using TradePet.Infrastructure.Mt5;
using Xunit;

namespace TradePet.Infrastructure.Tests;

public sealed class BridgeTests
{
    [Fact]
    public async Task PipeServer_ReceivesVersionedEnvelope()
    {
        var pipeName = $"TradePetTests.{Guid.NewGuid():N}";
        await using var server = new BridgePipeServer(pipeName);
        var connectionStates = new List<bool>();
        server.ConnectionChanged += connectionStates.Add;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = server.RunAsync(cancellation.Token);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var source = ProtocolEnvelope.Create("bridge-test", 1, "trade_dirty", new { reason = "transaction" });
        await writer.WriteLineAsync(JsonSerializer.Serialize(source, ProtocolJson.Options));

        var received = await server.Events.ReadAsync(cancellation.Token);

        Assert.Equal("trade_dirty", received.Kind);
        Assert.Equal("bridge-test:1", received.EventId);
        Assert.Contains(true, connectionStates);
        cancellation.Cancel();
        await serverTask;
        Assert.Contains(false, connectionStates);
    }

    [Fact]
    public async Task PipeServer_PublishesLatestLossZoneSnapshotOnReadOnlyCommandPipe()
    {
        var pipeName = $"TradePetTests.{Guid.NewGuid():N}";
        await using var server = new BridgePipeServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = server.RunAsync(cancellation.Token);
        server.SetLossZones(
            "C:/MetaTrader/terminal64.exe",
            "Broker|1",
            new DateOnly(2026, 8, 31),
            [new BridgeLossZone("zone-a", "XAUUSD.s", 4438.475m, 4440.475m, 4442.475m, 6, 5, -17.27m)]);

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName + BridgePipeServer.CommandPipeSuffix,
            PipeDirection.In,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, leaveOpen: true);
        var line = await reader.ReadLineAsync(cancellation.Token);

        Assert.NotNull(line);
        Assert.True(ProtocolLineParser.TryParse(line!, out var envelope, out var error), error);
        Assert.Equal("loss_zone_snapshot", envelope!.Kind);
        Assert.Equal("Broker|1", envelope.AccountKey);
        Assert.Equal(new DateOnly(2026, 8, 31), envelope.ServerDate);
        var snapshot = envelope.Payload.Deserialize<LossZoneChartSnapshot>(ProtocolJson.Options);
        var zone = Assert.Single(Assert.IsType<LossZoneChartSnapshot>(snapshot).Zones);
        Assert.Equal(Path.GetFullPath("C:/MetaTrader"), snapshot!.TerminalPath);
        Assert.Equal("XAUUSD.s", zone.Symbol);
        Assert.Equal(4438.475m, zone.LowerBound);
        Assert.Equal(4442.475m, zone.UpperBound);
        Assert.Equal(6, zone.AttemptCount);

        cancellation.Cancel();
        await serverTask;
    }

    [Fact]
    public void PayloadMapper_NormalizesTerminalAndChartObject()
    {
        var payload = new
        {
            terminalId = @"C:\Program Files\WeTrade MetaTrader 5 Terminal",
            chartId = 99L,
            objectName = "zone-a",
            symbol = "XAUUSD.s",
            timeframe = "PERIOD_M5",
            kind = "rectangle",
            anchors = new[]
            {
                new { timeEpoch = 1_777_000_000L, price = 3349m },
                new { timeEpoch = 1_777_000_100L, price = 3355m },
            },
            text = "不交易区",
            colorArgb = 255,
            capturedAtUtc = DateTimeOffset.UtcNow,
        };
        var envelope = ProtocolEnvelope.Create("bridge-a", 1, "chart_upsert", payload);

        var chartObject = BridgePayloadMapper.MapChartObject(envelope);

        Assert.Equal(ChartObjectKind.Rectangle, chartObject.Kind);
        Assert.Equal("XAUUSD.s", chartObject.Symbol);
        Assert.Equal(3349m, chartObject.Anchors[0].Price);
        Assert.False(chartObject.IsDeleted);
        Assert.Equal(64, BridgePayloadMapper.ComputeContentHash(chartObject).Length);
    }

    [Fact]
    public void PayloadMapper_MapsAuthoritativeSnapshotAndRejectsWrongSource()
    {
        var terminalPath = @"C:\Program Files\WeTrade MetaTrader 5 Terminal";
        var payload = new
        {
            terminalPath,
            hostChartId = 99L,
            objects = new[]
            {
                new
                {
                    terminalId = terminalPath,
                    chartId = 99L,
                    objectName = "zone-a",
                    symbol = "XAUUSD.s",
                    timeframe = "PERIOD_M5",
                    kind = "rectangle",
                    anchors = new[] { new { timeEpoch = 0L, price = 3352m } },
                    text = "计划",
                    colorArgb = 255,
                },
            },
        };
        var expectedTerminal = Mt5TerminalDiscovery.CreateTerminalId(Path.Combine(terminalPath, "terminal64.exe"));
        var envelope = ProtocolEnvelope.Create("bridge-a", 1, "chart_snapshot", payload, "Broker|1");

        var snapshot = BridgePayloadMapper.MapChartSnapshot(envelope);

        Assert.Equal(expectedTerminal, snapshot.TerminalId);
        Assert.Single(snapshot.Objects);
        Assert.True(BridgePayloadMapper.MatchesSource(envelope, expectedTerminal, "Broker|1"));
        Assert.False(BridgePayloadMapper.MatchesSource(envelope, expectedTerminal, "Broker|2"));
        Assert.False(BridgePayloadMapper.MatchesSource(envelope, "another-terminal", "Broker|1"));
    }

    [Fact]
    public void PayloadMapper_MapsEconomicCalendarValuesAndNulls()
    {
        var terminalPath = @"C:\Program Files\WeTrade MetaTrader 5 Terminal";
        var scheduled = new DateTimeOffset(2026, 9, 17, 12, 30, 0, TimeSpan.Zero);
        var payload = new
        {
            terminalPath,
            serverUtcOffsetSeconds = 10_800,
            events = new[]
            {
                new
                {
                    valueId = 1001L,
                    eventId = 2001L,
                    scheduledAtUtc = scheduled,
                    countryCode = "US",
                    countryName = "United States",
                    currency = "USD",
                    name = "Initial Jobless Claims",
                    type = "indicator",
                    importance = "high",
                    timeMode = "CALENDAR_TIMEMODE_DATETIME",
                    unit = "CALENDAR_UNIT_JOB",
                    multiplier = "CALENDAR_MULTIPLIER_THOUSANDS",
                    digits = 1,
                    previousValue = (decimal?)20.6m,
                    revisedPreviousValue = (decimal?)null,
                    forecastValue = (decimal?)20.5m,
                    actualValue = (decimal?)null,
                    impact = "CALENDAR_IMPACT_NA",
                    sourceUrl = "https://example.test",
                    eventCode = "US-CLAIMS",
                },
            },
        };
        var envelope = ProtocolEnvelope.Create("bridge-a", 2, "calendar_snapshot", payload, "Broker|1");

        var snapshot = BridgePayloadMapper.MapEconomicCalendar(envelope);
        var item = Assert.Single(snapshot.Events);

        Assert.Equal(10_800, snapshot.ServerUtcOffsetSeconds);
        Assert.Equal(EconomicEventImportance.High, item.Importance);
        Assert.Equal(EconomicEventType.Indicator, item.Type);
        Assert.Equal(scheduled, item.ScheduledAtUtc);
        Assert.Equal(20.6m, item.PreviousValue);
        Assert.Null(item.ActualValue);
    }

    [Fact]
    public void Installer_CopiesOnlyIntoResolvedExpertDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "TradePetBridgeInstall", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "TerminalData");
        var sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var compiled = Path.Combine(sourceDirectory, "TradePetBridge.ex5");
        var source = Path.Combine(sourceDirectory, "TradePetBridge.mq5");
        File.WriteAllBytes(compiled, [1, 2, 3]);
        File.WriteAllText(source, "// read-only bridge");
        try
        {
            var terminal = new Mt5TerminalInstallation("C:/terminal64.exe", "terminal", dataDirectory, true);
            var result = new BridgeInstaller().Install(terminal, compiled, source);

            Assert.StartsWith(Path.GetFullPath(dataDirectory), result.ExpertDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.CompiledPath));
            Assert.True(File.Exists(result.SourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BridgeSource_ValidatesReverseCommandScopeAndPublishesSnapshotIdentity()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mt5", "TradePetBridge.mq5"));

        Assert.Contains("expected_account != current_account", source, StringComparison.Ordinal);
        Assert.Contains("expected_date != current_date", source, StringComparison.Ordinal);
        Assert.Contains("expected_terminal", source, StringComparison.Ordinal);
        Assert.Contains("\\\"terminalPath\\\"", source, StringComparison.Ordinal);
    }
}
