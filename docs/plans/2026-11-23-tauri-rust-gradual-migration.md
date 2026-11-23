# AutoNet 2.0：Tauri 2 + React + Rust 渐进迁移计划

2026-11-23 立项，对应 [ADR-0022](../adr/0022-tauri-rust-gradual-migration.md) 与 [ADR-0023](../adr/0023-readonly-preview-boundary.md)。本文件取代此前的草案，作为仓库内的评审与拆单依据。

## 1. 范围

- **目标**：桌面 UI 迁移到 Tauri 2 + React/TypeScript；业务引擎最终迁移到 Rust；Windows Service 仍以 SCM 独立进程运行；认证辅助保留受控代理与提交额度链；发行收敛为单一安装器。
- **非目标**：不同时做 Linux/macOS；不引入网络新功能；不提高提交频次；不以“Rust 更快”作为验收条件；不在迁移中并行运行两个可写守护。
- **平台**：Windows 11 x64。Windows 10 与其他架构需单独补测。

## 2. 目标目录

```text
apps/desktop/            Tauri 2 + React + TypeScript 预览（P1）
  src/                   React 页面与命令 DTO
  src-tauri/             Tauri Rust：commands、tray、capabilities
crates/
  autonet-contracts/     版本化 DTO、脱敏投影、协议版本
  autonet-core/          纯恢复策略与时间语义（P0 起）
  autonet-storage/       只读历史状态解释、日志尾部读取（P0/P3）
  autonet-network/       WLAN/DNS/HTTP（P3）
  autonet-auth/          Dr.COM（P3）
  autonet-ipc/           身份核验与授权（P2b）
  autonet-service/       Windows Service（P4）
  autonet-authchannel/   独立认证辅助（P5）
fixtures/cross-language/ C# → Rust 黄金语料
installer/               事务安装助手（P6）
```

新依赖一律固定 `Cargo.lock`、`pnpm-lock.yaml` 与 Rust/Tauri 版本；不以“跟最新”替代可复现构建。

## 3. 兼容红线

| 契约域 | 必须保持 | 不允许的回归 |
|---|---|---|
| 服务 | 名称 `AutoNet`，SYSTEM，`Automatic`，依赖 `WlanSvc`/`Tcpip`，5/15/60 秒重启 | UI 退出导致服务停止；两个恢复服务 |
| 安装路径 | `C:\Program Files\AutoNet\{service,tray,authchannel}\*.exe`（过渡期不变） | 旧路径遗失；混装；失败不可回退 |
| 数据权限 | `ProgramData\AutoNet\private` 仅 Administrators 与 SYSTEM；`status.json`/`logs` 保留普通用户只读 | 给 Users 写 private 或安装目录 |
| DPAPI | `LocalMachine`，`optionalEntropy = null`，既有密文默认不改写 | 明文降级；解密失败即抹除；自动重加密 |
| 业务配置 | `settings.json` 字段名、默认值、验证规则、`AuthChannelMode` 三态；旧数据只读不迁移 | 保存设置清除暂停或重置额度 |
| 认证状态 | `auth-block.txt` `Version=1` 七字段语义；未知/损坏保守阻断 | 未知状态当无阻断；删除原始证据 |
| 提交额度 | `submission-budget.json` 0–3 次、120 秒冷却、`Reserved/PossiblySent/Concluded`；两把锁的一致性 | 重启重新计数；双通道各自额度；丢回执重复发送 |
| 认证通道 | 凭据经 stdin，不进命令行；结果文件契约；异常退出按可能已发送处理 | 凭据注入系统浏览器；门户页获得 Tauri IPC |
| 网络 | 固定门户、允许列表、`IP_UNICAST_IF`+源地址绑定、校园网卡 DNS、禁重定向/代理回退 | 绑定失败改走 TUN；受限代理失败改直连 |
| 界面启动 | 托盘、单实例、`--tray`/`--settings`、`HKLM Run`、公共开始菜单 | 登录前强制显示；重复托盘/通知 |
| 卸载 | 默认保留配置、密文、日志；显式清除需二次确认；先停服务再删程序 | 删除私有数据；僵尸服务；未停服务删 EXE |

历史状态金标准：[ADR-0020](../adr/0020-legacy-state-upgrade-rollback.md)、`scripts/check-released-reader.ps1`、`tests/AutoNet.Tests/LegacyStateRegression.cs`。Rust 读取器必须对照真实旧版语料，不能按 schema 猜测。

