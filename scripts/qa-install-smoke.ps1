#requires -Version 7.0
<#
工单 #20（ADR-0010）：安装/卸载契约真装冒烟矩阵。

⚠️ 仅限一次性 CI 环境（GitHub Windows runner，自带管理员）。在本机运行会真实
覆盖安装本机的 AutoNet——绝对不要在本机执行。

流程：预置假数据目录（假设置 + 占位 DPAPI 文件 + 认证暂停/提交额度/重新开始标记/解除审计记录，
逐字节基线）→ 全新安装 → 断言服务/自启/快捷方式/ACL/数据保留/辅助进程布局 → 坏包安装 →
断言失败回滚恢复旧服务、旧辅助进程与持久记录 → 覆盖安装 → 断言一致 → 卸载 → 断言清理与业务数据保留。
全程不读取真实凭据；假 settings 关闭恢复开关，服务空转不发起认证。
#>
param(
    [string]$Bundle = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/AutoNet-win-x64'),
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    Write-Host 'SMOKE-FAIL CI_ONLY：仅限 GitHub 托管的一次性 CI runner；拒绝在本机或自托管 runner 上执行'
    exit 2
}
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
# 新增持久记录（工单 #31/#33/#34/#36）：安装、覆盖安装、卸载与失败回滚都不得改写它们。
# 内容取当前有效形态，避免 Enabled=false 的服务空转时把它们当成损坏记录重写。
# 注意：auth-block.txt 走默认 JsonSerializer（无字符串枚举转换器），State/Failure 必须写数字；
# submission-budget.json 走带 JsonStringEnumConverter 的 SubmissionJson，State 必须写字符串。
$fakeAuthBlock = '{"Version":1,"State":0,"Failure":0,"Attempts":0,"RetryDelaySeconds":0,"RetryNotBefore":"0001-01-01T00:00:00+00:00","Revision":"11111111-1111-1111-1111-111111111111","ChannelFallbackCause":0,"BlocksSubmissions":false}'
$fakeBudget = '{"Version":1,"State":"Concluded","Attempts":0,"ReservedAt":"0001-01-01T00:00:00+00:00","NextPossibleAt":"0001-01-01T00:00:00+00:00","Revision":"22222222-2222-2222-2222-222222222222"}'
$fakeRestart = '{"Version":1,"Revision":"22222222-2222-2222-2222-222222222222"}'
$fakeAudit = '{"At":"2026-01-01T00:00:00+08:00","Action":"ci-fixture","Attempts":0,"Revision":"22222222-2222-2222-2222-222222222222"}'
Set-Content -LiteralPath (Join-Path $private 'auth-block.txt') -Value $fakeAuthBlock -Encoding UTF8
Set-Content -LiteralPath (Join-Path $private 'submission-budget.json') -Value $fakeBudget -Encoding UTF8
Set-Content -LiteralPath (Join-Path $private 'submission-restart.json') -Value $fakeRestart -Encoding UTF8
Set-Content -LiteralPath (Join-Path $private 'auth-release-audit.jsonl') -Value $fakeAudit -Encoding UTF8
$baseline = @('settings.json', 'credentials.dpapi', 'auth-block.txt', 'submission-budget.json', 'submission-restart.json', 'auth-release-audit.jsonl') | ForEach-Object {
    [pscustomobject]@{ Name = $_; Hash = (Get-FileHash -LiteralPath (Join-Path $private $_) -Algorithm SHA256).Hash }
}

function Assert-DataPreserved([string]$Stage) {
    foreach ($f in $baseline) {
        $path = Join-Path $private $f.Name
        Assert (Test-Path -LiteralPath $path) ("{0}后业务数据存在 {1}" -f $Stage, $f.Name)
        if (Test-Path -LiteralPath $path) {
            $now = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            Assert ($now -eq $f.Hash) ("{0}后数据逐字节保留 {1}" -f $Stage, $f.Name)
        }
    }
}

$authChannelPath = Join-Path $env:ProgramFiles 'AutoNet\authchannel\AutoNet.AuthChannel.exe'
$bundleAuthChannelHash = (Get-FileHash -LiteralPath (Join-Path $Bundle 'authchannel\AutoNet.AuthChannel.exe') -Algorithm SHA256).Hash

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
Assert (Test-Path -LiteralPath $authChannelPath) '通道辅助进程布局（工单 #26）'
Assert ((Get-FileHash -LiteralPath $authChannelPath -Algorithm SHA256).Hash -eq $bundleAuthChannelHash) '全新安装的通道辅助进程与包内一致'
Assert-DataPreserved '全新安装'

$acl = Get-Acl 'C:\ProgramData\AutoNet\private'
Assert (-not ($acl.Access | Where-Object { $_.IdentityReference -like '*Users*' })) 'private 无 Users 条目'
Assert ($acl.Owner -like '*Administrators' -and $acl.AreAccessRulesProtected) 'private 受保护且归管理员'

