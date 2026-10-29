# 可视化认证页隔离原型：证据与盲区

2026-10-27。本文件是 `artifacts/prototype-visual-auth-20261027/RESULTS.md` 的一手归档（`artifacts/` 被 gitignore，会随清理丢失）。原型源码**未**纳入版本库；需要重跑时按本文的复跑命令重建。

只使用本地虚构页面和虚构数据；**未**连接学校门户、读取真实凭据或修改 AutoNet 服务。本文件记录的是原型证据，不是生产实现，也不是完整验收。

## 复跑前提

本机 `PATH` 上的 `dotnet` 只有 6/7/8 运行时，必须用仓库内 SDK（`.tools/dotnet/dotnet.exe`，SDK 10.0.401）。

```powershell
# 主原型（Avalonia + WebView2）
.\.tools\dotnet\dotnet.exe artifacts\prototype-visual-auth-20261027\bin\Release\net10.0-windows\Prototype.dll

# state / ipc 原型会自行拉起子进程，子进程走 apphost，需要 DOTNET_ROOT
$env:DOTNET_ROOT=(Resolve-Path .tools\dotnet).Path
artifacts\prototype-visual-auth-20261027\state\bin\Release\net10.0-windows\StatePrototype.exe
artifacts\prototype-visual-auth-20261027\ipc\bin\Release\net10.0-windows\IpcPrototype.exe

$env:DOTNET_ROOT=(Resolve-Path .tools\dotnet).Path
.\.tools\dotnet\dotnet.exe artifacts\prototype-visual-auth-20261027\proxy\bin\Release\net10.0-windows\ProxyPrototype.dll
.\.tools\dotnet\dotnet.exe artifacts\prototype-visual-auth-20261027\webviewproxy\bin\Release\net10.0-windows\WebViewProxy.dll

$env:DOTNET_ROOT=(Resolve-Path .tools\dotnet).Path
.\.tools\dotnet\dotnet.exe artifacts\prototype-visual-auth-20261027\uac\bin\Release\net10.0-windows\UacPrototype.dll
```

直接双击 exe 或让子进程继承默认环境会报 "You must install or update .NET"，不是原型缺陷。`webviewproxy` 是 `WinExe`，apphost 脱离控制台看不到输出，**必须用 `dotnet X.dll` 运行**。`ProxyPrototype` 会监听绑定网卡的 80 端口，异常退出会留下占用端口的进程。

最后复跑：2026-10-27 20:21（两轮结论一致）。

## 已验证

### 主原型（Avalonia 12.1.4 + WinForms WebView2）

2026-10-27 20:21 复跑输出，全部 PASS：

```text
PASS: WebView2 为 Avalonia 主窗口子视图
PASS: 模拟输入未移动系统鼠标
PASS: 主页面导航和 HTTP 请求拦截；TCP连接=False; nav=1; intercepted=1; bytes=0
PASS: POST导航和 HTTP 请求拦截；TCP连接=False; nav=1; intercepted=1; bytes=0
PASS: WebSocket导航和 HTTP 请求拦截；TCP连接=False; nav=1; intercepted=0; bytes=0
PASS: 框架 WebSocket导航和 HTTP 请求拦截；TCP连接=False; nav=1; intercepted=0; bytes=0
PASS: 框架导航和 HTTP 请求拦截；TCP连接=False; nav=1; intercepted=1; bytes=0
PASS: 本地代理退出后不直连测试目标
PASS: 隔离页面、虚拟指针与模拟步骤，浏览器正常退出
```

- 主窗口内通过 `NativeControlHost` 承载 WebView2，用 `GetAncestor` 校验原生父子关系。
- 本地虚构页面显示虚拟鼠标；CDP 模拟输入前后 `GetCursorPos` 一致，未移动系统真实指针。100%、125%、150% 场景是 **WebView 浏览器缩放**，不是 Windows 系统 DPI；150% 需先把目标滚动进视口。
- 手动输入检测（主文档 `isTrusted` 事件 + 沙箱 iframe 经 `chrome.webview.postMessage` 上报）后等待满 15 秒才恢复自动步骤；期间再次输入会重置等待，已填内容不被覆盖；隐藏再显示页面后流程继续。
- 截图 `fixture.png` 为虚构页面：虚构账号、遮蔽的虚构密码、虚拟指针停在按钮上、状态"模拟完成"。无真实凭据。
- 浏览器销毁后等待 `BrowserProcessExited`，8 秒内正常结束。

### 网络拦截的两层证据（重要边界）

