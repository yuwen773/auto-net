# 故障诊断日志

日志的验收标准是：恢复失败后，仅依靠已记录证据，能够还原程序观察、决策、执行和验证的过程，并指出证据缺口。不能保证从有限采样推导出网络最初掉线的全部外部原因。

## 必须能回答的问题

1. 哪个程序版本、哪次进程运行、哪个目标网络，在何时首次发现异常？
2. Wi-Fi 是已断开、正在关联、等待地址还是已就绪？本机 IPv4 与门户参数分别是否可用？
3. 两个外网目标各自失败在哪一类：DNS、连接、TLS、超时、HTTP 状态还是内容检查？是否出现门户关键词重定向（劫持嫌疑）？
4. 程序为何执行某个动作，或为何没有动作？是等待样本、观察窗口、冷却、关闭恢复、缺少凭据、安全暂停还是额度限制？
5. 动作是否开始、是否返回、返回什么原生错误？认证是否真正发起凭据请求，服务器是否接受，外网是否随后恢复？
6. 服务重启、取消、写入失败或日志损坏，是否留下了需要承认的观测缺口？

## 关联与事件

继续使用每日 JSONL，目录 `C:\ProgramData\AutoNet\logs`，保留 14 天。新增记录为 `schemaVersion=2`；旧记录仍可阅读，但不能补造其未采集的信息。

每条新记录包含：

- `runId`：单次监测进程的随机编号。
- `incidentId`：同次故障的随机编号。正常观测恢复后结束；下一次异常使用新编号。暂停恢复时仍继续追踪目标网络。
- `networkId`：目标网卡与保存配置组合的散列片段，不记录原始配置名或地址。
- `sequence` / `cycle`：运行内事件顺序与检测轮次。
- `buildId`：程序集版本，以及 Service、Core 两个模块的 MVID，区别同版本号的不同构建。
- `time` / `monotonicMilliseconds`：实际时间及进程内单调计时。墙钟调整不作为冷却或恢复耗时依据。
- `diagnostics`：本条事件独有的受控证据；不把上一次失败当成每一轮的新失败。

| 事件代码 | 记录的事实 |
|---|---|
| SessionStarted / SessionStopped | 观测进程开始或正常停止；开始记录关联最近一次可读的新格式日志 |
| IncidentStarted | 首次观测到外网检查未通过，不等于物理断线发生时间 |
| IncidentResumed | 相同目标网络存在可关联的未结束故障；重启间隔为观测空白 |
| RecoveryDecision | 本轮观测条件、动作许可、策略门槛、决定、阻止原因和下次允许时间 |
| RecoveryActionStarted / RecoveryActionCompleted | 同一 actionId 的动作边界与结果；缺少完成事件不能推定成功 |
| PortalStep | 固定认证步骤的请求开始、收到响应头、读完响应或失败；收到 HTTP 200 不等于认证成功 |
| SubmissionAuthorized / SubmissionNotAuthorized | 持久提交授权与最后复核是否通过；未获授权的具体原因 |
| PortalLayoutChanged | 预检失败落在结构类集合（门户形状疑似变化）；含当时观察到的认证失败分类，自动认证按既有安全语义暂停 |
| BrowserFallbackRequested | 连续认证失败达阈值；记录请求在用户会话打开门户页，只开页不填表 |
| SafetyStateWriteStarted / SafetyStateWritten | 认证或断线安全状态的持久保存意图与成功；意图不代表成功 |
| DisconnectRecheck | 主动断线前的新鲜 HTTPS、认证、配置与链路复核 |
| WifiRequestResult | Issued / Skipped / Failed 及原生错误码；Issued 不代表物理连接或断线已完成 |
| OperationFailed / OperationRecovered | 指定操作异常及再次执行成功；重复同型错误不重复冒充新故障，原生错误码变化会重新记录 |
| IncidentRecovered | 外网内容检查通过；同时保留本次验证证据，恢复来源仍未确认 |
| IncidentSuspended / CycleInterrupted | 切换目标、用户切网、取消等造成的观测终止或中断；不计为恢复成功 |
| LoggingGap | 写入恢复后，明确补报未写入事件数量；不虚构缺失内容 |

