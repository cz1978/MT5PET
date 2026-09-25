using System.Threading.Channels;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Mt5;

public interface ITradingWorkerClient : IAsyncDisposable
{
    ChannelReader<ProtocolEnvelope> Events { get; }
    Task RunAsync(CancellationToken cancellationToken);
    Task RefreshAsync(int serverUtcOffsetSeconds, DateOnly serverDate, CancellationToken cancellationToken = default);
    Task RefreshHistoryAsync(IReadOnlyCollection<int> historyYears, int serverUtcOffsetSeconds,
        DateOnly serverDate, CancellationToken cancellationToken = default);
}
