# 功能门控 / 配置 / 实验清理审计（2026-11-22）

> **结论先行**
>
> 本仓库**不存在 Statsig**：没有 Statsig SDK、项目、清单、API Key、层级（layer）、动态配置或实验对象，也没有任何第三方功能开关 SDK（LaunchDarkly / Optimizely / Unleash / GrowthBook / Split 等）。因此“Statsig 云端资源占用（含 CPU）”为 **0**，没有可下线的云端门控。
>
> 按同一审计口径（过期、固定不变、已废弃、已全量发布、间接引用、生产值、调用点、评估成本）审查了本仓库**真实存在的**门控/配置面。可立即安全移除的只有 **2 个小对象**；另有 **1 处可观测性清单漂移**应修复而非删除，**3 个对象**需要产品/ADR 决策，其余全部有明确的生产用途或回滚契约，**不得移除**。

- 审计对象：工作树（HEAD `721a3b3` + 36 处未提交改动）。行号以工作树为准。
- 生产遥测：本机 `C:\ProgramData\AutoNet`，日志覆盖 **2026-11-08 ～ 2026-11-22（14 天）**，56,366 条 schema v2 记录、43,725 条恢复决策。
- 已安装生产版本：`0.3.1+6097320…`（来自日志 `buildId`），**不是** HEAD。遥测证明的是“当前安装版的行为”；代码行号来自工作树，两者已在文中分别标注。
- 边界：未修改任何线上配置、服务、注册表、私有目录或凭据；未执行真实网络测试；`C:\ProgramData\AutoNet\private` 因权限拒绝未读取。

---

## 1. Statsig 项目与清单审计：空集

| 检查项 | 方法 | 结果 |
|---|---|---|
| `statsig` 字样（全仓库，含文档/脚本/配置） | `grep -ri statsig` | **0 命中** |
| 其他实验/开关 SDK | `statsig\|launchdarkly\|optimizely\|unleash\|flagsmith\|split.io\|growthbook` | 仅 `artifacts/validation/cdp-profile/**` 的 Chromium 第三方过滤清单提到 amplitude/optimizely 域名，属于浏览器拦截名单，**不是集成** |
| NuGet 包引用 | 全部 `.csproj` 中 `PackageReference` | 7 个包：`ProtectedData`、`ServiceController`、`Hosting.WindowsServices`、`Avalonia`×3、`WebView2`。**无开关/实验 SDK** |
| 基础设施/清单文件 | `git ls-files` 中 terraform/bicep/pulumi/appsettings/flags/experiments | **0 个** |
| 云端 API Key / 环境变量 | 环境变量全量枚举 | 仅 `AUTONET_QA_*`（本地 QA）与 `AUTONET_CAPTURE_PATH`，**无 Statsig Key** |
| 门控评估的网络调用 | 搜 HTTP/客户端构造点 | 只有校园门户、外网探针、可选的本机代理/UU 诊断；**无开关服务出站调用** |

**云资源结论**：无 Statsig 项目、无云端配置、无边缘/计费资源；该部分的 CPU 与网络成本均为 0。报告中后续的“评估成本”只针对本仓库自己的设置与策略判定。

---

## 2. 实际门控/配置面全量清单

口径：`生产值` 来自 `status.json`、14 天日志遥测或代码默认值；`评估成本` 来自服务自报的 `CycleTrace` 阶段（见 §7）。本仓库没有“按请求/按用户”评估的开关，只有**每轮后台循环读取一次**的本地配置与纯策略判定。

### 2.1 用户可见开关（持久设置在 `private/settings.json`）

