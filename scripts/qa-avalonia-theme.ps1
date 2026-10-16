#requires -Version 7.0
# 主题与偏好 QA（迁移工单 #9 / T04）。
# 覆盖：默认跟随系统（故事 26：窗口实际变体 == 操作系统变体且无偏好文件）、设置页真实
# 单选钮经生产选中事件路径的选择即应用即保存（故事 27；物理点击的视觉确认为人工项）、
# 系统主题变化不覆盖手动选择（故事 29）、跟随模式下响应系统变化（故事 29）、
# 跨托盘重启保留（故事 28：第二阶段新进程不做任何选择，启动时偏好即上次保存值）、
# 偏好文件逐字只含主题枚举、QA 证据目录与本机真实用户偏好逐字节隔离（故事 70）。
# 边界：不安装、不替换、不重启服务；不修改 Windows 系统主题（真实明暗切换为人工项）；
# 偏好文件只写 QA 证据目录，绝不触碰本机真实 %LOCALAPPDATA%\AutoNet 或
# C:\ProgramData\AutoNet 下的任何受限存储，也不发起真实网络请求。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-theme'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_STATUS_PATH', 'AUTONET_QA_THEME_PHASE')) {
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
    param([string]$Executable, [string[]]$Arguments, [string]$QaDirectory, [string]$QaMode, [string]$StatusPath = '', [string]$ThemePhase = '')
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_STATUS_PATH', $StatusPath)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_THEME_PHASE', $ThemePhase)
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
function Get-ThemeStep($Report, [string]$Name) {
    $match = @($Report.Steps | Where-Object Scenario -eq $Name)
    Require ($match.Count -eq 1) ('主题步骤缺失或重复：' + $Name)
    return $match[0]
}

# 只清理本证据目录下的迁移进程，绝不触碰 C:\Program Files\AutoNet 下已安装的托盘。
$evidencePrefix = [IO.Path]::GetFullPath($evidence) + [IO.Path]::DirectorySeparatorChar
Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

