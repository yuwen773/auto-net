# Tauri 2 + Rust 渐进迁移的分段决策

2026-11-23 立项。本 ADR 冻结迁移的边界、阶段门槛与回退方式；详细设计与工单拆分见 [docs/plans/2026-11-23-tauri-rust-gradual-migration.md](../plans/2026-11-23-tauri-rust-gradual-migration.md)。首轮只交付 P0（兼容基线）与 P1（只读预览），不触达现有 SYSTEM 服务、安装脚本、凭据写入和真实认证。

## 基线冻结

- 源码基线：`main` 的 `43c3b0e4b23f0b3f0c080de8945664add7182a1d`（2026-11-22 UTC）。迁移夹具以该提交的 C# 行为为准。
- 最近已发布版本：`v0.4.0-rc.1`（2026-11-03），与上述源码基线不是同一份产物；任何“旧版读取”对照仍以真实 tag 为金标准（[ADR-0020](0020-legacy-state-upgrade-rollback.md)、`scripts/check-released-reader.ps1`）。
- 目标平台：已实测的 Windows 11 x64。Windows 10 与其他架构不因本次迁移默认获得支持。

## 决定

- **目的不是换语言，而是把不可回退的耦合拆开。** 现有 C# 服务、正式 Avalonia 托盘与安装脚本在 P0/P1 期间保持原样；新栈以独立目录、独立进程身份、独立 CI 任务并存。新栈只有在行为等价、证据齐全后才接管对应职责。
- **单个 PR 只改一个安全域。** UI、服务、认证安全状态、安装器不得在同一个 PR 里同时改变。推荐顺序固定为：P0 兼容基线 → P1 只读预览 → P2 只读全页 → P2b 受控交接 → P2c UI 切换 → P3 Rust 核心等价 → P4 服务单写者切换 → P5 认证辅助进程 → P6 统一安装器。
- **每一阶段都先定义回退。** P0/P1 的回退只是删除新文件；P4/P5/P6 的回退必须保留旧二进制、旧服务注册与可被旧版安全读取的数据，不能把“回退”建立在猜测之上。
- **兼容红线不因实现语言改变。** 服务名 `AutoNet`/SYSTEM/Automatic/依赖 `WlanSvc`、`Tcpip`/5-15-60 秒重启；旧安装路径；`private` 仅 Administrators 与 SYSTEM；DPAPI LocalMachine 与既有密文；`auth-block.txt` 的旧字段语义与保守阻断；提交额度 0–3 次与 120 秒冷却；门户来源、允许列表与网卡绑定；托盘单实例与旧启动参数；卸载默认保留数据。逐条清单在计划文档第 3 节。
- **历史状态以真实旧版为准。** Rust 读取器必须对照真实已发布 C# 版本的输入输出语料，不能用“按 schema 猜”替代。P0 的首批夹具由当前 C# 代码导出，并与 `check-released-reader.ps1` 的 v0.3.1 冻结读取规则互不替代。
- **Tauri 不获得通用执行能力。** 普通用户进程只暴露有界、白名单化的只读命令；不启用 `shell:allow-execute`、任意文件写入、服务控制或凭据解密。校园门户不加载进拥有 Tauri IPC 的 React WebView。
- **Windows Service 不是 Tauri sidecar。** 前端不得管理服务生命周期；服务由 SCM 持有。P4 切换时先停旧服务再替换，绝不允许两个可写守护同时运行。

## 首轮（P0 + P1）范围

- 新增本 ADR、迁移计划、首批跨语言黄金夹具。
- 新增 `apps/desktop/`：Tauri 2 + React/TypeScript 只读预览，使用独立产品名与单实例身份，不写 `HKLM Run`，保持 C# 托盘为正式 UI。
- 新增 `crates/`：`autonet-contracts`、`autonet-core`（纯恢复策略）、`autonet-storage`（只读历史状态解释）；Tauri 壳只做命令装配。
- 新增 `rust-tauri.yml` 与对应 QA 脚本；现有 `.github/workflows/windows.yml` 的 `build` 与 `install-smoke` 两条链保持不动。
- 输出预览构建、测试记录与 `NOT_RUN` 清单；不调整正式开机自启。

## 后果

- 仓库同时存在两套构建栈。CI 时间与本地工具链要求增加；README 与 `AGENTS.md` 需要说明何时运行哪一条命令。
- 兼容夹具成为长期资产：C# 行为漂移会被导出差异暴露，而不是等到真实回滚才发现。
- 预览应用可安全卸载，不影响服务；但它显示的只读状态不能替代现场验收，迁移完成前 15 秒恢复目标仍只由 `docs/VALIDATION.md` 的真实验收判定。
