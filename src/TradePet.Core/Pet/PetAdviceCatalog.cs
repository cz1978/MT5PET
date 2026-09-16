using TradePet.Core.Domain;

namespace TradePet.Core.Pet;

public enum PetAdviceContext
{
    Flat,
    Holding,
    CoolingDown,
    ProtectingProfit,
}

[Flags]
public enum PetAdviceSituation
{
    None = 0,
    HoldingProfit = 1 << 0,
    HoldingLoss = 1 << 1,
    WinningStreak = 1 << 2,
    DailyTargetReached = 1 << 3,
    ProfitGiveback = 1 << 4,
    All = HoldingProfit | HoldingLoss | WinningStreak | DailyTargetReached | ProfitGiveback,
}

public sealed record PetAdvice(
    string Key,
    string Headline,
    string Details,
    PetAdviceSituation RequiredSituation = PetAdviceSituation.None);

public sealed class PetAdviceCatalog
{
    private static readonly IReadOnlyDictionary<PetAdviceContext, PetAdvice[]> AdviceByContext =
        new Dictionary<PetAdviceContext, PetAdvice[]>
        {
            [PetAdviceContext.Flat] =
            [
                new("flat-trend", "先顺势，再谈进场。", "先看大方向，再等回踩或突破确认；没到位置就别硬做。"),
                new("flat-chase", "顺势不等于追价。", "趋势走远了就等下一次结构，追进去通常只剩更差的盈亏比。"),
                new("flat-stop-first", "先定止损，再碰下单键。", "止损没有客观位置，这笔交易就还没准备好。"),
                new("flat-wait", "看不懂，不代表必须做。", "没有清楚的方向、位置和失效条件，空仓就是正确仓位。"),
                new("flat-confirm", "等确认，别替行情脑补。", "让价格真正走出结构，再决定要不要跟；猜对不是纪律。"),
                new("flat-missed", "错过不算亏。", "别为了追回一个已经走掉的机会，去接一笔计划外的烂价格。"),
                new("flat-direction", "大方向不清，小周期都是噪音。", "先确定市场在走趋势还是震荡，再到小周期找触发。"),
                new("flat-location", "好方向，也要等好位置。", "方向对但位置差，止损和盈亏比一样会把优势吃掉。"),
                new("flat-three", "方向、位置、触发，缺一不做。", "入场前把三个答案写清楚，少一个就把手放下。"),
                new("flat-edge", "只做优势明显的那一边。", "多空理由差不多时没有优势，模棱两可就继续观察。"),
                new("flat-close", "等这根走完，再做决定。", "没收盘的形态随时会变，别用半根蜡烛替自己壮胆。"),
                new("flat-frequency", "交易次数不是产量。", "今天没出现好机会，就别靠增加次数制造存在感。"),
                new("flat-plan-profit", "计划外的盈利，也会教坏你。", "侥幸赚到不等于做对，别让偶然奖励错误动作。"),
                new("flat-risk-size", "先算能亏多少，再算下多少。", "仓位由止损距离和风险预算决定，不由信心决定。"),
                new("flat-patience", "耐心不是等待，是拒绝次品。", "把普通机会放过去，才有空间执行真正清楚的机会。"),
                new("flat-top-bottom", "别急着猜顶，也别急着抄底。", "先等结构证明转向，再用止损保护判断，不和趋势赌气。"),
                new("flat-bored", "无聊不是开仓信号。", "手痒时先离开图表，市场不会因为你盯着就多给机会。"),
                new("flat-price-condition", "价格到了，条件也得到了吗？", "只到价不算触发，结构、方向和风险必须一起过关。"),
                new("flat-risk-reward", "盈亏比不够，胜率救不了。", "入场前先看潜在收益是否值得承担这次止损。"),
                new("flat-breakout", "突破没站稳，先别急着追。", "等收盘确认或回踩承接，假突破最爱收拾急性子。"),
                new("flat-range", "震荡中间，谁进去谁难受。", "区间中部没有位置优势，靠近边界再找明确触发。"),
                new("flat-session", "不是你的时段，就少伸手。", "只在自己熟悉的交易时段出手，别全天候随机试错。"),
                new("flat-spread", "成本突然变贵，机会就变差。", "点差和波动异常时先等，别拿正常计划硬套异常行情。"),
                new("flat-scenario", "先写如果，再按那么做。", "入场、失效和退出条件提前写好，盘中只负责执行。"),
                new("flat-cancel", "会撤销计划，才算会做计划。", "条件迟迟不出现或结构已改变，就把旧想法作废。"),
                new("flat-simple", "看得越复杂，越可能没优势。", "说不清交易逻辑时先简化，简化后仍不清楚就不做。"),
                new("flat-first-move", "第一下没跟上，就别追第二下。", "错过启动段后等回踩，别在情绪最热的位置接力。"),
                new("flat-one-plan", "一张图，只留一个主计划。", "多空剧本互相打架时，先确定主方向和失效条件。"),
                new("flat-evidence", "理由再多，也要价格点头。", "观点只能生成计划，真正入场必须等市场给出证据。"),
                new("flat-small-loss", "小亏可控，烂单不可控。", "宁可接受计划内止损，也别接一笔没有边界的交易。"),
                new("flat-journal", "下单前写一句为什么。", "一句话都说不清的机会，通常也经不起真实波动。"),
                new("flat-choice", "今天可以一笔都不做。", "空仓不是缺席，是把资金留给真正属于你的行情。"),
            ],
            [PetAdviceContext.Holding] =
            [
                new("holding-stop", "别替止损搬家。", "入场逻辑失效就认，别把小亏拖成一场意志力比赛。"),
                new("holding-noise", "看结构，别盯每一下跳动。", "价格没触发计划条件前，不要被几根小波动来回指挥。"),
                new("holding-add", "浮亏不是加仓理由。", "只有原计划允许且风险仍受控，才考虑增加仓位。", PetAdviceSituation.HoldingLoss),
                new("holding-plan", "有计划就执行，没计划就减动作。", "别因为一时浮盈或浮亏，临时改止损、目标和方向。"),
                new("holding-risk", "一笔交易，只亏计划内的钱。", "先确认当前最大损失仍在接受范围内，再考虑利润能走多远。"),
                new("holding-screen", "少盯盈亏，多盯失效条件。", "账户数字会扰乱判断，结构是否失效才决定该不该继续拿。"),
                new("holding-hope", "希望不是持仓理由。", "原来的交易逻辑消失了，就别靠祈祷继续占着仓位。"),
                new("holding-profit", "让利润跑，先把风险拴住。", "风险已经受控时给行情空间，别因一次回撤就慌着兑现。", PetAdviceSituation.HoldingProfit),
                new("holding-breakeven", "保本不是越快越好。", "只有结构支持时才移动止损，别用恐惧把正常波动赶出去。", PetAdviceSituation.HoldingProfit),
                new("holding-size", "仓位让你坐不住，就是太大。", "如果每个跳动都影响情绪，下次先把手数降下来。"),
                new("holding-entry", "入场以后，别重新发明计划。", "执行入场前写下的条件，不要边看盈亏边修改规则。"),
                new("holding-decision", "一根波动，不值得改三次主意。", "除非关键结构被破坏，否则别让短线噪音接管决策。"),
                new("holding-target", "别让目标跟着贪心移动。", "行情走顺时按计划管理，别把合理目标改成无限幻想。", PetAdviceSituation.HoldingProfit),
                new("holding-time", "没走出来，也是一种信息。", "超过计划时间仍没有进展，就重新评估资金是否值得继续占用。"),
                new("holding-feeling", "别爱上一笔交易。", "仓位没有感情，逻辑失效就处理，不必证明最初判断正确。"),
                new("holding-exit", "该走的时候，别和价格讲道理。", "触发失效条件就执行，市场不会听你的解释。"),
                new("holding-still", "没触发条件时，不动也是执行。", "不要为了缓解焦虑频繁改仓，等待本来就是计划的一部分。"),
                new("holding-risk-fixed", "风险定了，就别偷偷加价。", "扩大止损等于临时加仓，只是动作看起来没那么明显。"),
                new("holding-average", "亏损仓不是打折商品。", "价格更低不代表更便宜，逻辑失效时补仓只会放大错误。", PetAdviceSituation.HoldingLoss),
                new("holding-winner-add", "想加仓，只给正确的仓位加。", "先让原仓证明方向，再确保总风险仍在计划范围内。"),
                new("holding-timeframe", "用什么周期进，就用什么周期管。", "别拿一分钟噪音推翻小时级计划，也别反过来拖延止损。"),
                new("holding-partial", "减仓是工具，不是情绪按钮。", "只有计划节点到了才分批处理，别涨一点卖一点。"),
                new("holding-trail", "移动止损要跟结构，不跟心跳。", "新结构形成后再保护利润，别贴着价格把自己扫出去。", PetAdviceSituation.HoldingProfit),
                new("holding-pnl", "盈亏数字大，不代表信息多。", "先看价格是否触发条件，再看账户数字，顺序别反了。"),
                new("holding-flip", "持仓中途，别一秒钟变两次方向。", "原方向失效先退出，重新评估后再考虑反向计划。"),
                new("holding-space", "好仓位也需要呼吸。", "正常波动范围内别乱处理，止损距离本来就是给波动留的。"),
                new("holding-checklist", "持仓只检查三件事。", "逻辑是否有效、风险是否受控、退出条件是否触发。"),
                new("holding-control", "你能控制动作，控制不了行情。", "别预测下一跳，只把止损、仓位和执行做好。"),
                new("holding-fatigue", "盯得太久，判断会缩水。", "设置关键价格提醒，别用持续紧张代替风险管理。"),
                new("holding-no-rescue", "别想着把仓位救回来。", "交易不是人质，逻辑坏了就结束，不必等它回到成本价。", PetAdviceSituation.HoldingLoss),
                new("holding-profit-room", "利润要跑，也得有路线。", "按计划分批、跟踪或退出，别把随缘叫作让利润奔跑。", PetAdviceSituation.HoldingProfit),
                new("holding-original", "记住你最初愿意亏多少。", "当前风险超过入场前预算时，先降风险，不要再找理由。"),
            ],
            [PetAdviceContext.CoolingDown] =
            [
                new("cooling-stop", "先停手，市场不欠你。", "连亏后离开鼠标一会儿，下一笔只做最清楚的顺势机会。"),
                new("cooling-size", "别拿手数找回尊严。", "下一笔仓位不要放大；想翻本，通常就是继续上头的开始。"),
                new("cooling-reason", "把进场理由重新说一遍。", "方向、位置、止损和失效条件说不完整，就继续等。"),
                new("cooling-revenge", "回本不是交易信号。", "价格结构不会因为你刚亏了钱就变得更可靠。"),
                new("cooling-reset", "离开屏幕，重置节奏。", "先喝水、走动，再回来只看计划，不看上一笔亏了多少。"),
                new("cooling-zone", "同一个坑，别反复交学费。", "刚在这个价格区亏过，就等结构真正改变后再考虑。"),
                new("cooling-frequency", "连亏以后，先降频率。", "减少出手次数，只保留方向、位置和触发都清楚的机会。"),
                new("cooling-body", "心跳快的时候，不做决定。", "先让身体慢下来；情绪没恢复，分析通常也不可靠。"),
                new("cooling-note", "先写复盘，后看下一单。", "分清是判断错误、执行错误还是正常止损，再决定是否继续。"),
                new("cooling-mistake", "亏损可以接受，失控不能。", "正常止损是成本；加码、追单和乱改计划才是真问题。"),
                new("cooling-skip", "允许自己错过下一班车。", "错过一次机会没有代价，上头再亏一笔才有。"),
                new("cooling-clock", "冷静期不是倒计时冲锋。", "时间到了也要重新检查条件，不是立刻获得下一次开仓资格。"),
                new("cooling-proof", "别用下一单证明上一单没错。", "每笔交易独立判断，上一笔的输赢不该参与这次入场。"),
                new("cooling-market", "市场不知道你亏了多少。", "别要求行情配合回本，只接受当下真实存在的结构。"),
                new("cooling-plan", "越想扳回来，越要缩小动作。", "降低仓位、减少频率，把目标从回本改成恢复纪律。"),
                new("cooling-tomorrow", "今天停下，明天还在。", "保存判断力比追回一笔损失重要，机会不会只出现一次。"),
                new("cooling-debt", "亏损不是市场欠你的债。", "别向下一笔索要回本，它只该接受独立的计划和风险。"),
                new("cooling-hands", "手先离开，脑子才有机会回来。", "关掉下单面板几分钟，别让动作跑在判断前面。"),
                new("cooling-count", "先数今天做了几笔。", "次数明显超出平时，就先停；频率失控时质量通常已经掉了。"),
                new("cooling-screenshot", "截图，别急着开下一单。", "把进场和出场位置留下来，先看错误是否正在重复。"),
                new("cooling-opposite", "反手不等于纠错。", "刚止损就换方向，多半是在追着价格承认情绪。"),
                new("cooling-smaller", "非要继续，就先把仓位砍小。", "风险降下来后再看机会，别让回本冲动决定手数。"),
                new("cooling-clean", "下一笔只做干净的。", "条件稍微含糊就跳过，现在需要的是恢复执行，不是增加样本。"),
                new("cooling-walk", "去走两分钟，行情不会跑完。", "离开屏幕后再回来，先重画结构，不继承上一笔情绪。"),
                new("cooling-speed", "下单越来越快，通常不是熟练。", "决策时间突然缩短，多半是情绪在替你省略检查。"),
                new("cooling-classify", "先分清：正常亏，还是乱做亏。", "正常止损不用报复；执行错误需要停手，不需要加码。"),
                new("cooling-budget", "今天的风险预算不是橡皮筋。", "额度用完就收工，别因为想回本临时把上限拉长。"),
                new("cooling-chart", "别盯着刚亏的价位较劲。", "切换视角或缩小图表，确认自己没有被一个价格绑住。"),
                new("cooling-proof-two", "市场不需要你证明勇敢。", "愿意停手比继续硬扛更难，也更像专业交易。"),
                new("cooling-reset-plan", "重新开始，不等于立刻再开。", "先恢复固定流程，等完整条件出现后才算真正重置。"),
                new("cooling-capital", "先保本金，再保面子。", "账户活着才有下一次机会，输赢不值得拿纪律交换。"),
                new("cooling-silence", "想马上点下去时，再等一根。", "冲动最强的时候延迟决定，往往就能看见刚才忽略的问题。"),
            ],
            [PetAdviceContext.ProtectingProfit] =
            [
                new("profit-pace", "盈利不是加速许可证。", "下一笔仍按原来的条件和风险做，别因为今天赚钱就放大仓位。"),
                new("profit-giveback", "赚到手的节奏，别亲手毁掉。", "达到目标后降低频率，只做明显优于平时的机会。", PetAdviceSituation.DailyTargetReached),
                new("profit-quality", "今天赚了，也不用硬凑下一单。", "没有高质量机会就停，少做一笔不会减少已经实现的利润。"),
                new("profit-risk", "别让浮躁替利润买单。", "连续盈利后最容易放松止损，风险标准一条都别降。"),
                new("profit-repeat", "重复正确动作，别重复兴奋。", "复盘盈利来自哪里，然后只等待同类条件再次出现。"),
                new("profit-target", "目标到了，选择权在你。", "降低频率或直接停手，不必为了多赚一点交回主动权。", PetAdviceSituation.DailyTargetReached),
                new("profit-size", "赚了钱，也别突然加手数。", "下一笔继续使用一致风险，别让兴奋替你调整仓位。"),
                new("profit-last", "别让最后一单毁掉整天。", "临近收工只做最清楚的机会，普通机会直接跳过。"),
                new("profit-stop", "会停手，也是交易能力。", "当注意力和执行质量下降时，保住成果比继续证明自己重要。"),
                new("profit-confidence", "连赢容易让人高估自己。", "把盈利归功于正确执行，不要误以为下一笔不会错。", PetAdviceSituation.WinningStreak),
                new("profit-process", "守住流程，利润才有机会留下。", "方向、位置、触发和风险标准，一项也别因为盈利而放松。"),
                new("profit-selective", "盈利以后，更该挑剔。", "账户已有安全垫时，没有必要拿它去试模糊机会。"),
                new("profit-round", "不用把每段行情都赚完。", "拿到计划内利润就够了，剩下的走势属于市场。"),
                new("profit-review", "先记下做对什么，再继续。", "把有效动作写进复盘，避免只记住盈利带来的兴奋。"),
                new("profit-calm", "赚钱时冷静，比亏钱时冷静更难。", "别追单、别加速、别降低标准，维持原来的节奏。"),
                new("profit-bank", "先保住判断力，再保住利润。", "感觉开始随意时就收手，回吐往往从纪律变松开始。"),
                new("profit-own-money", "今天赚的也是你的钱，别乱送。", "每一笔仍按风险预算做，别拿今天赚的给冲动买单。"),
                new("profit-high", "创新高以后，先把速度降下来。", "账户越顺越容易轻敌，下一笔标准只能更高。"),
                new("profit-greed", "多赚一点，常常先从多做一笔开始亏。", "达到计划后把普通机会全部过滤，只留最清楚的。", PetAdviceSituation.DailyTargetReached),
                new("profit-invincible", "连赢不是免死金牌。", "市场没有给你升级权限，止损和仓位一项都别放松。", PetAdviceSituation.WinningStreak),
                new("profit-cushion", "安全垫不是拿来试烂单的。", "已有利润时更该保护选择权，不必验证每一个想法。"),
                new("profit-pause", "赚完先停五分钟。", "把兴奋降下来再看图，避免把好状态交易成过度交易。"),
                new("profit-same-size", "账户变绿，手数别跟着膨胀。", "保持一致风险，别让短期结果改变长期规则。"),
                new("profit-clean-end", "漂亮的一天，要有干净的收尾。", "执行开始变形时立刻结束，别等亏回去才承认疲劳。"),
                new("profit-not-all", "行情的钱，不需要全部归你。", "拿到计划内那一段就够，放弃尾巴也是纪律。"),
                new("profit-select", "赚钱之后，拒绝机会更值钱。", "把低质量入场留给别人，利润不需要靠频率证明。"),
                new("profit-drawback", "回吐一点正常，回吐失控不正常。", "区分计划内波动和纪律松动，越线就按规则收手。", PetAdviceSituation.ProfitGiveback),
                new("profit-process-two", "今天做对的，明天还要能复制。", "记录有效条件和执行动作，不要只截图盈利数字。"),
                new("profit-ego", "别把顺风当成水平突然暴涨。", "尊重样本和运气，下一笔仍然可能正常止损。"),
                new("profit-last-click", "收工前那一下，最容易多余。", "如果理由只是再赚一点，这笔交易已经没有理由。"),
                new("profit-attention", "利润需要保护，注意力也一样。", "精力下降就停止决策，疲劳会把优势慢慢交回去。"),
                new("profit-stop-green", "绿着关机，也是一种本事。", "达到当天目标后主动结束，把好节奏留到下一次。", PetAdviceSituation.DailyTargetReached),
            ],
        };