# 本机真实存储的两份指纹：受限业务存储（ProgramData）与真实用户界面偏好（LocalAppData）。
# 主题 QA 必须全程保持两者逐字节不变——偏好隔离（故事 70）的直接证据。
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
function Get-RealUserPrefsFingerprint {
    $prefs = Join-Path $env:LOCALAPPDATA 'AutoNet/ui-preferences.json'
    if (Test-Path -LiteralPath $prefs) {
        $item = Get-Item -LiteralPath $prefs
        return @{ exists = $true; length = $item.Length; hash = (Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash; modified = $item.LastWriteTimeUtc.ToString('o') }
    }
    return @{ exists = $false }
}

try {
    $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    $serviceBefore = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
    $privateBefore = Get-RealPrivateFingerprint
    $userPrefsBefore = Get-RealUserPrefsFingerprint

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

    # 2) 选择阶段（故事 26、27、29）：真实单选钮驱动 + 系统变化优先级 + 落盘
    try {
        $shellDir = New-QaDirectory 'shell'
        $statusPath = Join-Path $shellDir 'status.json'
        @{ Enabled = $true; UpdatedAt = [DateTimeOffset]::Now.ToString('o'); State = '校园网在线'; Detail = ''
           Direct = '外网可达'; Wifi = 'TEST-AP-1'; Portal = '未查询（外网检查已通过）'; Tunnel = '系统路径可达，出口未核验'
           Uu = '未核验'; Attempts = 0; DisconnectAttempts = 0; AuthenticationStage = 'Unknown'; AuthenticationFailure = 'None'
           AuthenticationPersistentState = 'Ready' } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statusPath -Encoding utf8
        $process = Start-App $exe @() $shellDir 'theme-prefs' $statusPath
        $reportPath = Join-Path $shellDir 'theme-prefs.json'
        Require (Wait-ForFile $reportPath 60) '主题选择阶段 QA 报告未生成'
        Require (Wait-Exit $process 120) '主题选择阶段 QA 未在超时内退出'
        Require ($process.ExitCode -eq 0) ('主题选择阶段退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Phase -eq 'select') ('阶段标记不符：' + $report.Phase)
        Require ($null -eq $report.Error) ('主题 QA 报告包含错误：' + $report.Error)
        Require ($report.MutexName -eq 'Local\AutoNet.Tray.Avalonia') ('互斥名不符：' + $report.MutexName)

        # 故事 26：无偏好文件时默认跟随系统——请求变体 Default，窗口实际变体与操作系统一致。
        $initial = Get-ThemeStep $report 'default-system'
        Require ($initial.Preference -eq 'System') ('初始偏好不是跟随系统：' + $initial.Preference)
        Require ($initial.RequestedVariant -eq 'Default') ('初始请求变体不是 Default：' + $initial.RequestedVariant)
        Require ($initial.ActualVariant -eq $initial.OsVariant) ('窗口实际变体与系统不一致：' + $initial.ActualVariant + ' / ' + $initial.OsVariant)
        Require ($initial.PrefsContent -eq '(absent)') ('初始阶段不应有偏好文件：' + $initial.PrefsFile)

        # 故事 27：真实单选钮选择浅色/深色——即选即用即存。
        $light = Get-ThemeStep $report 'select-light'
        Require ($light.Preference -eq 'Light' -and $light.RequestedVariant -eq 'Light' -and $light.ActualVariant -eq 'Light') '浅色未被真实单选钮应用'
        Require ($light.PrefsContent -eq '{"theme":"light"}') ('浅色偏好文件逐字不符：' + $light.PrefsContent)
        $dark = Get-ThemeStep $report 'select-dark'
        Require ($dark.Preference -eq 'Dark' -and $dark.RequestedVariant -eq 'Dark' -and $dark.ActualVariant -eq 'Dark') '深色未被真实单选钮应用'
        Require ($dark.PrefsContent -eq '{"theme":"dark"}') ('深色偏好文件逐字不符：' + $dark.PrefsContent)

        # 故事 29：系统主题变化通知不覆盖手动深色（变体与文件都不变）。
        $ignored = Get-ThemeStep $report 'system-change-ignored'
        Require ($ignored.Preference -eq 'Dark' -and $ignored.RequestedVariant -eq 'Dark' -and $ignored.ActualVariant -eq 'Dark') '系统变化覆盖了手动深色'
        Require ($ignored.PrefsContent -eq '{"theme":"dark"}') '系统变化改写了偏好文件'

        # 故事 27+29：回到跟随系统——请求变体 Default，实际变体回到操作系统值。
        $system = Get-ThemeStep $report 'select-system'
        Require ($system.Preference -eq 'System' -and $system.RequestedVariant -eq 'Default') '未回到跟随系统'
        Require ($system.ActualVariant -eq $system.OsVariant) ('回到跟随系统后窗口变体与系统不一致：' + $system.ActualVariant + ' / ' + $system.OsVariant)
        Require ($system.PrefsContent -eq '{"theme":"system"}') ('跟随系统偏好文件逐字不符：' + $system.PrefsContent)
        $follows = Get-ThemeStep $report 'system-change-follows'
        Require ($follows.Preference -eq 'System' -and $follows.RequestedVariant -eq 'Default') '跟随模式未响应系统变化'

        # 收尾停在深色并落盘，供重启阶段验证（故事 27、28）。
        $final = Get-ThemeStep $report 'final-dark'
        Require ($final.Preference -eq 'Dark' -and $final.PrefsContent -eq '{"theme":"dark"}') '收尾深色未落盘'

        foreach ($shot in @('theme-default-system', 'theme-light', 'theme-dark', 'theme-system')) {
            $path = Join-Path $shellDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少主题截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }

        # 隐私契约（故事 70）：报告全文不出现凭据形、会话形或 URL 形字符串。
        $raw = Get-Content -LiteralPath $reportPath -Raw
        foreach ($forbidden in @('user_password', 'phpsessid', 'cookie', '://')) {
            Require (-not $raw.ToLower().Contains($forbidden)) ('主题报告泄露了 ' + $forbidden)
        }
        Pass 'theme-select' @{ report = $reportPath; steps = $report.Steps }
    }
    catch { Fail 'theme-select' $_.Exception.Message }

    # 3) 重启阶段（故事 28）：同一证据目录（偏好文件留存）内新进程不做任何选择，
    #    启动时偏好即上次保存值——跨托盘重启保留的直接证据。
    try {
        $resumeDir = Join-Path $evidence 'shell'
        $statusPath = Join-Path $resumeDir 'status.json'
        $process = Start-App $exe @() $resumeDir 'theme-prefs' $statusPath 'resume'
        $reportPath = Join-Path $resumeDir 'theme-prefs-resume.json'
        Require (Wait-ForFile $reportPath 60) '主题重启阶段 QA 报告未生成'
        Require (Wait-Exit $process 120) '主题重启阶段 QA 未在超时内退出'
        Require ($process.ExitCode -eq 0) ('主题重启阶段退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Phase -eq 'resume') ('阶段标记不符：' + $report.Phase)
        Require ($null -eq $report.Error) ('主题重启报告包含错误：' + $report.Error)
        Require ($report.Steps.Count -eq 1) ('重启阶段步骤数不符：' + $report.Steps.Count)
        $preserved = $report.Steps[0]
        Require ($preserved.Scenario -eq 'restart-preserved') ('重启阶段场景不符：' + $preserved.Scenario)
        Require ($preserved.Preference -eq 'Dark') ('重启后偏好未保留深色：' + $preserved.Preference)
        Require ($preserved.RequestedVariant -eq 'Dark' -and $preserved.ActualVariant -eq 'Dark') '重启后深色未被应用'
        Require ($preserved.PrefsContent -eq '{"theme":"dark"}') ('重启后偏好文件逐字不符：' + $preserved.PrefsContent)
        $shot = Join-Path $resumeDir 'theme-resume.png'
        Require (Test-Path -LiteralPath $shot) '缺少重启阶段截图'
        Require ((Get-Item -LiteralPath $shot).Length -gt 10KB) '重启阶段截图内容过小，疑似空白渲染'
        Pass 'theme-resume' @{ report = $reportPath; step = $preserved }
    }
    catch { Fail 'theme-resume' $_.Exception.Message }

    # 4) 本机真实受限存储与真实用户偏好逐字节未被触碰（只读核对，故事 70）。
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
        $userPrefsAfter = Get-RealUserPrefsFingerprint
        Require ($userPrefsBefore.exists -eq $userPrefsAfter.exists) '真实用户偏好文件存在性发生变化'
        if ($userPrefsBefore.exists) {
            Require ($userPrefsBefore.hash -eq $userPrefsAfter.hash) '真实用户偏好文件内容发生变化'
            Require ($userPrefsBefore.modified -eq $userPrefsAfter.modified) '真实用户偏好文件修改时间发生变化'
        }
        Pass 'real-storage-untouched' @{ private = $privateAfter; userPrefs = $userPrefsAfter }
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
        deferred = @('真实系统明暗切换下的跟随验证（人工，不修改系统主题）',
            '真实 UAC 提权窗口（设置/解除确认）的明暗一致性，含跨管理员账户提权时按账户隔离的确认（人工，需单独授权）',
            '主题单选钮的物理点击视觉确认（人工）',
            '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('主题 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
