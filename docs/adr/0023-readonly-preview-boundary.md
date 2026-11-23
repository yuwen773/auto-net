# 只读预览应用的进程与数据边界

2026-11-23，P1 首个只读预览落地时确定。接续 [ADR-0022](0022-tauri-rust-gradual-migration.md)，回答“新 UI 在只读阶段能看什么、不能做什么、失败时怎么表现”。

## 决定

### 进程身份

- 预览应用使用独立产品名 `AutoNet Preview` 与 Tauri 标识 `com.autonet.preview`。单实例由 Tauri 的单实例插件按该标识建立，与正式 `AutoNet.Tray.exe` 的互斥体不共享、不冲突。
- 预览应用以当前普通用户身份运行，不请求提升，不注册 `HKLM Run`，不创建开始菜单快捷方式。关闭或退出预览进程不得影响 `AutoNet` 服务。
- 预览应用不写 `C:\ProgramData\AutoNet` 下任何文件。它唯一的写入位置是 Tauri 自身在用户目录下的窗口/主题缓存。

### 数据访问白名单

预览只读取三类数据，全部通过 Rust 命令投影，React 永远拿不到文件句柄或原始 JSON：

1. `C:\ProgramData\AutoNet\status.json`：只取受控状态字段（状态、Wi-Fi/直连/门户显示、动作与断线次数、提交额度显示、认证阶段与失败分类、恢复耗时、最近更新时间）。**不投影** `Username`、`InterfaceId`、`WifiProfile`、`ProxyUrlDisplay`、`UuEndpointDisplay`、`LastError` 与任何自由文本或网络标识。
2. `C:\ProgramData\AutoNet\logs\autonet-YYYY-MM-DD.jsonl`：受限尾部读取。每条记录只投影时间、白名单事件名、白名单 Wi-Fi/门户/直连状态、阶段与失败分类（同样来自白名单）；`detail`、异常原文、账号和网络标识一律丢弃。损坏行计入缺口，不阻断其他记录。
3. SCM 中服务 `AutoNet` 的安装/运行状态：只读查询，用于显示“服务未安装 / 已停止 / 正在运行”。

`private` 目录（设置与凭据）对预览不可读，也不尝试读取；预览明确显示“设置与凭据仅在完整版可用”。

### 命令面

Rust 只注册四个只读命令：`get_supported_contract_version`、`get_snapshot`、`get_service_health`、`list_log_summaries`。不注册保存设置、导出诊断、解除暂停、启动认证通道、执行命令或写文件的命令。Capability 只包含 Tauri 核心默认能力。

### 失败语义

数据缺失、损坏、拒绝访问和过期都必须是显式状态，而不是空白页或乐观默认：

- 文件不存在 → `Missing`，提示服务可能尚未运行或尚未写入状态。
- JSON 损坏或字段不足 → `Corrupt`，不猜测缺失字段，不显示上一份数据。
- 无读取权限 → `Denied`，提示以普通权限无法读取。
- `UpdatedAt` 超过阈值或为未来时间 → `Stale`，仍显示内容但明确标注不可确认。
- 服务未安装 / 已停止 → 独立于状态文件显示，不得用旧状态文件冒充实时状态。

### 与正式 UI 的关系

预览不是正式托盘的替代品。它不显示通知、不打开门户、不提供任何恢复或认证入口；正式入口仍由 `AutoNet.Tray.exe` 持有。P2 之前双 UI 可以并存，但只有一个进程持有正式托盘身份。

## 后果

- 预览应用即使被篡改前端，也没有可调用的写接口或提权路径；最坏影响是其自身进程。
- 脱敏在 Rust 层完成，React 只接收已裁剪的 DTO；未来新增字段必须同时加入白名单与回归测试，不能默认透传。
- 预览无法解释“为什么没联网”之外的敏感细节；排障仍需正式诊断页或 `--diagnose`。这是刻意的边界。
