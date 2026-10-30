# 认证页展示嵌入：NativeControlHost 承载只读门户页

2026-10-30，实施 [#38](https://github.com/yuwen773/auto-net/issues/38)，接续 [ADR-0009](0009-embedded-auth-channel.md)。本条只覆盖「上网认证」页的**只读展示**，不改变 ADR-0009 对凭据提交载体的决定。

## 决定

- 「上网认证」页用 Avalonia `NativeControlHost` 在主窗口内承载 WinForms WebView2，加载固定门户根地址。页面不填表、不注入脚本、不提交凭据；填表与提交仍只属于独立辅助进程的内嵌认证通道（ADR-0009）。
- 该 WebView 的唯一出站路径是进程内的环回允许列表代理：完整主机匹配、明文只允许 80、CONNECT 只允许 443、拒绝请求体与协议升级；出站连接经 `DirectNetwork.ConnectAsync` 用 `IP_UNICAST_IF` 加源地址绑定钉到校园网卡。代理不可用时失败关闭，不回退直连。
- 浏览器参数只作用于这个 WebView（`--proxy-server`、`--proxy-bypass-list=<-loopback>`、关闭 QUIC 与 WebRTC 非代理 UDP），不读取或改写系统代理。
- 网卡来自后台服务发布的可信状态投影（`status.json` 的配置投影），不从普通权限读不到的 `private/settings.json` 取值。

## 为什么不沿用 ADR-0009 的「放弃嵌入」

ADR-0009 放弃的是把**凭据提交通道**嵌进主窗口：那时混合宿主没有成熟先例，而通道要处理密码、自动填表与提交，风险不可接受。展示页不接触凭据，原型已用 `NativeControlHost` 承载 WebView2 并核验原生父子关系；工程风险因此只落在呈现层，可由代理边界与失败关闭独立兜住。

## 边界

- 展示页与内嵌认证通道不共享会话、cookie 或用户数据目录；展示页使用一次性临时目录，失败或重试时丢弃。
- 本决定不声称真实门户页面、TLS、重定向、新窗口或 Service Worker 已验证；这些仍需现场验收（见 `docs/VALIDATION.md`）。
- 若将来要让展示页承担填表或提交，必须新开 ADR，不得把它当作 ADR-0009 通道的替代。
