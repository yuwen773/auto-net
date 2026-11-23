# AutoNet Preview（只读）

Tauri 2 + React 19 的只读状态/日志预览。它读取现有服务写的 `status.json`、日志目录与 SCM 服务状态，
**不**访问 `C:\ProgramData\AutoNet\private`，不提交凭据、不操作 Wi-Fi、不启停服务，也不写注册表自启。

## 开发

```powershell
pnpm install
pnpm tauri dev          # 需要 Rust 1.100.0 + MSVC + WebView2
pnpm typecheck
pnpm test
pnpm build              # 只构建前端
```

仓库根目录的脚本也可用：

```powershell
pwsh.exe -NoLogo -NoProfile -File scripts/qa-tauri-ui.ps1 -SkipInstall
pwsh.exe -NoLogo -NoProfile -File scripts/build-rust.ps1        # 纯 Rust crate
```

## 边界

- 只有四个只读命令：`get_supported_contract_version`、`get_snapshot`、`get_service_health`、`list_log_summaries`。
- 状态投影丢弃账号、网卡、配置名、代理、UU 地址、`Detail` 与 `LastError`；日志只返回白名单字段。
- 浏览器环境（没有 Tauri IPC）只显示提示，不读取任何本机数据。
- 关闭窗口只隐藏；退出预览不会停止服务，也不修改自启。

身份：`AutoNet Preview` / `com.autonet.preview`。它不是正式发布版本，详见
[ADR-0023](../../docs/adr/0023-readonly-preview-boundary.md) 与 [首轮记录](../../docs/progress/43-tauri-readonly-preview.md)。
