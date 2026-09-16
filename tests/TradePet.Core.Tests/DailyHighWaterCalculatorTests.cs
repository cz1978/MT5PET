using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class DailyHighWaterCalculatorTests
{
    [Fact]
    public void Calculator_DoesNotDoubleCountClosedProfitDuringSnapshotSkew()
    {
        var start = new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);
        var close = start.AddMinutes(1);
        var deals = new[]
        {
            Deal(1, DealEntryKind.In, 0m, start),
            Deal(2, DealEntryKind.Out, 100m, close),
        };
        var samples = new[]
        {
            Sample(start.AddSeconds(5), 1_000m, 1_080m, 80m),
            Sample(start.AddSeconds(10), 1_000m, 1_100m, 100m),
            // 成交历史先到，但账户余额与持仓快照仍是平仓前状态。
            Sample(close.AddMilliseconds(100), 1_000m, 1_100m, 100m),
            Sample(close.AddSeconds(1), 1_100m, 1_100m, 0m),
            Sample(close.AddSeconds(5), 1_100m, 1_100m, 0m),
        };

        var high = new DailyHighWaterCalculator().Calculate(deals, samples, [], 100m);

        Assert.Equal(100m, high);
    }

    [Fact]
    public void Calculator_ExcludesDepositsAndWithdrawalsFromHighWater()
    {
        const string account = "Broker|1";
        var start = new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);
        var close = start.AddMinutes(1);
        var depositAt = start.AddMinutes(2);
        var deals = new[]
        {
            Deal(1, DealEntryKind.In, 0m, start),
            Deal(2, DealEntryKind.Out, 50m, close),
        };
        var cashFlows = new[] { new AccountCashFlow(account, 99, "balance", 500m, depositAt) };
        var samples = new[]
        {
            Sample(start.AddSeconds(5), 1_000m, 1_020m, 20m),
            Sample(close.AddSeconds(5), 1_050m, 1_050m, 0m),
            Sample(depositAt.AddSeconds(5), 1_550m, 1_550m, 0m),
        };

        var high = new DailyHighWaterCalculator().Calculate(deals, samples, cashFlows, 50m);

        Assert.Equal(50m, high);
    }

    [Fact]
    public void Calculator_KeepsRealizedHistoryPeakWhenSamplesBeginAfterRestart()
    {
        var start = new DateTimeOffset(2026, 9, 2, 1, 0, 0, TimeSpan.Zero);
        var deals = new[]
        {
            Deal(1, DealEntryKind.In, 0m, start),
            Deal(2, DealEntryKind.Out, 125.38m, start.AddMinutes(1)),
            Deal(3, DealEntryKind.In, 0m, start.AddMinutes(2)),
            Deal(4, DealEntryKind.Out, -69.34m, start.AddMinutes(3)),
        };
        var samples = new[]
        {
            Sample(start.AddHours(10), 1_056.04m, 1_080.90m, 24.86m),
        };

        var high = new DailyHighWaterCalculator().Calculate(deals, samples, [], 80.90m);

        Assert.Equal(125.38m, high);
    }

    private static DealRecord Deal(long ticket, DealEntryKind entryKind, decimal profit, DateTimeOffset occurredAt) =>
        new(ticket, ticket, 7, "XAUUSD.s", TradeSide.Buy, entryKind, 0.01m, 4_400m,
            profit, 0m, 0m, 0m, occurredAt);

    private static EquitySample Sample(
        DateTimeOffset capturedAt,
        decimal balance,
        decimal equity,
        decimal floatingPnl) =>
        new("Broker|1", new DateOnly(2026, 9, 1), capturedAt, balance, equity, floatingPnl, floatingPnl != 0m);
}
