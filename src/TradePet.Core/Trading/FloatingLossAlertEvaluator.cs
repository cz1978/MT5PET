using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class FloatingLossAlertEvaluator
{
    public FloatingLossAlertEvaluation Evaluate(
        FloatingLossAlertPolicy policy,
        decimal balance,
        decimal floatingPnl,
        IReadOnlyCollection<PositionSnapshot> positions,
        IReadOnlyCollection<int> previouslyActiveStages,
        decimal previousEpisodePeakLossPercentage)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(previouslyActiveStages);

        var normalized = policy.Normalize();
        var lossPercentage = positions.Count > 0 && balance > 0m && floatingPnl < -0.01m
            ? Math.Abs(floatingPnl) * 100m / balance
            : 0m;
        var previousPeak = lossPercentage > 0m
            ? Math.Max(0m, previousEpisodePeakLossPercentage)
            : 0m;
        var activeLevels = normalized.Levels
            .Where(level => level.Enabled && lossPercentage >= level.LossPercentage)
            .OrderBy(level => level.LossPercentage)
            .ThenBy(level => level.Stage)
            .ToArray();
        var previous = previouslyActiveStages.ToHashSet();
        var primaryExposure = positions
            .GroupBy(position => (position.Symbol, position.Side))
            .Select(group => new FloatingLossExposure(
                group.Key.Symbol,
                group.Key.Side,
                group.Sum(position => position.Volume),
                group.Sum(position => position.Profit + position.Swap),
                group.Count()))
            .Where(exposure => exposure.FloatingPnl < -0.01m)
            .OrderBy(exposure => exposure.FloatingPnl)
            .ThenBy(exposure => exposure.Symbol, StringComparer.Ordinal)
            .ThenBy(exposure => exposure.Side)
            .FirstOrDefault();
        var triggers = activeLevels
            .Where(level => !previous.Contains(level.Stage) && level.LossPercentage > previousPeak)
            .Select(level => new FloatingLossAlertTrigger(
                level.Stage,
                level.LossPercentage,
                lossPercentage,
                floatingPnl,
                balance,
                primaryExposure))
            .ToArray();

        return new FloatingLossAlertEvaluation(
            lossPercentage,
            lossPercentage > 0m ? Math.Max(previousPeak, lossPercentage) : 0m,
            activeLevels.Select(level => level.Stage).ToArray(),
            triggers);
    }
}
