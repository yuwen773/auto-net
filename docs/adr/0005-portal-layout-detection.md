# 门户改版做检测信号，不做通用动态解析

借鉴 cmcc 的“每次从门户页动态解析提交地址和隐藏字段”初衷是防学校改版。但我们的门户是 JSON API（chkstatus JSONP + 网关 302 query + loadConfig 标志位 + 登录 GET query），没有 HTML 表单可抓；且改版后的新布局没有夹具，现在实现通用解析器等于写不可测的投机代码。

决定先做“改版检测”：预检失败落在结构类集合（MissingFlag/UnsupportedFlag/InvalidResponse/MissingGatewayParameters/chkstatus JSONP 结构变化）时聚合为 PortalLayoutChanged 信号，日志明确记录、状态标志置位、通知事件告警“门户疑似改版，自动认证已安全暂停，请导出诊断包反馈”。

## Consequences

- 改版真发生时，诊断包提供真实夹具，届时再建解析器（可能仍是配置驱动的参数映射，而非 HTML 抓取）。
- 在检测到位前，改版表现为“安全暂停 + 明确信号”，不会静默无限重试或误判凭据错误。
- 明确拒绝的方案：现在就实现通用动态解析。理由是不可测、无夹具，且会把猜测逻辑放进生产认证路径。
