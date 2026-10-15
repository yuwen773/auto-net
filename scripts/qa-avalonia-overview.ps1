#requires -Version 7.0
# 概览页 QA（迁移工单 #4）。
# 覆盖：自包含 win-x64 单文件发布契约、概览状态矩阵（状态/进度/下一步、在线与认证暂停分列、
# 不可信状态不可确认并禁用动作）真实窗口截图、恢复开关与解除入口的明确意图记录
# （--disable-recovery / --enable-recovery / --unblock-auth；过期与缺失状态必须被重验拦下）、
# 读取本机真实状态的实况证据、后台服务与已安装托盘未被触碰。
# 边界：不安装、不替换、不重启服务；不写本机真实 status.json（夹具只允许写 QA 证据目录内）；
# 不弹真实 UAC（QA 的启动适配器只记录命令）；明暗主题、真实 UAC 与托盘菜单视觉点击（#9/人工）。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-overview'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_STATUS_PATH')) {
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
    param([string]$Executable, [string[]]$Arguments, [string]$QaDirectory, [string]$QaMode, [string]$StatusPath = '')
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_STATUS_PATH', $StatusPath)
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
function Get-Step($Report, [string]$State) {
    $match = @($Report.Steps | Where-Object State -eq $State)
    Require ($match.Count -eq 1) ('概览步骤缺失或重复：' + $State)
    return $match[0]
}
function Get-Action($Report, [string]$ContextPrefix) {
    $match = @($Report.Actions | Where-Object { $_.Context.StartsWith($ContextPrefix) })
    Require ($match.Count -eq 1) ('动作记录缺失或重复：' + $ContextPrefix)
    return $match[0]
}
# 已安装托盘（C:\Program Files\AutoNet）的进程快照：只排除本证据目录下的迁移进程，绝不触碰已安装实例。
function Get-InstalledTrayPids {
    return @(Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and -not [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { $_.Id })
}

# 只清理本证据目录下的迁移进程，绝不触碰 C:\Program Files\AutoNet 下已安装的托盘。
$evidencePrefix = [IO.Path]::GetFullPath($evidence) + [IO.Path]::DirectorySeparatorChar
Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

try {
    $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    $serviceBefore = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
    $installedTrayBefore = Get-InstalledTrayPids

    # 1) 发布：自包含 win-x64 单文件，程序名保持 AutoNet.Tray.exe（安装器契约身份）
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

    # 2) 概览状态矩阵与动作意图（夹具模式）：八态截图 + 投影字段 + 明确意图记录
    try {
        $overviewDir = New-QaDirectory 'overview'
        $fixture = Join-Path $overviewDir 'status-fixture.json'
        $process = Start-App -Executable $exe -Arguments @() -QaDirectory $overviewDir -QaMode 'overview' -StatusPath $fixture
        $reportPath = Join-Path $overviewDir 'overview.json'
        Require (Wait-ForFile $reportPath 60) '概览 QA 报告未生成'
        Require (Wait-Exit $process 120) '概览 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('概览 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture' -and $report.Fixture) '夹具模式标记不符'
        Require ($null -eq $report.Error) ('概览 QA 报告包含错误：' + $report.Error)

        # 每个状态：投影字段 + 不变式（开关可用性只能跟随可信度；不可信时解除入口必须可见但禁用）
        foreach ($step in $report.Steps) {
            Require ($step.RecoveryEnabled -eq $step.Trusted) ('恢复开关可用性与可信度不符：' + $step.State)
            if ($step.Trusted) {
                Require ($step.UnblockEnabled -eq $step.UnblockVisible) ('解除入口可用性与可见性不符：' + $step.State)
            }
            else {
                Require ($step.UnblockVisible -and -not $step.UnblockEnabled) ('不可信状态的解除入口应为可见但禁用：' + $step.State)
            }
        }

        $online = Get-Step $report 'online'
        Require ($online.Trusted -and $online.Title -eq '网络在线') '在线状态投影不符'
        Require (-not $online.AuthVisible -and -not $online.UnblockVisible) '在线且未暂停不应出现认证列或解除入口'
        Require ($online.RecoveryLabel -eq '关闭自动恢复' -and $online.RecoveryEnabled) '在线时恢复开关应提供关闭意图'

        $disabled = Get-Step $report 'online-disabled'
        Require ($disabled.RecoveryLabel -eq '开启自动恢复' -and $disabled.RecoveryEnabled) '已关闭时恢复开关应提供开启意图'

        $suspended = Get-Step $report 'online-suspended'
        Require ($suspended.Title -eq '网络在线' -and $suspended.AuthVisible -and $suspended.AuthTitle -eq '认证提交已暂停') '在线与认证暂停未并存分列'
        Require ($suspended.UnblockVisible -and $suspended.UnblockEnabled) '认证暂停时解除入口应可用'
        Require ($suspended.RecoveryLabel -eq '关闭自动恢复') '在线暂停并存时恢复开关不应被连带禁用'

        $offline = Get-Step $report 'offline-suspended'
        Require ($offline.Title -eq '需要处理认证' -and $offline.AuthVisible -and $offline.UnblockVisible) '离线暂停未分列呈现'

        $layout = Get-Step $report 'layout-suspected'
        Require ($layout.AuthVisible -and $layout.AuthTitle -eq '门户结构疑似变化' -and -not $layout.UnblockVisible) '结构信号应独立成列且不提供解除入口'

        $stale = Get-Step $report 'stale'
        Require ((-not $stale.Trusted) -and $stale.Title -eq '后台状态已过期') '过期状态未按不可确认降级'
        Require ($stale.RecoveryLabel -eq '自动恢复状态待确认' -and -not $stale.RecoveryEnabled) '过期状态恢复开关未禁用'
        Require ($stale.AuthVisible -and $stale.AuthTitle -eq '认证状态不可确认') '过期状态认证列未按不可确认降级'
        Require ($stale.UnblockVisible -and -not $stale.UnblockEnabled) '过期状态解除入口应为可见但禁用'

        $invalid = Get-Step $report 'invalid'
        Require ((-not $invalid.Trusted) -and $invalid.Title -eq '状态不可确认') '损坏状态未按不可确认降级'
        $missing = Get-Step $report 'missing'
        Require ((-not $missing.Trusted) -and $missing.Title -eq '后台服务未就绪') '缺失状态未按未就绪降级'

        # 动作意图：逐字核对命令与重验拦截
        $close = Get-Action $report '在线且已开启'
        Require ($close.Intent -eq '--disable-recovery' -and $close.Launched -and $close.Command -eq '--disable-recovery') '关闭自动恢复未按明确意图发起'
        $enable = Get-Action $report '在线且已关闭'
        Require ($enable.Intent -eq '--enable-recovery' -and $enable.Launched -and $enable.Command -eq '--enable-recovery') '开启自动恢复未按明确意图发起'
        $unblock = Get-Action $report '在线且认证暂停'
        Require ($unblock.Intent -eq '--unblock-auth' -and $unblock.Launched -and $unblock.Command -eq '--unblock-auth') '解除入口未按既有命令发起'
        $staleDisplay = Get-Action $report '过期夹具：显示层'
        Require ((-not $staleDisplay.Launched) -and $null -eq $staleDisplay.Command) '过期状态显示层不应提供意图'
        $staleForced = Get-Action $report '过期夹具：强制意图'
        Require ((-not $staleForced.Launched) -and $null -eq $staleForced.Command) '过期状态的强制意图未被重验拦下'
        $staleUnblock = Get-Action $report '过期夹具：解除入口'
        Require ((-not $staleUnblock.Launched) -and $null -eq $staleUnblock.Command) '过期状态的解除入口未被重验拦下'
        $missingForced = Get-Action $report '缺失状态：强制意图'
        Require ((-not $missingForced.Launched) -and $null -eq $missingForced.Command) '缺失状态的强制意图未被重验拦下'

        # 截图：八态齐全且非空白
        foreach ($shot in @('overview-online', 'overview-online-disabled', 'overview-online-suspended', 'overview-offline-suspended',
                'overview-layout-suspected', 'overview-stale', 'overview-invalid', 'overview-missing')) {
            $path = Join-Path $overviewDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少概览截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }
        Pass 'overview-fixture' @{ report = $reportPath; steps = $report.Steps; actions = $report.Actions }
    }
    catch { Fail 'overview-fixture' $_.Exception.Message }

    # 3) 读取本机真实状态的实况证据（不重定向状态路径）
    try {
        $liveDir = New-QaDirectory 'overview-live'
        $process = Start-App -Executable $exe -Arguments @() -QaDirectory $liveDir -QaMode 'overview' -StatusPath ''
        $reportPath = Join-Path $liveDir 'overview.json'
        Require (Wait-ForFile $reportPath 60) '实况概览 QA 报告未生成'
        Require (Wait-Exit $process 120) '实况概览 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('实况概览 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'live' -and -not $report.Fixture) '实况模式标记不符'
        Require ($null -eq $report.Error) ('实况概览 QA 报告包含错误：' + $report.Error)
        $live = Get-Step $report 'live'
        Require ($live.Title.Length -gt 0) '实况概览标题为空'
        Require ($live.RecoveryEnabled -eq $live.Trusted) '实况恢复开关可用性与可信度不符'
        Require (Test-Path -LiteralPath (Join-Path $liveDir 'overview-live.png')) '缺少实况概览截图'
        Pass 'overview-live' @{ report = $reportPath; step = $live }
    }
    catch { Fail 'overview-live' $_.Exception.Message }

    # 4) 服务与已安装托盘未被触碰（只读核对）
    try {
        $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
        $serviceAfter = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
        $installedTrayAfter = Get-InstalledTrayPids
        Require ($serviceAfter.status -eq $serviceBefore.status -and $serviceAfter.processId -eq $serviceBefore.processId) '后台服务状态或进程发生变化'
        Require (($installedTrayBefore -join ',') -eq ($installedTrayAfter -join ',')) '已安装托盘进程发生变化'
        Pass 'service-untouched' @{ before = $serviceBefore; after = $serviceAfter; installedTrayPids = $installedTrayAfter }
    }
    catch { Fail 'service-untouched' $_.Exception.Message }
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
        deferred = @('明暗主题（#9）', '真实 UAC 点击（人工）', '托盘菜单视觉点击（人工）', '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('概览页 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
