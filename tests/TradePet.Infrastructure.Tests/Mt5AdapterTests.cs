using System.Text.Json;
using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;
using TradePet.Infrastructure.Mt5;
using Xunit;

namespace TradePet.Infrastructure.Tests;

public sealed class Mt5AdapterTests
{
    [Fact]
    public void ProtocolParser_AcceptsCurrentEnvelopeAndRejectsFutureVersion()
    {
        var envelope = ProtocolEnvelope.Create("python-test", 1, "hello", new { workerVersion = "0.1" });
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);

        Assert.True(ProtocolLineParser.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("hello", parsed?.Kind);

        var future = JsonSerializer.Serialize(envelope with { ProtocolVersion = "2.0" }, ProtocolJson.Options);
        Assert.False(ProtocolLineParser.TryParse(future, out _, out var futureError));
        Assert.Contains("Unsupported protocol", futureError);
    }

    [Fact]
    public void SnapshotMapper_ReadsAccountCurrencyInitialRiskEstimate()
    {
        var captured = new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        var envelope = ProtocolEnvelope.Create("python-test", 1, "snapshot", new
        {
            capturedAtUtc = captured,
            serverUtcOffsetSeconds = 0,
            account = new { server = "Broker", login = 1L, currency = "USD", marginMode = 2 },
            balance = 1000m,
            equity = 990m,
            floatingPnl = -10m,
            positions = new[]
            {
                new
                {
                    ticket = 1L, positionId = 1L, symbol = "EURUSD", side = "buy",
                    volume = 0.1m, entryPrice = 1.1m, currentPrice = 1.09m, profit = -10m,
                    stopLoss = 1.08m, takeProfit = 1.14m, openedAtUtc = captured,
                    swap = 0m, initialRiskAmount = (decimal?)25m,
                },
            },
            orders = Array.Empty<object>(),
            symbolSpecifications = Array.Empty<object>(),
        }, "Broker|1");

        var batch = Mt5PayloadMapper.MapSnapshot(envelope);

        Assert.Equal(25m, Assert.Single(batch.Positions).InitialRiskAmount);
    }

