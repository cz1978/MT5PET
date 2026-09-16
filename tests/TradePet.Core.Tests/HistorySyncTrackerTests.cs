using TradePet.Core.Domain;
using TradePet.Core.Session;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class HistorySyncTrackerTests
{
    [Fact]
    public void BeginRequest_RechecksCurrentYearAndSkipsCompletedOlderYears()
    {
        var tracker = new HistorySyncTracker();
        var now = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var persisted = new[]
        {
            new HistorySyncState("Broker|1", 2026, true, 10, now),
            new HistorySyncState("Broker|1", 2025, true, 10, now),
            new HistorySyncState("Broker|1", 2024, true, 10, now),
            new HistorySyncState("Broker|2", 2023, true, 10, now),
        };

        var plan = tracker.BeginRequest("Broker|1", 2026, persisted);

        Assert.NotNull(plan);
        Assert.Equal(2022, plan.EarliestYear);
        Assert.Equal([2026, 2023, 2022], plan.RequestedYears);
        Assert.Null(tracker.BeginRequest("Broker|1", 2026, persisted));
    }

    [Fact]
    public void PersistenceFailure_PreventsCompletionAndReleasesScopeForRetry()
    {
        var tracker = new HistorySyncTracker();
        tracker.BeginRequest("Broker|1", 2026, []);

        var failedChunk = tracker.RecordBatch(2026, sourceComplete: false, batchPersisted: false);
        var canComplete = tracker.CanMarkComplete(2026, sourceComplete: true);
        var completedFrame = tracker.RecordBatch(2026, sourceComplete: true, batchPersisted: true);

        Assert.False(failedChunk.RequiresRetry);
        Assert.False(canComplete);
        Assert.True(completedFrame.RequiresRetry);
        Assert.True(tracker.ShouldRequest("Broker|1"));
    }

    [Fact]
    public void SuccessfulCompletion_RemovesPendingYearWithoutRequestingAgain()
    {
        var tracker = new HistorySyncTracker();
        tracker.BeginRequest("Broker|1", 2026,
            [new HistorySyncState("Broker|1", 2025, true, 1, DateTimeOffset.UtcNow)]);

        Assert.True(tracker.CanMarkComplete(2026, sourceComplete: true));
        var result = tracker.RecordBatch(2026, sourceComplete: true, batchPersisted: true);

        Assert.False(result.RequiresRetry);
        Assert.Equal(3, result.PendingYearCount);
        Assert.False(tracker.ShouldRequest("Broker|1"));
    }

    [Fact]
    public void ResetAndLowerBound_AllowAReusableAccountScope()
    {
        var tracker = new HistorySyncTracker();
        var first = tracker.BeginRequest("Broker|1", 1972, []);

        tracker.Reset();
        var second = tracker.BeginRequest("Broker|1", 1972, []);

        Assert.Equal([1972, 1971, 1970], first!.RequestedYears);
        Assert.Equal(first.RequestedYears, second!.RequestedYears);
    }
}