| 对象 | 类型 | 默认 | 生产值（证据） | 定义 | 生产调用点 | 评估成本 | 结论 |
|---|---|---|---|---|---|---|---|
| `Settings.Enabled` | 总开关 | `true` | 43,723 次 `true`；**1 次 `false`**（2026-11-21T12:07:30Z，`RecoveryDisabled`） | `Settings.cs:15` | `MonitorWorker.cs:214,288,327,621,806,824`；`SubmissionReview.cs:32`；`AuthChannelWorkflow.cs:49`；`AuthChannelWindow.cs:329` | `SettingsRead` 每轮一次 | **保留**。真实用户使用过暂停；不能因“几乎总是 true”删除 |
| `Settings.AllowActiveDisconnect` | 安全子开关 | `true` | 43,723/43,723 次 `true`（14 天） | `Settings.cs:16` | `MonitorWorker.cs:288,328,806,824,1175`；`RecoveryPolicy.cs:112,123` | 同轮设置读取，纯布尔 | **保留**。这是破坏性动作的独立安全阀；遥测只证明“本机未关”，不证明可删 |
| `Settings.KeepAwakeOnAc` | 电源子开关 | `true` | `status.json` = `true` | `Settings.cs:20` | `MonitorWorker.cs:214` → `UpdatePower`（`:897`） | 每轮一次布尔与电源请求 | **保留**。用户可关；与 `Enabled` 组合 |
| `Settings.AuthChannelMode` | 三态枚举 | `ManualOnly` | `status.json` = `ManualOnly` | `Settings.cs:25,67-74` | `RecoveryPolicy.cs:51-55`；`MonitorWorker.cs:162,571-580,1189-1190`；`AuthChannelWorkflow.cs:49`；`AuthChannelLauncher.cs:113,154`；`AuthChannelWindow.cs:329`；`Program.cs`（AuthChannel）`:90` | 每轮字符串比较 | **保留**。`Automatic` 是显式 opt-in 的降级路径，不是“已全量”；`Off` 是用户安全阀 |
| `Settings.PortalOrigin` | 固定值配置 | `https://login.csust.edu.cn` | 经 `Validate` 后只能等于固定主机/443/根路径；**设置页不编辑该字段**（`SettingsView.cs` 无写入点） | `Settings.cs:19,31,60-61` | `MonitorWorker.cs:248,437,686,818`；`AuthChannelWorkflow.cs:72`；`NetworkDiagnostics.cs:32`；`UnblockAuthentication.cs:73`；`AuthChannelWindow.cs:329`；`Program.cs`（Service）`:23` | 每轮 `Uri` 解析 + 校验，**从未达到 500 ms** | **候选 F（需决策，低优先）**：值与常量完全重复，见 §6 |
| `Settings.ProxyUrl` / `UuEndpoint` | 可选诊断 | `""` | `status.json`：`ProxyUrlRedacted=true`、`UuEndpointRedacted=true`，展示值为空 → **已配置但含隐藏部分** | `Settings.cs:21-23,45-47` | `DiagnosticsPackage.cs:186`；`ConfigProjection`；诊断页 | 只在诊断/导出读取 | **保留**。仅诊断用途，不参与恢复决策 |
| `Settings.InterfaceId` / `WifiProfile` | 身份绑定 | 无 | `status.json` 有真实值 | `Settings.cs:17-18,29` | `MonitorWorker` 全链路 | 每轮用于 `ReadWifi` | **保留**。不是开关，是选择目标 |

### 2.2 生产二进制内的测试钩子

| 对象 | 激活条件 | 生产默认 | 调用点 | 结论 |
|---|---|---|---|---|
| `AUTONET_QA_SUBMISSION_LOST` | 环境变量 `=1` | 未设置 → 正常提交 | `src/AutoNet.Service/Program.cs:73-78`（ADR-0001 故障注入） | **保留**。唯一的生产内测试钩子；仅环境变量可激活，无云端/CPU 成本。若要缩小生产面，只能在 Release 编译剔除，但会破坏 QA 与生产的同构性，需单独 ADR |

### 2.3 QA 专用环境变量（非生产门控）

`AUTONET_QA_DIR`、`AUTONET_QA_MODE`、`AUTONET_QA_STATUS_PATH`、`AUTONET_QA_MUTEX_SUFFIX`、`AUTONET_QA_LOG_DIRECTORY`、`AUTONET_QA_EXPORT_OUTCOME`、`AUTONET_QA_THEME_PHASE`、`AUTONET_QA_PORTAL_PHASE`、`AUTONET_QA_NOTIFY_PHASE`、`AUTONET_QA_SUBMISSION_LOST`、`AUTONET_CAPTURE_PATH`。

- 入口：`QaEnvironment.FromProcess`（`QaEnvironment.cs:47-90`）在启动时读取 2 个变量；其余按 QA 场景读取。
- 生产成本：普通用户机器上 `AUTONET_QA_DIR` 为空即全部不激活（`QaEnvironment.cs:49-51`），无轮询、无云端。
- `QaMode` 的 15 个模式与 `scripts/qa-avalonia-*.ps1` 一一对应（已逐项核对），**无死模式**。
- **结论：全部保留**。删除等于删除整条受控 QA 证据链。

### 2.4 策略常量与受控词汇（生产引用，非开关）

