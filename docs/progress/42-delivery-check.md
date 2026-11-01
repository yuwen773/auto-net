# #42 交付检查

- 工单：[yuwen773/auto-net#42](https://github.com/yuwen773/auto-net/issues/42)
- 父规格：[#27](https://github.com/yuwen773/auto-net/issues/27)
- 基线：`codex/ui-avalonia` 的 `4596028`（构建证据取自该提交的干净工作树；本次只改脚本与文档，不改产品源码）
- 日期：2026-11-01
- 范围：构建与回归、安装/升级/回滚契约、日志与诊断包隐私、文档同步、未验证项清单。
- **不在本工单内**：真实学校门户、真实断网恢复、真实 UAC、真实 WebView2 现场承载。这些仍需单独授权与现场验证，离线测试不能替代。

## 1. 完整构建与全部回归

| 检查 | 结果 | 证据 |
|---|---|---|
| Release 构建 | **0 警告 / 0 错误** | `scripts/build.ps1`，SDK `10.0.401` |
| 全量回归 | **591/591 passed**，退出码 0 | `artifacts/validation/tests.txt` |
| 构建标识 | 提交 `4596028f500f65918d8673127d93ade6621581f9`，`trackedChanges=false`（构建时） | `artifacts/validation/build.json` |
| 运行器 | 自定义控制台运行器，`--filter` 为中文名称子串匹配 | `tests/AutoNet.Tests` |
| CI 离线回归 | `build` 与 `install-smoke` 均 **SUCCESS**（提交 `243d4ba`） | `Windows offline regression`，[run 41298120031](https://github.com/yuwen773/auto-net/actions/runs/41298120031) |

本次没有改动产品源码，回归数字与基线一致；改动只涉及安装/QA 脚本和文档。

## 2. 安装、升级、回滚契约

### 发现的缺口并修复

`scripts/install.ps1` 的失败回滚清单原先只备份 `service/AutoNet.Service.exe`、`tray/AutoNet.Tray.exe`、`uninstall.ps1` 与开始菜单快捷方式，**漏掉了工单 #26 新增的 `authchannel/AutoNet.AuthChannel.exe`**。后果：升级在复制新辅助进程之后失败时，回滚会把旧服务与旧托盘放回，却留下新的辅助进程，形成新旧混装。

修复（`scripts/install.ps1`）：

- 新增 `$authChannelExe`，与主程序一起参与重解析点预检。
- 把辅助进程加入失败回滚的备份/恢复清单；原先不存在的目标在回滚时删除。
- 覆盖前停止从安装目录启动的 `AutoNet.AuthChannel` 进程。运行中的辅助进程会锁住自己的 exe，不停止会让 `Copy-Item` 失败并把整次升级推进回滚分支；卸载脚本也停止同一进程，避免留下持有启动锁的残留实例。

### 冒烟矩阵扩展（`scripts/qa-install-smoke.ps1`，仅 CI 一次性 runner）

- 预置的持久记录从 2 个扩展到 6 个：`settings.json`、`credentials.dpapi`、`auth-block.txt`、`submission-budget.json`、`submission-restart.json`、`auth-release-audit.jsonl`，全部建立逐字节基线。`auth-block.txt` 使用数字枚举（默认序列化器没有字符串枚举转换器），`submission-budget.json` 使用字符串枚举（`SubmissionJson` 带 `JsonStringEnumConverter`），两者都按真实字段与枚举形态构造，服务读取时为有效记录而不是损坏记录。
- 全新安装：断言辅助进程布局，并核对其 SHA256 与发布包内一致；断言六个持久记录逐字节不变。
- **失败回滚**：复制发布包为坏包，损坏服务可执行文件并给辅助进程追加不同字节；断言安装以非零退出、日志出现“已恢复旧程序”回滚文案、旧服务恢复 Running、旧辅助进程按哈希恢复、六个持久记录逐字节不变。日志断言排除“复制前就失败”的假通过。
- 覆盖安装：先改动已安装的辅助进程并确认哈希已变，再断言覆盖安装把它恢复为包内字节；六个持久记录逐字节不变。
- 卸载（不带 `-PurgeData`）：断言服务/自启/快捷方式清理、程序文件（含辅助进程）保留、六个持久记录逐字节不变。

### QA 隔离覆盖新增持久记录

六套 Avalonia QA 脚本（export、notifications、portal-open、settings、theme、unblock）的“本机真实存储未被触碰”指纹原先只含 `settings.json`、`credentials.dpapi`、`auth-block.txt`、`auth-release-audit.jsonl`，现补入 `submission-budget.json` 与 `submission-restart.json`。锁文件（`auth-state.lock`、`submission-execution.lock`、`auth-channel-launch.lock`、`settings.lock`）是运行时句柄，不纳入逐字节指纹，避免把正常的获取/释放误判为篡改；`submission-channel-session.json` 是随会话结束删除的瞬态记录，同样不纳入。

### 执行状态

- 修改后的 9 个 PowerShell 脚本全部通过语法解析。
- `scripts/check-released-reader.ps1`：**READER-OK**，真实 `v0.3.1` 读取规则与测试内冻结副本在 24 条语料上逐条一致（`artifacts/released-reader-check/report.txt`），支撑回滚兼容判定。
- `qa-install-smoke.ps1` 带 CI 环境守卫，**本机未运行**（会覆盖安装本机 AutoNet）。GitHub 托管 runner 上已执行并通过：`Windows offline regression` 对提交 `243d4ba` 的 `build` 与 `install-smoke` 均 SUCCESS（[run 41298120031](https://github.com/yuwen773/auto-net/actions/runs/41298120031)），全新安装、坏包回滚、覆盖安装与卸载四阶段连同六个持久记录基线全部通过。这是合成夹具上的一次性环境，真实数据机器上的安装/覆盖/回滚仍为 `NOT_RUN`；本机已安装候选程序是热修版本，与仓库源码不一致。
- “覆盖前停止运行中的辅助进程”需要真实 WebView2 会话才能保持进程存活，CI 冒烟矩阵未单独触发；该分支为 `NOT_RUN`。文件层面的覆盖/回滚由坏包与覆盖阶段断言。

## 3. 日志与诊断包隐私

| 专项 | 结果 |
|---|---|
| `--filter 隐私` | **2/2** |
| `--filter 脱敏` | **7/7** |
| `--filter 诊断包` | **16/16** |
| `--filter 日志` | **38/38** |
| `--filter 回滚`（旧记录读取） | **3/3** |

覆盖点：诊断包在脏输入下全条目不含凭据、Cookie、Token、原始响应与真实 MAC；结果文件与缺口说明零泄漏；日志脱敏摘要只输出白名单字段、不含自由文本；日志尾部读取对损坏/截断/多字节边界的处理不产生凭据回显。全量回归已包含上述用例，凭据零出现。

## 4. 文档同步

- `docs/plans/2026-10-27-auth-recovery-and-settings-design.md`：开头“尚未实施、测试或部署”已过时。改为“离线实现已落在 `codex/ui-avalonia`，尚未发布，也未在真实门户、真实断网或真实 UAC 下验收”，并保留 ADR/VALIDATION 的优先关系。
- `CONTEXT.md`：“可视化认证页”不再以过时的“骨架已实现（#37）”开头，改为“「上网认证」页（工单 #37、#38、#39）把状态快照投影为…”，只描述页面行为与边界，不新增交付状态；其余“未发布”“仍未验证”的表述核对后保留。
- `docs/adr/0016-auth-page-display-embedding.md`：补上 **WebRTC/STUN 实际走向未查清** 的边界，明确关闭 QUIC/非代理 UDP 不等于现场证明拦截。
- `docs/adr/0001-submission-recheck-resolution.md`：把“ADR-0011 是决定，尚未实现”改为“离线实现已在 #32、#33 落线（未发布）；已安装程序仍按旧行为运行”。
- `docs/adr/0013-submission-budget-storage.md`、`0015-channel-shared-submission.md`：不改写历史决定正文，按 ADR-0001 的既有模式在文末追加 `> **修订（2026-11-01，工单 #42）**`，说明 #34/#36/#38/#39 已分别离线落线（未发布、未现场验收）。
- `docs/adr/0017`–`0021`、`docs/progress/37`–`41`：逐条核对，未发现与当前代码矛盾的结论；其中的 `NOT_RUN` 项原样保留。
- `docs/VALIDATION.md`：新增本次交付检查小节与仍需现场验证的清单。

## 4.1 审查修复（2026-11-01，Standards + Spec）

- **auth-block 夹具无效**：原先写成字符串枚举，而默认序列化器没有字符串枚举转换器，服务会判为 `InvalidJson`/`SafetyBlocked`；哈希断言仍会通过，属于“绿得不对”。改为数字枚举，并补上 `submission-restart.json`（`submission-channel-session.json` 是随会话结束删除的瞬态记录，不纳入逐字节基线）。
- **回滚断言可假通过**：坏包若在复制前就失败，非零退出 + 状态不变也能通过。新增日志断言“已恢复旧程序”，证明真的进入 catch 回滚分支。
- **覆盖安装断言无区分力**：覆盖前先给已安装的辅助进程追加标记并确认哈希改变，再断言覆盖安装把它恢复为包内字节。
- **锁文件名错误**：交付记录里的 `settings-write.lock` 改为实际文件名 `settings.lock`，并补上 `auth-state.lock`。
- **ADR 改写历史**：ADR-0013/0015 恢复原文，改为按 ADR-0001 的既有模式在文末追加 `> **修订（2026-11-01，工单 #42）**`；`CONTEXT.md` 去掉交付状态前缀，只描述页面行为。
- **清单重复**：`install.ps1` 把发布包布局与备份目标各收敛为一份数组，新增组件时不会漏改。
- **未修复并接受**：六套 QA 脚本仍各自维护同一份私有文件清单，跨脚本去重属后续重构；运行中辅助进程的停止逻辑需要真实 WebView2 会话，未在 CI 单独触发，记为 `NOT_RUN`。

## 5. 仍需现场验证的项

以下项目全部为 `NOT_RUN` / `NOT_VERIFIED`，不能用离线回归或截图替代：

1. **真实学校门户**：`/drcom/chkstatus`、网关 302、802 端口 `loadConfig` 与 `/eportal/portal/login` 的真实响应；账号前缀 `,0,`、MAC 令牌语义、`PHPSESSID` 是否必需。历史实测曾返回“只能绑定指定的VLAN设备”，原因未定。
2. **真实断网恢复**：从故障发生到恢复的完整时间（约 15 秒只是目标，不是保证）；真实认证失效后的自动重连与重新认证。
3. **跨管理员账户 UAC**：设置保存交接、解除认证暂停、诊断包导出、内嵌通道启动。本机内置管理员已禁用、当前账户是 Medium 拆分令牌，无法构造；同账户 UAC 点击/拒绝也仍需人工执行。
4. **WebRTC/STUN 走向**：认证页代理是否覆盖全部 WebRTC 流量。浏览器参数只关闭 QUIC 与非代理 UDP，未抓包证明。
5. **真实 WebView2 展示宿主**：主窗口内 `NativeControlHost` 承载、真实门户 TLS/重定向/新窗口/Service Worker、门户子资源与 802 端口经允许列表代理的兼容性。
6. **真实内嵌通道会话互斥与手动接管**：通道进程运行期间打开认证页、会话结束后的恢复、虚拟指针的真实位置与可见性、系统多 DPI、真实“减少动画”、真实人工输入与 15 秒提示清除、人工观察与提前收尾。
7. **真实 UAC 下的解除认证暂停与诊断包导出**：受限目录、审计写入、提权进程采集。
8. **真实安装/覆盖/回滚**：在带真实数据的机器上执行，而不是 CI 合成夹具；本机已安装程序与仓库源码仍有热修分叉。
9. **真实存储故障与 ACL 行为**：受限目录读写失败、锁竞争、旧记录在真实 ACL 下的读取。

## 6. 未解决事项与已知边界

- `scripts/qa-authchannel.ps1` 的夹具模式已与当前通道实现不兼容：`AuthChannelWindow.ExecuteAsync` 要求 `--from-settings` 且拒绝 `--fixture-dir`，因此该脚本无法再驱动真实通道。这是 #38/#40 收紧网络边界后的已知遗留，本工单不恢复夹具后门。
- 设置页的七个 `Keep*` 布尔仍未收敛为类型（#41 跟进项）。
- WinForms 旧托盘未增加提交额度行（#35 已知边界）。
- 认证页在内嵌通道会话开始时丢弃展示页未提交输入（ADR-0017 已接受的代价）。
- 通道窗口按用户决定保持屏幕外；正常自动认证期间用户看不到虚拟指针。
- 交互检测存在盲区：跨源框架、浏览器自身界面、脚本被禁用或页面被替换时可能漏检，不能声称拦截或计数全部人工提交。

## 7. 修改文件

- `scripts/install.ps1`：辅助进程纳入失败回滚与重解析点预检；覆盖前停止运行中的辅助进程。
- `scripts/uninstall.ps1`：卸载时停止运行中的辅助进程。
- `scripts/qa-install-smoke.ps1`：新增持久记录基线、辅助进程哈希断言与坏包回滚场景。
- `scripts/qa-avalonia-export.ps1`、`qa-avalonia-notifications.ps1`、`qa-avalonia-portal-open.ps1`、`qa-avalonia-settings.ps1`、`qa-avalonia-theme.ps1`、`qa-avalonia-unblock.ps1`：真实存储指纹补入 `submission-budget.json` 与 `submission-restart.json`。
- `docs/plans/2026-10-27-auth-recovery-and-settings-design.md`、`CONTEXT.md`、`docs/adr/0001`、`0013`、`0015`、`0016`、`docs/VALIDATION.md`、本文件。

未修改产品源码、服务、托盘、通道或测试用例；提交在 `codex/ui-avalonia`，未推送、未发布、未部署。
