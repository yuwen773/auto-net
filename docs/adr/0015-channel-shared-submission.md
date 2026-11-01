# 内嵌通道与协议共用提交额度

2026-10-28，实施 [#33](https://github.com/yuwen773/auto-net/issues/33)，依赖本地已验证但未提交的 #31 持久层与 #32 协议接入。接续 [ADR-0013](0013-submission-budget-storage.md) 和 [ADR-0014](0014-bounded-submission-service.md)。

## 决定

- 普通协议与内嵌认证通道只使用一份 `submission-budget.json` 和一把 `submission-execution.lock`。通道不另建计数，也不因“降级到通道”获得新次数；协议用掉两次后通道只剩最后一次，次数用完通道不再向页面填入凭据。
- 通道授予（`AuthChannelRequested`）只是启动意图，不是提交授权。`SafetyStateStore.TryBeginChannelSubmission` 在 `auth-state.lock` 内比较调用方读到的认证块与额度修订，检查结构性预检失败和 `SubmissionRetryPolicy` 授权，取得执行锁后持久预留额度，再把认证状态 CAS 为 `SubmissionPending`；任一步失败返回 `null`，绝不降级为发送。
- `AuthChannelWorkflow` 是通道侧的准入链：可信网络边界、设置/凭据/当前 Wi-Fi 绑定、外网不可达、门户结构性失败，然后才是租约。交付凭据前再次复核设置、Wi-Fi、认证块、额度修订、取消与墙钟/单调时钟偏差；通过后才调用浏览器的 `SubmitAsync`。
- 通道登记 `submission-channel-session.json`，把浏览器进程与辅助进程身份绑定到这次预留。进程异常退出后不立即退还次数：只有确认会话进程全部结束，才把 `Reserved` 保守推进为 `PossiblySent` 并重新持久启动完整 120 秒等待。会话文件存在但额度缺失或损坏时保持阻断，交人工核查，不通过删除证据恢复。
- 新增 `auth-channel-launch.lock`：辅助进程在自己整个生命周期内持有，托盘只读探测。托盘重启后仍运行的辅助进程因此仍独占通道启动；第二个辅助进程取不到锁时不写结果文件（避免覆盖第一个的回执），以退出码 4 结束。探测遇到权限异常按不忙处理，避免 ACL 异常永久禁用通道；真实提交仍由执行锁与持久额度兜底。
- 通道明确需要人工（`NeedsHuman`）时保存认证暂停并结束额度；未知结果保持 `PossiblySent`，由既有复检链处理。明确成功仍只转 `Concluded`，审计重置归 #34。

## 边界

本次为未提交工作区中的离线实现，不替换已安装服务，未运行真实门户、断网、UAC、安装或服务替换。#34 审计重置、#36 升级/回滚、#38 认证页网络边界与 #39 页面状态投影仍独立。通道夹具页恢复与成功页判定不在本工单范围内。证据与审查结果见 [#33 实施记录](../progress/33-channel-shared-budget.md)。

> **修订（2026-11-01，工单 #42）**：#34 审计重置、#36 升级/回滚、#38 认证页网络边界与 #39 页面状态投影均已分别离线落线（未发布、未现场验收）。
