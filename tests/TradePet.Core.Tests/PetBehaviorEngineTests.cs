using TradePet.Core.Domain;
using TradePet.Core.Pet;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class PetBehaviorEngineTests
{
    [Fact]
    public void RiskInterruptsActivityAndHoldingResumesAsReview()
    {
        var engine = new PetBehaviorEngine();
        var now = DateTimeOffset.UtcNow;
        var state = engine.ApplySignal(engine.CreateInitial(now), PetSignal.Connected, now);
        state = engine.ApplySignal(state, PetSignal.TradeOpened, now.AddSeconds(1));
        state = engine.ApplySignal(state, PetSignal.Risk, now.AddSeconds(2));

        Assert.Equal(PetActivity.Failed, state.Current.Activity);
        Assert.Equal(TradeContextState.Holding, state.Current.TradeContext);
        Assert.Equal(AlertPriority.Critical, state.Current.ActiveAlertPriority);

        state = engine.CompleteInterruption(state, now.AddSeconds(3));
        Assert.Equal(PetActivity.Review, state.Current.Activity);
        Assert.Equal(PetMood.Attentive, state.Current.Mood);
        Assert.Null(state.Current.ActiveAlertPriority);
    }

    [Fact]
    public void DisconnectionDoesNotStopPetLife()
    {
        var engine = new PetBehaviorEngine();
        var now = DateTimeOffset.UtcNow;
        var state = engine.CreateInitial(now);
        state = engine.Advance(state with { Current = state.Current with { TradeContext = TradeContextState.Flat } }, now.AddSeconds(1));
        var activity = state.Current.Activity;

        state = engine.ApplySignal(state, PetSignal.Disconnected, now.AddSeconds(2));

        Assert.Equal(activity, state.Current.Activity);
        Assert.Equal(TradeContextState.Disconnected, state.Current.TradeContext);
    }
}
