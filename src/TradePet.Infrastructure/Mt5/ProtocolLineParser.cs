using System.Text.Json;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Mt5;

public static class ProtocolLineParser
{
    public static bool TryParse(string line, out ProtocolEnvelope? envelope, out string? error)
    {
        envelope = null;
        error = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "empty_line";
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(line, ProtocolJson.Options);
            if (envelope is null)
            {
                error = "empty_envelope";
                return false;
            }

            envelope.Validate();
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
