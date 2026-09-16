using System.Text.Json;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Mt5;

public sealed record BridgeHeartbeat(
    string TerminalPath,
    string ServerTime,
    int ServerUtcOffsetSeconds,
    long HostChartId,
    DateOnly ServerDate);

public sealed record BridgeChartSnapshot(
    string TerminalId,
    long HostChartId,
    IReadOnlyList<ChartObjectSnapshot> Objects);

public sealed record BridgeEconomicCalendarSnapshot(
    string TerminalId,
    int ServerUtcOffsetSeconds,
    IReadOnlyList<EconomicCalendarEvent> Events);

public static class BridgePayloadMapper
{
    public static BridgeEconomicCalendarSnapshot MapEconomicCalendar(ProtocolEnvelope envelope)
    {
        if (envelope.Kind != "calendar_snapshot")
        {
            throw new ArgumentException("Envelope is not an economic calendar snapshot.", nameof(envelope));
        }

        var terminalId = GetTerminalId(envelope)
            ?? throw new InvalidDataException("Economic calendar snapshot has no terminal identity.");
        var events = envelope.Payload.GetProperty("events")
            .EnumerateArray()
            .Select(item => new EconomicCalendarEvent(
                item.GetProperty("valueId").GetInt64(),
                item.GetProperty("eventId").GetInt64(),
                item.GetProperty("scheduledAtUtc").GetDateTimeOffset(),
                item.GetProperty("countryCode").GetString() ?? string.Empty,
                item.GetProperty("countryName").GetString() ?? string.Empty,
                item.GetProperty("currency").GetString() ?? string.Empty,
                item.GetProperty("name").GetString() ?? string.Empty,
                Enum.Parse<EconomicEventType>(item.GetProperty("type").GetString() ?? string.Empty, true),
                Enum.Parse<EconomicEventImportance>(item.GetProperty("importance").GetString() ?? string.Empty, true),
                item.GetProperty("timeMode").GetString() ?? string.Empty,
                item.GetProperty("unit").GetString() ?? string.Empty,
                item.GetProperty("multiplier").GetString() ?? string.Empty,
                item.GetProperty("digits").GetInt32(),
                ReadNullableDecimal(item, "previousValue"),
                ReadNullableDecimal(item, "revisedPreviousValue"),
                ReadNullableDecimal(item, "forecastValue"),
                ReadNullableDecimal(item, "actualValue"),
                item.GetProperty("impact").GetString() ?? string.Empty,
                item.GetProperty("sourceUrl").GetString() ?? string.Empty,
                item.GetProperty("eventCode").GetString() ?? string.Empty))
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenByDescending(item => item.Importance)
            .ToArray();
        return new BridgeEconomicCalendarSnapshot(
            terminalId,
            envelope.Payload.GetProperty("serverUtcOffsetSeconds").GetInt32(),
            events);
    }

    public static ChartObjectSnapshot MapChartObject(ProtocolEnvelope envelope)
    {
        if (envelope.Kind is not ("chart_upsert" or "chart_delete"))
        {
            throw new ArgumentException("Envelope is not a chart object event.", nameof(envelope));
        }

        return MapChartObject(envelope.Payload, envelope.OccurredAtUtc, envelope.Kind == "chart_delete");
    }

    public static BridgeChartSnapshot MapChartSnapshot(ProtocolEnvelope envelope)
    {
        if (envelope.Kind != "chart_snapshot")
        {
            throw new ArgumentException("Envelope is not a chart snapshot event.", nameof(envelope));
        }

        var terminalId = GetTerminalId(envelope)
            ?? throw new InvalidDataException("Chart snapshot has no terminal identity.");
        var objects = envelope.Payload.GetProperty("objects")
            .EnumerateArray()
            .Select(item => MapChartObject(item, envelope.OccurredAtUtc, false))
            .ToArray();
        return new BridgeChartSnapshot(
            terminalId,
            envelope.Payload.GetProperty("hostChartId").GetInt64(),
            objects);
    }

