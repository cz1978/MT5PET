using System.Text.Json;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class ProtocolEnvelopeTests
{
    [Fact]
    public void Envelope_RoundTrips_WithStableEventIdentity()
    {
        var envelope = ProtocolEnvelope.Create(
            "worker-a",
            42,
            "snapshot",
            new { balance = 1234.56m },
            "Broker|1001",
            new DateOnly(2026, 8, 30),
            new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        var restored = JsonSerializer.Deserialize<ProtocolEnvelope>(json, ProtocolJson.Options);

        Assert.NotNull(restored);
        restored.Validate();
        Assert.Equal("worker-a:42", restored.EventId);
        Assert.Equal(new DateOnly(2026, 8, 30), restored.ServerDate);
        Assert.Equal(1234.56m, restored.Payload.GetProperty("balance").GetDecimal());
    }

    [Fact]
    public void Envelope_RejectsUnsupportedProtocolVersion()
    {
        var envelope = ProtocolEnvelope.Create("worker-a", 1, "hello", new { }) with
        {
            ProtocolVersion = "2.0",
        };

        Assert.Throws<InvalidDataException>(envelope.Validate);
    }

    [Fact]
    public void TradeKey_RoundTripsWithoutLosingCompositeIdentity()
    {
        var key = new TradeKey("Broker|1001", 9_007_199_254_740_993);

        var json = JsonSerializer.Serialize(key, ProtocolJson.Options);
        var restored = JsonSerializer.Deserialize<TradeKey>(json, ProtocolJson.Options);

        Assert.Equal(key, restored);
    }
}
