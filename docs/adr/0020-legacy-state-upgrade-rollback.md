# 历史安全状态兼容、升级与回滚

2026-10-31，实施 [#36](https://github.com/yuwen773/auto-net/issues/36)，接续 [ADR-0013](0013-submission-budget-storage.md) 与 [ADR-0014](0014-bounded-submission-service.md)。父规格 #27 要求“持久格式调整必须保持旧版本兼容；旧版未知状态、损坏记录、版本未知、写入失败或回滚后无法安全解释时保守保留阻断和证据”。

## 决定

- **读取不迁移。** `SafetyStateStore.ReadAuthenticationBlock` 只解释记录，不重写 `auth-block.txt`。历史形态（64 位十六进制指纹、无 `Version` 的失败-only 记录、缺字段、未知版本、未知状态、损坏 JSON、非对象 JSON）一律返回阻断块；旧字节原样保留，作为人工核查证据。启动、检测、保存设置和修改凭据都不会把旧记录“升级”为可提交状态。
- **升级只经显式、修订比较的写入。** 旧记录被读成阻断块后，只有显式的 `TryTransitionAuthentication`（或人工解除后的新写入）才能落成当前记录。升级写入保留阻断；失败-only 记录升级时保留原始 `Failure`，不降级为无原因的通用阻断。
- **回滚安全不依赖提交额度文件。** `submission-budget.json` 是 #31 新增的独立记录，v0.3.1 不认识也不会读取。因此只要“可能已发出凭据”，`auth-block.txt` 必须自身是 `SubmissionPending`（或明确的终态拒绝），旧版本读到它同样拒绝提交。额度文件只收紧、不放宽认证阻断。回滚后旧版本忽略额度文件：对提交未知或终态记录它仍按永久暂停阻断，不会重复提交；对表示“尚未发出凭据”的冷却记录或已确认成功的就绪记录，它按自身策略继续，但不会获得超出旧版上限的次数。
- **回滚不授予新额度。** 旧版本人工解除只删除 `auth-block.txt`，不改独立额度。重新升级后若额度已耗尽，仍按 `Exhausted` 停止提交；只有 #34 的人工“重新开始认证”审计重置能清零。
- **新增状态或字段的兼容规则。** 当前记录保持 `Version = 1` 和旧版认知的七个字段（`Version`、`State`、`Failure`、`Attempts`、`RetryDelaySeconds`、`RetryNotBefore`、`Revision`），字段名与语义不得变动；新增字段只能落在旧版反序列化会忽略的位置（当前为 `ChannelFallbackCause` 与派生的 `BlocksSubmissions`）。任何会改变旧版可见字段语义的变化必须提升 `Version`，让旧版走 `UnsupportedVersion` 保守阻断；新增枚举值在旧版中因 `Enum.IsDefined` 失败同样阻断。不得为兼容而删除旧字段。
- **回滚不是热切换。** 回滚前应停止服务并保留受限目录状态；旧版本不理解提交额度，恢复的是永久暂停语义。安装器覆盖/回退与真实门户仍需单独验收。
- **冻结副本必须机械比对，不靠人眼。** 测试里的“已发布版本读取规则冻结副本”若只由人维护，就会随当前版本漂移而无人察觉。`scripts/check-released-reader.ps1` 用 `git archive` 取出真实 tag、构建真实旧版 `AutoNet.Core`，与冻结副本在同一条语料上逐条比对判定（状态、失败分类、已用次数、是否阻断）；任何一条不一致即失败。CI 必须跑这一步，tag 漂移或冻结副本改写都会当场暴露，而不是留给一次真实回滚。

## 证据

`tests/AutoNet.Tests/LegacyStateRegression.cs` 覆盖全部历史形态、旧记录转新记录、以及 v0.3.1 读取规则的冻结副本对当前记录的判定。冻结副本固定旧版字段集合与枚举值快照，未来新增状态在旧版视角必须仍是未知值；并断言当前记录保留旧版七个字段名、额外字段只限旧版忽略的已知扩展。`scripts/check-released-reader.ps1` 把冻结副本与真实 v0.3.1 在 24 条语料上逐条比对，防止副本漂移。详见 [#36 实施记录](../progress/36-legacy-upgrade-rollback.md)。真实安装/回退、真实存储故障与真实门户未执行。