- 仅取消 `NavigationStarting` 时，本机回环监听器仍收到 HTTP GET，与 [WebView2 #4765](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4765) 一致。
- 增加覆盖所有来源（`CoreWebView2WebResourceRequestSourceKinds.All`）的 `WebResourceRequested` 后，主页面、POST、iframe 的测试目标不再收到 HTTP 数据；**但在没有代理的早期轮次仍观察到预先建立的 TCP 连接**，即"响应被拦下"不等于"没发出连接"。
- **WebSocket 绕过**：`WebResourceRequested` 对 ws/wss 不生效（本轮 `intercepted=0`），`Network.setBlockedURLs` 同样无效，本地监听器收到过虚构握手；与 [WebView2 #4303](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4303) 相符。
- 因此原型加了**只作用于该 WebView 的本地拒绝代理**（`--proxy-server=http://127.0.0.1:<port> --proxy-bypass-list=<-loopback>`，未改系统代理）。此后 WebSocket 之所以 PASS，是靠 `socketBlocked` 分支（代理收到请求 + 页面侧 socket error），而不是靠资源过滤；目标监听器也不再收到 TCP。
- 代理进程退出后再发 WebSocket，页面报错且不直连目标（PASS）。
- 结论边界：该代理**拒绝全部流量**，只用于实验，不能直接搬进正式程序；它也没有验证"允许本校目标"的正向路径、TLS、重定向、弹窗/新窗口、Service Worker。

### 允许列表代理 + 网卡绑定（`proxy/`、`webviewproxy/`）

`ProxyPrototype`：31/31 通过。`WebViewProxy`：11/11 通过。两者共用 `ProxyPolicy.cs`、`PinnedDialer.cs`、`AllowListProxy.cs`。

**证明了的**：

- 代理侧 `IP_UNICAST_IF` + 源地址绑定有效：出站连接的本地地址等于 `WLAN 2` 的 `10.171.185.51`（ifIndex 5），而本机默认路由指向 `Mihomo` TUN（ifIndex 3）。这正是要防的泄漏。
- 允许列表是完整匹配，后缀伪装（`login.csust.edu.cn.evil.com`）、子域前缀、父域都不放行；字面量 IP 一律拒绝。
- 未允许主机在明文、CONNECT、WebSocket 升级三种形态下均被 403，且目标监听器一次连接都没收到。
- **WebView2 确实走该代理**：`--proxy-server` + `--proxy-bypass-list=<-loopback>` 下，允许主机经代理加载成功；不允许主机、WebSocket、页面 fetch、导航到回环 IP 全部失败且未直连。
- 代理停止后端口不再监听，调用方只能失败，不会静默回退直连。

**原型开发中真实修掉的缺陷**（是实现错，不是测试写错——正式实现需带对应回归）：

1. `HostAllowed` 只在允许列表一侧规范化，传入主机未转小写时 `LOGIN.CSUST.EDU.CN` 匹配失败——大小写变体可绕过。改为两侧都重新规范化。
2. 明文 HTTP 到 443 端口曾被放行。这是 TLS 降级信号，已改为明文只允许 80、CONNECT 只允许 443。
3. `CreateAsync` 用位置参数会把用户数据目录绑到 `browserExecutableFolder`，随后抛 `WebView2RuntimeNotFoundException`——错误信息完全误导，实际是路径问题。必须用命名参数。
4. 异常分支未计入失败，导致崩溃时打印 `RESULT: PASS (0 passed, 0 failed)`。已改为异常计入失败。

### 独立状态模型（`state/`）

```text
PASS: 两个独立进程竞争时仅一次预留
PASS: 新进程保留未完成提交状态
PASS: 120 秒间隔、在线暂停、共享三次上限与时钟回拨
PASS: 损坏状态拒绝预留
```

假时钟 + 临时 JSON + 文件锁；验证仅一次预留、进程退出后执行中状态保留、120 秒最小间隔、外网模拟在线暂停、三次上限、时钟回拨（`WAIT_CLOCK`）与损坏状态抛 `JsonException` 拒绝预留。**它是独立模型，未接入真实服务、认证或内嵌浏览器。**

### 进程间交接与身份核验（`ipc/`、`uac/`）

```text
PASS: 本用户双进程身份核验和虚构数据交接
PASS: 对端进程不匹配时未发送数据
```

`uac/`：9/9 通过。

**本机侦察结论（决定性的限制）**：

- 当前身份 `SAIL\yuwen`：在管理员组，但令牌为 **Medium** 完整性级别，`BUILTIN\Administrators` 是 **deny-only**——标准 UAC 拆分令牌。
- 另一个本地管理员 `SAIL\Administrator`：**已禁用**且需要密码。
- `CodexSandboxOffline/Online`：已启用，但**不在管理员组**。
- UAC：`EnableLUA=1`，`ConsentPromptBehaviorAdmin=2`。

**因此无法在本机构造真实的"不同管理员账户 UAC"。** 启用 `Administrator` 并设密码属于修改系统账户状态，未经授权不做。

**证明了的**：

- 同一 SID 下交接成功，服务端确认收到虚构数据（`ACCEPT:secret`）。
- **对端 SID 不符时服务端拒绝**（`exit=4`，`REJECT:peer-sid-mismatch`）——即使客户端已把数据写进管道，服务端也不接受。
- **客户端侧对端 PID 不符时中止发送**（`client=22`），服务端一个字节都没收到。
- 管道 ACL 只授予当前账户 SID，**未授予 Everyone**；显式断开继承。
- 未提权进程在服务端要求提权时被直接拒绝（`exit=5`），连管道都不建。

**原型开发中修掉的缺陷**：

1. 把「服务端自检 SID」和「服务端核验对端 SID」混成同一个参数，导致测试二假失败。已拆成 `--expect-sid` 与 `--expect-client-sid`。
2. `integrity=unknown`：`WindowsIdentity.Claims` 不暴露完整性级别；改用 `WindowsIdentity.Token` 也不行（该运行时下不是可用原生句柄）。必须用 `GetCurrentProcess()` 伪句柄 + `TokenIntegrityLevel(25)`。

## 盲区（必须如实保留）

- **不同管理员账户之间的 UAC 成功交接未验证**（本机条件不允许）。跨完整性级别（Medium → High）的管道握手未验证，只验证了同级别。
- **恶意同用户进程、进程注入、路径替换、`AutoNet.AuthChannel.exe` 被替换后仍能通过 PID 核验——均未验证。** PID 与 SID 核验不构成完整安全证明。一次性关联、防重放、超时后的残留状态未覆盖。真实 `verb=runas` 提权路径（会弹 UAC 对话框并阻塞自动化）未验证。
- **WebRTC/STUN 探测没有到达代理**（`denied` 计数不变），也没有到达目标。这说明该路径既未被代理处理、也未直连成功；**本轮没有证明它被拦截，只证明这个夹具下没打通。不能写成"WebRTC 已被阻断"。**
- 原型为了让虚构域名不出本机，`PinnedDialer` 有 `OfflineOverrides` 映射。**正式实现绝不能有这个口子**——它能把允许域名解析到任意地址。
- 只验证了明文 HTTP 与 CONNECT 的放行判定，**没有验证真实 TLS 握手、证书校验、重定向、新窗口、Service Worker**。
- 没有验证跨账户 UAC、系统代理设置是否被改动（原型只给单个 WebView 传 `--proxy-server`，未改系统代理，但这一点本身未做回归断言）。
- 浏览器页面与后台服务的同一真实认证会话、受保护授权和结果交接**尚未接通**；当前模拟步骤来自同一原型进程，状态模型与管道原型彼此独立。
- 真实 Windows 多 DPI、键盘和无障碍、窗口层级/动画的所有可见交互、关闭托盘/用户会话退出尚未覆盖。
- 未验证本校真实页面或真实恢复。用户说明其验证码为展示项，无需填写。只有获得明确授权后才可进行现场验证；本任务不操作真实校园网。

## 关于"密码跨进程交接"门槛的核验结论

上一版交接记录把计划文档第 88–89 行与现有实现的差异记作"计划要求验证一个尚不存在的设计"。核对代码后，这个判断不成立：

- 现状确实不跨进程：`Elevation.LaunchAuthChannel`（`src/AutoNet.Tray.Avalonia/Elevation.cs`）只传 `--from-settings --result --timeout --human-wait`，凭据由提权进程自己用 LocalMachine DPAPI 读（`src/AutoNet.AuthChannel/Program.cs`）；`ElevatedSettings.Production()` 让提权进程自己开窗口收密码（`App.axaml.cs`）。
- 但计划文档本身要求了需要跨进程交接的场景：第 10 行「主窗口左侧设置页直接编辑，保存时请求管理员确认」，第 87 行「主窗口不整体提权。**已输入密码可能短暂存在普通 UI 进程内存**」。

一旦设置页按计划搬进主窗口，密码就在非提权的普通进程里输入，必须交给提权进程落盘——**需要跨进程交接的设计是计划的一部分，不是凭空要求**。因此该门槛**不应降级**；真正待定的只是跨账户 UAC 无法在本机构造，只能留待有条件的环境验证（见上文盲区）。

## 当前结论

原型的核心架构风险已排除：**主窗口内嵌 WebView2 + 本地允许列表代理 + 代理侧网卡绑定**这条路是通的，且 WebView2 确实服从代理。

身份核验侧：同账户内的 SID/PID/ACL 核验全部通过，**但跨管理员账户在本机无法构造**。

剩余未证明项（真实 TLS/重定向/新窗口/Service Worker、页面会话与后台服务的共享授权、真实 Windows 多 DPI 与无障碍、用户会话退出后行为）更适合做成正式实现的回归测试，而不是继续做一次性原型。
