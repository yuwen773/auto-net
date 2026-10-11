#requires -Version 7.0
# Uses fabricated status only. Does not install/start/stop services or read credentials.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'artifacts/ui-qa'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$tray = Join-Path $repo 'src/AutoNet.Tray/bin/Release/net10.0-windows/AutoNet.Tray.dll'
if (-not (Test-Path -LiteralPath $tray)) { throw 'Build the tray project first.' }
$cases = @{
    online = @{ State = '校园网在线'; Direct = '外网可达'; Portal = '未查询（外网检查已通过）'; Detail = '指定 Wi-Fi 绑定的 HTTPS 内容检查通过；物理出口需抓包核验。' }
    unknown = @{ State = '认证状态未知'; Portal = '未知，不自动认证'; NextAttempt = [DateTimeOffset]::UtcNow.AddSeconds(30).ToString('o'); Detail = '两个外网检查均未通过，认证状态未知；连续异常至少 30 秒且至少 3 轮失败后允许重连，确认需认证后再登录。认证状态查询：网络请求失败 [Status/Transport]；未发送凭据' }
    upstream = @{ State = '已认证但外网不可达'; Portal = '已认证'; Detail = '校园网显示已认证，但两个外网检查均未通过；连续异常至少 30 秒且至少 3 轮失败后允许重连校园 Wi-Fi。' }
    reconnect = @{ State = '正在重连校园 Wi-Fi'; Portal = '未知，不自动认证'; DisconnectAttempts = 1; NextAttempt = [DateTimeOffset]::UtcNow.AddSeconds(120).ToString('o'); Detail = '持续离线，已请求断开选定校园 Wi-Fi；等待断开后连接原配置，再检查是否需要认证。' }
    exhausted = @{ State = '认证状态未知'; Portal = '未知，不自动认证'; DisconnectAttempts = 3; Detail = '本次故障的 3 次主动断线额度已用尽，停止主动断线；继续探测与允许的认证，连续在线 60 秒后恢复额度。' }
    binding = @{ State = '需要处理认证'; CredentialsBlocked = $true; Portal = '需认证'; Detail = '网关或设备绑定被拒绝，认证及主动断线已暂停；请核实 VLAN、MAC/AC 参数后使用“解除认证暂停”，不要仅修改密码。' }
    proxy = @{ State = '校园网在线'; Direct = '外网可达'; Tunnel = '本机代理不可达'; Detail = '校园网检测通过；代理异常不会触发校园 Wi-Fi 重连。' }
    blocked = @{ State = '需要处理认证'; CredentialsBlocked = $true; Portal = '需认证'; Detail = '自动认证已暂停；认证模式校验：缺少必需字段 [AuthenticationMode/MissingFlag] 字段 password_cut；未发送凭据'; LastError = '认证模式校验：缺少必需字段 [AuthenticationMode/MissingFlag] 字段 password_cut；未发送凭据' }
    stale = @{ State = '校园网在线'; UpdatedAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o') }
    missing = @{}
}
foreach ($name in $cases.Keys | Sort-Object) {
    $statusPath = Join-Path $output ($name + '.json')
    if ($name -ne 'missing') {
        $status = @{ UpdatedAt = [DateTimeOffset]::UtcNow.ToString('o'); Enabled = $true; Wifi = '已连接'; Direct = '外网不可达或未知';
            Tunnel = '系统路径可达（路由样本推断为 TUN，出口未核验）'; Uu = 'UU 服务运行；在线状态待核验';
            Attempts = 2; LastRecoverySeconds = 4.74; LastRecoveryAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o') }
        foreach ($key in $cases[$name].Keys) { $status[$key] = $cases[$name][$key] }
        $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    }
    foreach ($scale in @('1', '1.5')) {
        $capture = Join-Path $output ($name + '-' + $scale + '.png')
        $start = [Diagnostics.ProcessStartInfo]::new($sdk)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
        $start.ArgumentList.Add($tray)
        $start.Environment['AUTONET_QA_STATUS_PATH'] = $statusPath
        $start.Environment['AUTONET_CAPTURE_PATH'] = $capture
        $start.Environment['AUTONET_QA_SCALE'] = $scale
        $child = [Diagnostics.Process]::Start($start)
        if (-not $child.WaitForExit(15000)) { $child.Kill($true); throw ('UI capture timed out: ' + $name) }
        if ($child.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $capture)) { throw ('UI capture failed: ' + $name) }
        Write-Host ('截图完成：' + $name + '，布局缩放 ' + $scale)
        $child.Dispose()
    }
}
$logs = Join-Path $output 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$logPath = Join-Path $logs 'autonet-2026-10-10.jsonl'
$entries = @(
    @{ time = '2026-10-10T16:39:13+08:00'; action = '认证状态未知'; detail = '两个外网检查均未通过。认证状态查询：请求超时；未发送凭据。'; wifi = '已连接'; direct = '外网不可达'; portal = '未知'; attempts = 0; nextAttempt = '2026-10-10T16:39:23+08:00' },
    @{ time = '2026-10-10T16:39:23+08:00'; action = '重连校园 Wi-Fi'; detail = '持续离线，已请求断开选定校园 Wi-Fi；等待断开后连接原配置，再检查是否需要认证。'; wifi = '已连接'; direct = '外网不可达'; portal = '未知'; attempts = 1; nextAttempt = '2026-10-10T16:40:23+08:00' },
    @{ time = '2026-10-10T16:39:29+08:00'; action = '观测到联网恢复'; detail = 'HTTPS 外网检查已通过；本轮自动动作 3 次。恢复来源无法确认，可能包含人工处理或网络自行恢复。'; wifi = '已连接'; direct = '外网可达'; portal = '未查询'; attempts = 3; seconds = 16 }
)
$entries | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content -LiteralPath $logPath -Encoding utf8
foreach ($scale in @('1', '1.5')) {
    $capture = Join-Path $output ('logs-' + $scale + '.png')
    $start = [Diagnostics.ProcessStartInfo]::new($sdk)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.ArgumentList.Add($tray); $start.ArgumentList.Add('--logs')
    $start.Environment['AUTONET_QA_LOG_DIRECTORY'] = $logs
    $start.Environment['AUTONET_CAPTURE_PATH'] = $capture
    $start.Environment['AUTONET_QA_SCALE'] = $scale
    $child = [Diagnostics.Process]::Start($start)
    if (-not $child.WaitForExit(15000)) { $child.Kill($true); throw 'Log UI capture timed out.' }
    if ($child.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $capture)) { throw 'Log UI capture failed.' }
    Write-Host ('截图完成：日志窗口，布局缩放 ' + $scale)
    $child.Dispose()
}
Write-Host '截图仅验证本机渲染和显式布局缩放，不代替 Windows 显示 DPI 100%/150% 的现场验收。'
