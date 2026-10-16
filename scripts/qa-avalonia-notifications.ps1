#requires -Version 7.0
# 关键通知消费 QA（迁移工单 #10 / T10）。
# 覆盖：首读不重播历史恢复（故事 56）、同一事件不重弹（52、58）、运行期联网恢复提醒一次且
# 文案受控（53）、复检过程只在界面显示（54）、托盘重启且认证仍暂停时提醒一次（55）、
# 改版提示与暂停提示并存不覆盖（59）、未知码无动作且不落记录（60）、
# 消费记录按 Windows 用户持久化且跨重启防重播（57）。
# 边界：QA 模式呈现走受控记录器（不弹真实气泡；真实气泡在系统通知中心为人工项）；
# 消费记录只写 QA 证据目录，绝不触碰本机真实 %LOCALAPPDATA%\AutoNet 或
# C:\ProgramData\AutoNet 下的任何受限存储，也不发起真实网络请求、不修改服务。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-notifications'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_STATUS_PATH', 'AUTONET_QA_NOTIFY_PHASE')) {
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
    param([string]$Executable, [string[]]$Arguments, [string]$QaDirectory, [string]$QaMode, [string]$StatusPath = '', [string]$NotifyPhase = '')
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_STATUS_PATH', $StatusPath)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_NOTIFY_PHASE', $NotifyPhase)
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
function Wait-Exit($Process, [int]$TimeoutSeconds = 120) { return $Process.WaitForExit($TimeoutSeconds * 1000) }
function New-QaDirectory([string]$Name) {
    $path = Join-Path $evidence $Name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
function Get-Decision($Report, [string]$Name) {
    $match = @($Report.Decisions | Where-Object Step -eq $Name)
    Require ($match.Count -eq 1) ('通知决策缺失或重复：' + $Name)
    return $match[0]
}
function Write-StatusFixture([string]$Path) {
    # 与服务端 status.json 同构的在线快照（无 Notification 字段）；通知场景由 QA 内部逐步改写。
    @{ Enabled = $true; UpdatedAt = [DateTimeOffset]::Now.ToString('o'); State = '校园网在线'; Detail = ''
       Direct = '外网可达'; Wifi = 'TEST-AP-1'; Portal = '未查询（外网检查已通过）'; Tunnel = '系统路径可达，出口未核验'
       Uu = '未核验'; Attempts = 0; DisconnectAttempts = 0; AuthenticationStage = 'Unknown'; AuthenticationFailure = 'None'
       AuthenticationPersistentState = 'Ready' } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Path -Encoding utf8
}

# 只清理本证据目录下的迁移进程，绝不触碰 C:\Program Files\AutoNet 下已安装的托盘。
$evidencePrefix = [IO.Path]::GetFullPath($evidence) + [IO.Path]::DirectorySeparatorChar
Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

# 本机真实存储的三份指纹：受限业务存储（ProgramData）、真实用户界面偏好与真实通知消费记录
# （LocalAppData）。通知 QA 必须全程保持三者逐字节不变——按用户隔离（故事 57、70）的直接证据。
function Get-RealPrivateFingerprint {
    $private = Join-Path $env:ProgramData 'AutoNet/private'
    $files = @('settings.json', 'credentials.dpapi', 'auth-block.txt', 'auth-release-audit.jsonl')
    $snapshot = [ordered]@{}
    foreach ($file in $files) {
        $path = Join-Path $private $file
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path
            $snapshot[$file] = @{ exists = $true; length = $item.Length; hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; modified = $item.LastWriteTimeUtc.ToString('o') }
        }
        else { $snapshot[$file] = @{ exists = $false } }
    }
    return $snapshot
}
function Get-RealUserFileFingerprint([string]$FileName) {
    $path = Join-Path (Join-Path $env:LOCALAPPDATA 'AutoNet') $FileName
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path
        return @{ exists = $true; length = $item.Length; hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; modified = $item.LastWriteTimeUtc.ToString('o') }
    }
    return @{ exists = $false }
}

