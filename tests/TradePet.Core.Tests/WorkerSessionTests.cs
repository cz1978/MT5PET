using TradePet.Core.Session;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class WorkerSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Session_BecomesLiveOnlyAfterSnapshotAndDeals(bool snapshotFirst)
    {
        var session = new WorkerSession();
        session.Connect("Broker|1");

        if (snapshotFirst)
        {
            session.MarkSnapshotReceived();
        }
        else
        {
            session.MarkDealsReceived();
        }

        Assert.Equal(WorkerSessionPhase.Recovering, session.Phase);
        Assert.True(session.IsRecovery);

        if (snapshotFirst)
        {
            session.MarkDealsReceived();
        }
        else
        {
            session.MarkSnapshotReceived();
        }

        Assert.Equal(WorkerSessionPhase.Live, session.Phase);
        Assert.False(session.IsRecovery);
    }

    [Fact]
    public void Reconnect_ClearsPreviousSessionBaselines()
    {
        var session = LiveSession();

        session.Disconnect();
        session.Connect("Broker|1");

        Assert.True(session.IsConnected);
        Assert.Equal("Broker|1", session.AccountKey);
        Assert.False(session.HasInitialSnapshot);
        Assert.False(session.HasInitialDeals);
        Assert.Equal(WorkerSessionPhase.Recovering, session.Phase);
    }

    [Fact]
    public void AccountChange_ClearsBothBaselinesAndChangesScope()
    {
        var session = LiveSession();

        session.ResetForAccount("Broker|2");

        Assert.Equal("Broker|2", session.AccountKey);
        Assert.False(session.HasInitialSnapshot);
        Assert.False(session.HasInitialDeals);
        Assert.Equal(WorkerSessionPhase.Recovering, session.Phase);
    }

    [Fact]
    public void ResetDealsBaseline_PreservesConnectionAndSnapshot()
    {
        var session = LiveSession();

        session.ResetDealsBaseline();

        Assert.True(session.IsConnected);
        Assert.True(session.HasInitialSnapshot);
        Assert.False(session.HasInitialDeals);
        Assert.Equal(WorkerSessionPhase.Recovering, session.Phase);
    }

    private static WorkerSession LiveSession()
    {
        var session = new WorkerSession();
        session.Connect("Broker|1");
        session.MarkSnapshotReceived();
        session.MarkDealsReceived();
        return session;
    }
}
