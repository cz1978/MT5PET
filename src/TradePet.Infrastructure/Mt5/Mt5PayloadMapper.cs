using TradePet.Core.Domain;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Mt5;

public sealed record Mt5SnapshotBatch(
    AccountSnapshot Account,
    IReadOnlyList<PositionSnapshot> Positions,
    IReadOnlyList<OrderSnapshot> Orders,
    int? ServerUtcOffsetSeconds,
    IReadOnlyList<SymbolSpecification> SymbolSpecifications);

public sealed record Mt5HistoryProgress(
    int RangeYear,
    bool IsComplete,
    int SourceCount,
    DateTimeOffset RangeFromUtc,
    DateTimeOffset RangeToUtc);

public sealed record Mt5DealBatch(
    IReadOnlyList<DealRecord> Deals,
    IReadOnlyList<AccountCashFlow> CashFlows,
    Mt5HistoryProgress? HistoryProgress,
    DateOnly? ServerDate,
    IReadOnlyList<SymbolSpecification> SymbolSpecifications);

public static class Mt5PayloadMapper
{
    public static Mt5SnapshotBatch MapSnapshot(ProtocolEnvelope envelope)
    {
        if (envelope.Kind != "snapshot")
        {
            throw new ArgumentException("Envelope is not a snapshot.", nameof(envelope));
        }

        var payload = envelope.Payload;
        var capturedAt = payload.GetProperty("capturedAtUtc").GetDateTimeOffset();
        var accountPayload = payload.GetProperty("account");
        var scope = new AccountScope(
            accountPayload.GetProperty("server").GetString() ?? string.Empty,
            accountPayload.GetProperty("login").GetInt64());
        var account = new AccountSnapshot(
            scope,
            accountPayload.GetProperty("currency").GetString() ?? string.Empty,
            payload.GetProperty("balance").GetDecimal(),
            payload.GetProperty("equity").GetDecimal(),
            payload.GetProperty("floatingPnl").GetDecimal(),
            accountPayload.GetProperty("marginMode").GetInt32(),
            capturedAt);
        var positions = payload.GetProperty("positions").EnumerateArray().Select(position => new PositionSnapshot(
            position.GetProperty("ticket").GetInt64(),
            position.GetProperty("positionId").GetInt64(),
            position.GetProperty("symbol").GetString() ?? string.Empty,
            ParseSide(position.GetProperty("side").GetString()),
            position.GetProperty("volume").GetDecimal(),
            position.GetProperty("entryPrice").GetDecimal(),
            position.GetProperty("currentPrice").GetDecimal(),
            position.GetProperty("profit").GetDecimal(),
            position.GetProperty("stopLoss").GetDecimal(),
            position.GetProperty("takeProfit").GetDecimal(),
            position.GetProperty("openedAtUtc").GetDateTimeOffset(),
            capturedAt,
            position.TryGetProperty("swap", out var swap) ? swap.GetDecimal() : 0m,
            position.TryGetProperty("initialRiskAmount", out var risk) && risk.ValueKind != System.Text.Json.JsonValueKind.Null
                ? risk.GetDecimal()
                : null)).ToArray();
        var orders = payload.GetProperty("orders").EnumerateArray().Select(order => new OrderSnapshot(
            order.GetProperty("ticket").GetInt64(),
            order.GetProperty("symbol").GetString() ?? string.Empty,
            order.GetProperty("type").GetString() ?? string.Empty,
            order.GetProperty("volume").GetDecimal(),
            order.GetProperty("price").GetDecimal(),
            order.GetProperty("stopLoss").GetDecimal(),
            order.GetProperty("takeProfit").GetDecimal(),
            order.GetProperty("createdAtUtc").GetDateTimeOffset())).ToArray();
        int? serverUtcOffsetSeconds = payload.TryGetProperty("serverUtcOffsetSeconds", out var offset)
            ? offset.GetInt32()
            : null;
        return new Mt5SnapshotBatch(account, positions, orders, serverUtcOffsetSeconds, MapSymbolSpecifications(payload));
    }

    public static IReadOnlyList<DealRecord> MapDeals(ProtocolEnvelope envelope)
        => MapDealBatch(envelope).Deals;

    public static Mt5DealBatch MapDealBatch(ProtocolEnvelope envelope)
    {
        if (envelope.Kind != "deals")
        {
            throw new ArgumentException("Envelope is not a deals event.", nameof(envelope));
        }

        var deals = envelope.Payload.GetProperty("deals").EnumerateArray().Select(deal => new DealRecord(
            deal.GetProperty("ticket").GetInt64(),
            deal.GetProperty("orderTicket").GetInt64(),
            deal.GetProperty("positionId").GetInt64(),
            deal.GetProperty("symbol").GetString() ?? string.Empty,
            ParseSide(deal.GetProperty("side").GetString()),
            Enum.Parse<DealEntryKind>(deal.GetProperty("entryKind").GetString() ?? string.Empty, ignoreCase: true),
            deal.GetProperty("volume").GetDecimal(),
            deal.GetProperty("price").GetDecimal(),
            deal.GetProperty("profit").GetDecimal(),
            deal.GetProperty("commission").GetDecimal(),
            deal.GetProperty("swap").GetDecimal(),
            deal.GetProperty("fee").GetDecimal(),
            deal.GetProperty("occurredAtUtc").GetDateTimeOffset())).ToArray();
        var accountKey = envelope.AccountKey ?? string.Empty;
        var cashFlows = envelope.Payload.TryGetProperty("cashFlows", out var cashFlowPayload)
            ? cashFlowPayload.EnumerateArray().Select(item => new AccountCashFlow(
                accountKey,
                item.GetProperty("ticket").GetInt64(),
                item.GetProperty("type").GetString() ?? string.Empty,
                item.GetProperty("amount").GetDecimal(),
                item.GetProperty("occurredAtUtc").GetDateTimeOffset())).ToArray()
            : [];
        Mt5HistoryProgress? progress = null;
        if (envelope.Payload.TryGetProperty("historySync", out var sync))
        {
            progress = new Mt5HistoryProgress(
                sync.GetProperty("rangeYear").GetInt32(),
                sync.GetProperty("isComplete").GetBoolean(),
                sync.GetProperty("sourceCount").GetInt32(),
                sync.GetProperty("rangeFromUtc").GetDateTimeOffset(),
                sync.GetProperty("rangeToUtc").GetDateTimeOffset());
        }

        return new Mt5DealBatch(deals, cashFlows, progress, envelope.ServerDate, MapSymbolSpecifications(envelope.Payload));
    }

    private static IReadOnlyList<SymbolSpecification> MapSymbolSpecifications(System.Text.Json.JsonElement payload)
    {
        if (!payload.TryGetProperty("symbolSpecifications", out var specifications))
        {
            return [];
        }

        return specifications.EnumerateArray()
            .Select(item => new SymbolSpecification(
                item.GetProperty("symbol").GetString() ?? string.Empty,
                item.GetProperty("point").GetDecimal(),
                item.GetProperty("tickSize").GetDecimal(),
                item.GetProperty("digits").GetInt32()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Symbol) && item.PriceStep > 0m)
            .ToArray();
    }

    private static TradeSide ParseSide(string? value) =>
        string.Equals(value, "buy", StringComparison.OrdinalIgnoreCase) ? TradeSide.Buy : TradeSide.Sell;
}
