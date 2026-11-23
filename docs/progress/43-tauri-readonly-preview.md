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
| `pwsh scripts/qa-crosslang-parity.ps1` | 4 个夹具逐字节一致；安装契约一致；Rust golden 通过 | `artifacts/validation/crosslang/` |
| `pwsh scripts/qa-tauri-ui.ps1 -SkipInstall` | typecheck、10 个 Vitest 用例、Vite 构建通过 | `artifacts/validation/qa-tauri-ui/` |
| `cargo check/clippy -p autonet-preview`（GNU） | Tauri 外壳的 Rust 代码通过类型检查与 `-D warnings` | 本机临时运行 |
| `AUTONET_DPAPI_SAME_MACHINE=1 cargo test -p autonet-storage` | 同机 DPAPI 解密夹具通过 | 本机临时运行 |

Rust 测试合计 29 个（contracts 单元 7 + 投影 3、core 单元 3 + golden 3、storage 单元 9 + golden 4）。

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

- **Tauri Windows 原生构建与启动**：本机没有 MSVC 链接器。GNU 工具链能通过
  `cargo check -p autonet-preview` 与 clippy，但链接 cdylib 时报 `export ordinal too large`，
  因此外壳的可执行文件、WebView2 窗口、托盘、单实例与关闭隐藏均未在本机实跑。
  CI 的 `rust-tauri` job 使用 `windows-2025` 的 MSVC 覆盖构建，但尚未在本次会话中看到远端运行结果。
- **真实非管理员账户启动**、真实 UAC、真实 Windows 11 VM 的视觉/键盘/高 DPI 检查。
- **真实安装/升级/卸载**：按要求不在本机执行 `install.ps1`；预览不生成安装器，也未注册服务。
- **校园网在线/离线现场验收**：未做任何会改变连接或提交凭据的实网测试。
- 浏览器版回退页只在单测中覆盖，未在真实浏览器做端到端检查。

## 已知限制

- 预览读取的是现有服务的 `status.json` 与日志；服务未运行时显示受控提示，不代替服务。
- 日志页只显示当日最新一段（尾部窗口），会显式标注跳过条数与是否完整。
- `contract.version` 与快照协议版本不一致时顶部告警，不尝试兼容未知字段。
- Rust `RecoveryPolicy` 是行为对照实现，尚未接入服务；C# 仍是唯一运行路径。

## 回滚

删除 `apps/desktop/`、`crates/`、`fixtures/`、Rust 工作区文件与新增 CI 即可回到基线；
现有服务、安装目录、注册表和自启未改动，无需数据迁移。
