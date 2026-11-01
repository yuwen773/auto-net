# #33 通道载体接入共享额度实施记录

日期：2026-10-28。已核对 GitHub [#33](https://github.com/yuwen773/auto-net/issues/33) 正文、空评论、父规格 #27、验收标准及依赖 #32。本轮继续使用 #30/#31/#32/#38 的本地未提交工作树，不把工单标签或其它工单的验证结果当作本工单的合并、发布证据。

起点：`codex/ui-avalonia`，HEAD `475f167f29ae82a49144f3c2e7fe521d7c4b6999`，工作树已有 35 个修改与 30 余个未跟踪项。仓库 SDK 10.0.401。本轮开始时保存了逐文件快照、SHA256、状态清单与补丁，位于忽略目录 `artifacts/issue33-start/`；运行产物在 `artifacts/issue33/`。

## 验收标准对照

- 协议与通道同一份额度记录与同一把锁：协议走 `submission-budget.json` + `submission-execution.lock`；通道的 `TryBeginChannelSubmission` 在同一认证锁内比较认证块与额度修订、取得执行锁、持久预留并把认证状态 CAS 为 `SubmissionPending`。
- 协议两次后通道只剩一次、耗尽不交付：共用 `Attempts` 与 120 秒间隔；通道先判 `Exhausted` 才可能进入填表。
- 降级不额外产生机会：通道不初始化独立计数，只预留同一记录。
- 并发只有一次通过预留：执行锁先于预留取得，额度比较用修订/CAS。
- 回归覆盖跨载体共享与并发：`共享额度：` 专项 22 条，含协议 Worker 与通道工作流并发、两个独立 Store 竞争、真实子进程会话身份。

## 实现

`SafetyStateStore.ChannelSubmission.cs` 提供通道租约；`AuthChannelWorkflow.cs` 是可测试的通道准入链（可信边界、设置/凭据/Wi-Fi、外网不可达、门户结构性失败、租约、最终复核）。`AuthChannelWindow` 在授予后运行该工作流，只有 `Submitted` 才由工作流调用 `SubmitAsync`；未取得租约不会填表。`SubmissionExecution.cs` 以 `submission-channel-session.json` 绑定浏览器与辅助进程身份，进程异常退出时保守计数并重新等待。

本轮补上缺失的通道启动互斥：`SafetyStateStore.ChannelLaunch.cs` 提供 `TryAcquireChannelLaunch` / `IsChannelLaunchActive`；辅助进程在 `ExecuteAsync` 开始时持有 `auth-channel-launch.lock`，托盘在发起前只读探测，`Busy` 时消费本次授予。取不到启动锁的第二个辅助进程不写结果文件，避免覆盖第一个的回执。决定见 [ADR-0015](../adr/0015-channel-shared-submission.md)。

## TDD 与验证

起点工作树编译失败：已有红灯用例引用尚未实现的 `TryAcquireChannelLaunch`（2 个 CS1061）。先实现该契约转绿，再补充两条用例：启动锁占用/释放的探测语义、托盘注入外部启动锁时不发起。

专项结果：`共享额度：` **22/22**、内嵌通道 **23/23**、有限重试 **17/17**、提交额度 **11/11**。最终 `scripts/build.ps1` 完整 Release 构建 **0 警告 / 0 错误**，全量 **427/427**，脚本与测试退出码均为 0：[全量清单](../baselines/2026-10-28-issue33-tests.txt)、[同次构建标识](../baselines/2026-10-28-issue33-build.json)、[共享额度专项](../baselines/2026-10-28-issue33-targeted-tests.txt)。

未运行真实门户、真实 WebView2 窗口链路、断网、UAC、安装或服务替换。真实窗口到工作流的接线没有进程级回归；工作流本身由假浏览器与真实子进程身份夹具覆盖。

## Standards

独立规范轴审查发现的可行动项已修复：托盘 `Describe().Busy` 现在包含外部启动锁；`Busy` 分支先清空旧结论；`IsChannelLaunchActive` 的注释与“权限异常按不忙处理”的实现一致；测试块缩进、误导性断言文案与名不副实的会话用例已更正（并修正注册参数顺序）。未改动但记录在案的判断项：`IMonitorEnvironment` 为新能力保留安全默认实现（未接入通道的假环境只会得到“拒绝”）；`ChannelSessionEnded` 在会话存在而额度缺失时保持阻断，属有意的保守策略；`AuthChannelWindow.ExecuteAsync` 较长且仍有直接存储/Wi-Fi 读取，属既有结构，未在本轮重构。

## Spec

独立规格轴确认五条验收标准均已实现，并逐项驱动了预留、耗尽、并发与会话结束场景。遗留边界：#33 的通道准入链与协议复核链仍是两条独立实现（共用额度/锁/认证状态，但不共用代码）；真实窗口链路未做进程级回归；通道夹具页恢复与成功页判定属 #38/#39 的未提交改动，`scripts/qa-authchannel.ps1` 仍按旧夹具契约运行，已知但不在本工单范围。

## 改动保留

本轮只新增 `SafetyStateStore.ChannelLaunch.cs`、ADR-0015、本记录与三份基线，并修改 `AuthChannelWindow.cs` 的启动锁接线、`AuthChannelLauncher.cs`/`App.axaml.cs` 的探测注入、两份回归与 `CONTEXT.md`/`docs/VALIDATION.md` 的 #33 说明。其余 #30/#31/#32/#38 未提交改动未被覆盖或回退。未暂存、提交、推送或关闭工单。

构建与回归证据对应起点提交加本机未提交工作树，包含其它工单改动，不能由该提交单独复现，不代表发布包或生产验收。
