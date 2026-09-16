# TradePet

> 陪你交易，不替你交易。

TradePet 是一款面向 Windows 和 MetaTrader 5 的本地交易桌宠。它以只读方式连接 MT5，把持仓、风险、交易计划、复盘、宏观事件和每日报告收进一个不打扰的桌面助手里。

TradePet 不下单、不平仓、不改单。它关心的是交易者最容易忽略的事：是否按计划执行、是否重复在同一区域亏损、盈利是否正在回吐，以及今天到底做对了什么。

## 核心特点

### 只读连接，不触碰交易权限

- 只读取 MT5 账户、持仓、订单、成交历史、行情和品种规格。
- MQL5 Bridge 用于传递图表对象、交易变化和 MT5 内置经济日历，不调用交易函数。
- 所有交易数据默认留在本机，无需云端账号或第三方数据密钥。

### 桌宠式持仓快览

- 桌宠默认停在桌面右下角，可调整大小、透明度、固定和鼠标穿透。
- 浮动持仓卡展示实时盈亏；支持折叠、隐藏，并可从宠物右键菜单恢复。
- 平仓后弹出快速复盘，自动分析退出原因，同时支持快捷修正和手动输入。

### 盘中风险与纪律提醒

- 跟踪已实现盈亏、浮动盈亏、日内高水位和利润回吐。
- 支持每日开仓次数、同品种同方向仓位上限、缺失止损和冷静期提醒。
- 可从 MT5 图表对象导入入场、止损、目标与组合风险计划。
- 所有提醒只提供事实和复盘线索，不会自动改变持仓。

### 亏损区域记忆

TradePet 会把完整亏损交易投影为可追溯的价格区域，记录进入次数、累计损失和当前价格位置。当交易者再次接近曾经反复亏损的地方时，桌宠会给出上下文提醒，而不是只显示一个红色数字。

### 完整的交易复盘工作台

- 总览、日历与日记、交易档案、统计分析、策略与改进、机会记录六个入口。
- 每笔交易保留原始成交、盘前/盘中/盘后笔记、执行评价、行为标签、附件和真实 bars/ticks 回放。
- 支持保存筛选、多标签 AND/OR、复盘队列、批量归类和需重审状态。
- 区分完整交易、现金流和净值口径；将费用、实际 R、MAE/MFE 与数据覆盖度分开展示，不用缺失数据补零。
- 策略规则和改进目标保留版本，避免日后修改规则时篡改历史。

### 每日交易报告

- 可设定每日自动弹出时间，时间基于 MT5 交易服务器。
- 汇总已实现净盈亏、费用、胜率、最佳/最差交易、计划执行、复盘进度、行为提醒、时间线和宏观事件。
- 自动归档为 Markdown，可另存、留档或一键复制给 AI 做进一步分析。

默认归档位置：

```text
%LOCALAPPDATA%\TradePet\reports\<account>\<yyyy-MM-dd>-trading-report.md
```

### MT5 宏观日历

- 直接使用 MT5 内置经济日历，展示国家/货币、重要度、前值、预期和公布值。
- 时间统一换算为当前 MT5 交易服务器时间。
- 高重要度事件在 30 分钟和 5 分钟前提醒，数据公布后再提醒。
- 同一时段多条事件会合并展示并标明总数，不会只保留第一条。

## 安装与首次连接

### 运行环境

- Windows 10/11 x64
- MetaTrader 5（当前主要在 WeTrade MT5 验证）
- 从源码构建时需要 .NET 8 SDK、Python 3.13 和 MT5 自带的 MetaEditor

### 从源码构建

```powershell
git clone https://github.com/cz1978/MT5PET.git
cd MT5PET
.\scripts\build-release.ps1
```

发布目录为 `artifacts\TradePet-win-x64`。该目录是自包含的 Windows x64 便携版，不包含 Python 本身。

### 首次连接 MT5

1. 保持 MT5 Terminal 已启动。
2. 运行 `TradePet.exe`。桌宠默认出现在桌面右下角，双击可打开主面板。
3. 如果提示 Python 环境未就绪，在发布目录中运行 `Runtime\setup-python.ps1`，完成后重启 TradePet。
4. TradePet 会将 `TradePetBridge` 安装到当前 MT5 数据目录。首次使用时，在 MT5 导航器中将它拖到任意图表并允许 EA 运行。
5. 顶部状态从“Bridge 已安装，等待挂图”变为“Bridge 已连接”后，服务器交易日和宏观日历即可工作。

如果存在多个 MT5 终端，可在“设置”中选择目标终端。

## 数据、隐私与备份

- 数据库、滚动日志、日报和复盘附件保存在 `%LOCALAPPDATA%\TradePet`。
- 业务数据按券商服务器、登录账号和 MT5 服务器交易日隔离。
- 公开分享 ZIP 会隐藏账户键和本机路径；完整本地备份包含 SQLite、附件和 SHA-256 清单。
- 恢复前会检查路径、文件哈希、数据库完整性和附件引用。
- 行情缓存可单独清理，不会删除人工笔记、截图、策略或改进目标。

## 账户模式与统计口径

完整交易投影、亏损区域和逐笔复盘目前面向 MT5 对冲账户（`ACCOUNT_MARGIN_MODE_RETAIL_HEDGING`）。净额和交易所账户仍可使用持仓、账户浮盈亏及账户级风险监控，界面会明确标记“仅监控持仓”，不生成可能失真的完整交易统计。

统计不会把缺失数据当成零：例如，实际 R 只统计开仓时已有止损且 MT5 能可靠估值初始风险的交易。

## 开发与验证

```powershell
dotnet test TradePet.sln --configuration Release
.\.venv\Scripts\python.exe -m unittest discover -s python -p "test_*.py" -v
dotnet run --project tools\TradePet.ReviewBenchmark\TradePet.ReviewBenchmark.csproj --configuration Release -- artifacts\review-benchmark.json
.\scripts\build-bridge.ps1
```

## 项目状态

TradePet 目前是持续迭代中的个人工具，主要在 Windows x64、WeTrade MT5 和对冲账户上验证。使用其他券商、MT5 构建或账户模式时，欢迎通过 GitHub Issues 反馈兼容性问题。

## 许可证

当前仓库尚未附加开源许可证。在 `LICENSE` 文件落地前，代码可公开阅读，但不代表已授权复制、修改或再分发。
