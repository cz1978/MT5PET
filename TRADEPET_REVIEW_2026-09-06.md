# TradePet 功能与架构审查

> 实施状态（2026-09-06）：本报告以下正文保留修复前证据和反例。对应修改已实施并通过回归验证：F01–F06、F08–F14 已修复；F07 采用明确的支持边界，完整交易投影仅在 MT5 对冲账户启用，净额/交易所账户继续提供持仓和账户浮亏监控；A01–A03 已完成首轮改造；A04 已抽出可测试的增量/批次交易投影、复盘查询编排/日期范围、服务器时钟、worker 恢复会话、历史同步规划和通知交付门控边界，并建立后台任务监督，后续新增功能仍应继续从 `TradePetRuntime` 迁出。最终验证结果与文件清单记录在 `TODO.md`。

审查日期：2026-09-06。范围：当前工作区源码、主调用链、共享模型、持久化、Python worker、MQL5 Bridge、WPF 绑定和相关测试。

结论：项目已有可用的分层基础，但数据完整性、每日盈亏口径和实时状态恢复存在缺陷，会造成漏统计、误提醒、漏提醒。应先修正这些问题，再继续扩展复盘指标或界面功能。没有必要整体重写，也没有必要引入微服务。

本次没有修改业务代码、真实数据库或便携发布目录，没有连接真实账户制造交易。以下“离线复现”是调用当前程序集或使用 FakeApi 得到的结果；“静态确认”表示由具体源码路径确认；并发、耗时和桌面生命周期风险另行标注，未宣称已经在实盘现场发生。

验证结果：.NET Core 61/61、Infrastructure 22/22、Python 6/6 通过；WPF Release 构建 0 warnings / 0 errors。额外执行六组合成场景，见文末记录。现有测试通过不能证明下面这些未覆盖路径正确。

优先级：P1 = 会破坏数据正确性、提醒可靠性或应用稳定性，应优先修复；P2 = 功能闭环、适用范围或维护性缺陷，应在稳定性修复后处理。同一根因在功能和架构分析中会交叉引用，不重复计算为独立故障。

## 功能缺陷

### F01 · P1 · MT5 查询失败被当成空数据【离线复现】