| 对象 | 生产值 | 定义 | 跨服务消费者 | 结论 |
|---|---|---|---|---|
| `RecoveryPolicy.DisconnectAttemptCap=3`、`StalledRoundThreshold=3`、`AuthenticationAttemptCap=3`、`DisconnectCooldownSeconds=120`、`OnlineResetSeconds=60`、`StalledObservationSeconds=30`、`FastRetrySeconds=2`、`FastRetryWindowSeconds=15`、`BackoffCeilingSeconds=60` | 见左 | `RecoveryPolicy.cs:26-34` | 快照投影 `DisconnectBudgetMax`/`DisconnectCooldownSeconds`/`DisconnectResetAfterSeconds`（`StatusSnapshot`，`RecoveryPolicy.cs:244-249`）→ 托盘只渲染不重写 | **保留**；单源已被测试锁定 |
| `RecoveryPolicy.BrowserFallbackOpenCooldown` / `AuthChannelCooldown` | 10 分钟 | `RecoveryPolicy.cs:41-44` | `MonitorWorker.cs:580` 授予时间；托盘冷却 | **保留**；`AuthChannelCooldown` 是显式别名，防止两处节奏漂移 |
| `RecoveryPolicy.FallbackPortalPageUrl` | `https://login.csust.edu.cn/` | `RecoveryPolicy.cs:62` | `AuthPageModel.cs:73`、`PortalPageOpener.cs:87` | **保留**；由 `Settings.PortalHost` 派生，无重复字面量 |
| `Settings.PortalHost` | `login.csust.edu.cn` | `Settings.cs:13` | `Settings.PortalOrigin` 默认值、`ProxyHostPolicy.cs:16`（`internal const PortalHost = Settings.PortalHost`）、`FallbackPortalPageUrl`、`AuthPageWebViewHost.cs:284-285` → `Settings.IsPortalOrigin` | **保留**；这是全仓库唯一主机名来源 |
| `AuthChannelModes.*` | `Automatic`/`ManualOnly`/`Off` | `Settings.cs:67-74` | 服务策略、AuthChannel 进程、托盘启动器、`status.json` 字符串 | **保留**；三种都有生产分支 |
| `NotificationCodes.*`（12 个） | 见 `RecoveryPolicy.cs:345-367` | 同上 | 服务写入 `status.json` → 托盘按码措辞 | **保留**；12 个全部有生产发射点（逐个计数均 ≥1） |
| `WifiLinkCodes.*`（6 个） / `SubmissionBudgetCodes.*`（6 个） | 见 `NetworkDisplay.cs:7-17`、`SubmissionVisibility.cs:7-23` | 同上 | 服务投影 → 托盘映射中文 | **保留**；`WifiLinkCodes`/`SubmissionBudgetCodes` 各成员均有生产读写 |
| `SubmissionRetryDecision`（8 个） / `PortalFailure`（21 个） / `AuthChannelConclusion`（4 个） | 枚举 | `SubmissionRetryPolicy.cs:3`、`PortalDiagnostics.cs:4`、`AuthChannelResult.cs:4` | 服务决策 → 日志/快照 → 托盘 | **保留**；枚举成员均有分支消费者 |

### 2.5 迁移/兼容/遗留面

| 对象 | 生产状态 | 证据 | 结论 |
|---|---|---|---|
| `ShellContent.LoadAsync` + “后续迁移”脚手架文本 | **生产不可达** | `ShellRoutes.cs:36` 只有 Logs 是 `AsyncLoad`；`App.axaml.cs:303-306` 把它接到 `logsPage.LoadAsync`；`ShellController.cs:66-73` 同步页不调用 `load` | **候选 A（安全移除）**，见 §6 |
| `PageScaffold` / `ScaffoldView` | **仍在使用** | `ShellController.cs:160` 的 `ApplyFailure`；`MainView.axaml.cs:286,344` | **保留**。这是异步页加载失败的受控降级，不是迁移残留 |
| `ShellPages` 注释“由后续迁移工单填充” | 注释过期 | 五个页面均已迁移（`ShellRoutes.cs:26-39`） | **仅注释清理**，无行为影响 |
| `AuthChannelWorkflow.TryResumeAutoAsync` 接缝 | ADR-0019 声明“只在回归与窗口可见的人工等待期间可能被调用” | `CLAUDE.md:140`；`docs/adr/0019-...md:3`；`AuthChannelWorkflow.cs:57-64`；`AuthChannelWindow.cs:383,603-605` | **候选 E（需 ADR 决策，不得机械删除）** |
| 旧版日志渲染（`LogsModel.LegacyDetail`、`ActivityLog.LegacyRecoveryNote`） | 生产 14 天 **0 条**旧 schema | 56,366/56,366 条 `schemaVersion:2`；`LogsModel.cs:247-254,263-290`；`ActivityLog.cs:65-71,118` | **候选 D（时间窗口候选，暂缓）** |
| 旧版认证状态读取（`LegacyFingerprintShape`、`LegacyRecord`、`UnsupportedVersion`…） | 回滚契约要求保留 | `SafetyStateStore.cs:16,56,65-67,98,180-190`；ADR-0020 | **明确不可移除**，见 §5 |
| 旧版 `StatusSnapshot` 字段缺省语义 | 回滚/滚动升级必需 | `RecoveryPolicy.cs:214-317` 大批 `旧版…缺省` 注释；托盘逐字段降级 | **明确不可移除** |