# 3) 失败回滚（坏包触发 catch 分支）：旧程序、辅助进程与持久记录都必须恢复。
Write-Host '-- 阶段：安装失败回滚 --'
$beforeRollbackChannel = (Get-FileHash -LiteralPath $authChannelPath -Algorithm SHA256).Hash
$brokenBundle = Join-Path ([IO.Path]::GetTempPath()) ('autonet-smoke-broken-' + [Guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $Bundle -Destination $brokenBundle -Recurse -Force
# 服务可执行文件损坏 → Start-Service 失败 → 进入 install.ps1 的 catch 回滚分支。
Set-Content -LiteralPath (Join-Path $brokenBundle 'service\AutoNet.Service.exe') -Value 'not-a-windows-executable'
# 辅助进程也写入不同字节：若回滚没有恢复它，下面的哈希断言会失败。
Add-Content -LiteralPath (Join-Path $brokenBundle 'authchannel\AutoNet.AuthChannel.exe') -Value 'rollback-sentinel'
$installLogBroken = Join-Path $env:TEMP 'smoke-install-broken.log'
& pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $brokenBundle 'install.ps1') -Quiet *> $installLogBroken
Assert ($LASTEXITCODE -ne 0) "坏包安装应以非零退出（实际 $LASTEXITCODE）"
ShowLog '坏包安装' $installLogBroken
# 非零退出也可能是复制前的预检失败；日志里出现回滚文案才证明真的走到了恢复分支。
$brokenLogText = if (Test-Path -LiteralPath $installLogBroken) { Get-Content -LiteralPath $installLogBroken -Raw } else { '' }
Assert ($brokenLogText.Contains('已恢复旧程序')) '坏包安装走到了安装脚本的回滚分支'
$svcAfterRollback = Get-CimInstance Win32_Service -Filter "Name='AutoNet'" -ErrorAction SilentlyContinue
Assert ($null -ne $svcAfterRollback -and $svcAfterRollback.State -eq 'Running') '回滚后旧服务恢复 Running'
Assert ((Get-FileHash -LiteralPath $authChannelPath -Algorithm SHA256).Hash -eq $beforeRollbackChannel) '回滚恢复旧通道辅助进程'
Assert-DataPreserved '回滚'
Remove-Item -LiteralPath $brokenBundle -Recurse -Force -ErrorAction SilentlyContinue

# 4) 覆盖安装（重复安装起点）
Write-Host '-- 阶段：覆盖安装 --'
# 先改动已安装的辅助进程，证明下面的“与包内一致”来自覆盖安装而不是旧文件残留。
Add-Content -LiteralPath $authChannelPath -Value 'pre-reinstall-sentinel'
Assert ((Get-FileHash -LiteralPath $authChannelPath -Algorithm SHA256).Hash -ne $bundleAuthChannelHash) '覆盖安装前已改动已安装的辅助进程'
$installLog2 = Join-Path $env:TEMP 'smoke-install2.log'
& pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installPs1 -Quiet *> $installLog2
Assert ($LASTEXITCODE -eq 0) "覆盖安装退出码 0（实际 $LASTEXITCODE）"
ShowLog '覆盖安装' $installLog2
$svc2 = Get-CimInstance Win32_Service -Filter "Name='AutoNet'"
Assert ($svc2.State -eq 'Running') '覆盖安装后服务仍 Running'
Assert ((Get-FileHash -LiteralPath $authChannelPath -Algorithm SHA256).Hash -eq $bundleAuthChannelHash) '覆盖安装后通道辅助进程仍与包内一致'
Assert-DataPreserved '覆盖安装'

# 5) 卸载（无 -PurgeData：业务数据保留）
Write-Host '-- 阶段：卸载 --'
$uninstallLog = Join-Path $env:TEMP 'smoke-uninstall.log'
& pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $uninstallPs1 *> $uninstallLog
Assert ($LASTEXITCODE -eq 0) "卸载退出码 0（实际 $LASTEXITCODE）"
ShowLog '卸载' $uninstallLog
Assert ($null -eq (Get-Service -Name AutoNet -ErrorAction SilentlyContinue)) '卸载后服务不存在'
Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name AutoNet -ErrorAction SilentlyContinue)) '卸载后自启键删除'
Assert (-not (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AutoNet.lnk'))) '卸载后快捷方式删除'
Assert (Test-Path -LiteralPath $authChannelPath) '卸载保留程序文件（含通道辅助进程）'
Assert-DataPreserved '卸载'

# 收尾：清理假数据（一次性环境整洁）
Remove-Item -LiteralPath 'C:\ProgramData\AutoNet' -Recurse -Force -ErrorAction SilentlyContinue

$report += ("结论：{0} 项失败" -f $failures)
$reportPath = Join-Path $RepoRoot 'artifacts/validation/qa-install-smoke/report.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $reportPath) | Out-Null
$report | Set-Content -LiteralPath $reportPath -Encoding utf8
Get-Content -LiteralPath $reportPath
exit $(if ($failures -gt 0) { 1 } else { 0 })