## 4. 阶段与 Gate

### P0 — 冻结兼容基线（首个 PR）

- 新增 ADR、本计划、服务/状态/配置黄金夹具。
- 抽取 C# 测试驱动器导出稳定 JSON（隔离目录、无真实网络）。
- 新增跨语言对照脚本；现有 C# CI 与 `check-released-reader.ps1` 不动。
- **通过**：RecoveryPolicy、旧状态、提交额度、DPAPI 同机密文、安装路径与权限均有夹具；C# 侧导出一致；无网络副作用。
- **回退**：删除新文件。

### P1 — Tauri 只读预览（独立 PR）

- `apps/desktop/`：只读状态、脱敏日志、服务运行状态、WebView2 检测、主题、托盘、单实例、关闭隐藏。
- 使用独立名称与互斥体；不改 `HKLM Run`；C# 托盘仍是正式 UI。
- **通过**：普通用户可运行；退出不影响服务；无 `private` 读写；缺失/损坏/拒绝访问明确显示；前端无法触发认证或断网；Windows 11 x64 实机/VM 视觉与键盘测试。
- **回退**：移除预览应用。

### P2 — Tauri 正式 UI 功能对等（仍用 C# 服务）

- 逐页迁移总览、设置、日志、诊断、通知、托盘、认证页状态。
- 写操作先保留旧 C# 设置窗口跳转；独立 Rust↔提权助手交接须单独安全审查。
- 门户仍由现有 WebView2 C# 辅助进程承载，不进入拥有 Tauri API 的 React 页面。
- **通过**：`QaOverview`/`QaSettings`/`QaLogs`/`QaDiagnostics`/`QaNotifications`/`QaAuthPage` 行为全部有新测试；UAC 取消/超时、保存结果未知、单实例、日志隐私、通知去重、认证页禁用均有证据。
- **回退**：恢复 C# 托盘入口，不替换 SCM 服务、不改私有格式。

### P3 — Rust 核心库行为等价（服务不切换）

- 优先级：`autonet-contracts` → 纯策略/冷却/时间 → 兼容读取/日志 → DPAPI/原子写/锁 → WLAN/DNS/HTTP → Dr.COM。
- 只读诊断 CLI 与 C# `--diagnose`/`--diagnose-portal` 分型对比；不主动断网、不写安全状态、不提交凭据。
- **通过**：关键策略与安全夹具 0 个未解释差异；未知状态一致 fail-closed；按字节验证“只读不重写”；锁争用、时钟跳变、崩溃恢复、网卡绑定、TLS/重定向均有独立用例；已发布 DPAPI 密文同机只读解密且明文不进日志。
- **回退**：关闭 Rust 对照，C# 继续控制网络。

### P4 — Rust 服务单写者切换

- 使用 Windows Service API，保留服务名 `AutoNet`；先停 C# 服务再替换，绝不同时运行。
- 初期继续与已验证的 C# 认证辅助协作。
- **通过**：额度、暂停、DPAPI、断网上限、0/1/多用户登录、注销后守护、SCM 失败恢复、断电/崩溃、安装失败恢复旧服务与数据。
- **回退**：停 Rust，恢复 C# 服务、文件与设置；先证明旧 reader 能安全读取 Rust 数据。

### P5 — Rust 认证辅助与认证页

- 独立 EXE 宿主 WebView2、受限代理、进程作业、会话目录、结果文件；复用共享额度。
- **通过**：单页最多一次自动提交；UAC 取消不重试；两载体总额度不超限；人工挑战可用；远程页不能调用 Tauri 命令；代理失败无直连回退；TUN/UDP 出口有现场证据。
- **回退**：恢复兼容的 C# 辅助进程，不动持久阻断。

### P6 — 统一安装器与 Rust-only Release

- Tauri NSIS `perMachine`，事务安装助手负责暂存、预检、停旧、备份、替换、注册、启动、验证、提交、逆序恢复。
- **通过**：全新、ZIP→NSIS、同版重装、跨版升级、坏包回滚、离线 WebView2、无 PS7、无 .NET、普通用户启动/提权、卸载保留与显式清数据全部通过；发布带 SHA256SUMS/BUILD-ID。
- **回退**：提供最后兼容的 C#/混合安装包与人工恢复流程。

## 5. 验收体系

