using Microsoft.Data.Sqlite;
using TradePet.Core.Domain;

namespace TradePet.Infrastructure.Persistence;

public sealed partial class AppDatabase
{
    public async Task UpsertSymbolSpecificationsAsync(
        string accountKey,
        IReadOnlyCollection<SymbolSpecification> specifications,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        if (specifications.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var specification in specifications
                     .Where(item => !string.IsNullOrWhiteSpace(item.Symbol) && item.PriceStep > 0m)
                     .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.Last()))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO symbol_specifications(account_key, symbol, point, tick_size, digits, updated_at_utc)
                VALUES ($account, $symbol, $point, $tick, $digits, $updated)
                ON CONFLICT(account_key, symbol) DO UPDATE SET
                    point = excluded.point,
                    tick_size = excluded.tick_size,
                    digits = excluded.digits,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$account", accountKey);
            command.Parameters.AddWithValue("$symbol", specification.Symbol);
            command.Parameters.AddWithValue("$point", specification.Point);
            command.Parameters.AddWithValue("$tick", specification.TickSize);
            command.Parameters.AddWithValue("$digits", specification.Digits);
            command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, SymbolSpecification>> LoadSymbolSpecificationsAsync(
        string accountKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol, point, tick_size, digits
            FROM symbol_specifications
            WHERE account_key = $account;
            """;
        command.Parameters.AddWithValue("$account", accountKey);
        var result = new Dictionary<string, SymbolSpecification>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var specification = new SymbolSpecification(
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetInt32(3));
            result[specification.Symbol] = specification;
        }
        return result;
    }
}
