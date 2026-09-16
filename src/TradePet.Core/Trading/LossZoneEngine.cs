using System.Security.Cryptography;
using System.Text;
using TradePet.Core.Domain;

namespace TradePet.Core.Trading;

public sealed class LossZoneEngine
{
    private const decimal BreakevenEpsilon = 0.01m;
    private readonly AlertComposer _alertComposer = new();

    public LossZoneProjection Rebuild(
        string accountKey,
        DateOnly serverDate,
        decimal defaultTolerance,
        IReadOnlyCollection<TradeRecord> trades,
        IReadOnlyCollection<LossZoneState>? existingZones = null,
        Func<TradeRecord, decimal>? toleranceResolver = null)
    {
        var preservedTolerance = (existingZones ?? [])
            .Where(zone => zone.AccountKey == accountKey && zone.ServerDate == serverDate)
            .GroupBy(zone => zone.Id)
            .ToDictionary(group => group.Key, group => group.First().Tolerance);
        var zones = new List<LossZoneState>();
        var attempts = new List<LossZoneAttempt>();
        var assignmentByPosition = new Dictionary<long, string>();
        var events = trades
            .Where(trade =>
                trade.AccountKey == accountKey &&
                (trade.OpenServerDate == serverDate || trade.CloseServerDate == serverDate))
            .SelectMany(trade => CreateProjectionEvents(trade, serverDate))
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Trade.PositionId)
            .ToArray();

        foreach (var item in events)
        {
            var trade = item.Trade;
            if (item.Kind == ProjectionEventKind.Open)
            {
                if (assignmentByPosition.ContainsKey(trade.PositionId))
                {
                    continue;
                }

                var matchingZone = FindNearestZone(trade, serverDate, zones);
                if (matchingZone is null)
                {
                    continue;
                }

                var attempt = CreateAttempt(matchingZone, trade);
                attempts.Add(attempt);
                assignmentByPosition[trade.PositionId] = matchingZone.Id;
                ReplaceZone(zones, Reconcile(matchingZone, attempts));
                continue;
            }

            if (!trade.IsComplete || trade.ClosedAtUtc is null)
            {
                continue;
            }

            if (assignmentByPosition.TryGetValue(trade.PositionId, out var assignedZoneId))
            {
                var attemptIndex = attempts.FindIndex(attempt =>
                    attempt.ZoneId == assignedZoneId && attempt.PositionId == trade.PositionId);
                var zone = zones.First(candidate => candidate.Id == assignedZoneId);
                var attempt = attempts[attemptIndex] with
                {
                    NetPnl = trade.NetPnl,
                    ClosedAtUtc = trade.ClosedAtUtc,
                };
                attempts[attemptIndex] = attempt;
                ReplaceZone(zones, Reconcile(zone, attempts));
                continue;
            }

            if (trade.CloseServerDate != serverDate || trade.NetPnl >= -BreakevenEpsilon)
            {
                continue;
            }

            var zoneId = CreateZoneId(trade, serverDate);
            var tolerance = preservedTolerance.TryGetValue(zoneId, out var storedTolerance)
                ? storedTolerance
                : toleranceResolver?.Invoke(trade) ?? defaultTolerance;
            var newZone = new LossZoneState(
                zoneId,
                trade.AccountKey,
                serverDate,
                trade.Symbol,
                trade.EntryPrice,
                tolerance,
                0,
                0,
                0m,
                trade.ClosedAtUtc.Value);
            var newAttempt = CreateAttempt(newZone, trade) with
            {
                NetPnl = trade.NetPnl,
                ClosedAtUtc = trade.ClosedAtUtc,
            };
            zones.Add(newZone);
            attempts.Add(newAttempt);
            assignmentByPosition[trade.PositionId] = newZone.Id;
            ReplaceZone(zones, Reconcile(newZone, attempts));
        }

