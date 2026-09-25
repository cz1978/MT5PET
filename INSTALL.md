# TradePet 安装与升级说明

适用于 **1.0.0-rc.3 / Windows 10、11 x64**。当前是候选版；MT5 提供完整功能，MT4 提供实时监控。TradePet 只读采集，不执行下单、平仓或改单。

## 下载并启动

1. 打开 [GitHub 发布页](https://github.com/cz1978/MT5PET/releases/tag/v1.0.0-rc.3)。
2. 在 Assets 中下载 `TradePet-1.0.0-rc.3-win-x64.zip`。`Source code` 是开发用源码，不是可运行安装包。
3. 将 ZIP **完整解压**到自己的应用目录，例如 `D:\Apps\TradePet-1.0.0-rc.3`。
4. 运行解压目录内的 `TradePet.exe`，跟随四步设置向导完成配置。不要在压缩包内直接运行，也不要只复制 EXE。

便携包已包含 .NET 8 运行时，无需安装 .NET SDK。保留同目录的 DLL、`Runtime` 和 `Assets` 文件夹。

发布页还提供 `SHA256SUMS.txt`。需要核对下载完整性时，在 ZIP 所在目录执行并与该文件比较：

```powershell
Get-FileHash .\TradePet-1.0.0-rc.3-win-x64.zip -Algorithm SHA256
```

该校验用于确认文件一致性，不代替数字签名。当前包未做代码签名；遇到 Windows 提示时先核对来源及哈希，不要关闭系统防护。

便携包中的 `LICENSE` 是 TradePet 的 MIT 许可证。允许个人使用、商用、修改和分发，包括收费分发；分发时须保留版权声明和许可声明。第三方组件适用各自许可证。`rc.3` 相比 `rc.2` 仅更新许可、文档和版本信息，安装与连接方式相同。

## 连接 MT5

### 1. 准备终端和 Python

- 安装并启动自己的 MetaTrader 5，登录要监控的账户。
- 安装 **64 位 Python 3.13**。便携包不包含 Python 安装器。
- 向导中选择“MT5”，选中对应 `terminal64.exe`。未自动发现时点击“浏览终端”。

每次只连接一个终端。需要完整交易复盘时，请使用 MT5 对冲账户；净额和交易所账户提供持仓及账户级风险监控。

### 2. 准备采集环境

在向导中检测 Python。缺少依赖时点击修复；自动路径不匹配时，选择已安装的 Python 3.13 的 `python.exe`。修复需要联网下载 `Runtime\python\requirements.txt` 中固定版本的依赖。

修复会使用 `%LOCALAPPDATA%\TradePet\python\venv` 作为专用 Python 环境。也可在解压目录手动运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Runtime\setup-python.ps1 -PythonPath 'C:\路径\Python313\python.exe'
```

请将示例中的 Python 路径替换为实际安装位置。

### 3. 安装只读插件

1. 在向导中点击“安装 / 更新只读插件”。
2. 切回所选 MT5，在“导航器 → EA / 专家顾问”列表右键刷新。
3. 将 `TradePet / TradePetBridge` 拖到一个图表，并保持终端和图表打开。
4. 检查助手连接状态。图表对象和经济日历需要桥接插件连接成功。

插件安装位置为所选终端数据目录的 `MQL5\Experts\TradePet`。若未出现插件，在终端“文件 → 打开数据文件夹”核对是否选中了同一个终端。

### 4. 保存并检查

设置桌宠偏好后点击“保存并完成设置”。若刚更换平台、终端或修复 Python，从桌宠/托盘菜单退出助手后重新启动。保存设置只表示配置已记住，不代表已经连接成功。

首次历史同步需要时间，完成前统计可能尚不完整。请以控制台的连接、桥接与历史同步状态判断进度。

## 连接 MT4

1. 启动 MetaTrader 4 并登录账户。
2. 在向导中选择“MT4”，选中对应的 `terminal.exe`；MT4 不需要 Python。
3. 点击“安装 / 更新只读插件”。
4. 在 MT4“导航器 → EA / 专家顾问”中刷新，将 `TradePet / TradePetBridge` 拖到 **一个** 图表，并保持图表打开。
5. 检查状态、保存设置；切换平台或终端后重新启动助手。

MT4 插件位于终端数据目录的 `MQL4\Experts\TradePet`，通过 `MQL4\Files` 中的本地快照传递数据，无需 DLL 或“允许实盘交易”。不要在多个图表同时运行本插件，以免重复写入快照。

MT4 当前支持账户、持仓、挂单、浮亏监控。成交历史、完整复盘、图表计划、经济日历和日报尚未接入，相关指标显示“—”；当前也不显示持仓时长。数据超过 10 秒未更新、终端断线或插件被移除时显示过期状态。

## 日常使用与快速复盘

- 右键桌宠或系统托盘图标，可打开控制台、交易计划、复盘分析、设置等入口。
- MT5 平仓后快速复盘只进入待处理队列，**不会自动弹窗**。空闲时右键选择“快速复盘”逐笔处理。
- 快速复盘不强制置顶、不自动抢焦点；“稍后”会在 10 分钟后重新入队，不会自动打开窗口。
- 快速复盘队列保存在当前进程内。退出后，历史交易仍可在“复盘分析 → 交易档案”查看并补写；保存过的复盘不会因退出丢失。
- 没有本次待处理交易时，“快速复盘”会进入复盘分析页。自动分析的原因和改进建议均可修改。
- 设置向导可从“设置中心 → 打开设置向导”重新进入。

## 从旧版升级

1. 保存正在编辑的笔记，从桌宠或系统托盘右键菜单选择“退出天禄交易助手”。关闭控制台窗口可能只是隐藏窗口，不等于退出。
2. 在助手退出后，备份整个 `%LOCALAPPDATA%\TradePet` 文件夹，保存数据库、附件、设置、日志和日报。
3. 将新版 ZIP 解压到 **新目录**，运行其中的 `TradePet.exe`。应用会继续使用原来的本地数据目录。
4. 旧版首次升级可能显示设置向导；核对平台、终端和偏好并保存。按需点击“安装 / 更新只读插件”，在终端移除旧图表上的插件并重新挂载。
5. 若切换了连接配置或修复 Python，完成向导后重新启动助手。检查持仓、连接和历史同步状态正常后，再清理旧程序目录。
6. 若使用了桌面快捷方式或开机启动，更新为新版路径；开机启动可在新版设置里关闭再开启。

不要删除 `%LOCALAPPDATA%\TradePet` 来“卸载旧版”，否则会丢失本地数据。需要回退时退出新版，使用升级前的完整数据备份配合旧版程序恢复，避免混用数据库版本。

## 常见问题

| 现象 | 处理方式 |
| --- | --- |
| 启动后没有新窗口 | 检查托盘是否已有助手。应用限制单实例；先退出旧进程，再运行新版。 |
| 找不到终端 | 先启动并登录终端，再刷新或浏览实际 EXE；确认 MT4 对应 `terminal.exe`，MT5 对应 `terminal64.exe`。 |
| 保存的终端已不存在 | 重新选择终端并保存。助手不会自动换到其他终端。 |
| MT5 Python 检测失败 | 选择 64 位 Python 3.13，联网修复依赖后重启助手；检查 `%LOCALAPPDATA%\TradePet` 下的日志。 |
| 只读插件文件不完整 | 重新完整解压便携 ZIP，确认 `Runtime\TradePetBridge.ex5` 和 `Runtime\mt4\TradePetBridge.ex4` 存在。 |
| 等待桥接插件 / MT4 数据过期 | 确认插件挂在所选终端的图表上，终端已登录且保持运行；MT4 只挂一个图表。 |
| MT4 历史和日报为空 | 当前版本不支持，不是连接故障；实时持仓和浮亏仍可使用。 |
| 升级后仍自动弹快速复盘 | 确认旧进程已退出，快捷方式指向新版目录；仅修改源码或解压文件不会更新正在运行的进程。 |

反馈问题时可在 [GitHub Issues](https://github.com/cz1978/MT5PET/issues) 提供版本号、Windows 版本、终端平台、复现步骤和已脱敏日志片段。不要上传完整数据库、账号信息或原始交易截图。

## 从源码构建

开发机需要 Git、.NET 8 SDK，以及 MT4/MT5 自带的 MetaEditor 编译器。运行 MT5 采集还需要 64 位 Python 3.13。

```powershell
git clone https://github.com/cz1978/MT5PET.git
cd MT5PET
.\scripts\build-release.ps1 `
  -Mt5MetaEditorPath 'C:\你的MT5目录\MetaEditor64.exe' `
  -Mt4MetaEditorPath 'C:\你的MT4目录\metaeditor.exe'
```

脚本编译两种只读插件并发布 .NET 应用，默认输出到 `artifacts\TradePet-win-x64`。可传 `-OutputDirectory 'D:\Builds\TradePet-win-x64'` 改变输出位置，避免覆盖正在运行的程序。

不传编译器参数时，MT5 默认使用 `C:\Program Files\WeTrade MetaTrader 5 Terminal\MetaEditor64.exe`；MT4 从 Program Files 查找。完整发布包需要两种编译器，即使运行时只选择一种平台。

```powershell
dotnet test TradePet.sln --configuration Release
```

Python 采集测试可使用已安装的 Python 3.13 执行：

```powershell
python -m unittest discover -s python -p "test_*.py" -v
```
