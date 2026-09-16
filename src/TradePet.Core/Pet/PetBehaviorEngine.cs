using TradePet.Core.Domain;

namespace TradePet.Core.Pet;

public sealed class PetBehaviorEngine
{
    private static readonly PetActivity[] FlatSequence =
    [
        PetActivity.Idle,
        PetActivity.Waiting,
        PetActivity.RunningRight,
        PetActivity.Idle,
        PetActivity.RunningLeft,
        PetActivity.Waiting,
    ];

    private static readonly PetActivity[] HoldingSequence =
    [
        PetActivity.Review,
        PetActivity.Waiting,
        PetActivity.Idle,
    ];

    public PetBehaviorState CreateInitial(DateTimeOffset nowUtc) =>
        new(
            new PetState(PetMood.Calm, PetActivity.Idle, TradeContextState.Disconnected, null, nowUtc),
            null,
            [PetActivity.Idle],
            false);

    public PetBehaviorState ApplySignal(PetBehaviorState state, PetSignal signal, DateTimeOffset nowUtc)
    {
        var current = state.Current;
        var resume = state.ResumeActivity;
        var next = signal switch
        {
            PetSignal.Connected => current with
            {
                TradeContext = TradeContextState.Flat,
                ActiveAlertPriority = null,
                ActivityStartedAtUtc = nowUtc,
            },
            PetSignal.Disconnected => current with
            {
                TradeContext = TradeContextState.Disconnected,
                ActiveAlertPriority = null,
                ActivityStartedAtUtc = nowUtc,
            },
            PetSignal.TradeOpened => Interrupt(PetMood.Attentive, PetActivity.Waving, TradeContextState.Holding, AlertPriority.Normal),
            PetSignal.Holding => current with
            {
                Mood = PetMood.Attentive,
                Activity = PetActivity.Review,
                TradeContext = TradeContextState.Holding,
                ActiveAlertPriority = null,
                ActivityStartedAtUtc = nowUtc,
            },
            PetSignal.Flat => current with
            {
                Mood = PetMood.Calm,
                TradeContext = TradeContextState.Flat,
                ActiveAlertPriority = null,
                ActivityStartedAtUtc = nowUtc,
            },
            PetSignal.ProfitClosed => Interrupt(PetMood.Happy, PetActivity.Jumping, TradeContextState.Flat, AlertPriority.Normal),
            PetSignal.LossClosed => Interrupt(PetMood.Concerned, PetActivity.Failed, TradeContextState.Flat, AlertPriority.Normal),
            PetSignal.Risk => Interrupt(PetMood.Concerned, PetActivity.Failed, current.TradeContext, AlertPriority.Critical),
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };

        return state with
        {
            Current = next,
            ResumeActivity = resume,
            RecentActivities = AppendHistory(state.RecentActivities, next.Activity),
        };

        PetState Interrupt(PetMood mood, PetActivity activity, TradeContextState context, AlertPriority priority)
        {
            resume ??= current.Activity;
            return new PetState(mood, activity, context, priority, nowUtc);
        }
    }

    public PetBehaviorState CompleteInterruption(PetBehaviorState state, DateTimeOffset nowUtc)
    {
        var nextActivity = state.Current.TradeContext == TradeContextState.Holding
            ? PetActivity.Review
            : state.ResumeActivity ?? PetActivity.Idle;
        var nextMood = state.Current.TradeContext == TradeContextState.Holding ? PetMood.Attentive : PetMood.Calm;
        return state with
        {
            Current = state.Current with
            {
                Mood = nextMood,
                Activity = nextActivity,
                ActiveAlertPriority = null,
                ActivityStartedAtUtc = nowUtc,
            },
            ResumeActivity = null,
            RecentActivities = AppendHistory(state.RecentActivities, nextActivity),
        };
    }

    public PetBehaviorState Advance(PetBehaviorState state, DateTimeOffset nowUtc)
    {
        var sequence = state.Current.TradeContext == TradeContextState.Holding ? HoldingSequence : FlatSequence;
        var currentIndex = Array.IndexOf(sequence, state.Current.Activity);
        var next = sequence[(currentIndex + 1 + sequence.Length) % sequence.Length];
        return state with
        {
            Current = state.Current with
            {
                Activity = next,
                ActivityStartedAtUtc = nowUtc,
            },
            RecentActivities = AppendHistory(state.RecentActivities, next),
        };
    }

    private static IReadOnlyList<PetActivity> AppendHistory(IReadOnlyList<PetActivity> history, PetActivity next) =>
        history.Append(next).TakeLast(5).ToArray();
}
