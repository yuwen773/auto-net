#requires -Version 7.0
# 网络诊断页 QA（迁移工单 #6）。
# 覆盖：自包含 win-x64 单文件发布契约、五类诊断分列（UU 本机探测与远程会话拆分）、
# 系统选路推断标注、门户改版信号与认证暂停分别呈现、受控阶段与失败分类（无原始响应/敏感 URL）、
# 不可信状态不可确认降级、只读边界（查看过程无提权请求、无凭据提交）真实窗口截图，
# 以及读取本机真实状态的实况证据；后台服务与已安装托盘未被触碰。
# 边界：不安装、不替换、不重启服务；不写本机真实 status.json（夹具只允许写 QA 证据目录内）；
# 不发起任何网络动作（诊断页零交互）；明暗主题、键盘与真实 DPI（#9/#14，人工）。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-diagnostics'
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
    Require ($match.Count -eq 1) ('诊断步骤缺失或重复：' + $State)
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

    # 2) 诊断页状态矩阵（夹具模式）：七态截图 + 分列/信号/受控分类/只读边界逐项核验
    try {
        $diagnosticsDir = New-QaDirectory 'diagnostics'
        $fixture = Join-Path $diagnosticsDir 'status-fixture.json'
        $process = Start-App -Executable $exe -Arguments @() -QaDirectory $diagnosticsDir -QaMode 'diagnostics' -StatusPath $fixture
        $reportPath = Join-Path $diagnosticsDir 'diagnostics.json'
        Require (Wait-ForFile $reportPath 60) '诊断页 QA 报告未生成'
        Require (Wait-Exit $process 120) '诊断页 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('诊断页 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture' -and $report.Fixture) '夹具模式标记不符'
        Require ($null -eq $report.Error) ('诊断页 QA 报告包含错误：' + $report.Error)

        $captions = @('校园 Wi-Fi', '校园网认证', '校园网直连', '代理 / 系统路径', 'UU 远程 · 本机探测', 'UU 远程 · 远程会话')
        foreach ($step in $report.Steps) {
            Require ($step.Captions.Count -eq 6 -and (-not (Compare-Object $step.Captions $captions -SyncWindow 0))) ('五类分列结构被破坏：' + $step.State)
            if ($step.Trusted) {
                Require (@($step.Values | Where-Object { $_ -eq '不可确认' }).Count -eq 0) ('可信状态的分类值出现降级占位：' + $step.State)
            }
            else {
                Require (@($step.Values | Where-Object { $_ -ne '不可确认' }).Count -eq 0) ('不可信状态未整体降级：' + $step.State)
                Require ($step.AttemptSummary -eq '不可确认') ('不可信状态的认证尝试未降级：' + $step.State)
            }
        }

        # 只读边界（故事 23）：页脚声明 + 查看期间没有任何提权请求产物。
        Require ($report.ReadOnlyNotice -match '只读' -and $report.ReadOnlyNotice -match '不提交凭据' -and $report.ReadOnlyNotice -match '不触发认证或断线') '只读边界声明缺失'
        Require (-not (Test-Path -LiteralPath (Join-Path $diagnosticsDir 'elevation.json'))) '诊断页查看期间出现了提权请求记录'
        Require (-not (Test-Path -LiteralPath (Join-Path $diagnosticsDir 'elevated-inline.json'))) '诊断页查看期间出现了提权执行记录'

        $online = Get-Step $report 'online'
        Require ($online.Values[0] -eq '已连接' -and $online.Values[2] -eq '外网可达') '在线分类值不符'
        Require ($online.Values[3] -match '推断' -and $online.Values[3] -match '出口未核验') '选路值缺少推断标注'
        Require ($online.Values[4] -match '会话待实测' -and $online.Values[5] -eq '未实测') 'UU 本机探测与远程会话未分列'
        Require ($online.SignalCaptions.Count -eq 1 -and $online.SignalCaptions[0] -eq '认证信号') '在线状态不应出现暂停或结构信号'
        Require ($online.AttemptSummary -eq '尚无认证尝试记录。') '无尝试记录时不得显示受控通过结论'

        $failed = Get-Step $report 'auth-failed'
        Require ($failed.SignalCaptions.Count -eq 1 -and $failed.SignalCaptions[0] -eq '认证提交暂停') '认证暂停信号缺失'
        Require ($failed.AttemptSummary -match '\[Submission/BindingRejected\]' -and $failed.AttemptSummary -match 'HTTP 401') '受控阶段与失败分类缺失'
        Require ($failed.AttemptSummary -match '凭据请求已发起') '凭据请求事实缺失'
        Require ($failed.PersistentState -eq 'BindingRejected') '认证持久状态不符'

        $layout = Get-Step $report 'layout-suspected'
        Require ($layout.SignalCaptions.Count -eq 1 -and $layout.SignalCaptions[0] -eq '门户结构疑似变化') '结构信号未单独呈现'
        Require ($layout.SignalCaptions -notcontains '认证提交暂停') '结构信号被误当作认证暂停'

        $both = Get-Step $report 'layout-and-blocked'
        Require ($both.SignalCaptions.Count -eq 2 -and $both.SignalCaptions[0] -eq '认证提交暂停' -and $both.SignalCaptions[1] -eq '门户结构疑似变化') '两类信号未同时分别呈现'
        Require ($both.AttemptSummary -match '缺少必需字段' -and $both.PersistentState -eq 'SafetyBlocked') '改版场景的受控分类不符'

        foreach ($state in @('stale', 'invalid', 'missing')) {
            $step = Get-Step $report $state
            Require (-not $step.Trusted) ($state + '：可信度判定不符')
            Require ($step.SignalCaptions.Count -eq 1 -and $step.SignalCaptions[0] -eq '认证信号') ($state + '：信号未按不可确认降级')
        }

        # 隐私契约（故事 22）：报告全文不出现凭据形、会话形或 URL 形字符串。
        $raw = Get-Content -LiteralPath $reportPath -Raw
        foreach ($forbidden in @('user_password', 'phpsessid', 'cookie', '://')) {
            Require (-not $raw.ToLower().Contains($forbidden)) ('诊断页报告泄露了 ' + $forbidden)
        }

        foreach ($shot in @('diagnostics-online', 'diagnostics-auth-failed', 'diagnostics-layout-suspected', 'diagnostics-layout-and-blocked',
                'diagnostics-stale', 'diagnostics-invalid', 'diagnostics-missing')) {
            $path = Join-Path $diagnosticsDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少诊断页截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }
        Pass 'diagnostics-fixture' @{ report = $reportPath; steps = $report.Steps }
    }
    catch { Fail 'diagnostics-fixture' $_.Exception.Message }

    # 3) 读取本机真实状态的实况证据（不重定向状态路径）
    try {
        $liveDir = New-QaDirectory 'diagnostics-live'
        $process = Start-App -Executable $exe -Arguments @() -QaDirectory $liveDir -QaMode 'diagnostics' -StatusPath ''
        $reportPath = Join-Path $liveDir 'diagnostics.json'
        Require (Wait-ForFile $reportPath 60) '实况诊断页 QA 报告未生成'
        Require (Wait-Exit $process 120) '实况诊断页 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('实况诊断页 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'live' -and -not $report.Fixture) '实况模式标记不符'
        Require ($null -eq $report.Error) ('实况诊断页 QA 报告包含错误：' + $report.Error)
        $live = Get-Step $report 'live'
        Require ($live.Captions.Count -eq 6) '实况五类分列结构被破坏'
        Require (-not (Test-Path -LiteralPath (Join-Path $liveDir 'elevation.json'))) '实况查看期间出现了提权请求记录'
        Require (Test-Path -LiteralPath (Join-Path $liveDir 'diagnostics-live.png')) '缺少实况诊断页截图'
        # 隐私契约对实况证据同样适用：真实状态值进入报告后不得出现凭据形、会话形或 URL 形字符串。
        $rawLive = Get-Content -LiteralPath $reportPath -Raw
        foreach ($forbidden in @('user_password', 'phpsessid', 'cookie', '://')) {
            Require (-not $rawLive.ToLower().Contains($forbidden)) ('实况诊断页报告泄露了 ' + $forbidden)
        }
        Pass 'diagnostics-live' @{ report = $reportPath; step = $live }
    }
    catch { Fail 'diagnostics-live' $_.Exception.Message }

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
        deferred = @('明暗主题（#9）', '键盘与可访问名称（#14）', '真实 UAC 点击（人工）', '托盘菜单视觉点击（人工）', '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('网络诊断页 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
