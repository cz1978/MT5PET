using Microsoft.Data.Sqlite;
using TradePet.Core.Domain;

namespace TradePet.Infrastructure.Persistence;

public sealed partial class AppDatabase
{
    public async Task SaveTradesAsync(
        IReadOnlyCollection<TradeRecord> trades,
        CancellationToken cancellationToken = default)
    {
        if (trades.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var trade in trades)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO trades(
                    account_key, position_id, symbol, side, opened_at_utc, closed_at_utc,
                    open_server_date, close_server_date, entry_price, exit_price, opening_volume,
                    maximum_volume, remaining_volume, net_pnl, is_complete)
                VALUES (
                    $account, $position, $symbol, $side, $opened, $closed,
                    $openDate, $closeDate, $entry, $exit, $openingVolume,
                    $maximumVolume, $remainingVolume, $netPnl, $complete)
                ON CONFLICT(account_key, position_id) DO UPDATE SET
                    symbol = excluded.symbol,
                    side = excluded.side,
                    opened_at_utc = excluded.opened_at_utc,
                    closed_at_utc = excluded.closed_at_utc,
                    open_server_date = excluded.open_server_date,
                    close_server_date = excluded.close_server_date,
                    entry_price = excluded.entry_price,
                    exit_price = excluded.exit_price,
                    opening_volume = excluded.opening_volume,
                    maximum_volume = excluded.maximum_volume,
                    remaining_volume = excluded.remaining_volume,
                    net_pnl = excluded.net_pnl,
                    is_complete = excluded.is_complete;
                """;
            command.Parameters.AddWithValue("$account", trade.AccountKey);
            command.Parameters.AddWithValue("$position", trade.PositionId);
            command.Parameters.AddWithValue("$symbol", trade.Symbol);
            command.Parameters.AddWithValue("$side", trade.Side.ToString());
            command.Parameters.AddWithValue("$opened", Format(trade.OpenedAtUtc));
            command.Parameters.AddWithValue("$closed", DbValue(trade.ClosedAtUtc is null ? null : Format(trade.ClosedAtUtc.Value)));
            command.Parameters.AddWithValue("$openDate", Format(trade.OpenServerDate));
            command.Parameters.AddWithValue("$closeDate", DbValue(trade.CloseServerDate is null ? null : Format(trade.CloseServerDate.Value)));
            command.Parameters.AddWithValue("$entry", trade.EntryPrice);
            command.Parameters.AddWithValue("$exit", DbValue(trade.ExitPrice));
            command.Parameters.AddWithValue("$openingVolume", trade.OpeningVolume);
            command.Parameters.AddWithValue("$maximumVolume", trade.MaximumVolume);
            command.Parameters.AddWithValue("$remainingVolume", trade.RemainingVolume);
            command.Parameters.AddWithValue("$netPnl", trade.NetPnl);
            command.Parameters.AddWithValue("$complete", trade.IsComplete ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveDealBatchAsync(
        string accountKey,
        IReadOnlyCollection<DealRecord> deals,
        IReadOnlyCollection<AccountCashFlow> cashFlows,
        HistorySyncState? historyState = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        if (cashFlows.Any(item => item.AccountKey != accountKey) ||
            historyState is not null && historyState.AccountKey != accountKey)
        {
            throw new ArgumentException("Every batch item must belong to the requested account.", nameof(accountKey));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var deal in deals)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO deals(
                    account_key, ticket, order_ticket, position_id, symbol, side, entry_kind,
                    volume, price, profit, commission, swap, fee, occurred_at_utc)
                VALUES (
                    $account, $ticket, $order, $position, $symbol, $side, $entry,
                    $volume, $price, $profit, $commission, $swap, $fee, $occurred)
                ON CONFLICT(account_key, ticket) DO UPDATE SET
                    order_ticket = excluded.order_ticket,
                    position_id = excluded.position_id,
                    symbol = excluded.symbol,
                    side = excluded.side,
                    entry_kind = excluded.entry_kind,
                    volume = excluded.volume,
                    price = excluded.price,
                    profit = excluded.profit,
                    commission = excluded.commission,
                    swap = excluded.swap,
                    fee = excluded.fee,
                    occurred_at_utc = excluded.occurred_at_utc;
                """;
            command.Parameters.AddWithValue("$account", accountKey);
            command.Parameters.AddWithValue("$ticket", deal.Ticket);
            command.Parameters.AddWithValue("$order", deal.OrderTicket);
            command.Parameters.AddWithValue("$position", deal.PositionId);
            command.Parameters.AddWithValue("$symbol", deal.Symbol);
            command.Parameters.AddWithValue("$side", deal.Side.ToString());
            command.Parameters.AddWithValue("$entry", deal.EntryKind.ToString());
            command.Parameters.AddWithValue("$volume", deal.Volume);
            command.Parameters.AddWithValue("$price", deal.Price);
            command.Parameters.AddWithValue("$profit", deal.Profit);
            command.Parameters.AddWithValue("$commission", deal.Commission);
            command.Parameters.AddWithValue("$swap", deal.Swap);
            command.Parameters.AddWithValue("$fee", deal.Fee);
            command.Parameters.AddWithValue("$occurred", Format(deal.OccurredAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var cashFlow in cashFlows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO account_cash_flows(account_key, ticket, type, amount, occurred_at_utc)
                VALUES ($account, $ticket, $type, $amount, $occurred)
                ON CONFLICT(account_key, ticket) DO UPDATE SET
                    type = excluded.type,
                    amount = excluded.amount,
                    occurred_at_utc = excluded.occurred_at_utc;
                """;
            command.Parameters.AddWithValue("$account", accountKey);
            command.Parameters.AddWithValue("$ticket", cashFlow.Ticket);
            command.Parameters.AddWithValue("$type", cashFlow.Type);
            command.Parameters.AddWithValue("$amount", cashFlow.Amount);
            command.Parameters.AddWithValue("$occurred", Format(cashFlow.OccurredAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (historyState is not null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO history_sync_state(account_key, range_year, is_complete, deal_count, updated_at_utc)
                VALUES ($account, $year, $complete, $count, $updated)
                ON CONFLICT(account_key, range_year) DO UPDATE SET
                    is_complete = excluded.is_complete,
                    deal_count = excluded.deal_count,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$account", historyState.AccountKey);
            command.Parameters.AddWithValue("$year", historyState.RangeYear);
            command.Parameters.AddWithValue("$complete", historyState.IsComplete ? 1 : 0);
            command.Parameters.AddWithValue("$count", historyState.DealCount);
            command.Parameters.AddWithValue("$updated", Format(historyState.UpdatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
