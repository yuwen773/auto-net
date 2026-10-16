#requires -Version 7.0
# 浏览器兜底开页 QA（迁移工单 #11 / T11、T12）。
# 覆盖：首次有效授予立即开页且 URL 逐字为允许根页（故事 61–63）、同一授予会话内与跨托盘重启
# 都只自动开一次（64）、服务换发授予不绕本地 10 分钟冷却（65）、手动开页计入消费与冷却（66）、
# 冷却到期本身不循环重开（67）、请求消失/联网恢复/不可信状态不开页（68）、
# 启动失败受控且不在轮询中无限重试 + 手动重试入口（69）、时钟异常不得提前重复开页（决策 11）、
# 开页消费记录按 Windows 用户持久化且与通知消费记录分开（70）、概览页兜底开页卡真实窗口证据。
# 边界：QA 模式浏览器启动走受控记录器（不弹真实浏览器；真实浏览器为人工项）；
# 消费记录只写 QA 证据目录，绝不触碰本机真实 %LOCALAPPDATA%\AutoNet 或
# C:\ProgramData\AutoNet 下的任何受限存储，也不发起真实网络请求、不修改服务。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-portal-open'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_STATUS_PATH', 'AUTONET_QA_PORTAL_PHASE')) {
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
    param([string]$QaDirectory, [string]$QaMode, [string]$StatusPath, [string]$Phase = '')
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_STATUS_PATH', $StatusPath)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_PORTAL_PHASE', $Phase)
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($start)
    $tracked.Add($process)
    return $process
}
function Wait-ForFile([string]$Path, [int]$TimeoutSeconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { Start-Sleep -Milliseconds 250; return $true }
        Start-Sleep -Milliseconds 120
    }
    return $false
}
function Wait-Exit($Process, [int]$TimeoutSeconds = 120) { return $Process.WaitForExit($TimeoutSeconds * 1000) }
function Get-Decision($Report, [string]$Name) {
    $match = @($Report.Decisions | Where-Object Step -eq $Name)
    Require ($match.Count -eq 1) ('开页决策缺失或重复：' + $Name)
    return $match[0]
}

# 只清理本证据目录下的迁移进程，绝不触碰 C:\Program Files\AutoNet 下已安装的托盘。
$evidencePrefix = [IO.Path]::GetFullPath($evidence) + [IO.Path]::DirectorySeparatorChar
Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

