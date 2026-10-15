# 诊断包内容与脱敏契约

10-12/13 故障排查靠人工解析 4MB 日志。诊断包让用户 10 秒自助导出证据，也可直接交给学校网管。内容红线沿用 VALIDATION.md：不含账号、密码、Cookie、Token；MAC 掩码；导出物只证明“采集时看到了什么”，不证明恢复性能。

## 内容（zip）

1. 近 3 天日志 JSONL（schemaVersion 2 记录自带脱敏约束）
2. net_state.json：NetworkDiagnostics.RunAsync 现成报告（Wi-Fi/IPv4/DNS/外网探测/门户只读检查/路由证据）
3. status.json 当前快照
4. 设置脱敏快照（白名单：Enabled / AllowActiveDisconnect / KeepAwakeOnAc / InterfaceId / Wi-Fi 配置名 / PortalOrigin；无凭据字段；InterfaceId 与 Wi-Fi 配置名保留。代理与 UU 检测地址不在白名单：与校园网诊断无关，且属用户自身环境信息）
5. 门户只读诊断（InspectDetailedAsync 只读结果：阶段、失败分类、HTTP 状态、参数存在性；较 InspectAsync 的 PortalState 三元组更能定位问题，同样无凭据）
6. README.txt：内容清单、导出时间、构建号、阅读指引

## 触发与落点

先做 Service `--export-diagnostics [path]` CLI（管理员运行）；SaveFileDialog 默认桌面的菜单触发留给 UI PR。不自动导出：暂停态只提示“建议导出”，避免隐私与磁盘意外。网络信息用 .NET 原生 API 重建，不实采 ipconfig/netsh，保持零进程依赖；网管若需要原生命令口径，后续可加。

## Consequences

- 导出库在 Core，CLI 与将来的菜单共用同一实现，不会出现两份脱敏逻辑。
- 包内字段要么来自受控词表，要么来自 NetworkDiagnostics 既有脱敏模型；新增字段必须先过红线清单。