---

## 3. 间接引用图谱（枚举、常量、包装器、别名、动态拼接、跨服务）

| 间接方式 | 实例 | 验证结果 |
|---|---|---|
| **常量别名** | `ProxyHostPolicy.PortalHost = Settings.PortalHost`（`ProxyHostPolicy.cs:16`） | 同源；无第二份字面量 |
| **派生常量** | `FallbackPortalPageUrl = "https://" + Settings.PortalHost + "/"`（`RecoveryPolicy.cs:62`）；`AuthChannelCooldown = BrowserFallbackOpenCooldown`（`:44`） | 同源；无漂移 |
| **枚举 → switch 分发** | `AuthChannelModes`、`ElevatedOperationKind`、`QaMode`、`SubmissionRetryDecision`、`ActionKind` | 逐成员核对：`ElevatedOperationKind` 6 个全部有解析器+执行器+显示名；`QaMode` 15 个全部映射到脚本；`AuthChannelModes` 3 个全部有分支 |
| **动态拼接名称** | `RecoveryPolicy.cs:134` `action + "Cooldown"`、`:136` `action + "Required"` | 生成 `ReconnectWifiCooldown` / `AuthenticateCooldown` / `DisconnectWifiCooldown` 与对应 `Required`；`LogSummary.AllowedReasons` **恰好覆盖**（4 个条目只能由拼接产生，静态 grep 会误报为“无生产者”） |
| **enum → 字符串 → 跨服务** | `MonitorWorker.cs:648` `NextAttemptAction = policy.NextAttemptAction.ToString()` → `status.json` → `AuthPageModel.cs:153` 与 `nameof(ActionKind.Authenticate)` 比较 | 契约稳定；新增枚举成员时旧托盘只按 `None` 降级 |
| **受控词汇 → 跨服务 JSON** | `NotificationCodes` / `WifiLinkCodes` / `SubmissionBudgetCodes` 写入 `status.json`，托盘按码措辞 | 服务端只发码；托盘 `IsKnown` 守卫；旧版本缺字段时降级，不猜测 |
| **包装器 / 共享校验** | `Settings.IsPortalOrigin` 同时供设置校验、AuthChannel 导航、WebView2 导航门（`AuthChannelWindow.cs:570`、`AuthPageWebViewHost.cs:284-285`） | 单规则；无平行实现 |
| **依赖注入接缝** | `IMonitorEnvironment`（`MonitorEnvironment.cs`） | 生产/测试各一实现；门控逻辑不直接触达 OS |
| **动态装配** | `ShellRoutes.All` → `ShellController.SelectAsync` 分发（`ShellRoutes.cs:26-39`） | 5 页各出现一次，有回归约束；据此证明 `ShellContent.LoadAsync` 不可达 |

**常量核对结果**：工作树中 `const` / `static readonly` 去重后共 230 个常量名。按“名称在 `src` 中只出现于声明处”筛查出 3 个：
- `ShellAccessibility.NavList` / `StatusStrip`：最初误判为死常量，实际由 `MainView.axaml:17,29` 的 `{x:Static}` 使用 → **在用**。
- `AuthPagePointerPolicy.BlindSpotNote`（`AuthPagePointer.cs:117`）：生产代码只声明、不发射；仅测试读取（`AuthPagePointerRegression.cs:227`）→ **无生产引用**（候选 B）。

除此之外，未发现无生产引用的常量。

---

## 4. 生产遥测与评估成本（CPU / 阶段耗时）

### 4.1 数据来源

- `C:\ProgramData\AutoNet\logs\autonet-YYYY-MM-DD.jsonl`，14 天，56,366 条，**全部** `schemaVersion:2`。
- `status.json`（2026-11-22 09:48 UTC）：`CycleMilliseconds=416.8`，`SlowestStage=WifiRead`，`SlowestStageMilliseconds=410`。
- `scripts/analyze-cycle-timing.ps1 -Days 8`（只读，不启停服务）：67 条 `SlowCycle`、2,474 条 `SlowStep`。
- **没有** CPU 采样/火焰图/性能分析文件；本仓库唯一的性能遥测就是服务自报的阶段耗时。以下不把耗时外推为 CPU 归因。

### 4.2 慢阶段排行（8 天内累计）

