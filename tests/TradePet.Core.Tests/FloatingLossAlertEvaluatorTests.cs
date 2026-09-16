using TradePet.Core.Domain;
using TradePet.Core.Trading;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class FloatingLossAlertEvaluatorTests
{
    private readonly FloatingLossAlertEvaluator _evaluator = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_TriggersStageOnlyOnceWhileLossStaysAboveThreshold()
    {
        var positions = new[] { Position(1, "XAUUSD.s", TradeSide.Buy, 0.01m, -120m, 0m, Now) };
        var first = _evaluator.Evaluate(FloatingLossAlertPolicy.BalancedDefault, 10_000m, -120m, positions, [], 0m);
        var second = _evaluator.Evaluate(
            FloatingLossAlertPolicy.BalancedDefault, 10_000m, -150m, positions, first.ActiveStages,
            first.EpisodePeakLossPercentage);

        var trigger = Assert.Single(first.NewTriggers);
        Assert.Equal(1, trigger.Stage);
        Assert.Equal(1.2m, trigger.ActualLossPercentage);
        Assert.Empty(second.NewTriggers);
    }

    [Fact]
    public void Evaluate_ReturnsAllNewStagesWhenOneSnapshotCrossesSeveralThresholds()
    {
        var positions = new[] { Position(1, "XAUUSD.s", TradeSide.Buy, 0.01m, -350m, 0m, Now) };
        var result = _evaluator.Evaluate(
            FloatingLossAlertPolicy.BalancedDefault, 10_000m, -350m, positions, [], 0m);

        Assert.Equal([1, 2, 3], result.ActiveStages);
        Assert.Equal([1, 2, 3], result.NewTriggers.Select(item => item.Stage));
    }

    [Fact]
    public void Evaluate_RearmsStageOnlyAfterFloatingLossEpisodeEnds()
    {
        var positions = new[] { Position(1, "XAUUSD.s", TradeSide.Buy, 0.01m, -120m, 0m, Now) };
        var first = _evaluator.Evaluate(
            FloatingLossAlertPolicy.BalancedDefault, 10_000m, -120m, positions, [], 0m);
        var recovered = _evaluator.Evaluate(
            FloatingLossAlertPolicy.BalancedDefault, 10_000m, 0m, [], first.ActiveStages,
            first.EpisodePeakLossPercentage);
        var crossedAgain = _evaluator.Evaluate(
            FloatingLossAlertPolicy.BalancedDefault, 10_000m, -110m, positions, recovered.ActiveStages,
            recovered.EpisodePeakLossPercentage);

        Assert.Empty(recovered.ActiveStages);
        Assert.Single(crossedAgain.NewTriggers, item => item.Stage == 1);
    }

    [Fact]
    public void Evaluate_IgnoresDisabledStagesAndInvalidBalance()
    {
        var policy = new FloatingLossAlertPolicy(
        [
            new(1, false, 0.5m),
            new(2, true, 1m),
            new(3, true, 2m),
        ]);

        var positions = new[] { Position(1, "XAUUSD.s", TradeSide.Buy, 0.01m, -150m, 0m, Now) };
        var enabledOnly = _evaluator.Evaluate(policy, 10_000m, -150m, positions, [], 0m);
        var invalidBalance = _evaluator.Evaluate(policy, 0m, -1_000m, positions, [], 0m);

        Assert.Single(enabledOnly.NewTriggers, item => item.Stage == 2);
        Assert.Empty(invalidBalance.NewTriggers);
        Assert.Equal(0m, invalidBalance.LossPercentage);
    }

    [Fact]
    public void Evaluate_AttributesAlertToWorstSymbolAndDirectionGroup()
    {
        var positions = new[]
        {
            Position(1, "XAUUSD.s", TradeSide.Buy, 0.02m, -40m, -2m, Now),
            Position(2, "XAUUSD.s", TradeSide.Buy, 0.03m, -20m, 0m, Now),
            Position(3, "XAUUSD.s", TradeSide.Sell, 0.01m, 10m, 0m, Now),
            Position(4, "EURUSD", TradeSide.Sell, 0.10m, -50m, -1m, Now),
        };

        var result = _evaluator.Evaluate(
            FloatingLossAlertPolicy.BalancedDefault, 1_000m, -103m, positions, [], 0m);

        var exposure = result.NewTriggers[^1].PrimaryExposure;
        Assert.NotNull(exposure);
        Assert.Equal("XAUUSD.s", exposure!.Symbol);
        Assert.Equal(TradeSide.Buy, exposure.Side);
        Assert.Equal(0.05m, exposure.Volume);
        Assert.Equal(-62m, exposure.FloatingPnl);
        Assert.Equal(2, exposure.PositionCount);
    }

    [Fact]
    public void Evaluate_DoesNotTriggerFromOneLosingDirectionWhenAccountNetIsProfitable()
    {
        var positions = new[]
        {
            Position(1, "XAUUSD.s", TradeSide.Buy, 0.02m, -200m, 0m, Now),
            Position(2, "XAUUSD.s", TradeSide.Sell, 0.02m, 250m, 0m, Now),
        };

        var result = _evaluator.Evaluate(
            FloatingLossAlertPolicy.BalancedDefault, 1_000m, 50m, positions, [], 0m);

        Assert.Empty(result.NewTriggers);
        Assert.Empty(result.ActiveStages);
    }

    [Fact]
    public void Evaluate_DoesNotAlertWhenLossRecoversFromTenPercentToSevenPercent()
    {
        var policy = new FloatingLossAlertPolicy(
        [
            new(1, true, 5m),
            new(2, true, 10m),
            new(3, true, 15m),
        ]);
        var positions = new[] { Position(1, "XAUUSD.s", TradeSide.Buy, 0.02m, -70m, 0m, Now) };
        var atTenPercent = _evaluator.Evaluate(policy, 1_000m, -105m, positions, [], 0m);

        var recoveredToSeven = _evaluator.Evaluate(
            policy, 1_000m, -70m, positions, [], atTenPercent.EpisodePeakLossPercentage);
        var worsenedToNewStage = _evaluator.Evaluate(
            policy, 1_000m, -151m, positions, recoveredToSeven.ActiveStages,
            recoveredToSeven.EpisodePeakLossPercentage);

        Assert.Empty(recoveredToSeven.NewTriggers);
        Assert.Equal(10.5m, recoveredToSeven.EpisodePeakLossPercentage);
        Assert.Single(worsenedToNewStage.NewTriggers, trigger => trigger.Stage == 3);
    }

    [Fact]
    public void Evaluate_DoesNotRepeatWhenLossOscillatesAroundThreshold()
    {
        var policy = new FloatingLossAlertPolicy(
        [
            new(1, true, 5m),
            new(2, false, 10m),
            new(3, false, 15m),
        ]);
        var positions = new[] { Position(1, "XAUUSD.s", TradeSide.Buy, 0.02m, -50m, 0m, Now) };
        var firstCross = _evaluator.Evaluate(policy, 1_000m, -50.1m, positions, [], 0m);
        var dippedBelow = _evaluator.Evaluate(
            policy, 1_000m, -49.9m, positions, firstCross.ActiveStages,
            firstCross.EpisodePeakLossPercentage);
        var crossedAgain = _evaluator.Evaluate(
            policy, 1_000m, -50.2m, positions, dippedBelow.ActiveStages,
            dippedBelow.EpisodePeakLossPercentage);

        Assert.Single(firstCross.NewTriggers);
        Assert.Empty(dippedBelow.NewTriggers);
        Assert.Empty(crossedAgain.NewTriggers);
    }

    private static PositionSnapshot Position(
        long ticket,
        string symbol,
        TradeSide side,
        decimal volume,
        decimal profit,
        decimal swap,
        DateTimeOffset now) =>
        new(ticket, ticket, symbol, side, volume, 1m, 1m, profit, 0m, 0m,
            now.AddMinutes(-1), now, swap);
}
