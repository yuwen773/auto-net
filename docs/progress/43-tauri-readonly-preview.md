# 工单 #43：Tauri 2 只读预览（P0/P1 首轮）

日期：2026-11-23。基线：`43c3b0e4b23f0b3f0c080de8945664add7182a1d`（v0.4.0-rc.1）。
决策见 [ADR-0022](../adr/0022-tauri-rust-gradual-migration.md)、[ADR-0023](../adr/0023-readonly-preview-boundary.md)，
总体计划见 [迁移计划](../plans/2026-11-23-tauri-rust-gradual-migration.md)。

## 本轮做了什么

- 冻结兼容基线：C# 导出恢复策略、认证阻断、提交预算、DPAPI 与清单五类夹具，Rust 逐字段回放。
- 新建 Rust workspace：`autonet-contracts`（DTO 与白名单）、`autonet-core`（`RecoveryPolicy` 移植）、
  `autonet-storage`（只读状态/日志/认证记录/预算/DPAPI）。
- 新建 `apps/desktop/`：Tauri 2 + React 19 + TypeScript，四个只读命令（契约版本、快照、服务状态、日志摘要），
  基础窗口、托盘、单实例、关闭即隐藏；产品身份 `AutoNet Preview` / `com.autonet.preview`。
- 新增独立 CI（`rust-tauri.yml`），未改 `.NET` 的 `windows.yml`、`release.yml`，未改安装器与自启。
- 新增脚本：`build-rust.ps1`、`qa-crosslang-parity.ps1`、`qa-tauri-ui.ps1`。

## 安全边界（本轮不开放）

预览不替换 Windows 服务，不读写 `C:\ProgramData\AutoNet\private`，不提交凭据，不断开 Wi-Fi，
不在 React WebView 中加载校园门户，不写注册表 Run，不生成安装器。命令面固定为只读；状态投影丢弃
账号、网卡、配置名、代理、UU 地址、`Detail` 与 `LastError` 等自由文本。缺失、损坏、拒绝访问、过期
分别显示为受控状态与原因码。

## 兼容夹具

| 文件 | 内容 | 用例数 |
| --- | --- | --- |
| `fixtures/cross-language/recovery-policy.json` | 观察序列 → 动作/原因/退避/断网预算 | 13 |
| `fixtures/cross-language/authentication-block.json` | 版本化认证暂停与历史标记 | 37 |
| `fixtures/cross-language/submission-budget.json` | 提交预算记录与降级 | 20 |
| `fixtures/cross-language/status-log-projection.json` | C# `StatusSnapshot`/`ActivityLog` 真实序列化结果的脱敏投影（含敏感哨兵） | 2 |
| `fixtures/cross-language/dpapi-credential.json` | LocalMachine DPAPI 明文摘要（仅本机） | 1 |
| `fixtures/cross-language/install-lifecycle.json` | 服务、路径、ACL、自启契约（手工维护） | — |

`qa-crosslang-parity.ps1` 会重新导出这些文件与 `manifest.json` 做逐字节比对，再运行 Rust golden 测试，
并核对 `install.ps1` 中的服务名、显示名、启动类型、依赖、失败重启、bundle 路径、Run 键与 ACL 调用。
投影夹具在编写时暴露并修正了一个真实缺陷：Rust 曾用门户阶段白名单过滤 `SlowestStage`，
会把 `InternetProbe` 等周期阶段名清空；现已拆分为独立白名单并由夹具锁定。

## 验证记录

在本机（Windows 11 x64，无 Visual Studio/MSVC；.NET 10.0.401 安装于用户目录，Rust 1.100.0）
实际运行并通过：

| 命令 | 结果 | 证据 |
| --- | --- | --- |
| `pwsh scripts/build.ps1` | 0 警告，`633/633 passed` | `artifacts/validation/` |
| `pwsh scripts/build-rust.ps1`（纯 crate） | rustfmt、clippy `-D warnings`、29 个 Rust 测试通过 | `artifacts/validation/rust/` |
| `pwsh scripts/qa-crosslang-parity.ps1` | 5 个夹具逐字节一致；安装契约一致；Rust golden 通过 | `artifacts/validation/crosslang/` |
| `pwsh scripts/qa-tauri-ui.ps1 -SkipInstall` | typecheck、10 个 Vitest 用例、Vite 构建通过 | `artifacts/validation/qa-tauri-ui/` |
| `cargo check/clippy -p autonet-preview`（GNU） | Tauri 外壳的 Rust 代码通过类型检查与 `-D warnings` | 本机临时运行 |
| `AUTONET_DPAPI_SAME_MACHINE=1 cargo test -p autonet-storage` | 同机 DPAPI 解密夹具通过 | 本机临时运行 |

Rust 测试合计 29 个（contracts 单元 7 + 投影 3、core 单元 3 + golden 3、storage 单元 9 + golden 4）。

### CI 证据（PR #64，提交 `bbf5c14`）