- 位置：[worker:59](python/tradepet_mt5_worker.py:59)、[worker:235](python/tradepet_mt5_worker.py:235)、[worker:369](python/tradepet_mt5_worker.py:369)。
- `positions_get()`、`history_deals_get()` 的结果经 `or ()` 转为空集合，错误和真正的空账户无法区分。MT5 官方明确规定这两个 API 出错返回 `None`。参见 [positions_get](https://www.mql5.com/en/docs/python_metatrader5/mt5positionsget_py)、[history_deals_get](https://www.mql5.com/en/docs/python_metatrader5/mt5historydealsget_py)。
- 复现：模拟持仓查询返回 `None`，`emit_snapshot()` 仍返回成功并发布 `positions=[]`；模拟历史查询持续失败，增量游标仍前进，年度历史最终被标记 `isComplete=true, sourceCount=0`。
- 影响：持仓列表可能被清空，波动采样被错误结束；恢复后的同一持仓可能再次走开仓路径；历史缺口可能被“同步完成”掩盖。
- 建议：采集结果明确区分成功、有数据、成功但为空、失败；失败时保留上一次有效状态、记录错误和重试，不推进查询游标或完成标记。
- 验收：分别注入持仓、订单、成交查询的单次和连续失败，确认不清仓、不推进进度；恢复后补齐缺失区间。

### F02 · P1 · 今日盈亏混用了“完整交易”和“当日成交”口径【离线复现】

- 位置：[DailyStateCalculator:17](src/TradePet.Core/Trading/DailyStateCalculator.cs:17)、[Runtime:2540](src/TradePet.App/Runtime/TradePetRuntime.cs:2540)。
- “今日已实现”仅累加今天最终平仓的完整交易，而最高值计算又使用当日成交现金收益。两者不是同一条曲线。
- 复现：开 2 手，先平 1 手实现盈利 100，剩余仓浮盈为 0。完整交易仍未结束，因此今日已实现为 0；在高点 100 下，回吐显示 100。实际这笔已实现盈利仍在账户内。
- 跨日同样有问题：昨天部分平仓的收益会在今天最终平仓时一起计入今天；开仓费用也可能延后归属。
- 建议：每日现金盈亏由当天成交及明确分类的费用计算；完整交易模型用于胜率、连亏、持仓时长和交易复盘。目标、日亏损线、高点、回吐必须消费同一个每日盈亏口径，并明确隔夜浮盈亏的日界处理。
- 验收：部分平仓、跨日分批平仓、开仓佣金、隔夜费及入出金场景；今日现金收益可逐项核对，高点和回吐与当前值一致。

### F03 · P1 · 年度完成标记会掩盖停用期间的历史缺口【静态确认】

- 位置：[Runtime:1914](src/TradePet.App/Runtime/TradePetRuntime.cs:1914)、[worker:141](python/tradepet_mt5_worker.py:141)。
- 年度历史只要曾经完成，就不再请求；当前年也如此。worker 新启动默认只追溯最近 7 天。
- 触发：当前年已完成一次同步，随后关闭 TradePet 超过 7 天，期间在 MT5 完成交易。再次打开时，那些既不在旧库、也不在最近 7 天窗口内的已平仓交易没有补齐路径，但 UI 仍可能显示历史覆盖完整。
- 历史范围另外固定为当前年向前 4 年；若产品继续称“全历史”，需要明确这个边界。
- 建议：按账户保存“已确认连续覆盖至哪个时间”，启动从最后成功点带重叠补查；按月/时间段保存覆盖，不把当前年视为永久封闭。提供指定日期范围重同步入口。
- 验收：停用 20 天后回归，与连续运行的最终成交集合一致；当前年、跨年和历史更正均可重新同步。

### F04 · P1 · 重连没有完整的恢复阶段，断线期间的操作可能补弹【部分离线复现】

- 位置：[Runtime:538](src/TradePet.App/Runtime/TradePetRuntime.cs:538)、[Runtime:625](src/TradePet.App/Runtime/TradePetRuntime.cs:625)、[Runtime:666](src/TradePet.App/Runtime/TradePetRuntime.cs:666)、[worker:148](python/tradepet_mt5_worker.py:148)。
- 断线只抑制下一次浮亏提醒，没有重置持仓差异及成交恢复基线。重连首帧仍和断线前快照比较，断线期间形成的开仓、加仓、止损变化可能被当作现在发生；普通成交批次也可能补发旧平仓反馈。
- 离线复现另确认：若账户在断线期间由 A 切为 B，`connect()` 先覆盖账户键，后续 `emit_snapshot()` 看不到这次切换，A 的已见 ticket 和历史队列仍保留。不同服务器 ticket 冲突时会抑制 B 的数据，旧历史任务也会进入 B 的同步过程。
- worker 意外退出/被 watchdog 重启主要发诊断，没有统一的断线事件和会话代次；应用的历史请求状态可能仍认为旧进程正在完成工作。
- 建议：引入连接会话状态 `Disconnected → Recovering → Live`。账户变化统一执行重置；恢复期间先对账并建立快照基线，再开放提醒。新进程使用新会话代次，未确认历史任务重新派发。
- 验收：断线期间开仓/平仓/修改止损、断线切账户、worker 崩溃重启均不补弹旧提醒，同时能补齐数据。

### F05 · P1 · Bridge 缺少终端/账户身份校验，服务器日期也没有失效机制【静态确认】

- 位置：[BridgePipeServer](src/TradePet.Infrastructure/Mt5/BridgePipeServer.cs:11)、[Runtime:480](src/TradePet.App/Runtime/TradePetRuntime.cs:480)、[Runtime:1083](src/TradePet.App/Runtime/TradePetRuntime.cs:1083)、[MQL5:198](mt5/TradePetBridge.mq5:198)。
- 多终端默认竞争同一个管道；应用接收心跳后直接采用其日期、时差和图表 ID，未核对所选终端及事件中的账户。图表对象处理也未验证来源账户。
- 反向亏损带命令携带账户和日期，但 EA 应用命令时只检查 kind/revision，没有校验当前账户。错误终端接入时可能采用另一账户的日期、计划对象或亏损区图形。
- Bridge 断开后 `_serverDateAuthoritative` 仍为 true，日期只由后续心跳推进。跨午夜后可能继续用旧日期累计；从未连过 Bridge 时，已采集的日期偏移又不足以启用大部分日统计与复盘路径。
- 建议：握手绑定终端规范化标识、accountKey、sessionId，双向校验。为服务器时间增加采样时间、可信度和超时状态；正常时间流逝推算日界，失效时显示明确状态，不能无限沿用旧日期。
- 验收：同时打开两个终端、只在非目标终端挂 EA、切换账户、Bridge 断开跨午夜；目标账户数据和图形不得混入其他来源。

### F06 · P1 · 开仓风控依赖快照时序，快速交易和紧接平仓后的重进可能漏判【静态确认】

- 位置：[worker:283](python/tradepet_mt5_worker.py:283)、[Runtime:625](src/TradePet.App/Runtime/TradePetRuntime.cs:625)、[Runtime:770](src/TradePet.App/Runtime/TradePetRuntime.cs:770)、[Runtime:720](src/TradePet.App/Runtime/TradePetRuntime.cs:720)。
- worker 每轮先发持仓快照，再发成交；开仓行为只在快照差异的 `Opened` 路径求值。两次快照之间完成的短交易没有开仓差异事件。
- 刚亏损平仓又立即开仓时，新仓快照可能先于上一仓平仓成交抵达。此时 `_trades` 里还没有最新已完成亏损，冷静期、亏后加量或报复评分可能漏判；成交补到后没有对应的开仓规则补算。
- 建议：用成交序列构建有身份的领域交易事件，快照用于对账和估值。开仓评估需要一致的成交水位；短暂等待或二次确认时使用稳定事件 ID 去重，区分实时迟到与历史恢复。
- 验收：同一采样间隔内开平仓、亏损平仓后立即重进，以及 snapshot/deals 两种到达顺序，最终事实应一致。

### F07 · P1（净额账户）· 反手成交投影错误【离线复现】

- 位置：[TradeProjector:39](src/TradePet.Core/Trading/TradeProjector.cs:39)、[Models:63](src/TradePet.Core/Domain/Models.cs:63)。
- `InOut` 在当前有仓时整笔当作退出；没有拆分平旧仓和开新方向的剩余量。
- 复现：买入 1 手，再卖出 2 手反手。正确状态应仍有卖出 1 手；当前投影得到买入交易已完成、剩余 0。净额反手保持 `POSITION_IDENTIFIER` 不变，不能依赖 positionId 变化规避这个问题。参见 [MT5 持仓属性](https://www.mql5.com/en/docs/constants/tradingconstants/positionproperties)。
- 当前 `MarginMode` 虽然采集，却没有用于选择投影规则或限制支持范围。若只支持对冲账户，应明确拒绝/提示净额模式，不能静默输出错误结果。
- 建议：实现有符号仓量和反手拆分；定义一个 position 生命周期与多个方向交易段的关系，并按此调整 tradeId、复盘元数据、亏损带尝试和提醒键。费用不能在拆分时重复计入。
- 验收：反手、连续反手、部分减仓后反手，以及对冲账户回归；交易段与当前仓量可核对。

### F08 · P1 · 最大次数/最大手数设置没有执行效果【静态确认，手数场景已复现】

- 位置：[Models:133](src/TradePet.Core/Domain/Models.cs:133)、[Runtime:265](src/TradePet.App/Runtime/TradePetRuntime.cs:265)、[DailyStateCalculator](src/TradePet.Core/Trading/DailyStateCalculator.cs:7)。
- `MaximumTrades`、`MaximumLot` 在设置、存储和展示中存在，但全仓搜索未发现消费它们的规则判断。
- 复现：最大手数设为 0.01，当前 1 手，领域计算未产生超限事实。
- `StopLossReminderEnabled/Seconds` 和 `MissingStopLoss` 也只有契约定义，未形成持续无止损提醒路径；现有“撤掉止损”提示不等同于该功能。
- 建议：先确定次数按入场、完整交易还是订单计数，手数按单笔、同方向或总敞口计算，再实现越线和去重提醒。应用维持只读，不要把提醒描述成能阻止 MT5 下单的限制。
- 验收：等于/超过阈值、同方向多仓聚合、加仓、跨日清零、关闭规则以及重启恢复。

### F09 · P2 · 价格区域规则硬编码，不适用于多品种【离线复现】

- 位置：[BehaviorAnalyticsCalculator:578](src/TradePet.Core/Trading/BehaviorAnalyticsCalculator.cs:578)、[MainViewModel:29](src/TradePet.App/ViewModels/MainViewModel.cs:29)。
- 行为分桶统一四舍五入到 1 位小数；亏损区容差默认全局 2，且输入最小为 0.01。它们没有使用品种 point、tick size 或各自配置。
- 复现：EURUSD 入场 1.06、1.08、1.10 都落入 1.1 桶，价格执着评分为 100 并触发。以外汇品种直接使用默认容差 2，也会把极宽价格范围归为同区。
- 建议：增加品种规格与容差策略，使用点数/tick 数换算价格距离；行为分桶与亏损区域共享定义。首次使用未知品种时显示容差单位和覆盖范围。
- 验收：黄金、EURUSD、JPY 货币对和不同报价精度下，相同点数距离得到一致判定；边界附近不因十进制舍入随机分组。

### F10 · P2 · 交易计划和“计划外比例”缺少完整闭环【静态确认】

- 位置：[Runtime:1439](src/TradePet.App/Runtime/TradePetRuntime.cs:1439)、[Runtime:1462](src/TradePet.App/Runtime/TradePetRuntime.cs:1462)、[BehaviorAnalyticsCalculator:114](src/TradePet.Core/Trading/BehaviorAnalyticsCalculator.cs:114)。
- 自动匹配只写 `Matched`，匹配失败直接跳过。正常 UI 没有生成 `OutsidePlan/ManualOutside` 的编辑入口，所以“计划外比例”缺少真实的分子来源，容易长期显示 0。
- 结构化计划只有创建和列表展示，缺少编辑、失效/归档、逐笔人工归类闭环。新增计划仅验证数字能否解析，未验证买卖方向的止损/目标顺序或参考价是否在区间内。
- 建议：区分“有适用计划但未命中”“没有计划/信息不足”，不要都显示正常；提供计划版本与失效操作、逐笔计划内外确认、策略/标签编辑。交易应绑定开仓时有效的计划版本。
- 验收：同日有计划的区内/区外交易、无计划交易、错误止损方向、计划后改、人工修正后重同步。

### F11 · P2 · 历史复盘筛选会改变“今日行为”卡【静态确认】

- 位置：[MainViewModel:399](src/TradePet.App/ViewModels/MainViewModel.cs:399)、[MainWindow:458](src/TradePet.App/Views/MainWindow.xaml:458)、[Runtime:1693](src/TradePet.App/Runtime/TradePetRuntime.cs:1693)。
- 复盘选定周期的 `BehaviorRiskText/BehaviorTriggeredText` 同时用于今日概览；没有独立今日行为快照。
- 触发：查询以前一段严重违规的日期后返回今日，概览仍可能呈现历史周期的风险；换成无交易筛选又可能显示没有触发规则。默认近 30 天也不等于今天。
- 建议：拆分 `TodayBehavior` 与 `ReviewBehavior`，今日以当前账户、当前服务器日期和实时事实求值，复盘筛选只影响复盘页面。
- 验收：今日保持固定时，任意改变复盘日期、品种、方向，都不改变今日风险卡。

### F12 · P2 · 实际风险倍数没有数据生成路径，“总成交量”标签与计算不符【静态确认】

- 位置：[Runtime:2115](src/TradePet.App/Runtime/TradePetRuntime.cs:2115)、[ReviewAnalyticsCalculator:82](src/TradePet.Core/Trading/ReviewAnalyticsCalculator.cs:82)、[MainViewModel:329](src/TradePet.App/ViewModels/MainViewModel.cs:329)。
- `InitialRiskAmount`、`ActualRiskMultiple` 创建时为 null，后续采样也没有赋值路径。现在“实际 R”“MAE/MFE 的 R 倍数”不是多采样一会儿即可出现的数据。
- “总成交量”实际合计每笔交易的首笔开仓量，不含后续加仓，也不含退出成交，与标签含义不一致。
- 建议：在开仓时冻结初始止损和可靠的账户货币风险估值，再计算 R；无法可靠估值时说明原因。成交量明确区分首开量、累计入场量、双边成交量，按实际产品口径命名。
- 验收：有/无止损、修改止损、加减仓、多币种和汇率不可用；示例中首开 1 手再加 1 手，首开量=1、累计入场量=2，不混为一个指标。

### F13 · P2 · Bridge 全量图表快照没有消费，重连后导入可能缺对象【静态确认】

- 位置：[MQL5:405](mt5/TradePetBridge.mq5:405)、[MQL5:423](mt5/TradePetBridge.mq5:423)、[Runtime:492](src/TradePet.App/Runtime/TradePetRuntime.cs:492)。
- EA 每次重新连接会发 `chart_snapshot`，应用的 switch 没有这个分支；已有且未变化的对象又不会发 `chart_upsert`。
- 应用仅持久化已跟踪/正在记录的对象。若 EA 保持运行、TradePet 重启，尚未导入且未移动的图表对象可能无法进入新的内存对象表，“导入当前图表”就会遗漏。
- 建议：全量快照作为对象同步的权威基线，按终端/图表范围原子替换或对账，再接收增量。发送失败不能先推进 EA 端已同步状态。
- 验收：未导入对象保持不动，只重启 TradePet；对象仍应全部可导入。另覆盖重连期间删除对象和发送失败。

### F14 · P2 · “隔离回放”仍可能写到当前真实账户的提醒记录【静态确认】

- 位置：[Runtime:358](src/TradePet.App/Runtime/TradePetRuntime.cs:358)、[Runtime:2283](src/TradePet.App/Runtime/TradePetRuntime.cs:2283)、[AppDatabase.Review:11](src/TradePet.Infrastructure/Persistence/AppDatabase.Review.cs:11)。
- 回放交易使用 `replay|1`，但调用共用 `ShowAlertAsync()`；后者以运行时当前 `_account.Scope.AccountKey` 写 `alert_deliveries`。
- 因此真实账户已连接且持久化可用时，回放会留下真实账户作用域的演示提醒记录，与完成文案“未写入真实账户数据”不符。固定回放提醒 ID 还会使后续回放被去重。
- 建议：演示使用独立内存存储/独立运行上下文，或注入明确的非持久化通知出口，真实账户上下文不得隐式参与。
- 验收：真实账户场景下执行回放前后，所有真实作用域表内容不变；每次主动回放都可展示。

## 架构缺陷及建议

### A01 · P1 · 历史重算和实时提醒争用同一条处理路径

证据：[Runtime:430](src/TradePet.App/Runtime/TradePetRuntime.cs:430)、[Runtime:720](src/TradePet.App/Runtime/TradePetRuntime.cs:720)、[AppDatabase:214](src/TradePet.Infrastructure/Persistence/AppDatabase.cs:214)。每个成交批次都对内存全部成交重新投影，并逐笔 upsert 全部交易；包括只包含进度或重复成交的批次。单行 upsert 各自打开连接、执行独立 SQL，整个过程还占用 `_stateGate`。两个入站 Channel 都无容量限制。

后果：历史越多，每个新成交的处理成本越大。历史同步/磁盘拥塞会让实时快照和 Bridge 信号排队；无界队列还可能积累过期消息。每秒约 4 次快照另伴随账户、持仓替换和日状态写入。这里确认的是复杂度和排队机制，未做真实硬盘延迟或最大账户量压测。

修改方向：按受影响 position/tradeId 增量投影，历史导入采用批量事务，先保存源成交再更新投影；给持久化设置专门调度。可合并过期估值快照，但成交和领域事实必须可靠保存。增加队列长度、数据年龄、处理延迟和单批写入数量观测。

另需注意，`Microsoft.Data.Sqlite` 的 async ADO.NET 方法实际同步执行，不能靠方法名保证 UI 不阻塞；界面查询应切换到后台工作路径并返回不可变结果。参见 [Microsoft 官方说明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)。

### A02 · P1 · 共享状态所有权和任务生命周期不统一

证据：[Runtime:43](src/TradePet.App/Runtime/TradePetRuntime.cs:43)、[Runtime:1433](src/TradePet.App/Runtime/TradePetRuntime.cs:1433)、[Runtime:2383](src/TradePet.App/Runtime/TradePetRuntime.cs:2383)。worker/Bridge 消费者拿状态锁，但保存计划、设置、部分复盘操作、桌宠生活循环和延迟隐藏泡泡并未统一进入同一状态调度路径。

后果：后台枚举 `_trades/_planItems` 时 UI 或另一个任务可能修改字典；账户切换与复盘查询可能交错；低优先级普通提示可直接覆盖高优先级提醒。Bridge 消费循环只有 finally，没有逐事件异常隔离，映射、写库或管道发送失败可令整个消费者永久退出，而“管道已连接”仍可能保留。

退出还有单独风险：[App:77](src/TradePet.App/App.xaml.cs:77) 在 WPF UI 线程 `.GetResult()` 等待异步 Dispose，后台任务又可能等待 Dispatcher；Dispose 内 await 也可能需要 UI 上下文，形成死锁条件。本次未强制关闭用户运行中的应用验证该风险。

修改方向：应用状态由一个明确的调度器持有，UI 命令也发送给它；耗时 IO 和计算对快照工作，结果携带账户/会话代次再回写。所有后台任务纳入监督、取消和失败状态汇报。关闭窗口前先异步完成有超时的清理，OnExit 只做同步资源收尾。通知服务维护优先级和展示队列。

### A03 · P1 · “已处理/已完成”与持久化成功没有一致提交边界

证据：[Runtime:518](src/TradePet.App/Runtime/TradePetRuntime.cs:518)、[Runtime:677](src/TradePet.App/Runtime/TradePetRuntime.cs:677)、[Runtime:697](src/TradePet.App/Runtime/TradePetRuntime.cs:697)、[Runtime:2661](src/TradePet.App/Runtime/TradePetRuntime.cs:2661)。应用先更新内存序号/状态，再处理业务；`TryPersistLiveAsync()` 吞掉单次失败并返回 false，许多调用者继续执行，历史完成标记也可以在部分成交写入失败后保存。

后果：内存显示已同步、数据库实际缺成交；重启后同一年度被跳过，或从不完整源成交重新投影。另有一些路径直接写数据库，使同一种错误有时降级继续、有时终止任务、有时通过 `async void` 命令成为 UI 未处理异常。账户范围加载失败会将持久化长期关闭，也没有清晰的自动恢复状态机。

修改方向：源事件/成交入库和消费检查点在同一事务提交；派生投影可以重建，但要保存版本与成功水位。通知交付记录与待发送通知需要明确失败语义。统一失败分类、重试、只读/内存降级及恢复探测。界面展示“数据暂存/未持久化/历史存在缺口”，不要只显示笼统诊断。

### A04 · P2 · Runtime 责任过多，最关键的编排却没有测试边界

证据：[TradePetRuntime](src/TradePet.App/Runtime/TradePetRuntime.cs:17) 约 2,774 行，直接构造数据库、worker、Bridge、领域计算器，并持有 UI、账户、历史、图表、计划、提醒、采样和恢复状态。它还直接使用静态 WPF Dispatcher、当前时间和随机数。现有两个 .NET 测试工程覆盖 Core/Infrastructure，没有 App 运行时集成测试工程。

建议保留三层项目基础，逐步拆出：

| 建议职责 | 管理内容 | 首批对应问题 |
| --- | --- | --- |
| AccountSession / ServerClock | 账户、终端、连接代次、服务器时间可信度 | F04、F05 |
| TradeIngestion / HistorySync | 源成交、覆盖区间、重试、检查点 | F01、F03、A03 |
| TradeProjection / DailyLedger | 交易生命周期、每日现金口径、增量投影 | F02、F06、F07、A01 |
| RuleEvaluation / Notification | 统一规则开关、事实去重、恢复抑制、通知优先级 | F04、F06、F08 |
| PlanService / ReviewQuery | 计划生命周期、人工复盘、独立历史查询 | F10、F11、F12 |
| 各页面 ViewModel | 渲染不可变快照、收集经过验证的命令参数 | F11、A02 |

只为真实边界引入接口，例如交易源、仓储、时钟、通知出口；无需给每个纯计算器再套接口。让集成测试能注入 Fake MT5、临时数据库和可控时间，执行真实编排，而不需要运行桌面窗口或连接账户。

## 推荐修改顺序

每轮只实施一个 TODO 项；按照共享契约/类型 → 核心逻辑 → 适配与存储 → 应用集成 → UI → 定向测试和文档的依赖顺序推进。

1. **先建立故障回放测试入口。** 不改业务口径，注入假交易源、临时库、时钟和通知出口，覆盖查询失败、重连和事件乱序。得到可重复的修复验收条件。
2. **修数据不漏、不串。** F01、F03、F04、F05、A03：查询失败语义、历史连续覆盖、会话身份和事务检查点。保留旧原始数据与迁移版本，重新核对受影响历史。
3. **修数字和交易事件。** F02、F06、F07、F09：每日现金账、成交驱动事件、净额模式及品种规格。净额支持若暂缓，先明确限定支持账户模式。历史投影重建要静默，不能补发旧提醒。
4. **修实时性和退出稳定性。** A01、A02：批量/增量处理、队列控制、状态调度、任务监督和异步关闭。用合成大历史验证新成交成本不再随总历史线性增长。
5. **补已有功能闭环。** F08、F10、F11、F13、F14：阈值提醒、计划编辑和归类、今日与复盘分离、图表全量同步、隔离回放。
6. **最后完善高级指标。** F12：可靠的初始风险估值、R 指标、准确的成交量标签；在这之前清楚标出哪些指标尚未实现，而不是统称“覆盖不足”。

修改时必须保持的约定：不下单、不平仓、不改单；账户和终端隔离；历史恢复不补弹旧提醒；空数据与采集失败不同；事件时间与接收时间不同；源成交可以重放、投影可按版本重建；历史查询不能改变实时风控状态。Bridge 绘图属于允许的图表副作用，应单独列入协议能力。

## 离线复现记录

| 场景 | 输入或故障 | 当前实际输出 |
| --- | --- | --- |
| 部分平仓和手数上限 | 首开 2 手，平 1 手获利 100，剩余浮盈 0；手数上限 0.01 | 已实现 0、回吐 100、超限事实为空 |
| 净额反手 | Buy In 1；Sell InOut 2；同一 positionId | RemainingVolume=0、IsComplete=true、Side=Buy |
| 外汇价格分桶 | EURUSD 在 1.06/1.08/1.10 入场 | PriceFixationScore=100、Triggered=true |
| 持仓读取失败 | mock positions_get() 返回 None | emit_snapshot()=true、positions=[] |
| 历史读取失败 | mock history_deals_get() 持续返回 None | 增量游标前进；年度 isComplete=true、sourceCount=0 |
| 断线期间切账户 | A 已见 ticket 和历史队列；断线后切 B，再 connect/snapshot | A 的已见 ticket 与历史任务仍保留 |

复现方式：前三组通过 PowerShell 加载本次构建的 `TradePet.Core.dll`，直接调用实际领域类；后三组使用项目 `FakeApi` 与 `unittest.mock.patch`，只替换 MT5 查询返回值。未调用真实 MT5 采集或交易函数，未新增业务测试代码。

复现对应的回归断言应在实施时加入正式测试，而不是只保留本文的结果。

## 验收边界与同步记录

- 已检查和验证：核心/持久化现有测试、Python 现有测试、WPF 编译、六组合成反例、关键调用链与外部 API 返回值契约。
- 尚未实测：真实多终端同时运行、实际 Bridge 断线跨日、WPF 退出死锁触发、数据库锁争用和大历史下的端到端延迟。有关这些情境的条目是有源码依据的风险，不是实盘事故报告。
- 本次改动：新增本报告；更新 `TODO.md` 记录审查完成。业务源码和真实账户数据未改动。
- 下一项建议 TODO：建立可注入的采集/恢复集成测试入口，然后优先修 F01。本文列出的修复尚未实施。
