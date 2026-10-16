#requires -Version 7.0
# 日志页 QA（迁移工单 #7）。
# 覆盖：自包含 win-x64 单文件发布契约、日期入口默认最新、受限列表显式标注范围（故事 45）、
# 旧版记录兼容渲染（故事 51）、损坏记录跳过并说明缺口（故事 49）、快速切换最新获胜与
# 重复刷新稳定（故事 50）、脱敏复制文本契约（故事 47）、读取失败按固定文案降级（不回显异常消息）、
# 空目录固定空态，以及读取本机真实日志的实况证据；真实窗口截图。
# 边界：不安装、不替换、不重启服务；不触碰本机真实 status.json 与真实日志（夹具只允许位于
# QA 证据目录内）；不发起任何网络动作；不触碰用户剪贴板、不启动资源管理器（真实点击为人工项）；
# 明暗主题、键盘与真实 DPI（#9/#14，人工）。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-logs'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_LOG_DIRECTORY')) {
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
    param([string]$Executable, [string]$QaDirectory, [string]$QaMode, [string]$LogsDirectory = '')
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_LOG_DIRECTORY', $LogsDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
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
    return $Process.WaitForExit($TimeoutSeconds * 1000)
}
function New-QaDirectory([string]$Name) {
    $path = Join-Path $evidence $Name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
function Get-Step($Report, [string]$State) {
    $match = @($Report.Steps | Where-Object Scenario -eq $State)
    Require ($match.Count -eq 1) ('日志步骤缺失或重复：' + $State)
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

# 夹具日志（与应用内 QaLogs 契约一致）：新版 v2、旧版 v1、含损坏行、被脚本独占锁定的文件。
function Write-FixtureLogs([string]$Directory) {
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $v2 = '{"schemaVersion":2,"eventCode":"RecoveryDecision","time":"2026-10-16T00:00:00Z","action":"恢复决策","detail":"样本：两轮失败采样后按受控决策等待。","sequence":12,"cycle":3,"diagnostics":{"reason":"InsufficientFailureSamples","stage":"MainCycle","outcome":"Skipped"}}'
    $v2b = '{"schemaVersion":2,"eventCode":"PortalStep","time":"2026-10-16T00:01:00Z","action":"认证步骤","detail":"样本：门户状态查询完成。","diagnostics":{"stage":"MainCycle","outcome":"None"}}'
    Set-Content -LiteralPath (Join-Path $Directory 'autonet-2026-10-16.jsonl') -Value @($v2, $v2b) -Encoding utf8NoBOM
    $v1 = '{"time":"2026-10-15T01:00:00Z","action":"认证结果","detail":"旧版记录说明：门户返回需要认证。","wifi":"已连接","direct":"外网不可达或未知","portal":"需认证","attempts":2,"disconnectAttempts":1,"authenticationStage":"Submission","authenticationFailure":"BindingRejected"}'
    Set-Content -LiteralPath (Join-Path $Directory 'autonet-2026-10-15.jsonl') -Value @($v1) -Encoding utf8NoBOM
    $good1 = '{"time":"2026-10-14T00:00:00Z","action":"服务启动","detail":"损坏夹具前的正常记录。"}'
    $good2 = '{"time":"2026-10-14T00:02:00Z","action":"服务停止","detail":"损坏夹具后的正常记录。"}'
    Set-Content -LiteralPath (Join-Path $Directory 'autonet-2026-10-14.jsonl') -Value @($good1, '{not json', '{"time":"2026-10-14T00:01:00Z"}', $good2, '{"time":"2026-10-14T00:03:00Z","action":"服务启动","detail":null}') -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $Directory 'autonet-2026-10-13.jsonl') -Value @('{"time":"2026-10-13T00:00:00Z","action":"服务启动","detail":"该文件将被脚本独占锁定。"}') -Encoding utf8NoBOM
}

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

    # 2) 日志页场景矩阵（夹具模式）：八场景截图 + 范围/缺口/切换/筛选/复制/降级逐项核验
    try {
        $logsDir = New-QaDirectory 'logs'
        $fixture = Join-Path $logsDir 'fixture'
        Write-FixtureLogs $fixture
        $lockedPath = Join-Path $fixture 'autonet-2026-10-13.jsonl'

        # 脚本以独占方式锁定 10-13 文件：应用读取必须降级（故事 49/51），其他文件不受影响。
        $lockedStream = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            $process = Start-App -Executable $exe -QaDirectory $logsDir -QaMode 'logs' -LogsDirectory $fixture
            $reportPath = Join-Path $logsDir 'logs.json'
            Require (Wait-ForFile $reportPath 60) '日志页 QA 报告未生成'
            Require (Wait-Exit $process 120) '日志页 QA 运行未在超时内退出'
            Require ($process.ExitCode -eq 0) ('日志页 QA 退出码为 ' + $process.ExitCode)
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            Require ($report.Mode -eq 'fixture' -and $report.Fixture) '夹具模式标记不符'
            Require ($null -eq $report.Error) ('日志页 QA 报告包含错误：' + $report.Error)
            Require ($report.ScopeRule -match '300') '范围规则声明缺少单日上限'

            $dates = Get-Step $report 'dates'
            Require ($dates.Dates -eq 4) ('日期入口数量不符：' + $dates.Dates)
            Require ($dates.SelectedDate -eq 'autonet-2026-10-16.jsonl') '日期入口未默认选中最新一天'
            Require ($dates.Rows -eq 2) ('新版日期行数不符：' + $dates.Rows)
            Require ($dates.ScopeNote -match '已包含当日全部记录') '未触达上限时必须声明覆盖全部记录'
            Require ($dates.ScopeNote -match '单日最多 300 条') '范围标注必须写明单日上限'
            Require ($dates.FirstRow -eq '认证步骤') '列表必须最新在前'
            Require ($dates.DetailHeading -match '【认证步骤】') '详情必须带事件标题'
            Require ($dates.CopyAvailable) '脱敏复制文本必须可用（故事 47）'

            $legacy = Get-Step $report 'legacy'
            Require ($legacy.SelectedDate -eq 'autonet-2026-10-15.jsonl') '旧版日期未切换'
            Require ($legacy.Rows -eq 1) '旧版记录行数不符'
            Require ($legacy.DetailHeading.StartsWith('时间：', [StringComparison]::Ordinal)) '旧版详情必须按兼容文案渲染（故事 51）'
            Require ($legacy.GapNote -eq '') '旧版日期不得报告缺口'

            $corrupt = Get-Step $report 'corrupt'
            Require ($corrupt.SelectedDate -eq 'autonet-2026-10-14.jsonl') '损坏日期未切换'
            Require ($corrupt.Rows -eq 2) '损坏行不得进入列表，也不得拖垮整页（故事 49）'
            Require ($corrupt.GapNote -match '已跳过 3 条无法读取的记录') '必须显式说明缺口：' + $corrupt.GapNote
            Require ($corrupt.ScopeNote -match '已包含当日全部记录') '损坏日期的范围标注仍须成立'

            $switch = Get-Step $report 'switch'
            Require ($switch.SelectedDate -eq 'autonet-2026-10-14.jsonl') '快速切换后必须展示最新选择（故事 50）'
            Require ($switch.Rows -eq 2) '快速切换后的内容必须对应最新选择'

            $refresh = Get-Step $report 'refresh'
            Require ($refresh.SelectedDate -eq 'autonet-2026-10-14.jsonl' -and $refresh.Rows -eq $switch.Rows) '重复刷新必须稳定且保留选择（故事 50）'

            $filter = Get-Step $report 'filter'
            Require ($filter.SelectedDate -eq 'autonet-2026-10-16.jsonl') '筛选场景未回到含认证事件的日期'
            Require ($filter.Rows -eq 1 -and $filter.Rows -lt $dates.Rows) '认证筛选必须收窄列表'
            Require ($filter.ScopeNote.StartsWith('显示 1 条 / 已读取 2 条', [StringComparison]::Ordinal)) ('筛选后范围计数不符：' + $filter.ScopeNote)

            $failure = Get-Step $report 'read-failure'
            Require ($failure.SelectedDate -eq 'autonet-2026-10-13.jsonl') '锁定文件日期未切换'
            Require ($failure.FailureNote.StartsWith('读取失败：', [StringComparison]::Ordinal)) '读取失败必须有固定失败说明'
            Require ($failure.FailureNote -match '^读取失败：[A-Za-z]+$') ('失败说明只允许异常类型名：' + $failure.FailureNote)
            Require ($failure.Rows -eq 0 -and -not $failure.CopyAvailable) '失败时列表与复制必须清空'

            # 隐私契约（T15 同口径）：夹具报告全文不出现凭据形、会话形或 URL 形字符串。
            $raw = Get-Content -LiteralPath $reportPath -Raw
            foreach ($forbidden in @('user_password', 'phpsessid', 'cookie', '://')) {
                Require (-not $raw.ToLower().Contains($forbidden)) ('日志页报告泄露了 ' + $forbidden)
            }

            foreach ($shot in @('logs-dates', 'logs-legacy', 'logs-corrupt', 'logs-switch', 'logs-refresh', 'logs-filter', 'logs-read-failure')) {
                $path = Join-Path $logsDir ($shot + '.png')
                Require (Test-Path -LiteralPath $path) ('缺少日志页截图：' + $shot)
                Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
            }
            Require (-not (Test-Path -LiteralPath (Join-Path $logsDir 'elevation.json'))) '日志页查看期间出现了提权请求记录'
            Pass 'logs-fixture' @{ report = $reportPath; steps = $report.Steps }
        }
        finally { $lockedStream.Dispose() }
    }
    catch { Fail 'logs-fixture' $_.Exception.Message }

    # 3) 空目录场景：固定空态，不得虚构日期入口或列表
    try {
        $emptyDir = New-QaDirectory 'logs-empty'
        $fixture = Join-Path $emptyDir 'fixture'
        New-Item -ItemType Directory -Force -Path $fixture | Out-Null
        $process = Start-App -Executable $exe -QaDirectory $emptyDir -QaMode 'logs' -LogsDirectory $fixture
        $reportPath = Join-Path $emptyDir 'logs.json'
        Require (Wait-ForFile $reportPath 60) '空目录 QA 报告未生成'
        Require (Wait-Exit $process 120) '空目录 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('空目录 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($null -eq $report.Error) ('空目录 QA 报告包含错误：' + $report.Error)
        $empty = Get-Step $report 'empty'
        Require ($empty.Dates -eq 0 -and $empty.Rows -eq 0) '空目录不得虚构日期入口或列表'
        Require ($empty.ScopeNote -match '还没有日志文件') '空目录必须显示固定空态'
        Require (Test-Path -LiteralPath (Join-Path $emptyDir 'logs-empty.png')) '缺少空目录截图'
        Pass 'logs-empty' @{ report = $reportPath; step = $empty }
    }
    catch { Fail 'logs-empty' $_.Exception.Message }

    # 4) 读取本机真实日志的实况证据（不重定向日志目录；只读，不记录日志内容）
    try {
        $liveDir = New-QaDirectory 'logs-live'
        $process = Start-App -Executable $exe -QaDirectory $liveDir -QaMode 'logs' -LogsDirectory ''
        $reportPath = Join-Path $liveDir 'logs.json'
        Require (Wait-ForFile $reportPath 60) '实况日志页 QA 报告未生成'
        Require (Wait-Exit $process 120) '实况日志页 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('实况日志页 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'live' -and -not $report.Fixture) '实况模式标记不符'
        Require ($null -eq $report.Error) ('实况日志页 QA 报告包含错误：' + $report.Error)
        $live = Get-Step $report 'live'
        Require ($live.Dates -ge 0) '实况日期入口计数缺失'
        # 实况报告绝不携带详情或复制文本（隐私契约）：CopyText 必须为空声明。
        Require ($null -eq $live.CopyText) '实况报告不得携带复制文本'
        Require (-not (Test-Path -LiteralPath (Join-Path $liveDir 'elevation.json'))) '实况查看期间出现了提权请求记录'
        Require (Test-Path -LiteralPath (Join-Path $liveDir 'logs-live.png')) '缺少实况日志页截图'
        Pass 'logs-live' @{ report = $reportPath; step = $live }
    }
    catch { Fail 'logs-live' $_.Exception.Message }

    # 5) 服务与已安装托盘未被触碰（只读核对）
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
        deferred = @('真实剪贴板复制与资源管理器打开（人工）', '明暗主题（#9）', '键盘与可访问名称（#14）', '托盘菜单视觉点击（人工）', '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('日志页 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