    [Fact]
    public void TerminalDiscovery_MapsOriginDirectoryAndBuildsStableId()
    {
        var root = Path.Combine(Path.GetTempPath(), "TradePetDiscovery", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "WeTrade");
        var terminalPath = Path.Combine(install, "terminal64.exe");
        var metaQuotes = Path.Combine(root, "MetaQuotes", "Terminal");
        var data = Path.Combine(metaQuotes, "ABC123");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        File.WriteAllText(terminalPath, string.Empty);
        File.WriteAllText(Path.Combine(data, "origin.txt"), install);
        try
        {
            Assert.Equal(data, Mt5TerminalDiscovery.ResolveDataDirectory(terminalPath, metaQuotes));
            Assert.Equal(
                Mt5TerminalDiscovery.CreateTerminalId(terminalPath),
                Mt5TerminalDiscovery.CreateTerminalId(terminalPath.ToUpperInvariant()));
            Assert.Equal(
                Mt5TerminalDiscovery.CreateTerminalId(terminalPath),
                Mt5TerminalDiscovery.CreateTerminalId(install));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DealMapper_ReadsOptionalHistoryProgressAndCashFlows()
    {
        var envelope = ProtocolEnvelope.Create(
            "python-test",
            2,
            "deals",
            new
            {
                deals = Array.Empty<object>(),
                symbolSpecifications = new[]
                {
                    new { symbol = "EURUSD", point = 0.00001m, tickSize = 0.00001m, digits = 5 },
                },
                cashFlows = new[]
                {
                    new { ticket = 77L, type = "balance", amount = 500m, occurredAtUtc = DateTimeOffset.UtcNow },
                },
                historySync = new
                {
                    rangeYear = 2026,
                    isComplete = true,
                    sourceCount = 1,
                    rangeFromUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    rangeToUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            },
            "Test|123",
            new DateOnly(2026, 8, 31));

        var batch = Mt5PayloadMapper.MapDealBatch(envelope);

        Assert.Empty(batch.Deals);
        Assert.Equal(500m, batch.CashFlows[0].Amount);
        Assert.Equal(2026, batch.HistoryProgress?.RangeYear);
        Assert.Equal(new DateOnly(2026, 8, 31), batch.ServerDate);
        Assert.Equal(0.00001m, Assert.Single(batch.SymbolSpecifications).PriceStep);
    }

    [Fact]
    public void RefreshEnvelope_CarriesServerUtcOffsetWithoutAddingTradeCommands()
    {
        var envelope = ProtocolEnvelope.Create(
            "app-test",
            3,
            "refresh",
            new
            {
                historyYears = new[] { 2026 },
                serverUtcOffsetSeconds = 10_800,
                serverDate = new DateOnly(2026, 8, 31),
            });

        Assert.Equal("refresh", envelope.Kind);
        Assert.Equal(10_800, envelope.Payload.GetProperty("serverUtcOffsetSeconds").GetInt32());
        Assert.Equal(2026, envelope.Payload.GetProperty("historyYears")[0].GetInt32());
        Assert.Equal("2026-08-31", envelope.Payload.GetProperty("serverDate").GetString());
    }

    [Fact]
    public void HistoryMapper_MapsOrderedChunksAndKeepsSameMillisecondTicks()
    {
        var from = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var request = new MarketHistoryRequest("r1", "terminal-a", "Broker|1", "XAUUSD.s", "M5",
            from, from.AddMinutes(5), MarketDataPrecision.Ticks);
        var chunk = ProtocolEnvelope.Create("history", 0, "history_chunk", new
        {
            requestId = "r1", terminalId = "terminal-a", accountKey = "Broker|1", symbol = "XAUUSD.s", timeframe = "M5",
            chunkIndex = 0, isLast = true, bars = Array.Empty<object>(),
            ticks = new[]
            {
                new { occurredAtUtc = from, timeMilliseconds = 1_000L, bid = 1m, ask = 2m, last = 1.5m, volume = 1m, flags = 1L, fingerprint = "a" },
                new { occurredAtUtc = from, timeMilliseconds = 1_000L, bid = 1m, ask = 2m, last = 1.5m, volume = 1m, flags = 1L, fingerprint = "b" },
            },
        }, "Broker|1");
        var complete = ProtocolEnvelope.Create("history", 1, "history_complete", new
        {
            requestId = "r1", terminalId = "terminal-a", accountKey = "Broker|1", symbol = "XAUUSD.s", timeframe = "M5",
            precision = "ticks", sourceVersion = "worker-v1", requestedFromUtc = from, requestedToUtc = from.AddMinutes(5),
            actualFromUtc = from, actualToUtc = from, coverage = "partial", chunkCount = 1, error = "",
        }, "Broker|1");

        var result = Mt5HistoryProtocolMapper.Map(request, [chunk, complete]);

        Assert.Equal(MarketCoverageStatus.Partial, result.Range.Coverage);
        Assert.Equal(2, result.Ticks.Count);
    }

    [Fact]
    public void HistoryMapper_RejectsLateResponseFromAnotherAccount()
    {
        var from = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var request = new MarketHistoryRequest("r1", "terminal-a", "Broker|1", "XAUUSD.s", "M5",
            from, from.AddMinutes(5), MarketDataPrecision.Bars);
        var complete = ProtocolEnvelope.Create("history", 1, "history_complete", new
        {
            requestId = "r1", terminalId = "terminal-a", accountKey = "Broker|2", symbol = "XAUUSD.s", timeframe = "M5",
            precision = "bars", sourceVersion = "worker-v1", requestedFromUtc = from, requestedToUtc = from.AddMinutes(5),
            actualFromUtc = (DateTimeOffset?)null, actualToUtc = (DateTimeOffset?)null, coverage = "empty", chunkCount = 0, error = "",
        }, "Broker|2");

        Assert.Throws<InvalidDataException>(() => Mt5HistoryProtocolMapper.Map(request, [complete]));
    }
}
