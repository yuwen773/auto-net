# AutoNet

Windows 校园网连接守护：开机后台运行，自动连接指定的校园 Wi-Fi，并在 Dr.COM 认证失效时重新登录。登录 Windows 后可通过托盘窗口查看状态、设置凭据、暂停恢复和打开日志。

当前版本针对已核实的长沙理工大学认证站点 `https://login.csust.edu.cn`，运行于 Windows 11 x64。发布包自带 .NET 运行环境；安装脚本需要 **PowerShell 7** 和管理员 UAC 授权。

## 安装与首次设置

1. 打开 `artifacts/AutoNet-win-x64`，双击 `Install.cmd`，接受 Windows 管理员授权。
2. 在设置窗口中选择校园 Wi-Fi，输入校园网账号、密码并保存。不要把密码发到聊天或写入源码。
3. 双击系统托盘里的 AutoNet 图标查看状态。配置生效后，后台服务无需登录 Windows 即可运行。

“本机代理检测”和“UU 服务检测”可以留空。TUN 路径会通过系统选路辅助识别；本机代理检测填写真实的本地 HTTP 代理端口。UU 检测地址只能填写已确认的真实服务地址，不能用官网首页代替远程服务。

发布包：`service/AutoNet.Service.exe` 是后台服务，`tray/AutoNet.Tray.exe` 是托盘应用。首次安装前直接运行托盘可查看界面，但不会自动恢复网络。

## 恢复行为

- 每约 2 秒开始一轮主检测；DNS 和 TCP 连接明确绑定到选定 Wi-Fi 网卡，使用该网卡的 DNS。
- 通过两个 HTTPS 站点的 robots 内容检测校园网直连，避免将认证重定向或普通 HTTP 200 当成联网。
- Wi-Fi 未连接时请求连接已保存的指定配置。不会重置、禁用网卡或重启系统；切换到其他 Wi-Fi 时保留用户选择。
- 认证状态为未登录时，使用校园站点已核实的 `/drcom/login` 协议提交凭据。保留 TLS 校验，不跟随带凭据请求的重定向。
- 首次故障立即处理，前 15 秒采用短间隔重试，随后逐步退避至每 60 秒重试一次；检测继续运行。
- 已认证但外网不可达时继续检测，并记录上游异常，不持续提交密码。
- 服务器明确拒绝凭据或认证模式变化时暂停认证；重新保存设置和凭据后解除暂停。该暂停跨服务重启保留。
- 接通电源且启用监测时，申请阻止系统自动休眠；屏幕仍可关闭。暂停或服务停止后释放申请，不永久修改电源计划。
- UU 和系统路径检测独立运行，不阻塞主恢复循环，不自动修改或重启 UU、Mihomo。

**15 秒是待实测的恢复目标，不是已经验证的保证。** 界面的恢复耗时从首轮检测到故障开始计算；真实断网可能早于检测。真实验收必须包含故障发生到恢复的完整时间，不能只测登录接口耗时。

**UU 服务运行、存在 TCP 连接、指定服务可达，都不等于一次远程会话已成功。** 最终验收需用另一台设备连接 UU，特别要验证重启后停留在 Windows 登录界面的场景。

## 本机数据

- 安装位置：`C:\Program Files\AutoNet`。
- 认证设置：`C:\ProgramData\AutoNet\private`，仅管理员和 SYSTEM 可读写。
- 凭据采用 Windows DPAPI 机器级加密，后台服务可在用户未登录时解密；通过目录 ACL 限制本机其他账户访问密文。
- 状态：`C:\ProgramData\AutoNet\status.json`，普通用户只读。
- 日志：`C:\ProgramData\AutoNet\logs`，按天保存，保留 14 天。只记录受控原因、动作和耗时，不保存账号、密码、Cookie、原始响应或带凭据 URL。
- 注册服务名：`AutoNet`；依赖 `WlanSvc` 和 `Tcpip`；服务异常退出后按 5/15/60 秒重新启动。
- 托盘自启动：注册在 HKLM Run；退出托盘不停止后台服务。

暂停自动恢复请使用托盘菜单；完全停止后台服务可在 Windows 服务管理器停止 `AutoNet`。卸载服务及托盘自启：以 PowerShell 7 执行发布包中的 `uninstall.ps1`；脚本会请求管理员权限，保留安装文件、日志和加密配置以便复查。

## 开发与验证

需要 .NET 10 SDK。构建脚本优先使用项目的 `.tools/dotnet`，其次使用 PATH 中的 `dotnet.exe`。

```powershell
pwsh.exe -NoLogo -NoProfile -File .\scripts\build.ps1
pwsh.exe -NoLogo -NoProfile -File .\scripts\build.ps1 -Publish
```

测试程序包含实际策略、认证适配、DNS 解析和 captive portal 误判场景；失败会返回非零退出码。

只读本机诊断，不连接/断开 Wi-Fi、不提交凭据：

```powershell
.\artifacts\AutoNet-win-x64\service\AutoNet.Service.exe --diagnose
```

详情与验收清单见 [docs/VALIDATION.md](docs/VALIDATION.md)。

## 实现依据

- 校园认证站点公开脚本 `a41.js`、`a43.js`、`a78.js` 和当前 PC 方案：核实 HTTPS、端口 802 页面配置、Dr.COM 本地登录参数及错误分类。源码不包含抓取页面中的个人信息。
- [Microsoft Windows 后台服务](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)。
- [WlanConnect](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanconnect)。
- [Windows IP_UNICAST_IF](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options) 与 [DPAPI 作用域](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope)。