    public static bool MatchesSource(
        ProtocolEnvelope envelope,
        string? expectedTerminalId,
        string? expectedAccountKey)
    {
        if (string.IsNullOrWhiteSpace(expectedTerminalId) ||
            !string.Equals(GetTerminalId(envelope), expectedTerminalId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(expectedAccountKey) ||
               string.Equals(envelope.AccountKey, expectedAccountKey, StringComparison.Ordinal);
    }

    public static string? GetTerminalId(ProtocolEnvelope envelope)
    {
        var payload = envelope.Payload;
        var propertyName = envelope.Kind is "chart_upsert" or "chart_delete" ? "terminalId" : "terminalPath";
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var pathOrId = value.GetString();
        if (string.IsNullOrWhiteSpace(pathOrId))
        {
            return null;
        }

        return Path.IsPathRooted(pathOrId)
            ? Mt5TerminalDiscovery.CreateTerminalId(pathOrId)
            : pathOrId;
    }

    private static ChartObjectSnapshot MapChartObject(
        JsonElement payload,
        DateTimeOffset envelopeOccurredAtUtc,
        bool deletedByEnvelope)
    {
        var terminalPath = payload.GetProperty("terminalId").GetString() ?? string.Empty;
        var terminalId = Path.IsPathRooted(terminalPath)
            ? Mt5TerminalDiscovery.CreateTerminalId(terminalPath)
            : terminalPath;
        var anchors = payload.GetProperty("anchors")
            .EnumerateArray()
            .Select(anchor => new PriceAnchor(
                anchor.TryGetProperty("timeEpoch", out var time) && time.GetInt64() > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(time.GetInt64())
                    : null,
                anchor.TryGetProperty("price", out var price) ? price.GetDecimal() : null))
            .ToArray();
        var kind = Enum.Parse<ChartObjectKind>(payload.GetProperty("kind").GetString() ?? string.Empty, ignoreCase: true);
        return new ChartObjectSnapshot(
            terminalId,
            payload.GetProperty("chartId").GetInt64(),
            payload.GetProperty("objectName").GetString() ?? string.Empty,
            payload.GetProperty("symbol").GetString() ?? string.Empty,
            payload.GetProperty("timeframe").GetString() ?? string.Empty,
            kind,
            anchors,
            payload.TryGetProperty("text", out var text) ? text.GetString() : null,
            payload.TryGetProperty("colorArgb", out var color) ? color.GetInt32() : 0,
            payload.TryGetProperty("capturedAtUtc", out var captured)
                ? captured.GetDateTimeOffset()
                : envelopeOccurredAtUtc,
            deletedByEnvelope ||
            payload.TryGetProperty("isDeleted", out var deleted) && deleted.GetBoolean());
    }

    public static BridgeHeartbeat MapHeartbeat(ProtocolEnvelope envelope)
    {
        if (envelope.Kind != "heartbeat" || envelope.ServerDate is null)
        {
            throw new ArgumentException("Envelope is not a complete heartbeat.", nameof(envelope));
        }

        return new BridgeHeartbeat(
            envelope.Payload.GetProperty("terminalPath").GetString() ?? string.Empty,
            envelope.Payload.GetProperty("serverTime").GetString() ?? string.Empty,
            envelope.Payload.GetProperty("serverUtcOffsetSeconds").GetInt32(),
            envelope.Payload.GetProperty("hostChartId").GetInt64(),
            envelope.ServerDate.Value);
    }

    public static string ComputeContentHash(ChartObjectSnapshot chartObject)
    {
        var normalized = JsonSerializer.Serialize(new
        {
            chartObject.Symbol,
            chartObject.Timeframe,
            chartObject.Kind,
            chartObject.Anchors,
            chartObject.Text,
            chartObject.ColorArgb,
            chartObject.IsDeleted,
        }, ProtocolJson.Options);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static decimal? ReadNullableDecimal(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;
}
