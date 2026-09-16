using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Infrastructure.Persistence;

const int tradeCount = 100_000;
const int dealCount = tradeCount * 3;
const int measuredRuns = 5;
const string accountKey = "BenchmarkBroker|100001";
var outputPath = Path.GetFullPath(args.FirstOrDefault() ??
    Path.Combine(Environment.CurrentDirectory, "artifacts", "review-benchmark.json"));
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var workDirectory = Path.Combine(Path.GetTempPath(), "TradePetReviewBenchmark", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDirectory);
var databasePath = Path.Combine(workDirectory, "benchmark.db");

try
{
    var database = new AppDatabase(databasePath);
    await database.InitializeAsync();
    var population = Stopwatch.StartNew();
    await PopulateAsync(database, databasePath);
    population.Stop();

    var service = new ReviewQueryService(database);
    var filter = new ReviewWorkspaceFilter(
        accountKey, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
        ComparisonMode: ReviewComparisonMode.Side, PageSize: 100);
    var context = new ReviewQueryContext("benchmark", 1, accountKey);

    var cold = Stopwatch.StartNew();
    var first = await service.QueryAsync(context, filter, () => 1);
    cold.Stop();
    EnsureSnapshot(first);
    EnsureSnapshot(await service.QueryAsync(context with { QueryId = "cache-warmup" }, filter, () => 1));

    var warmDurations = new List<double>();
    for (var index = 0; index < measuredRuns; index++)
    {
        var timer = Stopwatch.StartNew();
        var result = await service.QueryAsync(context with { QueryId = $"warm-{index}" }, filter, () => 1);
        timer.Stop();
        EnsureSnapshot(result);
        warmDurations.Add(timer.Elapsed.TotalMilliseconds);
    }

    var cancellation = new CancellationTokenSource();
    cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));
    var cancellationTimer = Stopwatch.StartNew();
    var cancellationObserved = false;
    try
    {
        await service.QueryAsync(context with { QueryId = "cancel" }, filter with { Search = "cancel-probe" }, () => 1, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        cancellationObserved = true;
    }
    cancellationTimer.Stop();

    var baselinePulse = await MeasurePulseDelaysAsync(TimeSpan.FromSeconds(2));
    var parallelQuery = service.QueryAsync(
        context with { QueryId = "parallel" },
        filter with { ComparisonMode = ReviewComparisonMode.PeriodHalves }, () => 1);
    var parallelPulse = await MeasurePulseDelaysAsync(TimeSpan.FromSeconds(2));
    await parallelQuery;
    var baselineP95 = Percentile95(baselinePulse);
    var parallelP95 = Percentile95(parallelPulse);

    var resultDocument = new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        environment = new
        {
            os = Environment.OSVersion.ToString(),
            framework = Environment.Version.ToString(),
            processorCount = Environment.ProcessorCount,
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            databaseBytes = new FileInfo(databasePath).Length,
        },
        data = new { accounts = 1, completeTrades = tradeCount, deals = dealCount, days = 365 },
        populationMilliseconds = population.Elapsed.TotalMilliseconds,
        query = new
        {
            pageSize = 100,
            coldMilliseconds = cold.Elapsed.TotalMilliseconds,
            warmMilliseconds = warmDurations,
            warmP95Milliseconds = Percentile95(warmDurations),
            comparison = ReviewComparisonMode.Side.ToString(),
            expectedTotalCount = tradeCount,
        },
        cancellation = new
        {
            cancellationObserved,
            elapsedMilliseconds = cancellationTimer.Elapsed.TotalMilliseconds,
        },
        fourHertzPulse = new
        {
            baselineP95Milliseconds = baselineP95,
            parallelQueryP95Milliseconds = parallelP95,
            additionalP95Milliseconds = Math.Max(0d, parallelP95 - baselineP95),
            baselineSamples = baselinePulse.Count,
            parallelSamples = parallelPulse.Count,
        },
        targets = new
        {
            warmQueryP95Milliseconds = 1_000,
            comparisonP95Milliseconds = 3_000,
            additionalRealtimeP95Milliseconds = 200,
        },
    };
    await File.WriteAllTextAsync(outputPath,
        JsonSerializer.Serialize(resultDocument, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(outputPath);
    Console.WriteLine(JsonSerializer.Serialize(resultDocument, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(workDirectory))
    {
        Directory.Delete(workDirectory, recursive: true);
    }
}

static void EnsureSnapshot(ReviewQueryResult result)
{
    if (!result.IsCurrentSession || result.Snapshot.TotalCount != tradeCount || result.Snapshot.Trades.Count != 100)
    {
        throw new InvalidDataException("Benchmark query returned an incomplete or stale snapshot.");
    }
}

static async Task PopulateAsync(AppDatabase database, string databasePath)
{
    await using var connection = await database.OpenConnectionAsync();
    await using (var pragma = connection.CreateCommand())
    {
        pragma.CommandText = "PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;";
        await pragma.ExecuteNonQueryAsync();
    }
    await using var transaction = await connection.BeginTransactionAsync();
    await using var trade = connection.CreateCommand();
    trade.Transaction = (SqliteTransaction)transaction;
    trade.CommandText = """
        INSERT INTO trades(account_key, position_id, symbol, side, opened_at_utc, closed_at_utc,
            open_server_date, close_server_date, entry_price, exit_price, opening_volume,
            maximum_volume, remaining_volume, net_pnl, is_complete)
        VALUES ($account, $position, $symbol, $side, $opened, $closed, $openDate, $closeDate,
            $entry, $exit, 1, 1, 0, $pnl, 1);
        """;
    var tradeAccount = trade.Parameters.Add("$account", SqliteType.Text);
    var tradePosition = trade.Parameters.Add("$position", SqliteType.Integer);
    var tradeSymbol = trade.Parameters.Add("$symbol", SqliteType.Text);
    var tradeSide = trade.Parameters.Add("$side", SqliteType.Text);
    var tradeOpened = trade.Parameters.Add("$opened", SqliteType.Text);
    var tradeClosed = trade.Parameters.Add("$closed", SqliteType.Text);
    var tradeOpenDate = trade.Parameters.Add("$openDate", SqliteType.Text);
    var tradeCloseDate = trade.Parameters.Add("$closeDate", SqliteType.Text);
    var tradeEntry = trade.Parameters.Add("$entry", SqliteType.Real);
    var tradeExit = trade.Parameters.Add("$exit", SqliteType.Real);
    var tradePnl = trade.Parameters.Add("$pnl", SqliteType.Real);

    await using var deal = connection.CreateCommand();
    deal.Transaction = (SqliteTransaction)transaction;
    deal.CommandText = """
        INSERT INTO deals(account_key, ticket, order_ticket, position_id, symbol, side, entry_kind,
            volume, price, profit, commission, swap, fee, occurred_at_utc)
        VALUES ($account, $ticket, $ticket, $position, $symbol, $side, $entryKind,
            $volume, $price, $profit, $commission, 0, 0, $occurred);
        """;
    var dealAccount = deal.Parameters.Add("$account", SqliteType.Text);
    var dealTicket = deal.Parameters.Add("$ticket", SqliteType.Integer);
    var dealPosition = deal.Parameters.Add("$position", SqliteType.Integer);
    var dealSymbol = deal.Parameters.Add("$symbol", SqliteType.Text);
    var dealSide = deal.Parameters.Add("$side", SqliteType.Text);
    var dealEntryKind = deal.Parameters.Add("$entryKind", SqliteType.Text);
    var dealVolume = deal.Parameters.Add("$volume", SqliteType.Real);
    var dealPrice = deal.Parameters.Add("$price", SqliteType.Real);
    var dealProfit = deal.Parameters.Add("$profit", SqliteType.Real);
    var dealCommission = deal.Parameters.Add("$commission", SqliteType.Real);
    var dealOccurred = deal.Parameters.Add("$occurred", SqliteType.Text);

    var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    for (var index = 0; index < tradeCount; index++)
    {
        var positionId = index + 1L;
        var opened = start.AddMinutes(index * 5L);
        var closed = opened.AddMinutes(4);
        var symbol = index % 3 == 0 ? "XAUUSD.s" : index % 3 == 1 ? "EURUSD" : "GBPUSD";
        var side = index % 2 == 0 ? "Buy" : "Sell";
        var pnl = index % 5 == 0 ? -20d : 8d;
        tradeAccount.Value = accountKey;
        tradePosition.Value = positionId;
        tradeSymbol.Value = symbol;
        tradeSide.Value = side;
        tradeOpened.Value = opened.ToString("O");
        tradeClosed.Value = closed.ToString("O");
        tradeOpenDate.Value = DateOnly.FromDateTime(opened.UtcDateTime).ToString("yyyy-MM-dd");
        tradeCloseDate.Value = DateOnly.FromDateTime(closed.UtcDateTime).ToString("yyyy-MM-dd");
        tradeEntry.Value = 100d;
        tradeExit.Value = side == "Buy" ? 101d : 99d;
        tradePnl.Value = pnl;
        await trade.ExecuteNonQueryAsync();

        for (var dealIndex = 0; dealIndex < 3; dealIndex++)
        {
            var ticket = index * 3L + dealIndex + 1;
            dealAccount.Value = accountKey;
            dealTicket.Value = ticket;
            dealPosition.Value = positionId;
            dealSymbol.Value = symbol;
            dealSide.Value = side;
            dealEntryKind.Value = dealIndex == 0 ? "In" : "Out";
            dealVolume.Value = dealIndex == 0 ? 1d : 0.5d;
            dealPrice.Value = dealIndex == 0 ? 100d : tradeExit.Value;
            dealProfit.Value = dealIndex == 2 ? pnl + 1d : 0d;
            dealCommission.Value = dealIndex == 2 ? -1d : 0d;
            dealOccurred.Value = (dealIndex == 0 ? opened : closed.AddSeconds(dealIndex - 2)).ToString("O");
            await deal.ExecuteNonQueryAsync();
        }
    }
    await transaction.CommitAsync();
    await using var checkpoint = connection.CreateCommand();
    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
    await checkpoint.ExecuteNonQueryAsync();
}

static async Task<IReadOnlyList<double>> MeasurePulseDelaysAsync(TimeSpan duration)
{
    var delays = new List<double>();
    var timer = Stopwatch.StartNew();
    var target = TimeSpan.Zero;
    while (timer.Elapsed < duration)
    {
        target += TimeSpan.FromMilliseconds(250);
        var remaining = target - timer.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
        delays.Add(Math.Max(0d, (timer.Elapsed - target).TotalMilliseconds));
    }
    return delays;
}

static double Percentile95(IReadOnlyCollection<double> values)
{
    if (values.Count == 0) return 0d;
    var ordered = values.Order().ToArray();
    return ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1)];
}