    public int Count(PetAdviceContext context) => AdviceByContext[context].Length;

    public static int CountConsecutiveWins(
        IReadOnlyCollection<TradeRecord> trades,
        DateOnly serverDate)
    {
        var completed = trades
            .Where(trade =>
                trade.IsComplete &&
                trade.CloseServerDate == serverDate &&
                trade.ClosedAtUtc is not null)
            .OrderBy(trade => trade.ClosedAtUtc)
            .ToArray();
        var count = 0;
        for (var index = completed.Length - 1; index >= 0 && completed[index].NetPnl > 0.01m; index--)
        {
            count++;
        }
        return count;
    }

    public PetAdvice Select(PetAdviceContext context, int candidateIndex, string? previousKey = null)
    {
        return Select(
            context,
            candidateIndex,
            previousKey is null ? [] : [previousKey],
            PetAdviceSituation.All);
    }

    public PetAdvice Select(
        PetAdviceContext context,
        int candidateIndex,
        IReadOnlyCollection<string> excludedKeys,
        PetAdviceSituation availableSituations = PetAdviceSituation.All)
    {
        var advice = AdviceByContext[context]
            .Where(item =>
                item.RequiredSituation == PetAdviceSituation.None ||
                (availableSituations & item.RequiredSituation) == item.RequiredSituation)
            .ToArray();
        var index = (int)((uint)candidateIndex % (uint)advice.Length);
        var excluded = excludedKeys.ToHashSet(StringComparer.Ordinal);
        for (var offset = 0; offset < advice.Length; offset++)
        {
            var candidate = advice[(index + offset) % advice.Length];
            if (!excluded.Contains(candidate.Key))
            {
                return candidate;
            }
        }

        return advice[index];
    }
}
