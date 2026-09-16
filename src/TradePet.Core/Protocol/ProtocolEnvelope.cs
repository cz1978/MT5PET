using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradePet.Core.Protocol;

public sealed record ProtocolEnvelope(
    string ProtocolVersion,
    string SourceInstanceId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string? AccountKey,
    DateOnly? ServerDate,
    string Kind,
    JsonElement Payload)
{
    public const string CurrentVersion = "1.0";

    public string EventId => $"{SourceInstanceId}:{Sequence}";

    public static ProtocolEnvelope Create<T>(
        string sourceInstanceId,
        long sequence,
        string kind,
        T payload,
        string? accountKey = null,
        DateOnly? serverDate = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return new ProtocolEnvelope(
            CurrentVersion,
            sourceInstanceId,
            sequence,
            occurredAtUtc ?? DateTimeOffset.UtcNow,
            accountKey,
            serverDate,
            kind,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));
    }

    public void Validate()
    {
        if (ProtocolVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported protocol version '{ProtocolVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(SourceInstanceId) || string.IsNullOrWhiteSpace(Kind) || Sequence < 0)
        {
            throw new InvalidDataException("Protocol envelope is missing a required field.");
        }
    }
}

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new DateOnlyJsonConverter());
        return options;
    }

    private sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        private const string Format = "yyyy-MM-dd";

        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateOnly.ParseExact(reader.GetString() ?? string.Empty, Format, null);

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(Format));
    }
}