| 阶段 | 次数（≥500 ms） | 累计 ms | 最大 ms | 性质 |
|---|---:|---:|---:|---|
| `InternetProbe` | 2,006 | 3,012,403.5 | 2,108.2 | 外网内容探针（网络 I/O） |
| `PortalInspection` | 223 | 312,673.3 | 1,542.5 | 门户只读检查（网络 I/O） |
| `WifiRead` | 169 | 160,403.5 | 4,867.9 | 原生 Wi-Fi 查询（系统调用/驱动） |
| `StatusWrite` | 64 | 56,842.6 | 3,336.4 | 原子写 `status.json`（文件 I/O/AV 竞争） |
| `AuthenticationTotal` | 3 | 7,394.4 | 2,481.5 | 认证动作（网络/表单） |
| `SettingsRead` | 5 | 4,960.1 | 1,658.5 | 读取设置文件（文件 I/O 尖峰） |
| `WifiRequest` | 2 | 1,434.5 | 774.3 | 请求连接/断开 |
| `AuthenticationStateRead` | 1 | 653.0 | 653.0 | 读取认证状态 |
| `CredentialsRead` | 1 | 615.5 | 615.5 | 读取凭据 |

### 4.3 结论：没有“高开销的无效门控检查”

- `SettingsValidation`（每轮执行 `Settings.Validate`，`MonitorWorker.cs:163`）在 8 天内 **从未达到 500 ms**。
- `SubmissionBudgetRead` 也 **从未达到 500 ms**。
- `SettingsRead` 的 5 次慢事件是文件 I/O 尖峰（最大 1,658.5 ms），不是 CPU 计算；平均轮次里设置读取是毫秒级。
- 因此：**删除任何“门控”都不会带来可测的 CPU 收益**。真正的耗时集中在 `InternetProbe` / `PortalInspection` / `WifiRead` / `StatusWrite`，它们不是开关，而是功能本身；按本次授权范围**不做任何改动**。

### 4.4 生产门控值分布（14 天，43,725 条 `RecoveryDecision`）

| `Enabled` | `HasCredentials` | `AllowActiveDisconnect` | 条数 | 说明 |
|---|---|---|---:|---|
| true | true | true | 43,723 | 绝对多数 |
| false | true | true | 1 | 2026-11-21T12:07:30Z，`RecoveryDisabled`（用户主动暂停） |
| null | null | null | 1 | 单条字段缺失记录，按旧/异常快照处理 |

`KeepAwakeOnAc` 只在 `status.json` 投影（`true`），日志不重复记录；`AuthChannelMode` 同样只投影（`ManualOnly`）。这两项没有足够的分布样本支撑“可删除”的结论。

---

## 5. 明确不可移除的对象（含证据）

| 对象 | 为什么不能删 | 证据 |
|---|---|---|
| 旧版认证状态读取与保守阻断（64 位指纹、无 `Version` 记录、损坏 JSON、未知版本/状态） | ADR-0020 的回滚契约：旧版本必须能安全解释新记录，新版本必须保守解释旧记录；`scripts/check-released-reader.ps1` 与冻结副本逐条比对 | `docs/adr/0020-legacy-state-upgrade-rollback.md`；`SafetyStateStore.cs:16-98,180-190`；`tests/AutoNet.Tests/LegacyStateRegression.cs` |
| `StatusSnapshot` 的旧版缺省语义（`SettingsProjected`、`CredentialsAvailable`、`Submission*`、`WifiLinkCode`、`NextAttemptAction`…） | 滚动升级/回滚期间旧服务写、旧托盘读；缺字段必须降级为“不可确认”，不能猜 | `RecoveryPolicy.cs:214-317`；`AuthPageModel.cs:150-228`；`OverviewModel.cs:55-65,264,377`；`SubmissionVisibility.cs:5-19` |
| `Settings.Enabled` / `AllowActiveDisconnect` / `KeepAwakeOnAc` / `AuthChannelMode` | 用户可见安全阀；生产遥测显示 `Enabled=false` 被真实使用过一次；`AllowActiveDisconnect` 控制破坏性断线 | `status.json`；43,725 条决策分布；§2.1 |
| `AUTONET_QA_*` 全套 | 删除即删除受控 QA 证据链；普通用户机器上不激活 | `QaEnvironment.cs:47-90`；15 个 `QaMode` 与脚本一一对应 |
| `AUTONET_QA_SUBMISSION_LOST` | ADR-0001 的唯一故障注入点；只在环境变量 `=1` 时生效 | `src/AutoNet.Service/Program.cs:73-78` |
| `--toggle` 的显式拒绝 | 启动契约兼容守卫：旧托盘参数不得被静默解释为新语义 | `StartupRequest.cs:20-23`；`tests/AutoNet.Tests/AvaloniaPrototypeRegression.cs` |
| TLS/重定向/代理允许列表等安全常量 | 安全边界，测试锁定 | `ProxyHostPolicy.cs`；`AuthPageBrowserArguments.cs:18-22`；`DirectNetwork.cs` |
| `PageScaffold` / `MainView.ScaffoldView` | 异步页加载失败的受控降级，仍在本生产路径 | `ShellController.cs:160`；`MainView.axaml.cs:286,344` |

