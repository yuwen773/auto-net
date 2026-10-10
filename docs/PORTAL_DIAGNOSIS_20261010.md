# Dr.COM 分阶段诊断与历史故障边界

日期：2026-10-10（Asia/Shanghai）。本次只读检查使用工作区构建的服务程序；没有替换已安装服务，没有保存凭据、清除阻断或主动断网。

## 已核实的本机记录

读取本机公开状态及当天日志中的受控字段（time/action/detail/seconds），未读取 private 目录的凭据：

- 15:35:34 和 15:35:56 请求连接保存的校园 Wi-Fi。
- 15:35:40 和 15:36:07 显示认证已失效；中间也出现认证状态无法确认。
- 15:36:10 自动认证返回旧版笼统的 Unsupported 文案，随后暂停认证。
- 15:36:42 HTTPS 检测恢复，观测耗时 3216.26 秒。此记录不能证明 AutoNet 成功认证，也不能把该时长解释为一次登录耗时。
- 15:59:32 状态快照：校园网在线、Enabled=true、CredentialsBlocked=true。它只是该时刻的状态。

历史日志未记录 Unsupported 的具体阶段，因此不能确定当时是首页状态、IPv4 提取、802 响应还是配置模式问题；不能认定学校修改了协议。

## 本次只读实机结果

约 15:59 执行：

```powershell
pwsh.exe -NoLogo -NoProfile -Command '& ./.tools/dotnet/dotnet.exe src/AutoNet.Service/bin/Release/net10.0-windows/AutoNet.Service.dll --diagnose-portal; exit $LASTEXITCODE'
```

真实退出码 0。受控结果：ObservedPortalState=LoggedIn、Stage=Complete、Failure=None；en_md5/password_cut/enable_r3 均为 Zero；CredentialRequestStarted=false。这证明当前已认证环境下首页 IPv4 提取及 HTTPS 802 配置请求可以完成，不证明历史故障已被复现或真实自动认证已成功。

未输出或保存原始页面、响应、客户端地址、账号、Cookie 或凭据 URL。认证参数保持原已核实方案，没有猜测替代 IP 字段、改端口、绕过 TLS 或验证码。

## 当前错误与暂停规则（审查后加固）

上面的 15:59 结果是历史版本证据；当时的 Complete/None 未检查必需网关参数，不能作为当前完整登录前置条件已通过的证明。本轮加固仅做离线验证，没有重新执行现场诊断。

| 阶段 | 受控错误示例 | 行为 |
|---|---|---|
| Status | AlreadyAuthenticated / StatusUnknown | 不提交密码，继续检测 |
| GatewayRedirect | HttpStatus / InvalidTarget / InvalidResponse / Timeout / Transport | 仅接受受支持的 302 与绝对学校 HTTPS 443 根目标；异常状态、相对目标或重复关键字段不发凭据，保留阶段并退避重查 |
| HomePage | HttpStatus / Timeout / Transport | 暂时性错误退避；不兼容/重定向安全暂停 |
| ClientIpv4 | MissingClientIpv4 | 不猜测客户端地址，不发凭据，退避后重新检查，不写持久暂停 |
| Configuration | HttpStatus / MalformedConfiguration / InvalidResponse | 5xx/408/429 和传输超时退避；结构不可确认安全暂停 |
| AuthenticationMode | MissingFlag / DuplicateFlag / InvalidFlagType / UnsupportedFlag | 三字段必须明确为受支持的零，否则安全暂停 |
| Submission | MissingGatewayParameters / CredentialsRejected / InteractionRequired / LoginRejected | MAC/AC 缺失或无效时诊断与登录均失败且不发凭据；明确拒绝或人工验证暂停；普通失败退避；接口接受后继续验证外网 |
| 任意网络阶段 | Tls | 保留证书校验，安全暂停 |

有限退避通过恢复策略限制请求频率；长故障逐步退避至每 60 秒重试一次。暂时性前置失败不发凭据；认证状态未知不进入提交阶段。既有认证阻断不会因为一次只读检查通过、联网恢复或进程重启而自动解除。

只读前置检查与实际登录共用前置解析路径，从类型入口不接收 Credentials，永不发起凭据请求；缺少 MAC/AC 时可报告 Submission/MissingGatewayParameters。只有全部已实现的必需检查通过才返回 Complete/None。状态及日志记录 AuthenticationStage、AuthenticationFailure、AuthenticationHttpStatus 和 CredentialRequestStarted；详细错误仅包含受控分类，不包含异常原文。服务重启后遇到旧阻断标记会诚实显示“历史原因未知”，需查旧日志。

## 仍需授权的现场闭环

尚未验证未认证页面、真实凭据接受、实际断网恢复时间、TUN 物理出口或登录界面 UU 会话。当前已安装服务仍为原版本且阻断保留。后续先审查构建包，再由本机操作者授权升级；出现真实故障时采集新版阶段码，按 VALIDATION.md 单独批准受控恢复。不以重新保存密码代替诊断。