| 工作流 | 结果 | 链接 |
| --- | --- | --- |
| Rust / Tauri read-only preview（PR run） | 两个 job 均通过，含 MSVC 下的 `pnpm tauri build --no-bundle` | https://github.com/yuwen773/auto-net/actions/runs/45122072881 |
| Windows offline regression（PR run） | `build` 与 `install-smoke` 均通过 | https://github.com/yuwen773/auto-net/actions/runs/45122072762 |
| 同提交 push run | 两个工作流均通过 | https://github.com/yuwen773/auto-net/actions/runs/45122034771 · https://github.com/yuwen773/auto-net/actions/runs/45122034757 |

### 验收状态（M1）

| 项 | 状态 | 依据 |
| --- | --- | --- |
| C# 回归与既有 Windows CI | **PASS** | 本机 `633/633`；CI `build`、`install-smoke` 通过 |
| Rust fmt/clippy/test | **PASS** | 本机 29 个测试；CI parity 与 workspace job 通过 |
| React typecheck/test/build | **PASS** | 本机 10 个 Vitest；CI 前端步骤通过 |
| 跨语言夹具与安装契约 | **PASS** | 本机与 CI parity 均通过 |
| Tauri Windows 原生构建 | **PASS（CI）** | MSVC runner 上的 `tauri build --no-bundle` 通过 |
| Tauri 原生启动、GUI、托盘、单实例、关闭隐藏 | **PASS（主机）** | 2026-11-23 在本机以普通用户实跑，见下节 |
| 真实非管理员 / UAC / 高 DPI | **PARTIAL（主机）** | 非管理员启动通过；未触发 UAC；主机 100% DPI，125%/150% 待隔离 VM |
| 隔离 Windows 11 VM 共存验收 | **NOT_RUN** | 当前环境没有可用 VM；主机结果不能替代 |
| 真实安装/升级/卸载、实网认证 | **NOT_RUN** | 按边界不在本机执行 |

### 主机普通用户非破坏性运行验收（2026-11-23，SAIL）

本节是**主机验收**，不等同于隔离 Windows 11 VM 共存验收；M1 不因本节关闭。

