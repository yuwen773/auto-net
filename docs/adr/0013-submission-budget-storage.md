# 独立提交额度记录与持久预留

2026-10-28，实施 [#31](https://github.com/yuwen773/auto-net/issues/31)，接续 [ADR-0011](0011-bounded-submission-retry.md) 与父规格 #27。#31 无前置阻塞，#32 有限重试/服务接入、#36 升级回滚、#42 交付检查依赖本工单。

## 决定

- 在 `SafetyStateStore` 的同一受限目录中新增 `submission-budget.json`，与 `auth-block.txt` 分开持久化、校验和升级，不读取预检次数来初始化提交次数，也不修改既有认证暂停。类以 partial 文件承载新增功能，原文件仅增加 partial 修饰。
- 固定六个字段：`Version`（1）、`State`（精确名称 Reserved / PossiblySent / Concluded）、`Attempts`（0–3）、`ReservedAt`、`NextPossibleAt`、`Revision`（非空 Guid）。字段无账号、密码、Cookie、门户响应或 URL。未知/重复字段、类型异常、缺字段、未知版本或不一致记录均拒绝，不通过忽略字段或默认值恢复额度。
- 零次记录仅允许 Concluded、两个时间均为默认值；有次数时必须有非默认预留时刻，最早下次时刻至少晚 120 秒。缺少文件表示尚无记录；不存在的目录、读写失败和锁失败不能当作缺少记录。
- 读取、预留和修订比较转换全部复用现有 `auth-state.lock`，以 `FileShare.None` 排他打开。该锁与认证安全记录串行化；两种提交载体应共用同一目录和同一存储 API，不各自建额度文件。
- `TryReserveSubmissionBudget(expected, now)` 在锁内重读并比较完整预期记录（含修订），检查时钟、额度、活动状态和持久冷却，生成新修订，调用既有 `Storage.AtomicWrite` 完成 flush + 原子替换后才返回 Reserved。并发变化返回 StateChanged；锁或存储失败返回 StorageUnavailable；损坏记录返回 InvalidState；均不授予提交许可。
- 活动 Reserved 返回 InFlight，不追加计数；PossiblySent 可在持久时间满足后追加预留；次数达到 3 返回 Exhausted；非零 Concluded 返回 Concluded，不自动重新开始。首次预留立即计入一次，即使后来进程退出，也不撤销。重启只读取原 Reserved，不改写为“未发送”；后续 #32 应按可能已发送处理并通过修订比较推进状态。
- 状态转换仅允许 Reserved → PossiblySent / Concluded，以及 PossiblySent → Concluded，固定已用次数和两个时刻，只换状态与修订。反向、未知状态或过时/伪造预期返回 false。没有通用覆盖、扣回次数或无审计重置 API；两条重置入口由 #34 单独实施。

## 时钟边界

持久层接受调用方的时间快照，写入规范 UTC。当前时刻早于上次持久预留时刻时返回 WaitClock，保留原始记录与等待；默认时间或无法容纳 120 秒的边界时间返回 InvalidState。持久 NextPossibleAt 的 119.999 / 120 秒边界由假时钟验证。

该检查**不取代** #32 的单调时钟与发送前安全复核：本工单没有将普通墙钟时刻包装成可信时间，也不承诺墙钟前跳、跨重启或慢 I/O 后两次实际发送仍相隔 120 秒。#32 必须在此持久层之外保守重建单调等待，并以“可能发出凭据”的真实边界判断，不能只比较 JSON 中的墙钟。

## 异常与边界

`LoadSubmissionBudget` 读取无效记录抛受控 InvalidDataException，锁/存储异常按既有存储 API 传播；调用方必须停止提交。`TryTransitionSubmissionBudget` 对存储和无效记录同样传播异常，CAS/状态不匹配返回 false。`TryReserveSubmissionBudget` 将无效记录与存储异常转换为受控结果；任何非 Reserved 都不授权发送。

保留旧认证安全记录与断线额度行为，不做迁移、自动恢复文件、服务接线、额度清零、门户认证或 UI 通知。继承受限目录的 ACL 与既有原子写模式，未新增权限配置或系统副作用。

## 证据

真实两个独立进程在屏障后同时预留，恰好一个成功；原进程退出后第三个进程读到 Reserved / Attempts=1 并拒绝额外预留。假时钟及隔离目录覆盖修订冲突、120 秒、三次上限、回拨、损坏矩阵、文件锁争用及真实原子替换失败。具体结果与范围见 [#31 实施记录](../progress/31-submission-budget.md)。完整恢复、实际提交性能与升级回滚仍需后续工单验证。

> **修订（2026-11-01，工单 #42）**：两条重置入口已由 #34 离线实施（门户明确成功、用户确认重新开始认证，均带审计与修订比较），未发布、未现场验收。
