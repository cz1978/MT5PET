using System.Diagnostics;
using System.Text.Json;
using TradePet.Application.Review;
using TradePet.Core.Domain;
using TradePet.Core.Protocol;

namespace TradePet.Infrastructure.Mt5;

public sealed record Mt5HistoryOptions(
    string PythonExecutable,
    string WorkerScriptPath,
    string TerminalPath,
    string TerminalId,
    TimeSpan? Timeout = null);

public sealed class Mt5HistoryClient : IMarketHistorySource
{
    private readonly Mt5HistoryOptions _options;

    public Mt5HistoryClient(Mt5HistoryOptions options) => _options = options;

    public async Task<MarketHistoryResult> LoadAsync(
        MarketHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.TerminalId, _options.TerminalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("历史请求的终端身份与客户端配置不一致。");
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(_options.WorkerScriptPath);
        startInfo.ArgumentList.Add("--terminal-path");
        startInfo.ArgumentList.Add(_options.TerminalPath);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 MT5 历史行情进程。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var envelope = ProtocolEnvelope.Create($"review-{Guid.NewGuid():N}", 0, "history_request", request);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(envelope, ProtocolJson.Options).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout ?? TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        var output = await outputTask;
        var diagnostics = await errorTask;
        var envelopes = new List<ProtocolEnvelope>();
        using var reader = new StringReader(output);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!ProtocolLineParser.TryParse(line, out var parsed, out var error))
            {
                throw new InvalidDataException($"历史行情响应格式无效：{error}");
            }
            envelopes.Add(parsed!);
        }
        if (envelopes.Count == 0)
        {
            throw new InvalidDataException($"历史行情进程没有返回结果。{diagnostics}".Trim());
        }
        return Mt5HistoryProtocolMapper.Map(request, envelopes);
    }
}

public static class Mt5HistoryProtocolMapper
{
    public static MarketHistoryResult Map(MarketHistoryRequest request, IReadOnlyCollection<ProtocolEnvelope> envelopes)
    {
        var completeEnvelope = envelopes.LastOrDefault(item => item.Kind == "history_complete")
            ?? throw new InvalidDataException("历史行情响应缺少完成帧。");
        var complete = completeEnvelope.Payload.Deserialize<HistoryCompletePayload>(ProtocolJson.Options)
            ?? throw new InvalidDataException("历史行情完成帧无效。");
        ValidateIdentity(request, complete.RequestId, complete.TerminalId, complete.AccountKey, complete.Symbol, complete.Timeframe);

        var bars = new List<MarketBar>();
        var ticks = new List<MarketTick>();
        var expectedChunk = 0;
        foreach (var envelope in envelopes.Where(item => item.Kind == "history_chunk"))
        {
            var chunk = envelope.Payload.Deserialize<HistoryChunkPayload>(ProtocolJson.Options)
                ?? throw new InvalidDataException("历史行情分块无效。");
            ValidateIdentity(request, chunk.RequestId, chunk.TerminalId, chunk.AccountKey, chunk.Symbol, chunk.Timeframe);
            if (chunk.ChunkIndex != expectedChunk++)
            {
                throw new InvalidDataException("历史行情分块不连续。");
            }
            bars.AddRange(chunk.Bars.Select(item => new MarketBar(
                request.TerminalId, request.ExpectedAccountKey, request.Symbol, request.Timeframe,
                item.OpenedAtUtc, item.Open, item.High, item.Low, item.Close,
                item.TickVolume, item.Spread, item.RealVolume)));
            ticks.AddRange(chunk.Ticks.Select(item => new MarketTick(
                request.TerminalId, request.ExpectedAccountKey, request.Symbol, item.OccurredAtUtc,
                item.TimeMilliseconds, item.Bid, item.Ask, item.Last, item.Volume, item.Flags, item.Fingerprint)));
        }
        if (expectedChunk != complete.ChunkCount)
        {
            throw new InvalidDataException("历史行情分块数量与完成帧不一致。");
        }

        var range = new MarketDataRange(
            request.RequestId, request.TerminalId, request.ExpectedAccountKey, request.Symbol, request.Timeframe,
            complete.RequestedFromUtc, complete.RequestedToUtc, complete.ActualFromUtc, complete.ActualToUtc,
            complete.Precision, complete.Coverage, complete.SourceVersion, complete.Error, completeEnvelope.OccurredAtUtc);
        return new MarketHistoryResult(
            range,
            bars.GroupBy(item => item.OpenedAtUtc).Select(group => group.Last()).OrderBy(item => item.OpenedAtUtc).ToArray(),
            ticks.GroupBy(item => (item.TimeMilliseconds, item.Fingerprint)).Select(group => group.Last()).OrderBy(item => item.OccurredAtUtc).ToArray());
    }

    private static void ValidateIdentity(
        MarketHistoryRequest request,
        string requestId,
        string terminalId,
        string accountKey,
        string symbol,
        string timeframe)
    {
        if (requestId != request.RequestId || terminalId != request.TerminalId || accountKey != request.ExpectedAccountKey ||
            !symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase) || timeframe != request.Timeframe)
        {
            throw new InvalidDataException("历史行情响应的请求、账户、终端或品种身份不匹配。");
        }
    }

    private sealed record HistoryCompletePayload(
        string RequestId,
        string TerminalId,
        string AccountKey,
        string Symbol,
        string Timeframe,
        MarketDataPrecision Precision,
        string SourceVersion,
        DateTimeOffset RequestedFromUtc,
        DateTimeOffset RequestedToUtc,
        DateTimeOffset? ActualFromUtc,
        DateTimeOffset? ActualToUtc,
        MarketCoverageStatus Coverage,
        int ChunkCount,
        string Error);

    private sealed record HistoryChunkPayload(
        string RequestId,
        string TerminalId,
        string AccountKey,
        string Symbol,
        string Timeframe,
        int ChunkIndex,
        bool IsLast,
        IReadOnlyList<HistoryBarPayload> Bars,
        IReadOnlyList<HistoryTickPayload> Ticks);

    private sealed record HistoryBarPayload(
        DateTimeOffset OpenedAtUtc,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        long TickVolume,
        int Spread,
        long RealVolume);

    private sealed record HistoryTickPayload(
        DateTimeOffset OccurredAtUtc,
        long TimeMilliseconds,
        decimal Bid,
        decimal Ask,
        decimal Last,
        decimal Volume,
        long Flags,
        string Fingerprint);
}
