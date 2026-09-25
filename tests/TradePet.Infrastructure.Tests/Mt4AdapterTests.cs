using System.Text.Json;
using TradePet.Core.Protocol;
using TradePet.Infrastructure.Mt4;
using TradePet.Infrastructure.Mt5;
using Xunit;

namespace TradePet.Infrastructure.Tests;

public sealed class Mt4AdapterTests
{
    private static string Frame(string terminal, DateTimeOffset captured, long sequence = 1, long login = 42) =>
        JsonSerializer.Serialize(new
        {
            version = 1, platform = "mt4", sourceInstanceId = "ea-test", sequence,
            terminalPath = Mt5TerminalDiscovery.GetInstallationDirectory(terminal), connected = true,
            capturedAtUtc = captured, account = new { server = "Broker", login, currency = "USD" },
            balance = 1000m, equity = 989m, floatingPnl = -11m, serverUtcOffsetSeconds = 7200,
            positions = new[] { new { ticket = 51L, positionId = 51L, symbol = "EURUSD", side = "sell",
                volume = 0.2m, entryPrice = 1.1m, currentPrice = 1.101m, profit = -10m, swap = -1m,
                stopLoss = 1.12m, takeProfit = 1.05m, openedAtUtc = captured.AddMinutes(-10) } },
            orders = new[] { new { ticket = 52L, symbol = "EURUSD", type = "buy_limit", volume = 0.1m,
                price = 1.09m, stopLoss = 1.08m, takeProfit = 1.11m, createdAtUtc = captured } },
        }, ProtocolJson.Options);

    [Fact]
    public void Frame_MapsPositionsAndOrdersWithoutMixingMt5AccountsOrInventingHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var terminal = Path.Combine(Path.GetTempPath(), "mt4-broker", "terminal.exe");
        var frame = Mt4FileClient.ReadFrame(Frame(terminal, now), terminal, now);
        var batch = Mt5PayloadMapper.MapSnapshot(ProtocolEnvelope.Create("test", 1, "snapshot", frame.Payload, frame.AccountKey));
        Assert.Equal("MT4:Broker|42", batch.Account.Scope.AccountKey);
        Assert.Equal(frame.AccountKey, batch.Account.Scope.AccountKey);
        Assert.Equal(-1, batch.Account.MarginMode);
        Assert.Equal(-11m, batch.Account.FloatingPnl);
        Assert.Equal(-1m, Assert.Single(batch.Positions).Swap);
        Assert.Null(Assert.Single(batch.Positions).InitialRiskAmount);
        Assert.Equal("buy_limit", Assert.Single(batch.Orders).Type);
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(6)]
    public void Frame_RejectsExpiredOrFutureData(int seconds)
    {
        var terminal = Path.Combine(Path.GetTempPath(), "mt4-broker", "terminal.exe");
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<InvalidDataException>(() => Mt4FileClient.ReadFrame(Frame(terminal, now.AddSeconds(seconds)), terminal, now));
    }

    [Fact]
    public void Frame_RejectsWrongTerminalAndMissingAccount()
    {
        var terminal = Path.Combine(Path.GetTempPath(), "mt4-broker", "terminal.exe");
        var other = Path.Combine(Path.GetTempPath(), "other-broker", "terminal.exe");
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<InvalidDataException>(() => Mt4FileClient.ReadFrame(Frame(terminal, now), other, now));
        Assert.Throws<InvalidDataException>(() => Mt4FileClient.ReadFrame(Frame(terminal, now, login: 0), terminal, now));
    }

    [Fact]
    public async Task Client_DisconnectsOnBrokenFileAndRecoversForAnotherAccount()
    {
        var root = Path.Combine(Path.GetTempPath(), "TradePetMt4Tests", Guid.NewGuid().ToString("N"));
        var terminal = Path.Combine(root, "terminal.exe");
        var path = Path.Combine(root, "MQL4", "Files", Mt4FileClient.SnapshotFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = new Mt4FileClient(terminal, root);
        await File.WriteAllTextAsync(path, Frame(terminal, DateTimeOffset.UtcNow), cancellation.Token);
        var running = client.RunAsync(cancellation.Token);
        try
        {
            Assert.True((await client.Events.ReadAsync(cancellation.Token)).Payload.GetProperty("connected").GetBoolean());
            Assert.Equal("snapshot", (await client.Events.ReadAsync(cancellation.Token)).Kind);
            await File.WriteAllTextAsync(path, "{broken", cancellation.Token);
            Assert.False((await client.Events.ReadAsync(cancellation.Token)).Payload.GetProperty("connected").GetBoolean());
            await File.WriteAllTextAsync(path, Frame(terminal, DateTimeOffset.UtcNow, 2, 88), cancellation.Token);
            var connected = await client.Events.ReadAsync(cancellation.Token);
            Assert.Equal("MT4:Broker|88", connected.AccountKey);
            Assert.Equal("snapshot", (await client.Events.ReadAsync(cancellation.Token)).Kind);
            Assert.False(client.Events.TryRead(out _)); // No synthetic empty history batch.
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Discovery_SeparatesMt4AndMt5AndPreservesMissingSavedSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "TradePetMt4Tests", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Arbitrary Broker");
        var registered = Path.Combine(root, "registration");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(registered);
        var terminal = Path.Combine(install, "terminal.exe");
        File.WriteAllText(terminal, string.Empty);
        File.WriteAllText(Path.Combine(registered, "origin.txt"), install);
        try
        {
            Assert.Equal(terminal, Assert.Single(Mt5TerminalDiscovery.DiscoverRegisteredPaths(root, TradingPlatform.Mt4)));
            Assert.Empty(Mt5TerminalDiscovery.DiscoverRegisteredPaths(root, TradingPlatform.Mt5));
            Assert.Null(new Mt5TerminalDiscovery().FindPreferred(Path.Combine(root, "missing", "terminal.exe"), TradingPlatform.Mt4));
            Assert.Equal(terminal, new Mt5TerminalDiscovery().FindPreferred(terminal, TradingPlatform.Mt4)?.TerminalPath);
        }
        finally { Directory.Delete(root, true); }
    }
}
