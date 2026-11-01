#requires -Version 7.0
# 上网认证页 QA（迁移工单 #37）。
# 覆盖：自包含 win-x64 单文件发布契约、真实窗口中的七态认证页（含恢复开关关闭）与额度/存储/暂停/
# 缺少设置与凭据子状态、假浏览器宿主只附着一次、
# 普通刷新/切页/最小化不增加导航、已填内容保留，以及运行时缺失、网络边界未就绪与加载失败的
# 受控提示。所有状态均为 QA 目录内的虚构夹具；不连接真实门户、不读取真实凭据、不安装 WebView2。
# 边界：不安装、不替换、不重启服务；不写本机真实 status.json；不弹真实 UAC。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-auth'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_STATUS_PATH', 'AUTONET_QA_MUTEX_SUFFIX')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Pass([string]$Name, $Detail) { $steps[$Name] = @{ result = 'pass'; detail = $Detail }; Write-Host ('PASS ' + $Name) }
function Fail([string]$Name, [string]$Message) {
    $steps[$Name] = @{ result = 'fail'; detail = $Message }
    $failures.Add($Name + '：' + $Message)
    Write-Host ('FAIL ' + $Name + '：' + $Message)
}
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Start-App {
    param([string]$Executable, [string[]]$Arguments, [string]$QaDirectory, [string]$QaMode, [string]$StatusPath = '', [string]$MutexSuffix = '')
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_STATUS_PATH', $StatusPath)
    # 本机可能仍有 0.3.1 候选托盘占用 QA 默认互斥名；按证据目录生成后缀隔离，不触碰已安装进程。
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MUTEX_SUFFIX', $MutexSuffix)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $tracked.Add($process)
    return $process
}
function Wait-ForFile([string]$Path, [int]$TimeoutSeconds = 25) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { Start-Sleep -Milliseconds 250; return $true }
        Start-Sleep -Milliseconds 120
    }
    return $false
}
function Wait-Exit($Process, [int]$TimeoutSeconds = 90) {
    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) { return $false }
    return $true
}
function New-QaDirectory([string]$Name) {
    $path = Join-Path $evidence $Name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
function Get-Step($Report, [string]$Scenario) {
    $match = @($Report.Steps | Where-Object Scenario -eq $Scenario)
    Require ($match.Count -eq 1) ('认证页步骤缺失或重复：' + $Scenario)
    return $match[0]
}
function Get-InstalledTrayPids {
    return @(Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and -not [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { $_.Id })
}

$evidencePrefix = [IO.Path]::GetFullPath($evidence) + [IO.Path]::DirectorySeparatorChar
Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

try {
    $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    $serviceBefore = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
    $installedTrayBefore = Get-InstalledTrayPids

    if ($SkipPublish) { Pass 'publish' @{ skipped = $true; exe = $exe } }
    else {
        try {
            if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
            & $sdk publish (Join-Path $repo 'src/AutoNet.Tray.Avalonia/AutoNet.Tray.Avalonia.csproj') -c Release -r win-x64 --self-contained true -o $publishDir -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:AutoNetPublishIdentity=true --nologo
            Require ($LASTEXITCODE -eq 0) 'dotnet publish 返回非零退出码'
            Require (Test-Path -LiteralPath $exe) '发布产物缺少 AutoNet.Tray.exe'
            $inventory = @(Get-ChildItem -LiteralPath $publishDir -File | ForEach-Object { @{ name = $_.Name; length = $_.Length } })
            $extra = @($inventory | Where-Object { $_.name -notmatch '\.(exe|pdb)$' })
            Require ($extra.Count -eq 0) ('发布目录存在非单文件运行时文件：' + (($extra | ForEach-Object name) -join '、'))
            $version = (Get-Item -LiteralPath $exe).VersionInfo
            Pass 'publish' @{
                exe = $exe; files = $inventory; productVersion = $version.ProductVersion
                assemblyName = 'AutoNet.Tray（-p:AutoNetPublishIdentity，csproj 内激活，保持安装契约身份）'
                sdk = (& $sdk --version); commit = (& git -C $repo rev-parse HEAD)
            }
        }
        catch { Fail 'publish' $_.Exception.Message }
    }

    if (-not (Test-Path -LiteralPath $exe)) { throw '没有可运行的壳发布产物，后续核验无法进行。' }

    try {
        $qaDir = New-QaDirectory 'auth-page'
        $fixture = Join-Path $qaDir 'status-fixture.json'
        $mutexSuffix = 'q' + ([Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($qaDir)))).Substring(0, 16).ToLowerInvariant()
        $process = Start-App -Executable $exe -Arguments @() -QaDirectory $qaDir -QaMode 'auth-page' -StatusPath $fixture -MutexSuffix $mutexSuffix
        $reportPath = Join-Path $qaDir 'auth-page.json'
        Require (Wait-ForFile $reportPath 60) '认证页 QA 报告未生成'
        Require (Wait-Exit $process 120) '认证页 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('认证页 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture' -and $report.Fixture) '夹具模式标记不符'
        Require ($null -eq $report.Error) ('认证页 QA 报告包含错误：' + $report.Error)
        Require ($report.MutexName -eq ('Local\AutoNet.Tray.Avalonia.' + $mutexSuffix)) ('QA 互斥后缀未生效：' + $report.MutexName)

        $fixedAddress = 'https://login.csust.edu.cn/'
        foreach ($step in $report.Steps) {
            Require ($step.PortalAddress -eq $fixedAddress) ('认证页显示了非固定门户地址：' + $step.Scenario)
        }

        $online = Get-Step $report 'online'
        Require ($online.Phase -eq 'Online' -and $online.Title -eq '已联网') '联网状态投影不符'
        $authenticated = Get-Step $report 'authenticated'
        Require ($authenticated.Phase -eq 'Authenticated' -and $authenticated.Title -eq '认证成功') '认证成功状态投影不符'
        $unknown = Get-Step $report 'outcome-unknown'
        Require ($unknown.Phase -eq 'OutcomeUnknown' -and $unknown.Title -eq '提交结果未知') '结果未知状态投影不符'
        Require ($unknown.Explanation -match '只读复检' -and $unknown.Explanation -match '有限自动重试' -and
            $unknown.Explanation -match '不会重发凭据') '结果未知未准确区分只读复检与有限自动重试'
        $exhausted = Get-Step $report 'outcome-exhausted'
        Require ($exhausted.Phase -eq 'NeedsManual' -and $exhausted.Title -eq '提交额度已用尽') '提交额度用尽投影不符'
        Require ($exhausted.Explanation -match '不会自动重发凭据') '提交额度用尽仍暗示自动重发'
        $onlinePending = Get-Step $report 'outcome-online-pending'
        Require ($onlinePending.Phase -eq 'Online' -and $onlinePending.Title -eq '已联网') '外网恢复仍显示结果未知'
        Require ($onlinePending.Explanation -match '上次提交结果' -and $onlinePending.Explanation -match '不会重新提交') '外网恢复未说明暂停提交'
        $storageFault = Get-Step $report 'storage-fault'
        Require ($storageFault.Phase -eq 'NeedsManual' -and $storageFault.Title -eq '认证状态需检查') '提交消解存储故障投影不符'
        # 恢复开关关闭：必须与“等待重试”区分，并说明如何恢复；不得声称会自动认证或有限重试。
        $paused = Get-Step $report 'paused-needs-auth'
        Require ($paused.Phase -eq 'Paused' -and $paused.Title -eq '自动恢复已关闭') '恢复开关关闭未投影为已暂停'
        Require ($paused.Explanation -match '不会重连' -and $paused.Explanation -match '自动提交') '暂停说明未声明不会重连或自动提交'
        Require ($paused.Explanation -notmatch '自动认证将在' -and $paused.Explanation -notmatch '重试节奏') '恢复关闭仍声称会自动认证'
        Require ($paused.Background -match '自动恢复已关闭' -and $paused.Background -notmatch '按协议认证运行') '暂停状态的背景说明仍声称协议认证在运行'
        $pausedUnknown = Get-Step $report 'paused-outcome-unknown'
        Require ($pausedUnknown.Phase -eq 'Paused' -and
            $pausedUnknown.Explanation -match '上次凭据提交的结果仍未确认' -and
            $pausedUnknown.Explanation -match '只读复检' -and
            $pausedUnknown.Explanation -notmatch '有限自动重试') '暂停与结果未知并存文案不准确'
        Require ($pausedUnknown.Background -match '只读复检' -and $pausedUnknown.Background -notmatch '有限自动重试') '暂停背景说明仍承诺有限自动重试'
        $pausedStorage = Get-Step $report 'paused-storage-fault'
        Require ($pausedStorage.Phase -eq 'NeedsManual' -and $pausedStorage.Title -eq '认证状态需检查') '暂停与存储故障并存投影不符'
        Require ($pausedStorage.Explanation -match '自动恢复当前已关闭' -and
            $pausedStorage.Explanation -notmatch '有限自动重试') '存储故障在暂停下仍承诺有限自动重试'
        $pausedExhausted = Get-Step $report 'paused-exhausted'
        Require ($pausedExhausted.Phase -eq 'NeedsManual' -and $pausedExhausted.Title -eq '提交额度已用尽') '暂停与额度耗尽并存投影不符'
        Require ($pausedExhausted.Explanation -match '即使开启自动恢复也不会自动重发凭据') '额度耗尽未说明开启恢复也不会重发'
        # 缺少设置/凭据：不得让用户继续等待自动认证，并指引到设置。
        $missingCredentials = Get-Step $report 'missing-credentials'
        Require ($missingCredentials.Phase -eq 'NeedsManual' -and $missingCredentials.Title -eq '需要保存认证信息') '未保存凭据未投影为需要设置'
        Require ($missingCredentials.Explanation -match '没有可用的认证凭据' -and
            $missingCredentials.Explanation -match '不会发出认证请求') '未保存凭据未说明不会发出认证请求'
        Require ($missingCredentials.Explanation -notmatch '自动认证将在' -and
            $missingCredentials.Explanation -notmatch '等待') '未保存凭据仍让用户等待自动认证'
        Require ($missingCredentials.NextStep -match '设置' -and $missingCredentials.NextStep -match '保存账号密码') '未保存凭据未指引到设置'
        Require ($missingCredentials.Background -match '不会发出认证请求' -and
            $missingCredentials.Background -notmatch '按协议认证运行') '未保存凭据的背景说明仍声称协议认证在运行'
        $settingsMissing = Get-Step $report 'settings-missing'
        Require ($settingsMissing.Phase -eq 'NeedsManual' -and $settingsMissing.Title -eq '尚未保存设置') '缺少设置未投影为需要设置'
        Require ($settingsMissing.NextStep -match '设置') '缺少设置未指引到设置'
        Require ($storageFault.Explanation -match '有限自动重试' -and $storageFault.Explanation -notmatch '不会据此重发凭据') '存储故障错误地承诺停止重试'
        $layoutUnblocked = Get-Step $report 'layout-unblocked'
        Require ($layoutUnblocked.Title -eq '门户结构疑似变化' -and $layoutUnblocked.Explanation -match '不代表认证已暂停') '结构信号被误称认证暂停'
        $layoutBlocked = Get-Step $report 'layout-blocked'
        Require ($layoutBlocked.Title -eq '门户结构疑似变化' -and $layoutBlocked.Explanation -match '认证已暂停') '结构信号合并认证阻断未显示暂停'
        $waiting = Get-Step $report 'waiting-retry'
        Require ($waiting.Phase -eq 'WaitingRetry' -and $waiting.Title -eq '等待重试') '等待重试状态投影不符'
        $manual = Get-Step $report 'needs-manual'
        Require ($manual.Phase -eq 'NeedsManual' -and $manual.Title -eq '需要人工处理') '需要人工状态投影不符'
        $unconfirmed = Get-Step $report 'unconfirmed'
        Require ($unconfirmed.Phase -eq 'Unconfirmed' -and $unconfirmed.Title -eq '状态待确认') '不可确认状态降级不符'

        $titles = @($online.Title, $authenticated.Title, $unknown.Title, $waiting.Title, $manual.Title) | Select-Object -Unique
        Require ($titles.Count -eq 5) '五种状态标题未清楚区分'

        # 打开/刷新/切页/最小化只附着与导航一次；已填内容保留。
        Require ($report.AttachCount -eq 1) ('打开、刷新或切页增加了宿主附着：' + $report.AttachCount)
        Require ($report.NavigationCount -eq 1) ('打开、刷新或切页增加了导航：' + $report.NavigationCount)
        Require ($report.InputPreserved -and $report.InputBefore -eq $report.InputAfter) '普通状态刷新覆盖了已填内容'
        Require ($report.VisibleTrueCount -ge 2 -and $report.VisibleFalseCount -ge 2) '切页/最小化未反映为可见性变化'

        $missing = Get-Step $report 'runtime-missing'
        Require ($missing.HostStatus -eq 'RuntimeMissing' -and $missing.RetryVisible -and -not $missing.SurfacePresent) '运行时缺失未给出受控重试提示'
        $boundary = Get-Step $report 'boundary-unavailable'
        Require ($boundary.HostStatus -eq 'NetworkBoundaryUnavailable' -and $boundary.RetryVisible -and -not $boundary.SurfacePresent) '网络边界未就绪未失败关闭'
        $failed = Get-Step $report 'attach-failed'
        Require ($failed.HostStatus -eq 'Failed' -and $failed.RetryVisible -and -not $failed.SurfacePresent) '宿主失败未给出受控重试提示'
        $restored = Get-Step $report 'switched-and-minimized'
        Require ($restored.HostStatus -eq 'Attached') '切页/最小化后宿主未保持附着'
        $inputStep = Get-Step $report 'input-preserved'
        Require ($inputStep.Phase -eq 'Online') '刷新后页面状态丢失'

        foreach ($shot in @('auth-online', 'auth-authenticated', 'auth-outcome-unknown', 'auth-outcome-exhausted',
                'auth-outcome-online-pending', 'auth-storage-fault', 'auth-layout-unblocked', 'auth-layout-blocked',
                'auth-paused-needs-auth', 'auth-paused-outcome-unknown', 'auth-paused-storage-fault', 'auth-paused-exhausted',
                'auth-settings-missing', 'auth-missing-credentials',
                'auth-waiting-retry', 'auth-needs-manual', 'auth-unconfirmed', 'auth-input-preserved',
                'auth-runtime-missing', 'auth-boundary-unavailable', 'auth-attach-failed')) {
            $path = Join-Path $qaDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少认证页截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }

        # 隐私扫描：报告与截图文件名不含凭据字段；报告只有固定地址与受控文案。
        $reportText = Get-Content -LiteralPath $reportPath -Raw
        foreach ($forbidden in @('user_password', 'user_account', 'PHPSESSID', 'Cookie')) {
            Require ($reportText -notmatch [regex]::Escape($forbidden)) ('认证页 QA 报告含受限字段：' + $forbidden)
        }

        Pass 'auth-page-fixture' @{ report = $reportPath; steps = $report.Steps; attachCount = $report.AttachCount; navigationCount = $report.NavigationCount }
    }
    catch { Fail 'auth-page-fixture' $_.Exception.Message }

    try {
        $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
        $serviceAfter = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
        $installedTrayAfter = Get-InstalledTrayPids
        Require ($serviceBefore.status -eq $serviceAfter.status -and $serviceBefore.processId -eq $serviceAfter.processId) '后台服务状态被 QA 改变'
        Require ((@($installedTrayBefore) -join ',') -eq (@($installedTrayAfter) -join ',')) '已安装托盘进程被 QA 触碰'
        Pass 'boundaries' @{ service = $serviceAfter; installedTrayPids = $installedTrayAfter }
    }
    catch { Fail 'boundaries' $_.Exception.Message }
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    foreach ($process in $tracked) {
        if ($process -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
    }
    $summary = [ordered]@{
        time = [DateTimeOffset]::UtcNow.ToString('o')
        exe = $exe
        steps = $steps
        failures = @($failures)
        result = if ($failures.Count -eq 0) { 'pass' } else { 'fail' }
        deferred = @(
            '真实门户承载（#44）：生产宿主已由 #38 交付的真实 WebView2 宿主接替，本 QA 仍用假宿主，不能替代主窗口内真实承载与真实门户页面',
            '主窗口内 NativeControlHost 的真实生命周期、真实 WebView2 窗口、真实门户页面与真实 TLS/重定向/新窗口/Service Worker（#38/#44，未验证）',
            '认证页与后台内嵌通道的同一会话附着（#39，未验证）',
            '真实校园网凭据提交、断网恢复与 UAC（需单独授权与现场验证）'
        )
    }
    $summaryPath = Join-Path $evidence 'summary.json'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8

    if ($failures.Count -gt 0) {
        Write-Host ('FAILED：' + ($failures -join '；'))
        exit 1
    }
    Write-Host ('PASS 认证页 QA：' + $summaryPath)
    exit 0
}