状态变化、动作和安全状态转移及时记录。未恢复时，条件不变也至少每 30 秒记录一次决策证据；正常在线条件不变时每 5 分钟记录一次。两条外网探测的结果独立保留，不用一个 false 代替原因。时间、耗时的小幅波动不单独制造状态变化日志。

## 决策原因

例如 `AuthenticationPaused`、`CredentialsMissing`、`RecoveryDisabled`、`WaitingForAddress`、`ActiveDisconnectDisabled`、`InsufficientFailureSamples`、`ObservationWindow`、`AuthenticateCooldown`、`DisconnectBudgetExhausted`、`DisconnectCompletionUnconfirmed`。样本数、观察秒数、各类尝试次数和对应上限来自真实策略常量及运行状态。

认证参数只记录来源与合法性：状态响应、网关重定向、首页回退分别为 Missing / Invalid / Valid / NotChecked；AC 地址与 MAC 令牌分别校验。客户端参数缺失不能被解释成 Windows 没有 IPv4。配置错误保留固定字段名、形状、响应类型、字符数、HTTP 状态及受控错误分类。

暂停记录读取分类区分旧指纹形状、旧记录、非法 JSON、缺少字段、未知版本、无效或矛盾状态。旧指纹形状只能证明格式符合旧记录，不能倒推出原始故障；新增分类不清除安全暂停，也不改变提交授权。

## 分享和排查

日志中心选择事件后，新格式技术详情显示完整关联、决策条件和受控证据。“复制脱敏摘要”保留故障及运行编号、程序模块标识、阶段、错误码、参数合法性与关键决策；不会直接复制任意自由文本或原始网络标识。

排查顺序：先按 incidentId 聚合同次故障，再按 runId 与 sequence 排序；找到最后一次完整观测，查看 RecoveryDecision 的阻止条件；有动作时检查 actionId 的起止、提交授权与步骤结果；最后检查是否出现 IncidentRecovered、观测中断或 LoggingGap。记录缺失时保留不确定性，不把“没有失败事件”当作“没有发生失败”。

异常只记录类型、HRESULT、HTTP 错误分类、Socket/Windows 原生错误及最多八个产品方法调用位置。禁止记录异常 Message、完整堆栈文件路径、账号密码、Cookie、原始页面/响应、IP/MAC 值或带凭据 URL。未知自由文本字段不会被复制进分享摘要。

## 日志自身失败

主日志写入失败不放宽安全守卫，恢复流程继续按原策略执行。尝试向 Windows Application 日志写入事件 1101，内容仅包含运行/故障编号、序号、缺口数量及受控错误码；最多每分钟一次。写入恢复后，主日志报告 LoggingGap。

如果主日志和独立告警都不可写，状态快照显示相应错误，但仍不能保证证据已持久化。没有使用无限内存队列伪装成可靠日志。突然断电、强制结束或存储设备故障仍可能丢失最后事件；新进程只能关联保留范围内的可读日志，并明确记录观测中断。跨进程只汇总关联，本段计时不包含无法观测的停机时间。

## 验证边界

新增离线回归覆盖持续暂停及摘要频率、完整动作关联、DNS 与原生错误、参数缺失、状态写入失败与恢复、日志缺口与独立告警、重启关联、检测 API 异常及错误码变化、HTTP/内容失败、提交后断连、取消、策略阻止条件、响应解析和摘要脱敏、暂停记录分类。

这些测试使用模拟 HTTP、虚构凭据、手动时钟和临时文件。真实 Windows 事件日志写入权限、安装后日志 ACL、实际门户兼容、物理 WLAN 事件、15 秒恢复和 UU 会话仍须现场验收。采样间隔内发生并恢复的短断网可能未被观测，不能承诺记录每一次物理链路波动。
