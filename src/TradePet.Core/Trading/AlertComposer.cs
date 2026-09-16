using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class AlertComposer
{
    public CombinedAlert? Compose(
        string alertId,
        IEnumerable<RuleFact> facts,
        DateTimeOffset occurredAtUtc,
        int maximumFacts = 4)
    {
        var selected = facts
            .GroupBy(fact => fact.Kind)
            .Select(group => group.OrderByDescending(fact => fact.Priority).First())
            .OrderBy(fact => fact.DisplayOrder)
            .ThenByDescending(fact => fact.Priority)
            .Take(maximumFacts)
            .ToArray();
        if (selected.Length == 0)
        {
            return null;
        }

        var headline = selected.Any(fact => fact.Kind == RuleFactKind.LossZoneHistory)
            ? "又是这里。"
            : selected[0].Summary;
        return new CombinedAlert(
            alertId,
            selected.Max(fact => fact.Priority),
            headline,
            selected,
            occurredAtUtc);
    }
}