---

## 6. 验证过的清理候选项（按可并行工作流分组）

> 分组原则：**文件不相交、可并行提交**。仓库为单人维护（`yuwen`），没有团队花名册；“归属”按项目/模块边界给出。所有候选均**未实际修改**。

### 工作流 A — 托盘壳层：删除不可达的 `ShellContent.LoadAsync`（安全，优先；已实施）

| 项 | 内容 |
|---|---|
| 归属 | `src/AutoNet.Tray.Avalonia`（壳层）／建议负责人 `yuwen` |
| 对象 | `ShellContent.LoadAsync`（`ShellContent.cs:15-28`）及“此页内容将在后续迁移中提供”文本（`:26`） |
| 生产值 | 不可达；从未被调用 |
| 证据 | `ShellRoutes.cs:36`：5 页中只有 `Logs` 是 `AsyncLoad`；`App.axaml.cs:303-306`：`AsyncLoad` 分支固定接 `logsPage.LoadAsync`；`ShellController.cs:66-73`：`SyncProjection` 在调用 `load` 之前 return；认证页分支也 return。因此 `load` 仅在 `Logs` 到达，而 `Logs` 已被显式接管；`else` 分支的 `ShellContent.LoadAsync` 永不执行 |
| 调试/测试调用点 | `AvaloniaShellRegression.cs:124,291`、`LogsPageRegression.cs:327`、`DiagnosticsPageRegression.cs:241`、`OverviewRegression.cs:316`（用默认 loader 期望 `PageScaffold`） |
| 评估成本 | 0（未执行） |
| 改动建议 | 删除 `ShellContent` 类；`ShellController` 的 `load` 参数改为必填（或默认抛 `InvalidOperationException`）；`PageScaffold` 保留给 `ApplyFailure`；测试改用测试内 stub loader |
| 风险 | 低。唯一风险是测试把“脚手架内容”当作通用占位；需同步改测试断言，不改生产行为 |
| 验证 | `dotnet tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll --filter 壳回归`；`pwsh -File scripts/build.ps1` |
| 并行性 | 与 B/C 无文件重叠；可独立 PR |

### 工作流 B — Core：处理仅测试引用的 `BlindSpotNote`（低价值，可选）

| 项 | 内容 |
|---|---|
| 归属 | `src/AutoNet.Core`／`yuwen` |
| 对象 | `AuthPagePointerPolicy.BlindSpotNote`（`AuthPagePointer.cs:117`） |
| 生产值 | 无；生产代码不发射该提示 |
| 证据 | `src` 中仅声明 1 次；唯一读取方是 `AuthPagePointerRegression.cs:227` |
| 评估成本 | 0 |
| 改动建议 | 二选一：(a) 删除常量并把文案移入测试；(b) 保留为“盲区说明”的单一文案源并在文档中引用。**推荐 (b)**：它记录的是“交互检测有盲区”这一安全事实，删除不会减少生产代码，只会减少可读性 |
| 风险 | 极低 |
| 验证 | `--filter 指针` |
| 并行性 | 与 A/C 无重叠 |

### 工作流 C — Core：修复可观测性清单漂移（应修复，不是删除；已实施）

| 项 | 内容 |
|---|---|
| 归属 | `src/AutoNet.Core`（`LogSummary`）／`yuwen` |
| 对象 | `LogSummary.AllowedOperations`（`LogSummary.cs:79-87`）与实际阶段名不一致 |
| 生产值 | 以下阶段会被可读摘要丢弃：`SubmissionBudgetRead`（`MonitorWorker.cs:176`）、`RecheckSubmission`（`:253`）、`AuxiliaryStart`（`:874`）、`AuthenticationTotal`（`:447`） |
| 证据 | `LogSummary.cs:136` 只渲染 `AllowedOperations` 或 `AllowedStages` 中的阶段；上述 4 个不在 `AllowedOperations` 中；`DisconnectBudgetRead` 在清单中且确有生产者 |
| 评估成本 | 仅日志渲染时，非热路径 |
| 改动建议 | 把 4 个实际阶段名加入 `AllowedOperations`（或统一命名），并加一条回归断言“每个 `Trace` 阶段名都能被摘要渲染” |
| 风险 | 低；只影响可读日志摘要，不影响 `status.json` 或恢复决策 |
| 验证 | `--filter 日志`；`pwsh -File scripts/analyze-cycle-timing.ps1` 对比 |
| 并行性 | 与 A/B 无重叠 |

### 工作流 D — 托盘：旧版日志渲染（暂缓，时间窗口候选）