try {
    $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    $serviceBefore = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
    $privateBefore = Get-RealPrivateFingerprint
    $prefsBefore = Get-RealUserFileFingerprint 'ui-preferences.json'
    $consumptionBefore = Get-RealUserFileFingerprint 'notification-consumption.json'

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
            Pass 'publish' @{
                exe = $exe; files = $inventory
                productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
                sdk = (& $sdk --version); commit = (& git -C $repo rev-parse HEAD)
            }
        }
        catch { Fail 'publish' $_.Exception.Message }
    }

    if (-not (Test-Path -LiteralPath $exe)) { throw '没有可运行的壳发布产物，后续核验无法进行。' }

    # 2) 选择阶段（故事 52–60）：夹具驱动通知路由，逐决策核验。
    try {
        $shellDir = New-QaDirectory 'shell'
        $statusPath = Join-Path $shellDir 'status.json'
        Write-StatusFixture $statusPath
        $process = Start-App $exe @() $shellDir 'notifications' $statusPath
        $reportPath = Join-Path $shellDir 'notifications.json'
        Require (Wait-ForFile $reportPath 60) '通知选择阶段 QA 报告未生成'
        Require (Wait-Exit $process 120) '通知选择阶段 QA 未在超时内退出'
        Require ($process.ExitCode -eq 0) ('通知选择阶段退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Phase -eq 'select') ('阶段标记不符：' + $report.Phase)
        Require ($null -eq $report.Error) ('通知 QA 报告包含错误：' + $report.Error)
        Require ($report.MutexName -eq 'Local\AutoNet.Tray.Avalonia') ('互斥名不符：' + $report.MutexName)

        # 故事 56：首条可信读取携带的历史恢复只记录不提醒。
        $first = Get-Decision $report 'first-reading-no-replay'
        Require ($first.Presented -eq $false -and $first.Reason -eq 'first-reading') '首读重播了历史恢复'
        # 故事 58：同一事件不按任何节奏重弹。
        $same = Get-Decision $report 'same-event-not-repeated'
        Require ($same.Presented -eq $false -and $same.Reason -eq 'already-consumed') '同一事件重复弹出'
        # 故事 53：运行期新恢复提醒一次，标题受控。
        $recovery = Get-Decision $report 'recovery-presented-once'
        Require ($recovery.Presented -eq $true) '运行期恢复未提醒'
        Require ($recovery.Code -eq 'InternetRecovered') ('提醒码不符：' + $recovery.Code)
        Require ($recovery.Heading -eq 'AutoNet · 联网恢复') ('恢复提醒标题不受控：' + $recovery.Heading)
        Require ($recovery.Body.Contains('恢复来源无法确认')) '恢复提醒正文缺少口径声明'
        # 故事 58：新事件消费后不再重弹。
        $recoveryAgain = Get-Decision $report 'recovery-not-repeated'
        Require ($recoveryAgain.Presented -eq $false) '恢复事件重复弹出'
        # 故事 54：复检过程绝不在托盘弹出。
        $recheck = Get-Decision $report 'recheck-interface-only'
        Require ($recheck.Presented -eq $false -and $recheck.Reason -eq 'interface-only') '复检过程弹出了通知'
        # 故事 60：未知码无动作，决策证据不含原文。
        $unknown = Get-Decision $report 'unknown-code-inert'
        Require ($unknown.Presented -eq $false -and $unknown.Reason -eq 'unknown-code') '未知码触发了动作'
        Require ($null -eq $unknown.Code -and $null -eq $unknown.Heading -and $null -eq $unknown.Body) '未知码决策证据携带原文'
        # 故事 59：改版提示与随后的通用暂停提示并存，文案互不覆盖。
        $layout = Get-Decision $report 'layout-presented'
        Require ($layout.Presented -eq $true -and $layout.Code -eq 'PortalLayoutChanged') '改版提示未弹出'
        $pause = Get-Decision $report 'pause-not-overriding-layout'
        Require ($pause.Presented -eq $true -and $pause.Code -eq 'AuthenticationPaused') '暂停提示未独立弹出'
        Require ($pause.Heading -eq 'AutoNet 需要检查认证') ('暂停提醒标题不受控：' + $pause.Heading)
        Require ($pause.Body -eq '自动认证已安全暂停。请先查看诊断和日志，勿仅为解锁而重存密码。') '暂停提醒正文与既有托盘不一致'
        Require ($layout.Heading -ne $pause.Heading -and $layout.Body -ne $pause.Body) '改版提示被通用暂停文案覆盖'

        # 故事 57：消费记录落证据目录；逐字只含受控载荷（已知码 → 事件时刻）。
        $consumptionPath = Join-Path $shellDir 'notification-consumption.json'
        Require (Test-Path -LiteralPath $consumptionPath) '消费记录文件未生成'
        $consumption = Get-Content -LiteralPath $consumptionPath -Raw
        foreach ($code in @('InternetRecovered', 'PortalLayoutChanged', 'AuthenticationPaused')) {
            Require ($consumption.Contains('"' + $code + '"')) ('消费记录缺少已提醒事件：' + $code)
        }
        foreach ($absent in @('SubmissionRechecking', 'BrandNewCode', 'password', 'cookie', 'phpsessid')) {
            Require (-not $consumption.ToLower().Contains($absent.ToLower())) ('消费记录含受限内容：' + $absent)
        }
        # 故事 70：报告全文不出现凭据形、会话形或 URL 形字符串。
        $raw = Get-Content -LiteralPath $reportPath -Raw
        foreach ($forbidden in @('user_password', 'phpsessid', 'cookie', '://')) {
            Require (-not $raw.ToLower().Contains($forbidden)) ('通知报告泄露了 ' + $forbidden)
        }
        Pass 'notifications-select' @{ report = $reportPath; decisions = $report.Decisions; consumption = $consumption }
    }
    catch { Fail 'notifications-select' $_.Exception.Message }

    # 3) 重启阶段（故事 55、57）：同一证据目录（同一消费记录）内新进程——
    #    已消费恢复不重播，仍暂停时启动提醒恰好一次。
    try {
        $resumeDir = Join-Path $evidence 'shell'
        $statusPath = Join-Path $resumeDir 'status.json'
        Write-StatusFixture $statusPath
        $process = Start-App $exe @() $resumeDir 'notifications' $statusPath 'resume'
        $reportPath = Join-Path $resumeDir 'notifications-resume.json'
        Require (Wait-ForFile $reportPath 60) '通知重启阶段 QA 报告未生成'
        Require (Wait-Exit $process 120) '通知重启阶段 QA 未在超时内退出'
        Require ($process.ExitCode -eq 0) ('通知重启阶段退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Phase -eq 'resume') ('阶段标记不符：' + $report.Phase)
        Require ($null -eq $report.Error) ('通知重启报告包含错误：' + $report.Error)
        Require ($report.Decisions.Count -eq 3) ('重启阶段决策数不符：' + $report.Decisions.Count)

        # 故事 57：消费记录里的恢复事件跨重启不得再次提醒。
        $noReplay = Get-Decision $report 'resume-no-replay'
        Require ($noReplay.Presented -eq $false -and $noReplay.Reason -eq 'first-reading') '重启后重播了已消费恢复'
        # 故事 55：重启时认证仍暂停 → 启动提醒恰好一次，文案与词表一致。
        $reminded = Get-Decision $report 'resume-pause-reminded-once'
        Require ($reminded.Presented -eq $true -and $reminded.Reason -eq 'startup-pause') '重启后暂停提醒缺失'
        Require ($reminded.Code -eq 'AuthenticationPaused') ('启动提醒码不符：' + $reminded.Code)
        Require ($reminded.Body -eq '自动认证已安全暂停。请先查看诊断和日志，勿仅为解锁而重存密码。') '启动暂停提醒文案与词表不一致'
        # 会话内不再重复。
        $notRepeated = Get-Decision $report 'resume-pause-not-repeated'
        Require ($notRepeated.Presented -eq $false) '启动暂停提醒重复弹出'

        # 重启阶段消费记录只保留原事件（无新 InternetRecovered 条目、无重复）。
        $consumption = Get-Content -LiteralPath (Join-Path $resumeDir 'notification-consumption.json') -Raw
        $internet = @([regex]::Matches($consumption, 'InternetRecovered')).Count
        Require ($internet -eq 1) ('消费记录中恢复事件条目数异常：' + $internet)
        Pass 'notifications-resume' @{ report = $reportPath; decisions = $report.Decisions; consumption = $consumption }
    }
    catch { Fail 'notifications-resume' $_.Exception.Message }

    # 4) 本机真实受限存储、真实用户偏好与真实消费记录逐字节未被触碰（只读核对，故事 57、70）。
    try {
        $privateAfter = Get-RealPrivateFingerprint
        foreach ($file in @('settings.json', 'credentials.dpapi', 'auth-block.txt', 'auth-release-audit.jsonl')) {
            $before = $privateBefore[$file]
            $after = $privateAfter[$file]
            Require ($before.exists -eq $after.exists) ($file + ' 存在性发生变化')
            if ($before.exists) {
                Require ($before.hash -eq $after.hash) ($file + ' 内容发生变化')
                Require ($before.length -eq $after.length) ($file + ' 长度发生变化')
                Require ($before.modified -eq $after.modified) ($file + ' 修改时间发生变化')
            }
        }
        foreach ($pair in @(@('ui-preferences.json', $prefsBefore), @('notification-consumption.json', $consumptionBefore))) {
            $after = Get-RealUserFileFingerprint $pair[0]
            $before = $pair[1]
            Require ($before.exists -eq $after.exists) ($pair[0] + ' 存在性发生变化')
            if ($before.exists) {
                Require ($before.hash -eq $after.hash) ($pair[0] + ' 内容发生变化')
                Require ($before.modified -eq $after.modified) ($pair[0] + ' 修改时间发生变化')
            }
        }
        Pass 'real-storage-untouched' @{ private = $privateAfter; userPrefs = (Get-RealUserFileFingerprint 'ui-preferences.json'); consumption = (Get-RealUserFileFingerprint 'notification-consumption.json') }
    }
    catch { Fail 'real-storage-untouched' $_.Exception.Message }

    # 5) 服务未被触碰（只读核对）
    try {
        $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
        $serviceAfter = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
        Require ($serviceAfter.status -eq $serviceBefore.status -and $serviceAfter.processId -eq $serviceBefore.processId) '后台服务状态或进程发生变化'
        Pass 'service-untouched' @{ before = $serviceBefore; after = $serviceAfter }
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
        deferred = @('真实气泡在系统通知中心的视觉确认（人工；真实 Windows 通知横幅出现一次）',
            '同轮覆盖禁止由服务端单槽（notifiedThisCycle）保证，托盘侧证据为两条具体文案并存；服务端行为属服务侧验收',
            '真实 UAC 提权窗口（如需在通知场景联动提权操作）',
            'Avalonia 升级后 TrayBalloonNotifier 反射路径复验（见 TrayBalloonNotifier.cs 已知脆弱点）',
            '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('通知 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
