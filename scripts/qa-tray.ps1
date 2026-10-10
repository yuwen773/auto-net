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
    unknown = @{ State = '认证状态未知'; Portal = '未知，不自动认证'; Detail = '外网和认证状态暂不可确认；持续检测，不提交密码。' }
    upstream = @{ State = '已认证但外网不可达'; Portal = '已认证'; Detail = '校园网仍显示已认证，外网不可达；继续检测，不重复提交密码。' }
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
Write-Host '截图仅验证本机渲染和显式布局缩放，不代替 Windows 显示 DPI 100%/150% 的现场验收。'
