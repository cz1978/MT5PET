using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class DailyHighWaterCalculator
{
    public decimal Calculate(
        IReadOnlyCollection<DealRecord> balanceChangingDeals,
        IReadOnlyCollection<EquitySample> equitySamples,
        IReadOnlyCollection<AccountCashFlow> cashFlows,
        decimal currentCombinedPnl)
    {
        var samples = equitySamples.OrderBy(sample => sample.CapturedAtUtc).ToArray();
        var deals = balanceChangingDeals.OrderBy(deal => deal.OccurredAtUtc).ThenBy(deal => deal.Ticket).ToArray();
        var realizedCurve = 0m;
        var realizedHighWater = 0m;
        foreach (var deal in deals)
        {
            realizedCurve += deal.NetPnl;
            realizedHighWater = Math.Max(realizedHighWater, realizedCurve);
        }

        if (samples.Length == 0)
        {
            return Math.Max(realizedHighWater, Math.Max(0m, currentCombinedPnl));
        }

        var flows = cashFlows.OrderBy(flow => flow.OccurredAtUtc).ThenBy(flow => flow.Ticket).ToArray();
        var openingBalanceCandidates = new decimal[samples.Length];
        var realized = 0m;
        var cashFlow = 0m;
        var dealIndex = 0;
        var flowIndex = 0;

        for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            var sample = samples[sampleIndex];
            while (dealIndex < deals.Length && deals[dealIndex].OccurredAtUtc <= sample.CapturedAtUtc)
            {
                realized += deals[dealIndex++].NetPnl;
            }

            while (flowIndex < flows.Length && flows[flowIndex].OccurredAtUtc <= sample.CapturedAtUtc)
            {
                cashFlow += flows[flowIndex++].Amount;
            }

            openingBalanceCandidates[sampleIndex] = sample.Balance - realized - cashFlow;
        }

        Array.Sort(openingBalanceCandidates);
        var middle = openingBalanceCandidates.Length / 2;
        var openingBalance = openingBalanceCandidates.Length % 2 == 0
            ? (openingBalanceCandidates[middle - 1] + openingBalanceCandidates[middle]) / 2m
            : openingBalanceCandidates[middle];

        var highWater = Math.Max(realizedHighWater, Math.Max(0m, currentCombinedPnl));
        cashFlow = 0m;
        flowIndex = 0;
        foreach (var sample in samples)
        {
            while (flowIndex < flows.Length && flows[flowIndex].OccurredAtUtc <= sample.CapturedAtUtc)
            {
                cashFlow += flows[flowIndex++].Amount;
            }

            highWater = Math.Max(highWater, sample.Equity - openingBalance - cashFlow);
        }

        return highWater;
    }
}
