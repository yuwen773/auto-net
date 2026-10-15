# 门户流程按阶段分配预算，复核链不再吃掉提交窗口

ExecuteAsync 全流程共享一个 6 秒 deadline，而 AuthorizeSubmission 提交前复核链（约 7 次文件读、2 次原子写、2 次完整 WLAN 枚举，无超时、不可取消）发生在预检之后、提交之前。10-12/13 的“超时”全部表现为未发送凭据：预检 200/302/200 正常，凭据根本没出去，时间耗在复核链上。共享预算让慢读直接挤掉提交窗口，超时分类因此失去意义。

决定按阶段独立分配：预检 4 秒（status/gateway/config 各 2 秒串行）；复核链 5 秒且步骤间可取消，超时按 SafetyPersistence 安全拒发（不降低安全等级，只是不再让慢 I/O 伪装成提交超时）；提交阶段独立 3 秒，只覆盖登录请求本身。

## status.json 写入竞争

10-12/13 出现 9 次 status.json UnauthorizedAccessException，嫌疑是 AtomicWrite 的 temp+File.Move 替换与不含 FILE_SHARE_DELETE 的读方（AV/索引器）竞争；ACL 验证正常，10-14 零次。AtomicWrite 内增加 3 次退避重试（50/100/200ms），仍失败才记 OperationFailed。改原地非原子写会丢掉设置与凭据写入依赖的原子性，不做；ProcMon 现场抓竞争方留作复发时的排查手段。

## Consequences

- 提交窗口从“剩余多少是多少”变为恒定 3 秒，超时分类重新变得可信。
- 复核链超时是显式拒绝（SubmissionNotAuthorized），日志可区分“链太慢”与“门户不回”。