环境与方法：Windows 11 家庭版 26200，用户 `SAIL\yuwen`（`isAdmin=False`），系统缩放 100%（96 DPI），
WebView2 `158.0.4370.33`。运行对象是 CI run
[45123544743](https://github.com/yuwen773/auto-net/actions/runs/45123544743) 的 `autonet-preview` artifact，
`head_sha` `b2357807e34e74af59cdd9342ce5d761d39d2cf0`；artifact zip SHA-256
`2BFA6F571A54EF83206FF65ED7066DE074F686DFEC48E958F6EAB33C64CFF19D`，解压出的
`autonet-preview.exe` SHA-256 `E2E8CF14F949053FD354F9E1FCC68B83823E1AFD6F55DAAC811BF14B5DEEBACA`，
产品名 `AutoNet Preview`，文件版本 `0.1.0`，`NotSigned`。该文件是 CI 构建产物，不是正式发布件。

基线：`AutoNet` 服务 `Running`（PID 15508），HKLM Run 项仍为
`"C:\Program Files\AutoNet\tray\AutoNet.Tray.exe" --tray`；`C:\ProgramData\AutoNet\private` 对普通用户
ACL 拒绝；无 `autonet-preview` 进程或托盘图标。全程未执行 `install.ps1`、未停止/启动服务、
未读取/写入 `private` 内容（仅尝试读取 ACL 以确认普通用户被拒绝）、未提交凭据、未主动触发联网恢复。

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 普通用户启动、无 UAC | **PASS** | 进程未提权；窗口标题 `AutoNet Preview（只读）` |
| 只读状态页与边界说明 | **PASS** | 显示“校园网在线”“AutoNet · 已安装”“正在运行”与“只读边界”，无保存/诊断入口 |
| 托盘图标与菜单 | **PASS** | 溢出区可见 `AutoNet Preview（只读）`；菜单为“显示主窗口”“退出预览” |
| 关闭即隐藏 | **PASS** | 发送 `WM_CLOSE` 后主窗口 `IsWindowVisible=False`，进程仍存活 |
| 托盘左键恢复 | **PASS** | 点击托盘图标后主窗口重新可见 |
| 单实例 | **PASS** | 再次启动 `autonet-preview.exe` 以退出码 0 结束，原窗口恢复，进程数保持 1 |
| 主题切换 | **PASS** | `浅色`/`深色` 可切换并立即重绘；键盘 Space 可激活；重启后仍保持深色 |
| 日志页 | **PASS** | 当日尾部 200 条、可切到 2026-11-22、可刷新；仅显示白名单字段 |
| 键盘顺序 | **PASS** | Tab 顺序为“跟随系统 → 浅色 → 深色 → 刷新状态 → 日期 → 刷新 → 日志列表” |
| 最小/大窗口 | **PASS** | 900×620（配置最小值）与 1600×1000 均正常重排，无崩溃 |
| 现有服务独立性 | **PASS** | 服务 PID 15508 全程及退出后均为 `Running`；Run 项不变；非日志稳定文件哈希前后一致；仅当日日志追加 |
| 高 DPI 125%/150% | **NOT_RUN** | 主机固定 100%；更改缩放需注销/重登，留待隔离 VM |
| UAC/提权路径 | **NOT_RUN** | 预览以 `asInvoker` 普通用户运行，未触发 UAC |
| 隔离 VM 共存 | **NOT_RUN** | 当前环境无可用 VM |

现场事件说明：验收窗口内（15:02:03–15:02:40）现有 C# 服务按其自身策略完成了一次门户 `Transport`
失败后的恢复动作：15:02:33 开始恢复并写入安全状态，15:02:36 请求 Wi-Fi 断开/重连，随后状态变为
“已切换其他 Wi-Fi”，`DisconnectAttempts=1`。这些动作由服务进程自主执行，不是预览命令；预览的四个命令
均为只读，没有服务控制、Wi-Fi 写入或凭据提交面。记录此事件是为了说明主机不是静默环境，不能把它当作
预览触发的恢复测试；本次日志中未观察到 `Submission` 阶段的认证提交事件。隔离 VM 验收仍应在服务静默或
可控条件下重做。

普通用户无法读取 `private`，因此不能做该目录内容的前后哈希比对；只能确认 ACL 始终拒绝、目录 mtime 在
15:02:36 由服务自身的安全状态写入更新。非 `private` 的稳定文件哈希前后一致；当日日志由 6,759,031 字节
增长到 7,024,560 字节，旧日志未变。截图、UI 树与 before/after JSON 保存在本地 gitignored 的
`artifacts/acceptance/host-2026-11-23/`，未提交，避免把桌面截图带入仓库。

结论：主机非破坏性运行验收通过；隔离 Windows 11 VM 共存仍未验证。M1 保持打开，PR #64 保持 Draft，
不启动 M2 功能迁移。

## 独立评审后的修复

首轮实现完成后做了一次只读代码评审，以下问题已修复并补了回归：

- 认证阻断与提交额度的 `Revision` 之前只检查存在或宽松的十六进制形状；C# 用 `Guid` 反序列化，
  非标准写法应视为无效并保持阻断。Rust 现在只接受标准 36 字符 `D` 形式，夹具新增四种坏 `Revision` 用例。
- Rust 日志动作白名单漏了 `NetworkDisplayMap.KnownStates`（服务会把状态名直接写成日志动作），
  导致“校园网在线”等被降级为“其他事件”；已补齐并逐个状态断言。
- 日志行现在要求 `time`/`action`/`detail` 各恰好一次且时间不是 `DateTimeOffset.MinValue`，
  与 C# `LogTailReader` 的拒绝条件对齐；状态时间同样拒绝默认值。
- 黄金夹具改为固定 LF，并新增 `.gitattributes`，避免 `autocrlf` 让 CI 的逐字节比对误报漂移。
- 跨语言校验现在也运行 `autonet-contracts` 的投影测试；预览读取失败时会清空旧状态/旧日志，
  不再在“无法读取”提示下继续展示上一次内容。
- 凭证解密模块改为 `dpapi` 特性，Tauri 预览关闭该特性，解密代码不进入预览二进制。

仍未覆盖：C# 对畸形 `Diagnostics` 的拒绝条件尚未在 Rust 日志摘要中复刻；该字段从不进入投影，属于解析严格度差异，不影响输出安全。

## 未运行（NOT_RUN）

- **隔离 Windows 11 VM 共存验收**：当前环境没有可用 VM；主机普通用户验收不能替代隔离环境证据。
- **高 DPI 125%/150% 与真实 UAC 提权**：主机固定 100% 缩放，预览以 `asInvoker` 运行，未触发 UAC。
- **真实安装/升级/卸载**：按要求不在本机执行 `install.ps1`；预览不生成安装器，也未注册服务。
- **主动的校园网在线/离线现场验收**：未由本次验收主动执行会改变连接或提交凭据的实网测试；
  验收窗口内现有服务自身发生过一次恢复动作（见上节），不计为预览验收结果。
- 浏览器版回退页只在单测中覆盖，未在真实浏览器做端到端检查。

## 已知限制

- 预览读取的是现有服务的 `status.json` 与日志；服务未运行时显示受控提示，不代替服务。
- 日志页只显示当日最新一段（尾部窗口），会显式标注跳过条数与是否完整。
- `contract.version` 与快照协议版本不一致时顶部告警，不尝试兼容未知字段。
- Rust `RecoveryPolicy` 是行为对照实现，尚未接入服务；C# 仍是唯一运行路径。

## 回滚

删除 `apps/desktop/`、`crates/`、`fixtures/`、Rust 工作区文件与新增 CI 即可回到基线；
现有服务、安装目录、注册表和自启未改动，无需数据迁移。