| 项 | 内容 |
|---|---|
| 归属 | `src/AutoNet.Tray.Avalonia`（`LogsModel`）+ `src/AutoNet.Core`（`ActivityLog`） |
| 对象 | `LogsModel.LegacyDetail`（`:263-290`）、`SchemaVersion < 2` 分支（`:254`）、`ActivityLog.LegacyRecoveryNote`（`:70-71,118`） |
| 生产值 | 14 天 56,366 条记录 **全部** schema v2；旧分支 0 命中 |
| 证据 | `grep '"schemaVersion"'` → 56,366 条，全部为 2；0 条缺字段 |
| 评估成本 | 0（未命中） |
| 判定 | **当前不可删**：日志保留 14 天，升级用户仍可能带着 schema v1 日志进入新版本；删除会让这些记录降级为空白/错误文本。建议在“发布版 schema v2 覆盖所有用户 ≥14 天”后，用同一遥测口径复核再删 |
| 风险（若提前删） | 中：升级窗口内旧日志详情页降级 |
| 并行性 | 与 A/B/C 无重叠，但**不应与它们同批发布** |

### 工作流 E — AuthChannel：休眠的恢复接缝（需 ADR 决策，禁止机械删除）

| 项 | 内容 |
|---|---|
| 归属 | `src/AutoNet.Service` + `src/AutoNet.AuthChannel` |
| 对象 | `IAuthChannelBrowser.TryResumeAutoAsync`（`AuthChannelWorkflow.cs:24`）、调用分支（`:57-64`）、实现（`AuthChannelWindow.cs:603-605`） |
| 声明状态 | `CLAUDE.md:140`：“retained for regression but is not reached in the current lifecycle”；ADR-0019：“只在回归与窗口可见的人工等待期间可能被调用” |
| 静态可达性 | `AuthChannelWindow.cs:383` 的 `do/while (Waiting)` 循环是唯一调用者链；`Waiting` 只由该分支产生。若接管在第一轮 `RunOnce` 前发生，则可达；正常生命周期由 `SubmitAsync` 内的人工等待持有 |
| 判定 | **不是安全清理项**。删除会改变 ADR-0019 明确定义的“窗口可见的人工等待”语义，需要新 ADR 或 ADR-0019 修订，而不是按“未使用”处理 |
| 成本 | 仅在接管期间每轮一次布尔检查，可忽略 |
| 并行性 | 与 A/B/C 无重叠，但需单独决策 |

### 工作流 F — Core/Settings：`PortalOrigin` 固值配置（需产品决策，低优先）

| 项 | 内容 |
|---|---|
| 归属 | `src/AutoNet.Core`（`Settings`）+ 诊断/服务/AuthChannel 调用方 |
| 对象 | `Settings.PortalOrigin`（`Settings.cs:19`） |
| 生产值 | 经 `Validate` 只能取固定主机/443/根路径；设置页不编辑该字段；`SettingsView.cs`、`SettingsModel.cs`、`ElevatedSettings.cs` 均无写入点 |
| 证据 | `IsSupportedPortal`（`Settings.cs:60-61`）拒绝其他主机/协议/端口/路径/查询/片段；默认值由 `PortalHost` 派生；所有运行时消费者都只是把它转成 `Uri` |
| 调用点 | `MonitorWorker.cs:248,437,686,818`；`AuthChannelWorkflow.cs:72`；`NetworkDiagnostics.cs:32`；`UnblockAuthentication.cs:73`；`AuthChannelWindow.cs:329`；`DiagnosticsPackage.cs:367`；Service `Program.cs:23`；AuthChannel `Program.cs:95` |
| 评估成本 | 每轮 `Uri` 解析，<500 ms |
| 改动选项 | 保留持久字段（兼容旧版本回滚），但把运行时取值改为派生常量，字段只读/忽略；或彻底删除字段（旧版本读不到时默认同值，行为不变） |
| 风险 | 低（行为等价），但涉及持久 settings 格式与回滚契约；收益也很低 |
| 判定 | **建议不做**，除非同时计划一次 settings schema 清理。列在此处是为了证明已核对，而不是推荐执行 |

---

## 7. 并行执行计划（如获授权）

