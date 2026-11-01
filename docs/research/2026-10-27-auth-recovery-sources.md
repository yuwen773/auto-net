# 校园网认证恢复与权限交接：开源调研

调研日期：2026-10-27。范围：阅读公开源码与官方文档；没有运行第三方软件，也没有访问校园门户。用户选择的“结果未知时至少间隔 120 秒、最多额外重试两次”和设置页合并均为 **AutoNet 目标设计，尚未实现**。不同认证方式是否共享提交次数、外网恢复而认证结果未知时如何处理，仍待用户确认。

## 校园网开源实现

### Lbl-Zms/cmcc-autoconnect

- 版本：提交 `53d2e1f93a0ed4bdc904d9d9f1708d7186ae59c8`，MIT（[项目及许可证](https://github.com/Lbl-Zms/cmcc-autoconnect/tree/53d2e1f93a0ed4bdc904d9d9f1708d7186ae59c8)）。
- 表单提交超时时，该实现注明请求可能已被门户接收，返回调用者以复检网络；它并没有把这个超时当成确认的认证失败。[提交及异常分类](https://github.com/Lbl-Zms/cmcc-autoconnect/blob/53d2e1f93a0ed4bdc904d9d9f1708d7186ae59c8/src/CampusNet.psm1#L521-L535)
- 主循环默认每轮最多三次自动登录；每次尝试后等待 3 秒并检查网络是否可用，之后可打开浏览器。[计数初始化](https://github.com/Lbl-Zms/cmcc-autoconnect/blob/53d2e1f93a0ed4bdc904d9d9f1708d7186ae59c8/src/Connect-CampusNet.ps1#L50-L57)、[尝试与复检](https://github.com/Lbl-Zms/cmcc-autoconnect/blob/53d2e1f93a0ed4bdc904d9d9f1708d7186ae59c8/src/Connect-CampusNet.ps1#L77-L108)
- 可借鉴“超时后先观察是否恢复”，但这里的计数在启动入口初始化，不能据此声称它保证跨进程重启的限额；外网可用也不能证明门户接受了本次凭据。该项目面向另一学校的门户，不能直接套用其地址、表单、明文协议或浏览器扩展。

### pvxacct/campus-network-auto-login

- 版本：提交 `f4ed4d8e85f741c8b40fac95aa06946c610968f3`；仓库 README 标为 2.1.2，MIT（[项目与许可证](https://github.com/pvxacct/campus-network-auto-login/tree/f4ed4d8e85f741c8b40fac95aa06946c610968f3)）。
- 实现把每次登录请求而非一轮流程计数；提交前复查最小间隔和小时额度，提交后登记等待，并在登录响应成功后读取门户状态。[请求计数](https://github.com/pvxacct/campus-network-auto-login/blob/f4ed4d8e85f741c8b40fac95aa06946c610968f3/src/CampusNet/Core/Engine.cs#L183-L201)、[限频和复检](https://github.com/pvxacct/campus-network-auto-login/blob/f4ed4d8e85f741c8b40fac95aa06946c610968f3/src/CampusNet/Core/Engine.cs#L799-L863)。
- 可借鉴每次提交重新检查节流，以及把“接口不可达”与“明确已在线”分开。但该实现对部分密码错误等业务提示继续按间隔重试；AutoNet 不应直接复制此政策。源码显示调用计数前有一次持久化，不能据此推断崩溃前已原子持久保留本次额度；也不能视作跨进程重启不重发的证明。
- 两项目仅为相关实现样本，不代表已证明适用于长沙理工大学的门户。每小时限额、提交间隔与错误处理的具体数字需要按本项目决策，而非照搬其他学校配置。

### Ye9137/CampusConnect（用户指定）

- 版本：Windows 源码提交 [`7761c20eb63245da796cbbcf92b859dd74f1cf97`](https://github.com/Ye9137/CampusConnect/tree/7761c20eb63245da796cbbcf92b859dd74f1cf97)；根 README 称 2.1.9。仓库简介仍写 MIT，但该版本的 [LICENSE](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/LICENSE#L1-L36) 明确为非商业许可，商业使用需事先书面授权；历史 MIT 内容的权利按原授权保留。不得把现行版本整体当作 MIT，复制前必须核实具体代码的许可及用途。
- WebView2 流程在导航时更新会话、绑定导航代次，并在失败或跳转异常时暂停；在注入前先以无凭据脚本识别表单，再在当前来源上下文填表。[导航处理](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/PortalController.cs#L80-L115)、[填表前探测和固定](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/PortalController.cs#L367-L394)、[同源判断](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/PortalTemplate.js#L1-L20)。这些设计值得参考；但其[跳转会话](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/Core.cs#L148-L180)允许同一次认证中的跨主机跳转（阻止 HTTPS 降级、限制时间和次数），当前来源会更新；因此来源一致并不等同于“已验证属于学校”。AutoNet 不能直接照搬其动态来源信任策略。
- 结果处理区分成功、拒绝、请求未确认；提交后最多等待 30 秒观察，然后复查联网，明确拒绝和未知错误进入需要交互的状态。[结果观察](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/PortalController.cs#L238-L270)、[提交分支](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/PortalController.cs#L390-L408)。与仅凭路径变化判断成功相比，明确识别已验证的结果形式更值得借鉴，但该项目的页面证据尚未证明对本校有效。
- [恢复策略](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/Core.cs#L129-L146)使用 5 秒节奏；`Attempts` 只做饱和记录而非 `ShouldAuthenticate` 的上限条件。[实际调用](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/MainForm.cs#L508-L553)还受当前窗口是否需要交互或正在认证限制，不能据此声称它具有 120 秒、最多三次或跨重启的持久提交额度。浏览器视图重置会要求交互，避免仅因视图重建重新提交；不等同于整个应用或系统重启后的持久保证。[重置逻辑](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/PortalController.cs#L186-L195)
- Windows 配置用当前用户 DPAPI 加密，写入用户本地配置目录并通过临时文件替换。[LocalStore](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/LocalStore.cs#L5-L35)。这与 AutoNet 的机器级凭据、服务及管理员写入模型不同，不能直接用当前用户存储替代机器级服务所需的凭据访问。
- 网络检测先确认所选网卡正常，再向百度发 ICMP；注释明确 ping 使用系统路由，未限定报文走目标校园网卡。[Connectivity.cs](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/Windows/source/Connectivity.cs#L5-L21) 不能替代 AutoNet 的指定链路双源 HTTPS 内容检测。
- 作者的 [验证记录](https://github.com/Ye9137/CampusConnect/blob/7761c20eb63245da796cbbcf92b859dd74f1cf97/VALIDATION.md#L15-L26)列出离线及 CI 覆盖，同时明确真实校园网自动恢复与多网卡路由仍需现场验证。本次未运行其测试、安装程序或实际校园认证。

## Windows 权限与页面安全

- [PowerToys Runner 官方设计文档](https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/core/runner.md#ipc-communication-mechanism)说明其 Runner 与 Settings 使用双向 Windows 命名管道交换 JSON 控制消息。这证明可用此类通道解耦进程，但文档没有证明此管道可安全传递密码，不应据此断言可直接复用。
- [命名管道安全文档](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)指出未提供安全描述符时默认 ACL 给 Everyone 和匿名账户读权限；凭据交接必须设计显式权限及通信双方身份验证，不能依赖默认权限或随机名字。
- [.NET 命名管道文档](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-namedpipes?view=aspnetcore-10.0#configuring-pipesecurity-for-named-pipes)指出应最小化授权主体，不可信客户端不应获得创建其他服务端实例的权限；该权限可被用于冒充服务端，单看管道所有者不足以识别。该文的 `CurrentUserOnly` 与 `PipeSecurity` 说明针对文中 Kestrel 命名管道配置，不自动证明 AutoNet 使用不同管道实现时的完整语义。
- UAC 由当前普通用户提供**另一个管理员账户**的凭据时，客户端与提权端身份不同。跨账户连接、身份核验、会话隔离和凭据传输尚未验证，不能假设仅用 `CurrentUserOnly` 就可用；应进行隔离多账户实测。
- [WebView2 安全指导](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security#treat-all-web-content-as-insecure)要求向页面注入内容前验证当前来源，并建议通过导航事件限制页面和框架导航；同一文档[建议 WebView2 宿主采用非提权用户权限](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security#recommended-privilege-level-for-webview2-host-applications)，将需要提权的操作隔离。来源校验不能替代可信页面、表单提交目标和请求完整安全设计。

## 对 AutoNet 的设计启示

1. 先只读复检、按每次实际可能提交计数，避免把超时直接判为失败；保留用户选择的 120 秒与最多两次额外重试。
2. 提交前持久预留额度、记录最早下一次提交时刻，并覆盖多进程竞态、突然退出与重启；对学校门户是否接收到提交仍有不确定性。需要确认与现有预检次数上限的关系及两种认证载体的共享范围。
3. 页面来源无法确认时不自动注入密码；已提交但结果未知时按受控节奏复检和重试。已在线是否暂停重试由用户另行确认。
4. 设置页单窗口体验与提权保存可并存，但密码传递、跨账户 UAC、取消和拒绝不写入，都需要专门设计与测试；本调研没有证明某个现成管道模块满足该项目威胁模型。
5. 不直接引入或复制第三方代码。若后续考虑复制，应另行核对具体版本的适用许可、维护状态、依赖以及是否符合项目的网络和凭据约束。