# 本机真实存储的指纹：受限业务存储（ProgramData）、真实用户界面偏好、真实通知与开页消费记录
# （LocalAppData）。兜底开页 QA 必须全程保持四者逐字节不变（故事 70 用户隔离的直接证据）。
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
    $notificationBefore = Get-RealUserFileFingerprint 'notification-consumption.json'
    $portalOpenBefore = Get-RealUserFileFingerprint 'portal-open-consumption.json'

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

    # 2) 选择阶段（故事 61–69）：夹具驱动开页控制器，逐决策核验。
    try {
        $shellDir = Join-Path $evidence 'shell'
        if (Test-Path -LiteralPath $shellDir) { Remove-Item -LiteralPath $shellDir -Recurse -Force }
        New-Item -ItemType Directory -Path $shellDir | Out-Null
        $statusPath = Join-Path $shellDir 'status.json'
        $process = Start-App $shellDir 'portal-open' $statusPath
        $reportPath = Join-Path $shellDir 'portal-open.json'
        Require (Wait-ForFile $reportPath 60) '开页选择阶段 QA 报告未生成'
        Require (Wait-Exit $process 120) '开页选择阶段 QA 未在超时内退出'
        Require ($process.ExitCode -eq 0) ('开页选择阶段退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($null -eq $report.Error) ('开页 QA 报告包含错误：' + $report.Error)
        Require ($report.MutexName -eq 'Local\AutoNet.Tray.Avalonia') ('互斥名不符：' + $report.MutexName)
        Require ($report.NotificationStoreSeparate) '开页消费记录与通知消费记录必须是两个文件'

        # 故事 61–63：首次授予立即开页；URL 逐字为固定允许的门户根地址（无查询、无凭据）。
        $first = Get-Decision $report 'first-grant-opens-immediately'
        Require ($first.Outcome -eq 'Opened' -and $first.LaunchDelta -eq 1) '首次授予未立即开页'
        Require ($report.Launches.Count -ge 1 -and $report.Launches[0].Url -eq 'https://login.csust.edu.cn/') ('开页 URL 不是固定允许根页：' + $report.Launches[0].Url)
        # 故事 64（会话内）：同一授予不再自动开页。
        Require ((Get-Decision $report 'same-grant-dedup').LaunchDelta -eq 0) '同一授予重复自动开页'
        # 故事 65：服务换发授予被本地冷却拦下。
        Require ((Get-Decision $report 'regrant-within-cooldown-deferred').Outcome -eq 'CooldownActive') '换发授予未受本地冷却约束'
        # 故事 68：请求消失与不可信读取都不开页。
        Require ((Get-Decision $report 'request-gone-no-open').Outcome -eq 'NotEligible') '请求消失仍开页'
        Require ((Get-Decision $report 'untrusted-no-open').Outcome -eq 'NotEligible') '不可信读取开页'
        # 故事 66：手动开页执行并消费当前授予；其后的自动评估不再开。
        $manual = Get-Decision $report 'manual-open-counts'
        Require ($manual.Outcome -eq 'Opened' -and $manual.LaunchDelta -eq 1) '手动开页未执行'
        Require ((Get-Decision $report 'manual-open-blocks-auto-reopen').Outcome -eq 'AlreadyConsumed') '手动开页未计入消费'
        # 故事 69：启动失败受控、不无限重试；手动重试入口保持可用。
        $failure = Get-Decision $report 'launch-failure-controlled'
        Require ($failure.Outcome -eq 'LaunchFailed' -and $failure.LaunchDelta -eq 1) '启动失败未受控呈现'
        $failLaunch = @($report.Launches | Where-Object Step -eq 'fail')
        Require ($failLaunch.Count -eq 1 -and $failLaunch[0].Url -eq 'https://login.csust.edu.cn/') '失败启动的 URL 不受控'
        Require ((Get-Decision $report 'launch-failure-not-retried').LaunchDelta -eq 0) '轮询重试了失败的浏览器启动'
        Require ((Get-Decision $report 'manual-retry-after-failure').Outcome -eq 'Opened') '失败后手动重试入口不可用'

        # 启动总账：选择阶段恰好 4 次启动（首次 + 手动 + 失败 + 手动重试），URL 全部为允许根页。
        Require ($report.Launches.Count -eq 4) ('选择阶段启动次数异常：' + $report.Launches.Count)
        foreach ($launch in $report.Launches) {
            Require ($launch.Url -eq 'https://login.csust.edu.cn/') ('出现非允许根页的启动：' + $launch.Url)
        }

        # 消费记录落证据目录：4 个已消费授予 + 最近开页时刻；载荷无受限内容。
        $storePath = Join-Path $shellDir 'portal-open-consumption.json'
        Require (Test-Path -LiteralPath $storePath) '开页消费记录未生成'
        $store = Get-Content -LiteralPath $storePath -Raw
        foreach ($absent in @('password', 'cookie', 'phpsessid', '://')) {
            Require (-not $store.ToLower().Contains($absent.ToLower())) ('开页消费记录含受限内容：' + $absent)
        }
        $storeJson = $store | ConvertFrom-Json
        # 3 个已消费授予：grant1（首次）、grant2（手动）、grant3（失败 + 手动重试复用同一键）。
        Require (@($storeJson.Consumed.PSObject.Properties).Count -eq 3) ('已消费授予数异常：' + @($storeJson.Consumed.PSObject.Properties).Count)
        Require ($null -ne $storeJson.LastOpenAt) '消费记录缺少最近开页时刻'

        # 故事 70：报告全文不出现凭据形、会话形字符串；概览兜底卡真实窗口证据已捕获。
        $raw = Get-Content -LiteralPath $reportPath -Raw
        foreach ($forbidden in @('user_password', 'phpsessid', 'set-cookie')) {
            Require (-not $raw.ToLower().Contains($forbidden.ToLower())) ('开页报告泄露了 ' + $forbidden)
        }
        Require (Test-Path -LiteralPath (Join-Path $shellDir 'overview-portal-card.png')) '概览兜底开页卡截图未生成'
        Pass 'portal-open-select' @{ report = $reportPath; decisions = $report.Decisions; launches = $report.Launches; store = $store }
    }
    catch { Fail 'portal-open-select' $_.Exception.Message }

    # 3) 重启阶段（故事 64、65、67、决策 11）：同一证据目录（同一消费记录）内新进程。
    try {
        $resumeDir = Join-Path $evidence 'shell'
        $statusPath = Join-Path $resumeDir 'status.json'
        $process = Start-App $resumeDir 'portal-open' $statusPath 'resume'
        $reportPath = Join-Path $resumeDir 'portal-open-resume.json'
        Require (Wait-ForFile $reportPath 60) '开页重启阶段 QA 报告未生成'
        Require (Wait-Exit $process 120) '开页重启阶段 QA 未在超时内退出'
        Require ($process.ExitCode -eq 0) ('开页重启阶段退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Phase -eq 'resume') ('阶段标记不符：' + $report.Phase)
        Require ($null -eq $report.Error) ('开页重启报告包含错误：' + $report.Error)
        Require ($report.Decisions.Count -eq 6) ('重启阶段决策数不符：' + $report.Decisions.Count)

        # 故事 64：重启后同一授予（含冷却已满足形态）不再自动开页。
        Require ((Get-Decision $report 'resume-same-grant-dedup').LaunchDelta -eq 0) '重启后重复开页'
        # 故事 65：换发授予被持久冷却拦下（跨重启）。
        Require ((Get-Decision $report 'resume-cooldown-cross-grant').Outcome -eq 'CooldownActive') '换发授予绕过了跨重启冷却'
        # ADR-0008：冷却满足后新授予恰好开一次，再评估不再开。
        $opened = Get-Decision $report 'resume-expired-cooldown-opens-once'
        Require ($opened.Outcome -eq 'Opened' -and $opened.LaunchDelta -eq 1) '冷却满足后未开新授予'
        Require ((Get-Decision $report 'resume-open-once-only').LaunchDelta -eq 0) '新授予重复开页'
        # 故事 67：冷却到期不循环重开同一授予。
        Require ((Get-Decision $report 'resume-expiry-no-loop').Outcome -eq 'AlreadyConsumed') '冷却到期循环重开'
        # 决策 11：时钟异常（LastOpenAt 在未来）不得提前开页。
        Require ((Get-Decision $report 'resume-time-anomaly-no-reopen').Outcome -eq 'CooldownActive') '时间异常导致提前开页'

        # 重启阶段恰好 1 次启动，URL 为允许根页；消费记录共 4 个授予（授予 5 因时间异常未消费）。
        Require ($report.Launches.Count -eq 1) ('重启阶段启动次数异常：' + $report.Launches.Count)
        Require ($report.Launches[0].Url -eq 'https://login.csust.edu.cn/') ('重启阶段 URL 不受控：' + $report.Launches[0].Url)
        $store = Get-Content -LiteralPath (Join-Path $resumeDir 'portal-open-consumption.json') -Raw | ConvertFrom-Json
        Require (@($store.Consumed.PSObject.Properties).Count -eq 4) ('重启后已消费授予数异常：' + @($store.Consumed.PSObject.Properties).Count)
        Pass 'portal-open-resume' @{ report = $reportPath; decisions = $report.Decisions; launches = $report.Launches }
    }
    catch { Fail 'portal-open-resume' $_.Exception.Message }

    # 4) 本机真实受限存储与真实用户记录逐字节未被触碰（只读核对，故事 70）。
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
        foreach ($pair in @(@('ui-preferences.json', $prefsBefore), @('notification-consumption.json', $notificationBefore), @('portal-open-consumption.json', $portalOpenBefore))) {
            $after = Get-RealUserFileFingerprint $pair[0]
            $before = $pair[1]
            Require ($before.exists -eq $after.exists) ($pair[0] + ' 存在性发生变化')
            if ($before.exists) {
                Require ($before.hash -eq $after.hash) ($pair[0] + ' 内容发生变化')
                Require ($before.modified -eq $after.modified) ($pair[0] + ' 修改时间发生变化')
            }
        }
        Pass 'real-storage-untouched' @{
            private = $privateAfter
            userPrefs = (Get-RealUserFileFingerprint 'ui-preferences.json')
            notification = (Get-RealUserFileFingerprint 'notification-consumption.json')
            portalOpen = (Get-RealUserFileFingerprint 'portal-open-consumption.json')
        }
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
        deferred = @('真实浏览器打开固定门户根页与人工认证（人工；QA 用受控记录器替代浏览器启动）',
            '真实系统时钟异常注入（受控场景以播种 LastOpenAt 模拟时钟回拨形态）',
            '真实 UAC / 真实安装与回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('兜底开页 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