| 波次 | 工作流 | 文件面 | 冲突面 | 验收 |
|---|---|---|---|---|
| 第 1 波（可并行） | A 壳层死代码 | `ShellContent.cs`、`ShellController.cs`、`App.axaml.cs`、4 个测试文件 | 仅测试文件内部 | `--filter 壳回归` + `scripts/build.ps1` |
| 第 1 波（可并行） | C 清单漂移 | `LogSummary.cs` + 1 个日志回归 | 无 | `--filter 日志` |
| 第 1 波（可并行） | B 常量 | `AuthPagePointer.cs` + 1 个测试 | 无 | `--filter 指针` |
| 第 2 波（需决策） | D 旧日志渲染 | `LogsModel.cs`、`ActivityLog.cs` + 日志测试 | 无 | 升级窗口遥测复核后执行 |
| 第 2 波（需决策） | E 休眠接缝 | `AuthChannelWorkflow.cs`、`AuthChannelWindow.cs` + 回归 | 与 #40/#41 语义相关 | ADR 修订后再动 |
| 第 2 波（需决策） | F `PortalOrigin` | `Settings.cs` + 诊断/服务/AuthChannel | 持久格式 | settings 回滚验收 |

**建议合并顺序**：A → C → B；D/E/F 不进入本轮。A 与 C 都涉及测试文件但文件不相交（A：`AvaloniaShellRegression.cs`/`LogsPageRegression.cs`/`DiagnosticsPageRegression.cs`/`OverviewRegression.cs`；C：`LogRegression.cs`），可并行。

---

## 8. 边界与未执行事项

- 未修改任何线上配置、服务、注册表、计划任务、私有目录或凭据；未执行安装/回滚/清理。
- 未读取 `C:\ProgramData\AutoNet\private`（权限拒绝），因此 `settings.json` 的原始值不可见；生产值来自 `status.json` 投影与服务日志。
- 未执行真实断网/门户提交测试；所有结论均为静态代码核对 + 本机只读遥测。
- 遥测来自已安装版本 `0.3.1+6097320…`，不是 HEAD `721a3b3`；代码行号来自工作树（含 36 处未提交改动）。若要把遥测结论迁移到 HEAD，需在 HEAD 上重新采一轮同类证据。
- `artifacts/**` 下的历史源码副本与 Chromium 过滤清单未纳入生产判定（未跟踪/归档物），其中出现的 amplitude/optimizely 字样不是本仓库集成。

---

## 9. 实施记录（2026-11-22）

| 工作流 | 状态 | 实际改动 |
|---|---|---|
| A 壳层死代码 | **已实施** | 删除 `ShellContent` 静态类及迁移占位文本；`ShellController` 的 `load` 改为必填；`App.axaml.cs:303-306` 对非异步页失败关闭；`PageScaffold` 保留给 `ApplyFailure`；测试改用 `tests/AutoNet.Tests/TestShellLoad.cs` |
| C 清单漂移 | **已实施** | `LogSummary.AllowedOperations` 补齐 `SubmissionBudgetRead`、`RecheckSubmission`、`AuxiliaryStart`、`AuthenticationTotal`；新增回归“日志：脱敏摘要不丢弃实际阶段名” |
| B `BlindSpotNote` | **未改（按建议保留）** | 作为“交互检测盲区”的单一文案源保留；生产不发射，测试引用 |
| D 旧日志渲染 | **未改** | 受升级窗口约束，等 schema v2 覆盖全部用户 ≥14 天后再复核 |
| E 休眠接缝 | **未改** | 受 ADR-0019 约束，需先改设计文档 |
| F `PortalOrigin` | **未改** | 涉及持久 settings 格式与回滚契约，收益低 |

验证：`pwsh.exe -NoLogo -NoProfile -File scripts/build.ps1` → **626/626 passed**（`artifacts/validation/build.json`，commit `721a3b3`，`testExitCode: 0`）；定向过滤“阶段名”2/2、“壳回归”12/12。未修改任何线上配置或服务。

---

## 附录：可复现的核对命令

```bash
# Statsig / 开关 SDK：应为 0 命中
grep -rInE '(?i)statsig' --include='*' . | grep -v '^./.git/'
grep -rInE '(?i)launchdarkly|optimizely|unleash|flagsmith|growthbook|split\.io' --include='*.cs' --include='*.csproj' src tests

# 包引用：应无开关 SDK
grep -rIn 'PackageReference' --include='*.csproj' src tests

# 生产门控分布（只读）
python - <<'EOF'
import json,glob,collections
vals=collections.Counter()
for f in glob.glob(r'C:/ProgramData/AutoNet/logs/autonet-*.jsonl'):
    for line in open(f,encoding='utf-8'):
        try: d=json.loads(line)
        except Exception: continue
        if d.get('eventCode')=='RecoveryDecision':
            g=d.get('diagnostics') or {}
            vals[(g.get('enabled'),g.get('hasCredentials'),g.get('allowActiveDisconnect'))]+=1
print(vals)
EOF

# 阶段耗时（只读，不启停服务）
pwsh.exe -NoLogo -NoProfile -File scripts/analyze-cycle-timing.ps1 -Days 8

# 单测
dotnet tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll --filter 壳回归
```