| 层 | 验收 | 阻断条件 |
|---|---|---|
| 纯策略 | C# 测试 ↔ Rust golden corpus | 不一致未解释；安全语义更宽松 |
| 数据兼容 | 真实旧 reader、损坏语料、原子写/锁、DPAPI 同机与旧版回退 | 未知状态自动恢复；读时改写；密文损坏 |
| 进程安全 | 管道身份/ACL/会话/挑战/重放/超时、UAC 取消、Tauri capability 最小化 | 提权绕过；秘密进命令行/日志；未知结果重发 |
| 网络隔离 | 假 DNS/HTTP/门户、网卡绑定、无默认代理/重定向、WebView2 代理失败 | 绑定失败静默改走 TUN/系统路由 |
| UI | React 单元/端到端、Windows GUI 截图/键盘、托盘、通知与日志脱敏 | 关闭 UI 停服务；重复托盘；敏感状态泄露 |
| 真实安装 | GitHub 托管一次性 runner + 隔离 VM 真实 UAC | SCM/ACL/自启/升级/卸载/回滚不一致 |
| 发布 | commit/tag 与 BUILD-ID/SHA256 一致 | 不能追溯；损坏；缺 SHA |
| 现场 | 人工脱敏证据：断线恢复、未登录认证、真实 Dr.COM、抓包 | 未通过时不得声明现场全部验收 |

恢复时间沿用现有计时起点，分别记录“发现故障→恢复”和“真实断开→恢复”；不得用不同起点的更短耗时宣称性能提升。

## 6. 第一个迭代（已交付内容）

1. `docs/adr/0022-tauri-rust-gradual-migration.md`、`docs/adr/0023-readonly-preview-boundary.md`。
2. 本文件。
3. `apps/desktop/`：只读预览、独立身份、托盘与窗口生命周期。
4. `fixtures/cross-language/`：恢复策略、历史认证状态、提交额度、DPAPI 已发布数据、安装生命周期五类夹具；C# 导出器 `tests/AutoNet.Tests --export-golden-fixtures`。
5. `crates/autonet-contracts`、`autonet-core`、`autonet-storage`：只读对照实现与 golden 测试。
6. `scripts/qa-crosslang-parity.ps1`、`scripts/qa-tauri-ui.ps1`、`scripts/build-rust.ps1`、`.github/workflows/rust-tauri.yml`。
7. 证据与 `NOT_RUN` 清单：`docs/progress/43-tauri-readonly-preview.md`。

明确不做：替换 SCM 服务、修改 `install.ps1` 正式行为、读写 `private`、自动断网/提交凭据、把门户加载进 Tauri WebView、调整正式开机自启。

## 7. PR 拆分

1. `P0 / ADR + compatibility fixtures`（不触发布程序）
2. `P1 / Tauri read-only preview`
3. `P2a / React full read-only pages`
4. `P2b / safe credential handoff`
5. `P2c / UI cutover + hybrid installer candidate`
6. `P3a / Rust policies + golden`
7. `P3b / Rust storage + network + auth parity`
8. `P4 / Rust Service single-writer cutover`
9. `P5 / Rust AuthChannel`
10. `P6 / Rust-only NSIS + Release`

每个 PR 附：改动边界、未变契约、自动测试清单、手工 `NOT_RUN` 项、失败回退路径、Build-ID/提交关联；不得用一个 PR 同时改 UI、服务、认证安全状态和安装器。

## 8. Go / No-Go

只有同时满足以下条件才可宣布“完整 Rust 化”：新程序在无 .NET/无 PS7 的干净 Windows 安装运行；只有一个可执行断网/认证的 SCM 服务；UAC/ACL/DPAPI 无弱化；与真实旧版关键判定无未解释差异；Tauri 远程页无本地命令能力；升级/回退/卸载可复现；CI 与发布哈希齐全；现场认证、断线与远程验收明确 PASS 或 NOT_RUN。离线测试替代不了现场。

## 9. 参考

- [ADR-0007 UI 迁移范围](../adr/0007-ui-framework-migration-scope.md)
- [ADR-0012 设置与凭据交接](../adr/0012-settings-credential-handoff.md)
- [ADR-0015 通道共享提交额度](../adr/0015-channel-shared-submission.md)
- [ADR-0020 历史状态升级与回滚](../adr/0020-legacy-state-upgrade-rollback.md)
- [Tauri 2 Windows Installer / NSIS](https://v2.tauri.app/distribute/windows-installer/)
- [Tauri 2 Capabilities](https://v2.tauri.app/security/capabilities/)
- [windows-service crate](https://docs.rs/windows-service/latest/windows_service/)
