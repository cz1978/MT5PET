namespace TradePet.Core.Protocol;

public sealed record BridgeLossZone(
    string Id,
    string Symbol,
    decimal LowerBound,
    decimal CenterPrice,
    decimal UpperBound,
    int AttemptCount,
    int LossCount,
    decimal CumulativeLoss);

public sealed record LossZoneChartSnapshot(
    long Revision,
    string TerminalPath,
    IReadOnlyList<BridgeLossZone> Zones);
