# #31 提交额度记录与持久化实施记录

日期：2026-10-28。已核对 GitHub [#31](https://github.com/yuwen773/auto-net/issues/31) 正文与评论、父规格 #27 和直接下游 #32。#31 开放且无前置阻塞，无评论修订；#32/#36/#42 依赖本工单。

起点：`codex/ui-avalonia`，HEAD `475f167f29ae82a49144f3c2e7fe521d7c4b6999`。已有 37 个修改/未跟踪文件，包括 #30 的完整未提交实现与证据。PowerShell 7.6.5、仓库 .NET SDK 10.0.401。基线补丁、清单、逐文件 SHA256 和需要叠加的小段文件副本在忽略目录 `artifacts/issue31/`。

## 实现

新增 `SafetyStateStore.SubmissionBudget.cs`，原类仅加 partial，复用其 `auth-state.lock`、受限目录和 `Storage.AtomicWrite`。六字段记录严格校验，持久预留才授予成功，CAS 状态推进不扣回次数，回拨和失效记录保守拒绝。未接入服务、协议或内嵌认证，不实施 #34 的审计重置。契约与时间边界见 [ADR-0013](../adr/0013-submission-budget-storage.md)。

用户确认以公开读取/预留/修订比较 API、假时钟、隔离目录及真实子进程为测试边界。新增 `SubmissionBudgetRegression`，现有 runner 按 `--filter 提交额度：` 运行，测试子进程入口只在测试项目中。

## 验证与审查

已完成 7 轮 red → green，覆盖首次持久预留、修订推进、活动/120秒/三次上限、时钟回拨、损坏记录矩阵、锁与原子写失败、无效时刻。[红绿循环汇总](../baselines/2026-10-28-issue31-tdd-cycles.txt)；原始日志在 `artifacts/issue31/cycle*-{red,green}.txt`。

额外真实跨进程与六字段记录验证均通过；最终专项 **9/9**：[完整专项清单](../baselines/2026-10-28-issue31-targeted-tests.txt)。所有持久层时间检查使用假时钟快照；两进程竞争使用真实 Windows 文件锁和同步屏障，不以同进程任务代替。

最终 `scripts/build.ps1` 完整 Release 构建 **0 警告 / 0 错误**，全量 **377/377**，脚本及测试退出码均为 0：[逐条清单](../baselines/2026-10-28-issue31-tests.txt)、[同次构建标识](../baselines/2026-10-28-issue31-build.json)。结果对应起点 HEAD 加本机未提交工作树，包含先前 #30 及其他改动，不是该提交单独的结果，不代表候选包或部署验证。

原有 37 个修改/未跟踪文件的内容 SHA256 核对保留：`Program.cs` 测试入口、`CONTEXT.md` 术语和 `docs/VALIDATION.md` 验证索引叠加本轮唯一小段，去除本轮小段后与开始时副本逐字节一致；其余 34 个文件直接一致。证据保留在 `artifacts/issue31/preservation.json`。原 `SafetyStateStore.cs` 是起点干净文件，本轮仅增加 partial 修饰。

## Standards

独立规范轴审查：**0 个规范违反、0 个需处理的代码异味**。partial 拆分、独占锁/原子写/异常风格、中文回归及真实子进程边界符合现有仓库规范；既存 #30 等变更未计入本轮。

## Spec

独立规格轴审查：**0 个发现**。独立六字段记录、严格失效关闭、锁内读取/预留/修订转换、持久成功后返回预留、120 秒边界、三次上限和回拨等待均已实现；服务/载体接入与完整单调等待明确留给后续工单。

双轴汇总：Standards **0**，Spec **0**；没有遗留可行动审查问题。

## 限制

本工单是持久层能力，不代表当前服务已启用有限重试。墙钟前跳、慢 I/O、跨重启的完整单调等待及实际发送边界属于 #32，载体共享接入属于 #33；升级/回滚属于 #36。没有运行真实门户、网络断连、UAC、安装或服务替换，没有暂存、提交、推送或发布。
