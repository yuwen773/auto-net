# #35 额度状态可见化实施记录

2026-10-29 在独立工作树 `D:\Work\code\auto-net-35`、分支 `codex/35-budget-visibility`，基线 `codex/ui-avalonia` 的 `4699c24` 上实施。已核对 GitHub [#35](https://github.com/yuwen773/auto-net/issues/35)、[#32](https://github.com/yuwen773/auto-net/issues/32)、[#34](https://github.com/yuwen773/auto-net/issues/34)、父规格 [#27](https://github.com/yuwen773/auto-net/issues/27) 的正文与评论，并阅读本地 #31/#32/#33/#34 实施记录与 ADR-0013/0014/0015。

#32/#34 仍开放；本轮把它们的本地实现当作依赖，不把本地结果当作完整验收、发布或实网证据。#37/#41 在其他工作树并行实施认证页与设置页；本工作树未包含它们的未提交改动。

## 实现

- Core 新增 `SubmissionBudgetCodes` 受控码（Unused / Available / NearLimit / Exhausted / Reset / Unknown），`StatusSnapshot` 新增额度级别码、重试决定码、已用次数、上限、是否耗尽与最早下次提交时刻。旧版服务不写这些字段时保持空/`null`，不赋予零次或已重置的默认含义。
- `SubmissionRetryPolicy` 暴露 `NextPossibleAt`：只在 Waiting 时给出可信的墙钟时刻；外网在线、时间不可信、执行中、耗尽或已明确结束时保持 `null`，不把不可信时间伪装成下次提交时间。
- `MonitorWorker` 在动作完成后重新投影额度，使本轮预留、结果转换或重置在同一条快照中可见；动作选择仍使用动作前的决定。耗尽通知只在“提交结果未知且额度是停止原因”时发出；明确密码/绑定拒绝或需要人工时不覆盖既有认证暂停语义。
- 概览页新增“认证提交额度”进度行与耗尽标签，认证列在耗尽或即将用尽时说明停止原因。托盘只把受控码映射为中文：即将用尽、已耗尽、已重置、等待重试、执行中、已明确结束、外网暂停、时间不可信分别可辨；未知额度码或重试码只显示固定降级文案。
- `SubmissionBudgetExhausted` 进入 `NotificationCodes` 与托盘受控词表；未知通知码仍无动作、无文案、不落消费记录。
- 旧版快照缺字段时概览显示“额度状态不可确认”，不显示 `0/3`，不据此重置额度或放行提交。
- 本轮不修改额度记录、认证暂停、主动断线额度或提交授权；展示失败不会改变提交决策。

## 审查修复：明确拒绝不再被额度文案掩盖

代码审查（P2）发现：`OverviewModel.AuthColumn` 在 `CredentialsBlocked` 时先判断“额度已耗尽/即将用尽”，再回落到通用暂停文案。第三次提交同时耗尽额度并收到明确密码错误（或第二次提交被绑定拒绝且额度即将用尽）时，认证列会显示额度原因，掩盖“门户明确拒绝”这一真实停止原因；服务侧已有通知不被覆盖的回归，但没有核验概览认证列。

修复：新增 `ExplicitPauseNote`，把 `AuthenticationPersistentState` / `AuthenticationFailure` 中受控的 `CredentialsRejected`、`BindingRejected`、`InteractionRequired` 作为人工处理原因，排在额度文案之前；额度耗尽/即将用尽仍由进度行与“认证提交额度已耗尽”标签独立呈现。存储故障、`SafetyPersistence` 与门户改版信号的既有优先级不变；结果未知且确因额度耗尽停止时，仍显示额度耗尽与只读复检。未改提交授权、额度计算、认证暂停或通知语义。

## 审查修复：切网后额度事实与授权文案

切换其他 Wi-Fi 时提前返回的快照保留本轮已读取的额度次数、上限与耗尽事实；不发布重试决定或下次提交时间。旧待决记录缺少额度时仍显示不可确认，不凭空计为未使用。概览明确当前校园网认证与复检暂停，不声称切网期间继续复检。

`Authorized` 只代表额度和间隔判定通过，概览改为“额度与间隔条件满足”，同时说明认证状态、恢复开关和网络等其他条件仍会影响提交。未改变提交授权和重试决策。

## 审查修复：耗尽文案只在提交待决时声称复检

代码审查发现：额度耗尽的说明无条件写“继续低频只读复检”。第三次提交若收到明确的密码错误或绑定拒绝，持久认证状态已是 `CredentialsRejected` / `BindingRejected`，服务不会继续提交结果复检链，这句话与事实不符；存储故障、旧版快照或未知认证状态也不应被说成正在复检。

修复：新增 `SubmissionRecheckActive`，只有 `CredentialsBlocked`、`AuthenticationPersistentState == SubmissionPending`、失败分类不是 `SafetyPersistence` 且不属于明确人工处理原因时才认定复检链存在。耗尽额度行据此分三种说明：

- 提交结果未知且待决：保留“继续低频只读复检”与重置入口；
- 明确拒绝/人工验证要求：说明额度已用尽、门户明确拒绝或要求人工处理、没有进行中的复检，额度不是唯一停止原因；
- 其他未知或终止状态：说明没有可证实的提交结果复检，不自动重发凭据。

认证列的耗尽分支与显式 `Unknown` 额度码使用同一判据，不再虚构复检。切换其他 Wi-Fi、旧版快照与未知状态继续安全降级；未改提交额度、认证暂停、通知或授权决策。

## 审查修复：复检文案跟随校园 Wi-Fi 与待决状态

代码审查发现两处仍可能虚构复检：

1. `SubmissionRecheckActive` 只检查认证状态，没有检查校园 Wi-Fi 是否可用。额度耗尽、提交待决但 Wi-Fi 断开、等待 IPv4 或网卡未就绪时，服务会跳过复检，概览却仍声称“继续低频只读复检”。
2. `InternetOnline` 重试码无条件追加“继续只读复检”。首次尚未提交凭据且外网在线时也会得到该码，但不存在提交结果复检链。

修复：把判据拆成 `SubmissionResultPending`（凭据已发出、结果未知、持久状态待决，且不是明确拒绝或存储故障）与 `CampusWifiReady`（`Wifi == "已连接"`），`SubmissionRecheckActive` 要求两者同时成立。额度行、认证列耗尽分支与显式 `Unknown` 额度码据此区分“正在复检”和“校园 Wi-Fi 不可用，复检已暂停，待网络条件恢复后再进行”；`InternetOnline` 只在确实存在待决提交时才说“继续只读复检”，否则说明没有待确认的提交结果。未改认证提交、额度、恢复或复检决策。

## 审查修复：耗尽通知的复检说明与同轮通知抢占

2026-10-30 审查发现两处 P2：

1. **Wi-Fi 不可用时，耗尽通知仍声称继续复检。** 概览页已跟随校园 Wi-Fi 状态降级，但 `NotificationRouter` 的词表文案是固定的；服务重启后重新发布耗尽通知时，若 Wi-Fi 未连接，通知仍写“继续只读复检”，而服务实际会跳过复检。
2. **同轮门户结构异常会吞掉耗尽通知。** 服务先发布并标记耗尽通知；同一轮若只读门户检查发现结构异常（例如 chkstatus 丢失 `result` 字段），后者覆盖同一个一次性通知槽。只要提交待决状态持续，耗尽通知不会补发。

修复：

- `NotificationRouter.ForSnapshot` 在呈现前按快照调整耗尽通知正文：`Wifi != "已连接"` 时改为“校园 Wi-Fi 当前不可用，暂不发起只读复检，恢复连接后继续”，与概览页 `CampusWifiReady` 判据一致；其他通知码与网络条件无关，保持词表原文。
- `MonitorWorker` 先计算门户结构信号与 `layoutWillPublish`，再决定是否发布耗尽通知：同轮结构异常要占用通知槽时，耗尽通知**不标记为已发布**，下一轮结构事件已发布后由耗尽通知取得通知槽。两个事件按轮次依次送达，不再互相覆盖。
- 同一判据也应用到服务侧：`SubmissionBudgetExhausted` 日志与 `status.json` 的三处提交待决详情（缺少额度记录、额度耗尽、一般结果未知）只在 `link.State == WifiState.Ready` 时声称继续复检，Wi-Fi 未就绪时说明“暂不发起只读复检”。日志中心与诊断包因此不再出现与通知相反的复检声明。
- 未改变额度计数、提交授权、认证暂停或复检决策。

## 审查修复：展示层不猜矛盾数值

2026-10-30 审查的两个 P3 防御性缺口：

- `Unused` 不再无条件渲染“尚未使用（0/{max} 次）”。若快照同时给出非零已用次数、已耗尽或非正上限，视为矛盾并降级为“额度状态不可确认”，不把缺省值当成事实。
- `OverviewModel.SubmissionBudget` 的 `default` 分支不再把所有未枚举级别渲染成“已重置”；`Reset` 改为显式分支，未知级别固定降级为“额度状态不可确认”，避免未来新增码被误报为重置。
- 新增 1 条回归覆盖三种矛盾组合与未知级别码；完整 Release 构建 0 警告 / 0 错误，全量 **457/457**。

## 验证

- 2026-10-30 审查修复：新增 3 项回归（Wi-Fi 未就绪的耗尽通知不声称继续复检且与 Wi-Fi 就绪对照；同轮结构异常后下一轮补发耗尽通知；Wi-Fi 未就绪时状态详情与耗尽日志不声称继续复检且就绪时保留说明）；修复前确认失败，修复后额度专项 **14/14**，完整 Release 构建 0 警告 / 0 错误、全量 **456/456**。
- `scripts/build.ps1` 完整 Release 构建与回归：**453/453 passed**，退出码 0；证据 `artifacts/validation/build.json`、`tests.txt`（对应基线提交加本工作树改动，不代表发布包）。
- 专项 `--filter 额度可见`：**11/11**，覆盖无记录时的“未使用”、首次/第二次/第三次提交的额度投影、耗尽通知、门户成功重置、第三次明确拒绝不被耗尽通知覆盖、明确拒绝与绑定拒绝不被额度文案掩盖、第三次凭据拒绝与绑定拒绝不声称复检、结果未知耗尽的复检对照、Wi-Fi 未连接/等待 IPv4/网卡未就绪时复检暂停、外网在线但无待决提交时不声称复检、未知/终止状态与其他 Wi-Fi 不虚构复检、旧版快照降级、未知码不渲染、四种额度状态文案与词表。
- `scripts/qa-avalonia-overview.ps1`：**pass**。状态矩阵新增 `budget-near-limit`、`budget-exhausted`、`budget-exhausted-wifi-down`、`budget-online-no-pending`、`budget-exhausted-rejected`、`budget-near-limit-binding`、`budget-reset`、`budget-waiting`、`budget-legacy`，真实窗口截图在 `artifacts/validation/ui-avalonia-overview/overview/`，报告 `overview.json` 逐项记录额度行与认证列文案；脚本断言即将用尽、已耗尽（含停止原因）、Wi-Fi 不可用时复检暂停、无待决提交时不声称复检、明确拒绝优先于额度文案、已重置、等待重试与旧版降级。
- QA 环境说明：本机已安装的候选托盘仍持有旧版固定互斥 `Local\AutoNet.Tray.Avalonia`，导致 QA 进程被单实例检查判为“已在运行”。为取得截图，仅在本地 QA 发布构建中临时给 QA 互斥加进程号后缀，运行后立即还原；该临时改动未提交，最终构建与回归使用还原后的代码。未停止、替换或触碰已安装托盘与服务。
- 未运行真实门户提交、断网、UAC、安装/升级或服务替换；未验证真实服务写入的 `status.json` 在真实门户状态下的展示。
- 前一轮审查修复：额度专项 **9/9**；完整 Release 构建 **0 警告 / 0 错误**，全量 **451/451**。概览 QA 增加 `budget-other-wifi`、`budget-authorized-paused`，发布、夹具、实况、服务和已安装托盘保持均通过；两张新增截图已人工检查。QA 为隔离已安装旧托盘而临时修改互斥名，运行后 `Program.cs` 逐字节恢复并核对 SHA256。
- 上一轮复检文案修复：额度专项 **10/10**；完整 Release 构建 **0 警告 / 0 错误**，全量 **452/452**。概览 QA 断言 `budget-exhausted-rejected` 的额度行包含“没有进行中的提交结果复检”且不含“继续低频只读复检”，`budget-exhausted`（提交待决）仍保留复检说明。
- 本轮 Wi-Fi 与待决状态修复：额度专项 **11/11**；完整 Release 构建 **0 警告 / 0 错误**，全量 **453/453**。概览 QA 增加 `budget-exhausted-wifi-down`、`budget-online-no-pending`，断言 Wi-Fi 不可用时额度行与认证列均说明等待网络条件恢复、外网在线但无待决提交时不声称复检；发布、夹具、实况、服务与已安装托盘检查均通过，两张新增截图已人工核对。真实校园网与 #34 原工作树未提交回归仍未验证。

## 范围与未完成

- WinForms 旧托盘未增加额度行；本工单的界面目标是 #27 的 Avalonia 单页应用。旧托盘仍会忽略新增快照字段。
- 新增展示不改变 #32/#34 的额度、认证暂停、审计与存储故障语义；#32/#34 的完整验收仍以各自工单为准。
- QA 只覆盖受控夹具矩阵，不证明真实门户、真实提交次数或真实时钟跳变下的界面表现。

## 修改的共享文件

- `src/AutoNet.Core/RecoveryPolicy.cs`（`StatusSnapshot` 字段与通知码）
- `src/AutoNet.Core/SubmissionRetryPolicy.cs`（`AttemptCap`、`NextPossibleAt`）
- `src/AutoNet.Service/MonitorWorker.cs`（额度投影与耗尽通知）
- `src/AutoNet.Tray.Avalonia/OverviewModel.cs`（额度文案、进度行、认证列拒绝优先级）
- `src/AutoNet.Tray.Avalonia/NotificationRouter.cs`（耗尽词表）
- `src/AutoNet.Tray.Avalonia/QaEnvironment.cs`、`QaOverview.cs`、`scripts/qa-avalonia-overview.ps1`（QA 证据）
- `tests/AutoNet.Tests/Program.cs`（注册专项）、`tests/AutoNet.Tests/OverviewRegression.cs`（进度行顺序）
- `CONTEXT.md`（术语）

新增 `src/AutoNet.Core/SubmissionVisibility.cs`、`tests/AutoNet.Tests/SubmissionVisibilityRegression.cs`。已提交在 `codex/35-budget-visibility` 分支，未推送、未创建 PR、未关闭工单、未部署。
