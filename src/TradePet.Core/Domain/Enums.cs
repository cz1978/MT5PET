namespace TradePet.Core.Domain;

public enum TradeSide
{
    Buy,
    Sell,
}

public enum DealEntryKind
{
    In,
    Out,
    InOut,
    OutBy,
}

public enum ChartObjectKind
{
    HorizontalLine,
    Rectangle,
    TrendLine,
    Text,
    Label,
}

public enum PlanCategory
{
    Unclassified,
    Support,
    Resistance,
    BreakoutWatch,
    EntryWatch,
    StopReference,
    MarkOnly,
    LongWatchZone,
    ShortWatchZone,
    NoTradeZone,
    KeyZone,
    Note,
}

public enum TimelineKind
{
    Connected,
    Disconnected,
    TradeOpened,
    TradeIncreased,
    TradeReduced,
    TradeClosed,
    StopLossChanged,
    TakeProfitChanged,
    LossZoneReentry,
    DirectionFlip,
    LotEscalation,
    AddingToLoss,
    DailyTargetReached,
    DailyLossReached,
    ProfitGiveback,
    PlanObjectAdded,
    PlanObjectChanged,
    PlanObjectDeleted,
    Alert,
}

public enum PetMood
{
    Calm,
    Tired,
    Attentive,
    Happy,
    Concerned,
}

public enum PetActivity
{
    Idle,
    Waiting,
    Review,
    RunningRight,
    RunningLeft,
    Running,
    Waving,
    Jumping,
    Failed,
}

public enum TradeContextState
{
    Disconnected,
    Flat,
    Holding,
}

public enum AlertPriority
{
    Informational,
    Normal,
    Important,
    Critical,
}

public enum GivebackMode
{
    Percentage,
    Amount,
}

public enum TradeDomainEventKind
{
    Opened,
    Increased,
    Reduced,
    Closed,
    StopLossAdded,
    StopLossModified,
    StopLossRemoved,
    TakeProfitAdded,
    TakeProfitModified,
    TakeProfitRemoved,
    AddingToLoss,
}

public enum RuleFactKind
{
    NoTradePlan,
    LossZoneHistory,
    RapidReentry,
    DirectionFlip,
    LotEscalation,
    AddingToLoss,
    ConsecutiveLosses,
    DailyTarget,
    DailyLoss,
    ProfitGiveback,
    MaximumTrades,
    MaximumLot,
    MissingStopLoss,
    BehaviorReentry,
    BehaviorLossZonePersistence,
    BehaviorRevenge,
    BehaviorOvertrade,
    BehaviorPlanDeviation,
    BehaviorSizeEscalation,
    BehaviorCooldown,
    BehaviorPriceFixation,
    Alert,
}

public enum PetSignal
{
    Connected,
    Disconnected,
    TradeOpened,
    Holding,
    Flat,
    ProfitClosed,
    LossClosed,
    Risk,
}