        return new LossZoneProjection(
            zones.OrderByDescending(zone => zone.LastAttemptAtUtc).ThenBy(zone => zone.Id).ToArray(),
            attempts.OrderBy(attempt => attempt.OpenedAtUtc).ThenBy(attempt => attempt.Id).ToArray());
    }

    public LossZoneState Reconcile(
        LossZoneState zone,
        IReadOnlyCollection<LossZoneAttempt> attempts)
    {
        var scoped = attempts
            .Where(attempt => attempt.ZoneId == zone.Id)
            .GroupBy(attempt => attempt.PositionId)
            .Select(group => group.OrderByDescending(attempt => attempt.ClosedAtUtc ?? attempt.OpenedAtUtc).First())
            .OrderBy(attempt => attempt.OpenedAtUtc)
            .ToArray();
        if (scoped.Length == 0)
        {
            return zone;
        }

        var losses = scoped.Where(attempt => attempt.NetPnl is < -BreakevenEpsilon).ToArray();
        var lastAttemptAtUtc = scoped.Max(attempt => attempt.ClosedAtUtc ?? attempt.OpenedAtUtc);
        return zone with
        {
            CenterPrice = losses.Length == 0
                ? zone.CenterPrice
                : Median(losses.Select(attempt => attempt.EntryPrice).Order().ToArray()),
            AttemptCount = scoped.Length,
            LossCount = losses.Length,
            CumulativeLoss = losses.Sum(attempt => attempt.NetPnl ?? 0m),
            LastAttemptAtUtc = lastAttemptAtUtc,
        };
    }

    public LossZoneOpenResult EvaluateOpen(
        TradeRecord trade,
        DateOnly serverDate,
        decimal defaultTolerance,
        IReadOnlyCollection<LossZoneState> zones,
        IReadOnlyCollection<LossZoneAttempt> attempts,
        IReadOnlyCollection<PlanItem> planItems,
        TradeRecord? previousClosedTrade,
        DailyPlanSettings settings)
    {
        var existingAttempt = attempts.FirstOrDefault(attempt => attempt.PositionId == trade.PositionId);
        var matchingZone = existingAttempt is null
            ? FindNearestZone(trade, serverDate, zones)
            : zones.FirstOrDefault(zone => zone.Id == existingAttempt.ZoneId);
        var previousZoneAttempt = matchingZone is null
            ? null
            : attempts
                .Where(attempt => attempt.ZoneId == matchingZone.Id && attempt.PositionId != trade.PositionId)
                .OrderByDescending(attempt => attempt.OpenedAtUtc)
                .FirstOrDefault();
        var attempt = existingAttempt;
        var updatedZone = matchingZone;
        if (matchingZone is not null && existingAttempt is null)
        {
            attempt = new LossZoneAttempt(
                $"{matchingZone.Id}:{trade.PositionId}",
                matchingZone.Id,
                trade.PositionId,
                trade.Side,
                trade.EntryPrice,
                trade.OpeningVolume,
                null,
                trade.OpenedAtUtc,
                null);
            updatedZone = matchingZone with
            {
                AttemptCount = matchingZone.AttemptCount + 1,
                LastAttemptAtUtc = trade.OpenedAtUtc,
            };
            updatedZone = Reconcile(
                updatedZone,
                attempts.Where(candidate => candidate.PositionId != trade.PositionId).Append(attempt).ToArray());
        }

        var facts = new List<RuleFact>();
        if (planItems.Any(item => IsInsideNoTradeZone(item, trade, serverDate)))
        {
            facts.Add(new RuleFact(
                RuleFactKind.NoTradePlan,
                AlertPriority.Important,
                0,
                "这里是你标记的不交易区。",
                "今天自己标记：不交易区"));
        }

        if (matchingZone is { LossCount: >= 2 })
        {
            var lowerBound = matchingZone.CenterPrice - matchingZone.Tolerance;
            var upperBound = matchingZone.CenterPrice + matchingZone.Tolerance;
            var attemptNumber = updatedZone?.AttemptCount ?? matchingZone.AttemptCount;
            facts.Add(new RuleFact(
                RuleFactKind.LossZoneHistory,
                AlertPriority.Critical,
                10,
                "亏损区里又开仓了。",
                $"{trade.Symbol} {lowerBound:0.#####}—{upperBound:0.#####}，第 {attemptNumber} 次进入；此前亏 {matchingZone.LossCount} 次，合计 {FormatSigned(matchingZone.CumulativeLoss)}"));
        }

        if (previousClosedTrade is { NetPnl: < -0.01m, ClosedAtUtc: not null })
        {
            var elapsed = trade.OpenedAtUtc - previousClosedTrade.ClosedAtUtc.Value;
            if (elapsed >= TimeSpan.Zero && elapsed.TotalSeconds <= settings.RapidReentrySeconds)
            {
                facts.Add(new RuleFact(
                    RuleFactKind.RapidReentry,
                    AlertPriority.Important,
                    20,
                    "上一单刚结束。",
                    $"上一单结束才 {Math.Round(elapsed.TotalSeconds):0} 秒"));
            }
        }

        if (previousZoneAttempt is not null && previousZoneAttempt.OpeningVolume > 0m &&
            trade.OpeningVolume >= previousZoneAttempt.OpeningVolume * settings.LotEscalationMultiplier)
        {
            facts.Add(new RuleFact(
                RuleFactKind.LotEscalation,
                AlertPriority.Critical,
                30,
                "仓位变大了。",
                $"仓位 {previousZoneAttempt.OpeningVolume:0.##} → {trade.OpeningVolume:0.##}"));
        }

        if (previousZoneAttempt is not null && previousZoneAttempt.Side != trade.Side)
        {
            facts.Add(new RuleFact(
                RuleFactKind.DirectionFlip,
                AlertPriority.Important,
                40,
                "方向又翻了。",
                $"上一笔{FormatSide(previousZoneAttempt.Side)}，这一笔{FormatSide(trade.Side)}。"));
        }

        var alert = _alertComposer.Compose($"open:{trade.AccountKey}:{trade.PositionId}", facts, trade.OpenedAtUtc);
        return new LossZoneOpenResult(updatedZone, attempt, alert, facts);
    }

    public LossZoneCloseResult RegisterClose(
        TradeRecord trade,
        DateOnly serverDate,
        decimal defaultTolerance,
        IReadOnlyCollection<LossZoneState> zones,
        IReadOnlyCollection<LossZoneAttempt> attempts)
    {
        if (!trade.IsComplete || trade.ClosedAtUtc is null)
        {
            return new LossZoneCloseResult(null, null);
        }

        var existingAttempt = attempts.FirstOrDefault(attempt => attempt.PositionId == trade.PositionId);
        var zone = existingAttempt is null
            ? null
            : zones.FirstOrDefault(candidate => candidate.Id == existingAttempt.ZoneId);
        if (trade.NetPnl >= -BreakevenEpsilon && existingAttempt is null)
        {
            return new LossZoneCloseResult(null, null);
        }

        if (zone is null && trade.NetPnl < -BreakevenEpsilon)
        {
            zone = new LossZoneState(
                CreateZoneId(trade, serverDate),
                trade.AccountKey,
                serverDate,
                trade.Symbol,
                trade.EntryPrice,
                defaultTolerance,
                0,
                0,
                0m,
                trade.ClosedAtUtc.Value);
        }

        if (zone is null)
        {
            return new LossZoneCloseResult(null, null);
        }

        var attempt = existingAttempt ?? CreateAttempt(zone, trade);
        attempt = attempt with
        {
            NetPnl = trade.NetPnl,
            ClosedAtUtc = trade.ClosedAtUtc,
        };

        var updatedZone = Reconcile(
            zone,
            attempts.Where(candidate => candidate.PositionId != trade.PositionId).Append(attempt).ToArray());

        return new LossZoneCloseResult(updatedZone, attempt);
    }

    public static bool IsConfirmedLossZone(LossZoneState? zone) => zone is { LossCount: >= 2 };

    public static bool ShouldNotifyLossClose(TradeRecord trade, LossZoneState? zone) =>
        trade.NetPnl < -BreakevenEpsilon && IsConfirmedLossZone(zone);

    private static LossZoneState? FindNearestZone(
        TradeRecord trade,
        DateOnly serverDate,
        IEnumerable<LossZoneState> zones) =>
        zones
            .Where(zone =>
                zone.AccountKey == trade.AccountKey &&
                zone.ServerDate == serverDate &&
                string.Equals(zone.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(zone.CenterPrice - trade.EntryPrice) <= zone.Tolerance)
            .OrderBy(zone => Math.Abs(zone.CenterPrice - trade.EntryPrice))
            .ThenByDescending(zone => zone.LastAttemptAtUtc)
            .ThenBy(zone => zone.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    private static IEnumerable<ProjectionEvent> CreateProjectionEvents(TradeRecord trade, DateOnly serverDate)
    {
        if (trade.OpenServerDate == serverDate)
        {
            yield return new ProjectionEvent(ProjectionEventKind.Open, trade.OpenedAtUtc, trade);
        }

        if (trade.IsComplete && trade.ClosedAtUtc is not null &&
            (trade.OpenServerDate == serverDate || trade.CloseServerDate == serverDate))
        {
            yield return new ProjectionEvent(ProjectionEventKind.Close, trade.ClosedAtUtc.Value, trade);
        }
    }

    private static LossZoneAttempt CreateAttempt(LossZoneState zone, TradeRecord trade) =>
        new(
            $"{zone.Id}:{trade.PositionId}",
            zone.Id,
            trade.PositionId,
            trade.Side,
            trade.EntryPrice,
            trade.OpeningVolume,
            null,
            trade.OpenedAtUtc,
            null);

    private static void ReplaceZone(IList<LossZoneState> zones, LossZoneState zone)
    {
        var index = zones.ToList().FindIndex(candidate => candidate.Id == zone.Id);
        if (index >= 0)
        {
            zones[index] = zone;
        }
        else
        {
            zones.Add(zone);
        }
    }

    private static bool IsInsideNoTradeZone(PlanItem item, TradeRecord trade, DateOnly serverDate) =>
        item.IsActive &&
        item.AccountKey == trade.AccountKey &&
        item.ServerDate == serverDate &&
        item.Category == PlanCategory.NoTradeZone &&
        string.Equals(item.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase) &&
        item.PriceLow is not null &&
        item.PriceHigh is not null &&
        trade.EntryPrice >= Math.Min(item.PriceLow.Value, item.PriceHigh.Value) &&
        trade.EntryPrice <= Math.Max(item.PriceLow.Value, item.PriceHigh.Value);

    private static string CreateZoneId(TradeRecord trade, DateOnly serverDate)
    {
        var raw = $"{trade.AccountKey}|{serverDate:yyyy-MM-dd}|{trade.Symbol}|{trade.PositionId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"zone-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static decimal Median(IReadOnlyList<decimal> orderedValues)
    {
        if (orderedValues.Count == 0)
        {
            throw new ArgumentException("Median requires at least one value.", nameof(orderedValues));
        }

        var middle = orderedValues.Count / 2;
        return orderedValues.Count % 2 == 1
            ? orderedValues[middle]
            : (orderedValues[middle - 1] + orderedValues[middle]) / 2m;
    }

    private static string FormatSigned(decimal value) => $"{(value >= 0m ? "+" : string.Empty)}{value:0.##}";

    private static string FormatSide(TradeSide side) => side == TradeSide.Buy ? "买入" : "卖出";

    private enum ProjectionEventKind
    {
        Close,
        Open,
    }

    private sealed record ProjectionEvent(
        ProjectionEventKind Kind,
        DateTimeOffset OccurredAtUtc,
        TradeRecord Trade);
}
