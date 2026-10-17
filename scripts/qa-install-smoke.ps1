#requires -Version 7.0
<#
工单 #20（ADR-0010）：安装/卸载契约真装冒烟矩阵。

⚠️ 仅限一次性 CI 环境（GitHub Windows runner，自带管理员）。在本机运行会真实
覆盖安装本机的 AutoNet——绝对不要在本机执行。

流程：预置假数据目录（假设置 + 占位 DPAPI 文件，逐字节基线）→ 全新安装 → 断言
服务/自启/快捷方式/ACL/数据保留 → 覆盖安装 → 断言一致 → 卸载 → 断言清理与业务数据保留。
全程不读取真实凭据；假 settings 关闭恢复开关，服务空转不发起认证。
#>
param(
    [string]$Bundle = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/AutoNet-win-x64'),
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'SMOKE-FAIL 需要管理员（CI runner 自带；本机严禁运行此脚本）'; exit 2 }

$installPs1 = Join-Path $Bundle 'install.ps1'
$uninstallPs1 = Join-Path $Bundle 'uninstall.ps1'
foreach ($p in @($installPs1, $uninstallPs1)) { if (-not (Test-Path -LiteralPath $p)) { Write-Host "SMOKE-FAIL 缺少 $p"; exit 2 } }
$report = [System.Collections.Generic.List[string]]::new()
$report += "=== 安装冒烟矩阵（$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')，一次性环境）==="
$failures = 0

function Assert([bool]$Condition, [string]$Name) {
    Write-Host ("{0} {1}" -f $(if ($Condition) { 'PASS' } else { 'FAIL' }), $Name)
    $script:failures += $(if ($Condition) { 0 } else { 1 })
    $report += ('{0} {1}' -f $(if ($Condition) { 'PASS' } else { 'FAIL' }), $Name)
}

function ShowLog([string]$Tag, [string]$Path) {
    Write-Host ("--- {0} 子进程日志 ---" -f $Tag)
    if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path | ForEach-Object { Write-Host $_ } }
}

# 1) 预置假数据（逐字节基线）
$private = 'C:\ProgramData\AutoNet\private'
New-Item -ItemType Directory -Force -Path $private | Out-Null
$fakeSettings = '{"Enabled":false,"AllowActiveDisconnect":false,"InterfaceId":"00000000-0000-0000-0000-000000000000","WifiProfile":"ci-fixture","PortalOrigin":"https://portal.fixture","KeepAwakeOnAc":false,"ProxyUrl":"","UuEndpoint":""}'
$fakeDpapi = [byte[]](1..262 | ForEach-Object { $_ % 256 })
Set-Content -LiteralPath (Join-Path $private 'settings.json') -Value $fakeSettings -Encoding UTF8
[IO.File]::WriteAllBytes((Join-Path $private 'credentials.dpapi'), $fakeDpapi)
$baseline = @('settings.json', 'credentials.dpapi') | ForEach-Object {
    [pscustomobject]@{ Name = $_; Hash = (Get-FileHash -LiteralPath (Join-Path $private $_) -Algorithm SHA256).Hash }
}

# 2) 全新安装（runner 为管理员：install.ps1 提权分支直接执行）
Write-Host '-- 阶段：全新安装 --'
$installLog1 = Join-Path $env:TEMP 'smoke-install1.log'
& pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installPs1 -Quiet *> $installLog1
Assert ($LASTEXITCODE -eq 0) "全新安装退出码 0（实际 $LASTEXITCODE）"
ShowLog '全新安装' $installLog1

$svc = Get-CimInstance Win32_Service -Filter "Name='AutoNet'" -ErrorAction SilentlyContinue
Assert ($null -ne $svc -and $svc.State -eq 'Running') '服务 Running'
Assert ($svc.StartMode -eq 'Auto') '服务自启 Auto'
Assert ($svc.PathName -eq ('"' + (Join-Path $env:ProgramFiles 'AutoNet\service\AutoNet.Service.exe') + '"')) '服务 PathName 契约'
$runKey = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name AutoNet -ErrorAction SilentlyContinue).AutoNet
Assert ($runKey -eq ('"' + (Join-Path $env:ProgramFiles 'AutoNet\tray\AutoNet.Tray.exe') + '" --tray')) '自启 Run 键契约'
Assert (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AutoNet.lnk')) '开始菜单快捷方式'
Assert (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'AutoNet\authchannel\AutoNet.AuthChannel.exe')) '通道辅助进程布局（工单 #26）'

$acl = Get-Acl 'C:\ProgramData\AutoNet\private'
Assert (-not ($acl.Access | Where-Object { $_.IdentityReference -like '*Users*' })) 'private 无 Users 条目'
Assert ($acl.Owner -like '*Administrators' -and $acl.AreAccessRulesProtected) 'private 受保护且归管理员'

# 3) 覆盖安装（重复安装起点）
Write-Host '-- 阶段：覆盖安装 --'
$installLog2 = Join-Path $env:TEMP 'smoke-install2.log'
& pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installPs1 -Quiet *> $installLog2
Assert ($LASTEXITCODE -eq 0) "覆盖安装退出码 0（实际 $LASTEXITCODE）"
ShowLog '覆盖安装' $installLog2
$svc2 = Get-CimInstance Win32_Service -Filter "Name='AutoNet'"
Assert ($svc2.State -eq 'Running') '覆盖安装后服务仍 Running'
foreach ($f in $baseline) {
    $now = (Get-FileHash -LiteralPath (Join-Path $private $f.Name) -Algorithm SHA256).Hash
    Assert ($now -eq $f.Hash) "覆盖安装后数据保留 $($f.Name)"
}

# 4) 卸载（无 -PurgeData：业务数据保留）
Write-Host '-- 阶段：卸载 --'
$uninstallLog = Join-Path $env:TEMP 'smoke-uninstall.log'
& pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $uninstallPs1 *> $uninstallLog
Assert ($LASTEXITCODE -eq 0) "卸载退出码 0（实际 $LASTEXITCODE）"
ShowLog '卸载' $uninstallLog
Assert ($null -eq (Get-Service -Name AutoNet -ErrorAction SilentlyContinue)) '卸载后服务不存在'
Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name AutoNet -ErrorAction SilentlyContinue)) '卸载后自启键删除'
Assert (-not (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AutoNet.lnk'))) '卸载后快捷方式删除'
foreach ($f in $baseline) {
    Assert (Test-Path -LiteralPath (Join-Path $private $f.Name)) "卸载后业务数据保留 $($f.Name)"
}

# 收尾：清理假数据（一次性环境整洁）
Remove-Item -LiteralPath 'C:\ProgramData\AutoNet' -Recurse -Force -ErrorAction SilentlyContinue

$report += ("结论：{0} 项失败" -f $failures)
$reportPath = Join-Path $RepoRoot 'artifacts/validation/qa-install-smoke/report.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $reportPath) | Out-Null
$report | Set-Content -LiteralPath $reportPath -Encoding utf8
Get-Content -LiteralPath $reportPath
exit $(if ($failures -gt 0) { 1 } else { 0 })
