# #41 设置页字段合并实施记录

- 工单：[yuwen773/auto-net#41](https://github.com/yuwen773/auto-net/issues/41)
- 父规格：[#27](https://github.com/yuwen773/auto-net/issues/27)；前置：[#30](https://github.com/yuwen773/auto-net/issues/30)（仍开放）
- 基线：本地 `codex/ui-avalonia` 的 `4699c24`
- 工作树：`D:\Work\code\auto-net-41`，分支 `codex/41-settings-merge`
- 状态：本地实现与验证完成；**未提交、未推送、未创建 PR、未关闭工单、未部署**

## 前置契约核对（#30）

开工前核对 #30 的本地实现与验证记录：

- 交接入口 `SettingsHandoffLauncher.SaveAsync` 经一次性本地命名管道把 `SettingsDraft` 交给提权助手；接收端校验 SID ACL、双方进程身份、新鲜挑战与期限，确认后才调用 `ElevatedSettings.Save`。
- `HandoffOutcome` 为 `Completed / Rejected / TimedOut / Cancelled / OutcomeUnknown`；`SettingsSaveOutcome` 为 `Saved / MissingProfile / PasswordRequired / InvalidSettings / StoreFailed`。
- 零写入只保证到提交确认前。确认可能已发出后的回执丢失必须报告 `OutcomeUnknown`，不能宣称零写入。
- 保存顺序仍是先凭据后设置，跨文件**没有原子性**；`StoreFailed` 必须如实说明凭据可能已写入。
- #30 的工单仍开放：真实同账户 UAC 成功、跨管理员账户 UAC 成功在其记录中均为 `NOT_RUN`，本次不把代码存在当作完整验收。

本次没有修改 #30 的管道、身份核验或保存顺序，只在普通界面侧接入并如实映射结果。

## 实现内容

### 1. 非秘密配置投影（共享状态模型）

普通 UI 进程无法读取 `C:\ProgramData\AutoNet\private`（仅管理员与 SYSTEM），因此新增非秘密投影，而不是放宽文件权限：

- `RecoveryPolicy.StatusSnapshot` 新增 `SettingsProjected`、`InterfaceId`、`WifiProfile`、地址安全展示值及隐藏标记、`AllowActiveDisconnect`、`KeepAwakeOnAc`；`Enabled` 与 `AuthChannelMode` 沿用既有字段。
- `MonitorWorker` 在本轮成功读取设置后，把上述字段合并进写出的 `status.json`；即使设置校验失败也只投影安全展示值。未读到设置时 `SettingsProjected=false`，UI 不把默认值当成已保存配置。
- 投影**不含账号与密码**；密码从不回读。

### 2. 主窗口设置页

- 新增 `SettingsPage`（无 Avalonia 依赖的页面模型）：字段与既有编辑器一致（Wi-Fi 配置、账号、密码、代理、UU、自动恢复、主动断线、保持唤醒、内嵌通道、外观），校验沿用 `Settings.Validate` 与既有凭据规则，不新增业务选项。
- 新增可编辑的 `SettingsView`：只读事实卡（自动恢复、守护 Wi-Fi、认证地址）+ 表单 + 保存/放弃 + 结果文案 + 投影说明 + 凭据说明 + 外观。
- 主窗口保持普通权限。保存经 `SettingsHandoffLauncher.SaveAsync` 独立提权交接，主窗口自身不提升。
- 页面控件只构建一次并在导航间复用；状态轮询不会覆盖已修改的草稿。只有可信且 `SettingsProjected` 的投影才会自动回填，且必须晚于最近一次成功保存。
- 托盘“设置”菜单改为打开主窗口设置页；保存仍走提权交接。
- 旧 `--settings` 提权编辑器保留为兼容入口，仍执行既有保存规则。

### 3. 凭据规则

- 密码输入框始终遮挡（`PasswordChar='●'`），不显示、不回读已保存密码。
- 同账号且密码留空：由提权接收端按既有规则保留旧密码。
- 首次设置或更换账号且密码留空：接收端返回 `PasswordRequired`，页面显示“首次设置或更改账号时，请输入校园网密码。”，零写入。
- 账号为空、配置非法、代理非本机回环等：普通进程在交接前拦截，交接未发起，零写入。
- 取消/拒绝/超时：显示受控文案并明确“未写入任何设置”。
- `StoreFailed`：说明凭据可能已写入、设置可能未写入，不声称原子性。
- `OutcomeUnknown`：说明可能已写入部分或全部内容，不宣称零写入。
- 并发保存：`Interlocked` 忙态门只允许一次交接。
- 每次交接结束（成功、失败、取消、异常）都在 `finally` 中清空草稿密码并通知视图清空输入框；“放弃修改”也清除密码。页面打开期间密码只存在于普通进程内存的草稿与遮挡输入框中，不进入命令行、环境变量、日志或明文临时文件。
- 保存不解除认证暂停：页面与交接路径都没有释放入口，QA 逐场景核对 `auth-block.txt` 逐字节不变。

### 4. QA 与证据

- `QaSettings.Run` 驱动 6 个状态矩阵（online / online-disabled / online-suspended / stale / invalid / missing）与 7 条保存路径（首次设置、保留密码、换账号必须新密码、校验失败、取消授权、结果未知、存储失败），截图前滚动到结果区，证据同时包含 JSON 与真实窗口 PNG。
- `QaSettings.SaveForQaAsync` 只在 QA 隔离目录内调用真实 `ElevatedSettings`；取消与结果未知直接返回受控结果，不写文件。
- QA 脚本新增投影矩阵断言、7 条保存断言、密码清除/不外泄断言、认证暂停保持断言、页面夹具明文扫描。
- 新增 `AUTONET_QA_MUTEX_SUFFIX`（仅 QA 生效）：本机仍运行旧预览版安装时，其正式实例占用固定 QA 互斥名 `Local\AutoNet.Tray.Avalonia`，设置页 QA 会得到 `already-running` 而不是报告。脚本用证据目录派生的受控后缀取得独立互斥身份，**不停止已安装托盘，也不干扰其他工作树的 QA 进程**。未设置后缀时行为不变，既有脚本与测试契约不变。

## 代码审查修复（第二轮）

基线仍为 `4699c24`，第一轮改动未提交；本轮在保留全部已有改动的前提下修复审查发现。

### 1. [P1] 配置地址中的秘密进入公开状态与诊断包

**根因**：`Settings.Validate` 允许代理地址带路径/查询、UU 地址带路径；`status.json` 对普通用户可读，且被诊断包原样收录。第一轮把完整地址写进快照，路径或查询里的令牌因此进入公开状态。

**修复**：新增无 I/O、无副作用的 `ConfigProjection`，服务投影与设置页共用同一规则：

- 不含路径/查询/片段（或路径仅为 `/`）：投影完整原值，保存时字节不变；
- 含隐藏部分：只投影 `scheme://host[:port]`，并置 `ProxyUrlRedacted` / `UuEndpointRedacted`；
- 含 userinfo 的地址不投影任何内容（`Settings.Validate` 本已拒绝）；
- 快照字段由 `ProxyUrl` / `UuEndpoint` 改为 `ProxyUrlDisplay` / `ProxyUrlRedacted` / `UuEndpointDisplay` / `UuEndpointRedacted`，旧服务写出的旧字段名被忽略，设置页按“无投影”降级。

**保存不截断**：设置页把红值字段标记为“保留原值”（`SettingsDraft.KeepProxyUrl` / `KeepUuEndpoint`）。用户未编辑该字段时，提权接收端 `ElevatedSettings.Save` 从受限存储读出完整原值写回，绝不以展示值覆盖；只有用户改动文本才清除标记。读取现有设置失败时返回新增的 `SettingsUnreadable`（“未写入任何内容”），**不回退到展示值**——宁可失败也不静默截断配置。

**边界（未声称）**：展示值仍包含主机与端口，可能暴露内网主机名；`status.json` 仍含 Wi-Fi 名称、网卡 ID 等非秘密配置，与既有投影范围一致。诊断包收录的是同一份 `status.json`，因此同样只有展示值。

### 2. [P1] 配置读取失败仍发布旧投影

**根因**：`projectedSettings` 只在读到设置时赋值，从未在轮次开始时复位；读取失败时快照沿用上一轮配置，并把复位后的 `ManualOnly` 当成当前通道模式。

**修复**：`RunOnceAsync` 每轮开始复位 `projectedSettings = null` 与 `authChannelMode = ManualOnly`；读取成功后立即赋值（校验失败仍投影本轮实际读到的文件，地址仍按安全规则展示）；读取失败时错误快照发布 `SettingsProjected=false` 与空 `AuthChannelMode`。`SettingsModel.From` 已对 `SettingsProjected=false` 降级，设置页提示“表单内容来自上次投影或你的输入，不保证是当前已保存值”。

### 3. [P2] 跨页指纹缓存导致返回后仍显示设置页

**根因**：`MainView.RenderContent` 的 Settings 分支不清空其他页面的指纹缓存；返回未变化的概览/诊断/日志时指纹命中提前返回，`pageHost.Content` 仍是设置控件。

**修复**：Settings 分支先 `ClearShown(except: null)` 再渲染；设置控件本身仍只构建一次并复用，草稿不丢。QA 用 11 步往返序列（设置↔概览/诊断/日志）逐次记录页面宿主实际标题与截图。

### 4. [P2] 保存期间编辑与“已保存”误报

**根因**：保存期间控件仍可编辑；保存完成后无条件把草稿标记为已保存，保存期间的新输入会被误标或丢失。

**修复**：

- 视图在 `page.Saving` 期间禁用全部编辑控件与“放弃修改”；
- 模型用 `draftVersion` 记录草稿版本：保存完成时版本已变化则保持 `Dirty`，文案改为“已保存提交时的内容；保存期间的新修改尚未保存，请再次保存。”；
- `finally` 只清除本次提交的密码；保存期间输入的新密码保留；
- 并发保存仍由忙态门拒绝第二次交接。

**顺带修复（QA 发现）**：程序化回填（投影应用）引发的迟到变更事件会被当成用户编辑，把草稿误标为已修改，后续状态投影因此不再生效（状态切到“已关闭”后表单仍勾选“启用自动恢复”，红值说明也不出现）。改为回填后延迟到下一轮调度再解除忽略，并让所有回写先与草稿比较、等值时不动草稿。

### 5. [P2] 保存按钮无障碍名称未随可见状态更新

**修复**：`ShellAccessibility.SettingsSave(bool)` 作为单一来源同时驱动按钮可见文字与 `AutomationProperties.Name`：保存中为“正在保存…”，结束后恢复“保存设置”。QA 在交接被刻意延迟时读取自动化名称并断言两态。

## 代码审查修复（后续复核）

- 无可信设置投影时，未编辑的代理和 UU 地址默认由提权端读取并保留受限原值；即使曾有旧投影，保存也取受限存储中的最新地址。明确编辑或通过带无障碍名称的清空按钮操作时，才使用新值或空值。
- 曾确认过设置存在后，保存时发现设置消失会在写入凭据和设置之前拒绝；首次配置仅在没有已知旧配置且对应保留字段为空时允许创建。提权端直接读取设置文件，仅将文件或目录确实不存在视为缺失，读取异常、损坏或文件被独占均保守拒绝，不再依赖 `File.Exists` 将读取失败误判为首次配置。
- 新增隔离目录行为回归，覆盖配置消失、无投影保留、读取失败零写入、文件锁、过期投影、首次设置、明确清空和跨管道保留标记。窗口 QA 覆盖两个清空按钮的名称、焦点与 Enter 激活。真实 UAC 和真实受限目录的保存仍未执行。
- 2026-10-30 对上述最终源码重新运行 `scripts/build.ps1`：Release 构建 **0 警告 / 0 错误**，全量 **465/465**；其中设置页回归 **34/34**、凭据交接回归 **14/14**。证据为忽略目录 `artifacts/validation/tests.txt` 与 `artifacts/validation/build.json`；这不是发布包或真实提权验收。下方 **458/458** 为前一轮历史结果。

## 代码审查修复（第三轮）

基线仍为 `4699c24`，前两轮改动未提交；本轮保留全部已有改动，按“先写行为回归、确认失败、再修复”的顺序处理三项发现。

### 1. [P1] URL 路径规范化绕过脱敏

**根因**：`ConfigProjection` 用 `Uri.GetComponents(PathAndQuery | Fragment)` 判断隐藏部分，而 `Uri` 会把点段规范化掉。`http://127.0.0.1:7897/TEST-URL-SECRET/../` 的规范化路径是 `/`，因此被判为“安全地址”并原样写进 `status.json` 与诊断包；`Display` 随后返回原始输入，秘密随之公开。

**修复**：隐藏部分改为按**原始输入**判定：

- 只做最小切分——在原始字符串的 `scheme://` 之后，授权部分在第一个 `/`、`?`、`#` 或 `\` 处结束；其余部分（含点段、编码路径、查询、片段）非空且不是 `/` 即视为隐藏；
- 隐藏时展示 `scheme://授权部分`，授权部分只允许主机名/端口/IPv6 字面量字符，否则不投影任何内容；
- userinfo、无效地址与无法可靠切分的输入一律返回空展示值并视为隐藏；
- 安全地址仍原样返回（仅去掉首尾空白），保存时字节不变。

**未改变**：未编辑地址的保存仍由提权接收端写回受限存储中的完整原值（`KeepProxyUrl`/`KeepUuEndpoint`），点段形式的原值同样逐字节保留；读取失败仍拒绝保存。

### 2. [P1] 无可信投影时未编辑的恢复选项被默认值覆盖

**根因**：设置页草稿对四个恢复选项（自动恢复、主动断线、保持唤醒、内嵌通道）总是持有页面默认值（开启/仅手动），保存时无条件写入。服务未运行或状态过期时页面没有可信投影，用户只改账号保存，就会把此前关闭的选项重新打开。

**修复**：

- `SettingsDraft` 新增 `KeepEnabled` / `KeepAllowActiveDisconnect` / `KeepKeepAwakeOnAc` / `KeepAuthChannelMode`；提权接收端在需要时读取现有设置，并按标记逐字段保留受限存储中的原值（读取失败或已知配置消失时仍在任何写入前拒绝）；
- 页面草稿的同名标记默认“保留”，只在用户明确改动时由 `SetEnabled` / `SetAllowActiveDisconnect` / `SetKeepAwakeOnAc` / `SetAuthChannelMode` 清除并标记为已修改；投影应用与放弃修改会恢复标记；
- 视图把无投影且未改动的选项渲染为三态“未确定”（复选框 `IsChecked = null`、通道三选一均不选中），并在选项区显示说明：已有设置时保留原值、首次设置按默认值、勾选或选择即明确修改。用户点击循环到“未确定”时恢复为草稿值，避免把未知态留在界面上；
- 首次设置（受限存储确认没有配置）仍按页面默认值创建；`page-first-setup` 场景断言默认值。

**语义边界**：保存成功后，仍带保留标记的字段继续显示“未确定”——接收端读到的原值对普通进程不可见，页面不猜测；下一次可信投影会回填确定值。

### 3. [P2] 跨进程并发保存覆盖刚更新的地址或密码

**根因**：保存是“读取既有值 → 写凭据 → 写设置”，没有任何跨进程互斥。两个 Windows 会话、兼容提权编辑器或恢复开关入口并发保存时，较早读取的旧地址、旧密码会覆盖另一方刚写入的新值。

**修复**：

- 新增 `AutoNet.Core.SettingsWriteLock`：在受限目录打开 `settings.lock`（`FileMode.OpenOrCreate` + `FileShare.None`），Windows 共享模式在整机范围内跨进程、跨会话互斥；锁被持有时每 25ms 重试，超时抛 `TimeoutException`，其他 I/O 错误立即上抛，不伪装成竞争；持有者退出后残留的零字节锁文件不阻塞下一次获取。
- 所有相关保存入口都在锁内完成读改写：设置页接收端 `ElevatedSettings.Save`（凭据读取也在锁内）、Avalonia 恢复开关 `ElevatedRecovery.Apply`、WinForms `--enable/--disable-recovery`、兼容提权编辑器 `SettingsForm.Save`。
- 锁竞争返回新增的 `SettingsSaveOutcome.SaveBusy`，页面显示“另一个设置保存正在进行，未写入任何内容”，**零写入**；兼容编辑器在锁内重新读取凭据与设置，发现设置已被其他会话修改时拒绝保存并提示重新打开，而不是静默覆盖。
- 明确不声称跨文件原子性：锁只串行化保存，写入阶段失败仍可能只写入凭据或只写入设置，`StoreFailed` 文案继续如实披露部分写入。
- #30 的管道、身份核验、一次性挑战与提交确认未改动；锁在接收端内部获取，不改变交接边界。

## 代码审查修复（第四轮）

基线仍为 `4699c24`，前三轮改动未提交；本轮处理 2026-10-30 审查发现的三个 P2 问题，并保留全部已有改动。

### 1. [P2] 并发保存把守护 Wi-Fi 改回旧配置

**根因**：草稿一旦变脏就不再接受 Wi-Fi 投影，而保存端无条件使用草稿里的网卡与配置。窗口 A 加载网络 A 后只改账号，窗口 B 已把守护目标改成网络 B；窗口 A 保存时仍写回 A。保存锁只串行化写入，无法识别过期草稿。

**修复**：

- `SettingsDraft` / `SettingsDraftValues` 新增 `KeepProfile`（页面默认 true）：用户没有在页面明确选择 Wi-Fi 时，提权接收端读取受限存储并把当前守护网卡与配置原样写回；`SelectProfile`（下拉框）才清除该标记。
- `needsExisting` 纳入 `KeepProfile`，因此保留守护目标与保留地址/开关走同一套“读取失败即零写入”的保守路径。
- 草稿已修改但 `KeepProfile` 仍为 true 时，页面跟随可信投影更新 `Draft.Profile`（仅在网卡与名称变化时回填），避免界面继续显示旧配置。
- 首次设置（受限存储确认没有配置）仍按用户选择的配置创建。

### 2. [P2] Wi-Fi 列表不随系统配置刷新

**根因**：配置列表只在 `SettingsPage` 构造时枚举一次，枚举失败也没有可见提示。

**修复**：

- `SettingsPage.RefreshProfiles()` 重新枚举并触发 `ProfilesChanged`；`MainView` 在导航进入设置页时调用。
- `SettingsView` 订阅 `ProfilesChanged`，重建下拉项并按网卡与名称保留当前选择；枚举失败或没有可用配置时显示受控说明（不携带异常文本）。

### 3. [P2] 本地校验失败后密码仍留在表单

**根因**：未选择 Wi-Fi 或地址校验失败时 `SaveAsync` 提前返回，绕过 `finally` 中的密码清除。

**修复**：本地校验失败分支同样调用 `ClearSubmittedPassword`（只清除本次提交的密码，保存期间新输入保留），并触发 `PasswordCleared`。

### 新增回归（3 条）

- 并发保存：另一窗口改成网络 B 后，本窗口只改账号保存，落盘仍是网络 B。
- 列表刷新：新增配置后 `RefreshProfiles` 可见、投影按网卡与名称重新匹配；枚举失败标记且列表清空。
- 密码清除：未选择 Wi-Fi 与非法代理两条本地失败路径都清空草稿密码、触发清除事件且零写入。

### 验证

- 完整 Release 构建：0 警告 / 0 错误；全量回归 **477/477**（本轮新增 3 条）。
- 设置页专项回归通过；`scripts/qa-avalonia-settings.ps1`：`PASS publish`、`PASS shell-page`、`PASS service-untouched`，`editor-rules` 与 `real-private-untouched` 仍为既有的 `NOT-VERIFIED`（非管理员上下文无法覆盖）。
- 真实 UAC、真实受限目录保存仍为既有 `NOT_RUN`。

## 代码审查修复（第五轮）

基线仍为 `4699c24`，前四轮改动未提交；本轮处理 2026-10-30 审查中两轴最重的 #41 P2，并统一 `CONTEXT.md` 的“恢复开关”术语。

### 1. [P2] 兼容提权编辑器在锁外读取、锁内覆盖

**根因**：Avalonia `--settings` 兼容编辑器在窗口构造时 `Load()` 读取设置快照并填入全部控件；保存时草稿没有“保留原值”标记，`ElevatedSettings.Save` 在锁内只重新读取凭据，设置字段仍取窗口打开时的旧值。另一个会话在窗口打开期间保存的新设置（代理地址、开关、守护目标等）会被旧快照静默覆盖。WinForms 兼容编辑器已有“设置已变化则拒绝”的守卫，Avalonia 兼容入口没有。

**修复**：

- 新增 `SettingsSaveGuard(Settings? Expected)`；`ElevatedSettings.Save(draft, guard = null)` 在取得保存锁后重新读取设置，与快照做记录值相等比较，不等则返回新增的 `SettingsSaveOutcome.SettingsChanged`，文案说明未写入并提示重新打开。
- `SettingsWindow.Load()` 保存快照，用户保存时传入守卫；QA 接缝 `RunSaveForQa` 使用自带夹具草稿，不附带守卫。
- 设置页（普通界面）继续使用 `Keep*` 标记，不依赖该守卫；守卫只覆盖没有保留语义的兼容编辑器。
- `SettingsHandoffLauncher` 的保存委托改为 lambda，避免方法组转换因新增可选参数失败。

### 2. 术语：恢复开关与设置页复选框

`CONTEXT.md` 的“恢复开关”原先定义为“单一入口”，与 #41 设置页的“启用自动恢复校园网”复选框矛盾。现明确：恢复开关是概览页与托盘菜单的快速开关；设置页复选框是保存时的字段编辑器，不经过 `--enable/--disable-recovery` 意图参数。同时补充兼容编辑器的快照守卫语义。

### 3. QA 证据补齐（第四轮行为的窗口级证据）

- `QaSettingsSaveStep` 新增 `KeepProfile`，保存场景提交并记录该标记；新增 `page-keep-profile` 场景：受限存储里的守护目标是 `TEST-AP-2`，页面默认选择 `TEST-AP-1` 且未改选，保存后 `settings.json` 必须仍是 `TEST-AP-2`。
- 设置页 QA 在真实窗口里追加一条配置后调用 `RefreshProfiles`，断言真实下拉框出现新配置并截图 `settings-profile-refresh.png`；脚本把该步骤与截图纳入断言。
- `scripts/qa-avalonia-settings.ps1` 复跑：`PASS publish`、`PASS shell-page`、`PASS service-untouched`；`editor-rules` 与 `real-private-untouched` 仍为既有的 `NOT-VERIFIED`（非管理员上下文）。

### 新增回归（4 条）

- 快照在保存前被另一会话改写：拒绝为 `SettingsChanged`，凭据逐字节不变，新设置不被覆盖。
- 首次设置期间出现新设置：空快照守卫拒绝，零写入。
- 快照未变化：按原规则保存成功。
- 守卫读取设置失败：`SettingsUnreadable`，零写入。

### 验证

- 完整 Release 构建：0 警告 / 0 错误；全量回归 **481/481**。
- 首轮全量曾出现 4 条通道/进程用例超时，单独复跑与再次全量均通过（22/22、7/7、1/1、481/481），判定为环境瞬时失败，与本轮设置保存改动无关。

## 代码审查修复（第六轮）

基线仍为 `4699c24`，前五轮改动未提交；本轮处理 2026-10-30 两轴审查的硬违规与正确性缺陷。

### 1. [硬违规] 新领域概念入词表

`CONTEXT.md` 新增 **配置投影（ConfigProjection）** 与 **保存锁（SettingsWriteLock）** 两个条目，并把“设置凭据交接”里的“账号与密码永不回读”改为“密码永不回读；账号经配置投影预填”。`ConfigProjection`、`SettingsWriteLock`、`SettingsPage` 的注释引用词表用词。

### 2. [硬违规] 无投影时 KeepProfile 无法保存

`SettingsPage.ValidateLocally` 与 `ElevatedSettings.Save` 原先都要求 `Profile` 非空，与 `KeepProfile`“未选择时保留当前守护配置”的语义矛盾：旧服务或状态不可信时页面永远无法保存。现在 `Profile == null && KeepProfile` 走保留路径，由提权接收端在锁内用受限存储中的守护目标；只有既未选择、也没有保留语义时才返回 `MissingProfile`。QA 场景 `page-keep-profile` 改为提交 `null` 草稿，并新增隔离目录回归。

### 3. [正确性] 账号预填与“保留账号”

- `StatusSnapshot` 新增 `Username`，由 `MonitorWorker` 在读到凭据后写入配置投影；`SettingsModel` 把账号投影到页面，`ApplyProjection` 在用户未改动账号时预填。密码仍不显示、不回读。
- `SettingsDraft.KeepUsername`（默认 false，页面草稿默认 true）让保存端在用户未改动账号时使用受限存储中的账号；无投影时只改其他字段也无需重输账号。
- 诊断包契约是“不含账号”：`DiagnosticsPackage.ReadStatus` 在收录 `status.json` 前剔除 `Username`，无法解析的状态文件不再原样收录。新增回归覆盖字段剔除与完整导出。

### 4. [正确性] 过期投影不再改写页面内容

`SettingsPage.Update` 在采用投影前先判新鲜度：`ConfigProjected && ProjectedAt <= lastSavedAt` 的状态直接忽略，等下一次状态写入再采用，避免把只读事实与脱敏提示刷回保存前的值。

### 5. [正确性] 完整显示的地址不再误报“无法读取配置”

`hasKeptFields` 只统计“页面从未显示、由接收端保留”的字段：地址要求投影缺失或本身带隐藏标记，选项要求无投影；完整显示的地址即使带 `Keep*` 标记也不再让 `ConfigProjected` 变 false。

### 6. [正确性] 无法识别的既有通道模式

`ElevatedSettings.SaveLocked` 在用户未编辑通道模式且受限存储中的值不是已知三态时返回受控 `InvalidSettings`，零写入，而不是静默改写成页面默认值。拒绝原因经枚举跨进程传递，界面只显示接收端与页面共用的 `InvalidSettingsMessage`；文案已覆盖“认证通道设置”，不再让用户面对三个都未选中的单选项却只看到“检查账号、代理与 UU 地址”。

### 7. [正确性] 零写入拒绝不再宣称“可能已写入”

新增 `SettingsPageSaveState.SettingsChanged`，`SettingsSaveOutcome.SettingsChanged` 映射到“未写入任何内容”的独立状态与文案，不再落入 `OutcomeUnknown`。

### 8. 便宜的 smell

- `KeepKeepAwakeOnAc` → `KeepAwakeOnAcKept`；`AllowMissingSettingsForUnprojected` → `AllowFirstTimeSetup`（QA 报告字段名保持不变，脚本无需改动）。
- 账号回写统一走 `SettingsPage.SetUsername`，视图不再直接改草稿。
- 结果色调改由 `MessageIsSuccess` 决定，不再解析“已保存”前缀。
- 地址与通道校验收敛到 `Settings.ValidateConnectionOptions`，设置页预检与保存端共用同一规则。
- 删除已无读取方的 `SettingsContent.ContentKey` / `ContentFingerprint`。
- `SettingsPage` 的 `InvalidSettingsMessage` / `SaveBusyMessage` / `SettingsChangedMessage` 改为引用 `ElevatedSettings` 的同名常量，去掉两处措辞漂移。
- 诊断包遇到无法解析的 `status.json` 时记录说明并整份丢弃，而不是静默省略。

### 明确不做

- 七个 `Keep*` 布尔收敛为 `SettingsKeepFlags` 类型：结构性重构，留作跟进项。
- 真实 UAC 点击/拒绝、真实受限目录保存：仍为 `NOT_RUN`，未声称实机验证。

### 验证

- 完整 Release 构建 **0 警告 / 0 错误**，全量回归 **488/488**；设置页专项 **53/53**。
- 设置页窗口 QA 未在本轮重跑（需要发布与真实窗口）；其断言依赖的 `page-keep-profile` 场景已改为 `null` 草稿并由隔离目录回归覆盖。

## 验证结果

- 完整 Release 构建：**0 警告 / 0 错误**；全量回归 **474/474**（第三轮新增 9 条：3 条地址脱敏/诊断包、3 条恢复选项保留与未知状态、3 条跨进程保存）。
- 第三轮专项：设置页 **40/40**、跨进程保存 **3/3**、凭据交接 **14/14**；跨进程用例使用真实独立进程（父进程持锁、子进程在锁内保存；子进程持锁、父进程受控拒绝），不是同一 `SettingsPage` 实例。
- 此前一轮历史结果：**465/465**；再前一轮 **458/458**。
- 设置页 QA：`PASS publish`、`PASS shell-page`、`PASS service-untouched`。
  - 7 个状态矩阵：可信状态投影非秘密配置；`online-redacted` 只显示来源并置红值标记；stale/invalid/missing 固定降级为“待确认 / 不可确认”，`ConfigProjected=false`，不泄露配置投影。
  - 10 条保存路径全部符合预期，且每条都满足：密码已清除、结果文案不含密码、认证暂停标记逐字节不变。
  - `page-first-setup`：设置与凭据写入，密文无明文。
  - `page-keep-password`：凭据重写且仍为旧密码；设置为同值重写，字节不变。
  - `page-change-account-required`：`ValidationFailed`，零写入。
  - `page-validation-failed`：交接未发起，零写入。
  - `page-uac-cancelled`：`Cancelled`，零写入，文案明确。
  - `page-outcome-unknown`：`OutcomeUnknown`，不宣称零写入。
  - `page-store-failed`：`StoreFailed`，凭据已写入、设置未写入，如实披露部分写入。
  - `page-keep-redacted-url`：草稿带保留标记（`KeepProxyUrl`/`KeepUuEndpoint` 均为 true），接收端读到夹具设置，保存后 `settings.json` 仍是完整原值 `http://127.0.0.1:7897/TEST-URL-SECRET?token=TEST-URL-SECRET` 与 `https://uu.example.test/TEST-URL-SECRET`；报告与状态夹具中 `TEST-URL-SECRET` 出现 0 次。
  - `page-keep-options`：种子为四项全部关闭/仅手动；提交的四个保留标记均为 true，落盘仍为关闭与 `Off`，页面默认值没有重新开启任何选项。
  - `page-busy-name`：保存期间自动化名称为“正在保存…”，结束后恢复“保存设置”。
  - 新增 `settings-options-unknown` 截图：无投影时把选项区滚入视野，三态“未确定”复选框、未选中的通道单选与保存语义说明同框。
  - 跨页往返 11 步（设置↔概览/诊断/日志）实际标题依次为 概览/设置/概览/设置/网络诊断/设置/网络诊断/设置/日志/设置/日志，截图均非空白。
  - 截图 29 张（7 状态 + 1 选项未知 + 10 保存 + 11 跨页），均来自真实窗口。
- 可访问性与缩放 QA：`PASS publish`、`PASS accessibility-fixture`；设置页 Tab 停留点包含保存按钮“保存设置”，100%/125%/150% 三档均无关键裁切。真实 Windows DPI 与辅助技术交互仍为人工项。
- 证据目录：`artifacts/validation/ui-avalonia-settings/`（`shell/settings.json`、27 张 PNG、`settings-page-fixture/`、`qa-result.json`）与 `artifacts/validation/ui-avalonia-accessibility/`。

### NOT_RUN / NOT-VERIFIED

- **真实 UAC 点击与拒绝**：未执行，需人工在真实桌面上操作。
- **跨管理员账户 UAC 成功路径**：本机无法构造，`NOT_RUN`。
- **提权编辑器逐场景 QA**：当前验证进程非管理员；普通上下文只验证了 `--settings` 记录提权请求并以退出码 4 结束。编辑器规则需在提权提示符下重跑，记为 `NOT_VERIFIED`。
- **本机真实受限目录逐字节未触碰**：普通权限进程无法读取 `C:\ProgramData\AutoNet\private`，无法核对前后指纹，记为 `NOT_VERIFIED`；未声称真实存储未被触碰。
- **真实受限存储上的“保留原值”保存**：QA 与回归都在隔离夹具内完成（真实 `ElevatedSettings` 规则 + 夹具设置/凭据文件），未在真实 `C:\ProgramData\AutoNet\private` 上执行保存，`NOT_RUN`。
- **真实网络、真实门户提交、安装/回退、服务替换**：未执行。

## 修改的共享文件（供 #35 / #37 集成）

- `src/AutoNet.Core/RecoveryPolicy.cs`：`StatusSnapshot` 的设置投影字段（公共状态模型）。第二轮把 `ProxyUrl`/`UuEndpoint` 改名为 `ProxyUrlDisplay`/`ProxyUrlRedacted`/`UuEndpointDisplay`/`UuEndpointRedacted`，**接口变化**。
- `src/AutoNet.Core/ConfigProjection.cs`（新增）：地址安全投影规则，服务与设置页共用；第三轮改为按原始输入判定隐藏部分。
- `src/AutoNet.Core/SettingsWriteLock.cs`（新增）：保存的跨进程互斥（受限目录锁文件），所有保存入口共用。
- `src/AutoNet.Service/MonitorWorker.cs`：投影写入 `status.json`；每轮复位投影；读取失败时不发布旧投影或复位后的通道模式。
- `src/AutoNet.Tray.Avalonia/Program.cs`：仅 QA 生效的互斥后缀（与 #37 的互斥规则同文件，需合并时对齐）。
- `src/AutoNet.Tray.Avalonia/MainView.axaml.cs`、`MainWindow.axaml.cs`、`App.axaml.cs`：设置页接线、导航复用与跨页指纹缓存清理。
- `src/AutoNet.Tray.Avalonia/SettingsPage.cs`、`SettingsView.cs`、`SettingsModel.cs`、`ElevatedSettings.cs`、`ShellAccessibility.cs`：草稿版本、保存忙态、红值保留与无障碍名称。
- `src/AutoNet.Tray.Avalonia/TrayHost.cs`、`TrayMenuModel.cs`：托盘设置入口改为打开页面。
- `src/AutoNet.Tray.Avalonia/QaEnvironment.cs`、`QaSettings.cs`：QA 报告与驱动（新增导航、忙态、红值保留证据字段）。
- `tests/AutoNet.Tests/MonitorRegression.cs`：`MonitorFixture.BeforeSettingsRead` 注入点（与 #35 的监测回归同文件）。
- `tests/AutoNet.Tests/SettingsPageRegression.cs`：设置页专项回归。
- `tests/AutoNet.Tests/SettingsSaveRegression.cs`（新增）：跨进程保存一致性与锁竞争回归（真实独立进程）。
- `src/AutoNet.Tray/SettingsForm.cs`、`src/AutoNet.Tray/Program.cs`：兼容提权编辑器与恢复开关接入保存锁（WinForms 侧）。
- `scripts/qa-avalonia-settings.ps1`：设置页 QA。
- `scripts/qa-avalonia-accessibility.ps1`：与设置页 QA 相同的 `AUTONET_QA_MUTEX_SUFFIX` 隔离约定（本机装有运行中的预览版托盘时才能跑起来）。
- `CONTEXT.md`、`AGENTS.md`、`docs/VALIDATION.md`：术语与状态说明。

本任务未修改 `docs/progress/34-*` 等其他工单文件，也未覆盖或回退并行工作树的改动。

## 工作树状态

- 基线 `4699c24`；已提交在 `codex/41-settings-merge` 分支，未推送、未合并。
- 新增：`src/AutoNet.Tray.Avalonia/SettingsPage.cs`、`src/AutoNet.Core/ConfigProjection.cs`、`src/AutoNet.Core/SettingsWriteLock.cs`、`tests/AutoNet.Tests/SettingsSaveRegression.cs`、本文件。
- 其余为上述修改文件。
