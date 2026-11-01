# #38 认证页网络边界

2026-10-30 在 `codex/ui-avalonia` 的 `26ab9aa` 基础上实施。本工单把「上网认证」页 WebView2 的出站流量收敛到一个进程内的允许列表代理，并补上真实浏览器宿主与失败关闭路径；真实门户页面与现场网络仍未验证。

## 实现

- **允许列表策略**：新增 `ProxyHostPolicy`（纯函数、无 I/O）。唯一允许 `Settings.PortalHost`，两侧都重新规范化（去空白、尾点、IDN、小写），只做完整匹配，拒绝后缀/子域/父域伪装与字面量 IP。
- **代理边界**：`AuthChannelProxy` 改用该策略；明文只允许 80、CONNECT 只允许 443；拒绝请求体（`Content-Length`/`Transfer-Encoding`）与协议升级（`Upgrade`）；Host 头省略默认端口时按请求行已校验的端口处理；隧道内再按 SNI 核验目标。出站连接统一交给可注入的 `ProxyConnector`，生产默认 `DirectNetwork.ConnectAsync`（`IP_UNICAST_IF` + 源地址绑定），没有域名到本地地址的映射口子。接受循环移到线程池，避免隧道 I/O 的续体回到 Avalonia 界面线程。
- **失败关闭**：新增 `AuthPageNetworkBoundary`。门户来源、浏览器运行时、校园网卡或代理任一步不满足都返回受控失败（`RuntimeMissing` / `NetworkBoundaryUnavailable` / `Failed`），不创建浏览器、不回退直连；取消按取消语义向上抛，不伪装成边界失败。
- **真实宿主**：新增 `AuthPageWebViewHost`，用 `NativeControlHost` 承载 WinForms WebView2。浏览器参数只给本 WebView：`--proxy-server`、`--proxy-bypass-list=<-loopback>`、关闭 QUIC 与 WebRTC 非代理 UDP；系统代理不读不写。浏览器与托盘共用 `ChannelProcessJob`，代理拒绝会话外进程的环回连接。浏览器初始化失败、渲染/浏览器进程异常退出都降级为可手动重试的 `Failed`；切页只解除父子关系，保留已填内容；重试或失败时丢弃一次性用户数据目录。
- **网卡来源**：`App.ReadProjectedLink` 只读服务发布的受控状态投影（`Trusted` 且 `SettingsProjected`），不读取普通权限不可达的 `private/settings.json`。
- **回归**：新增 `AuthPageBoundaryRegression` 9 项（主机归一/完整匹配、端口与请求形态、升级拒绝、连接器绑定语义、代理失败关闭、固定门户地址、浏览器参数、投影来源）；`WebViewProxyRegression` 改用生产参数构造器。

## 工单归属

`docs/progress/37-auth-page.md` 记录过“真实 WebView2 宿主的创建没有任何工单明确承接”。本次把它并入 #38 交付，并由 [ADR-0016](../adr/0016-auth-page-display-embedding.md) 记录“展示嵌入、不承担填表/提交”的边界。审查时 GitHub 不可达，`#38` 的 issue 正文尚未同步这段范围；恢复访问后应把“真实展示宿主”补进工单或单开一单，避免代码先于工单。

## 验收状态

| #38 验收点 | 状态 | 证据与边界 |
|---|---|---|
| 出站只经允许列表代理，完整主机匹配、两侧归一 | 完成（离线） | `ProxyHostPolicy` + 9 项边界回归 |
| 明文只允许 80、CONNECT 只允许 443、拒绝请求体/升级 | 完成（离线） | `ParseRequest` 回归 + 代理 QA |
| 连接钉到校园网卡，不回退默认路由 | 完成（离线） | 连接器回归断言主机/端口/网卡；真实路由未抓包 |
| 代理不可用时失败关闭 | 完成（离线） | `AuthPageNetworkBoundary` 回归 |
| 只给本 WebView 配代理，不改系统代理 | 完成（离线） | 参数构造器回归 + WebView2 代理 QA |
| 网卡只从可信投影读取 | 完成（离线） | `ReadProjectedLink` 回归 |
| 真实门户页面、TLS/重定向/新窗口/Service Worker | **未验证** | 需现场授权；离线证据不能替代 |
| 主窗口内 `NativeControlHost` 的真实生命周期 | **未验证** | 宿主 QA 覆盖生产参数与代理，未覆盖主窗口承载 |

## 验证

- `scripts/build.ps1`：Release 构建通过。工作区全量回归 **551/551**（含另一会话的诊断导出用例）；仅本工单暂存树 **550/550**（含新增 9 项）。
- `AutoNet.Tests.dll --filter 认证页边界`：**9/9**。
- `AutoNet.Tests.dll --webview-proxy-check`：PASS（导航、fetch、WebSocket 经代理；代理停止后不直连）。
- `scripts/qa-avalonia-auth.ps1`：PASS（单文件发布契约 + 假宿主生命周期 + 受控失败状态）。
- QA 证据在 `artifacts/validation/`（gitignored）。

## 审查修复（2026-10-30，Standards + Spec）

- **浏览器失败恢复**：`ProcessFailed`（浏览器/渲染进程退出）降级为 `Failed`；`AttachAsync`/`SetVisible` 在原生表面已创建但浏览器未启动时补一次初始化；失败/重试丢弃旧的一次性用户数据目录。
- **取消语义**：`AuthPageNetworkBoundary` 不再把 `OperationCanceledException` 吞成 `NetworkBoundaryUnavailable`。
- **单一来源**：`Settings` 新增 `IsPortalOrigin`，认证页导航与设置校验共用；`AuthChannelProxy` 的固定 Host、`RecoveryPolicy.FallbackPortalPageUrl` 与设置默认值都改从 `Settings.PortalHost` 取值。
- **测试清理**：TLS ClientHello 夹具抽成 `TlsClientHelloFixture`，消除两份字节级重复；去掉无意义断言。
- **文档**：`CONTEXT.md`、`docs/VALIDATION.md` 不再把 #38 写成“尚未实现”；`CLAUDE.md` 只声明实际存在的 `NavigationStarting` 纵深防御；新增 ADR-0016。
- 审查遗留：真实门户现场验证、主窗口内真实 WebView2 承载仍为 NOT_RUN；代理允许列表对门户子资源/802 端口的兼容性只能由现场验收回答。
