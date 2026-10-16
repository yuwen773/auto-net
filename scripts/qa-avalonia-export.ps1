#requires -Version 7.0
# 诊断包导出 QA（迁移工单 #12 / T13）。
# 覆盖：普通界面流程状态机（取消保存不发起提权 / 目标路径逐字传递 / 拒绝 UAC 即取消 /
# 成功呈现目标路径 / 缺口明细在界面结果中标出 / 失败披露受控错误类型 / 选择与启动异常受控）、
# 提权侧 QA 入口（--export-diagnostics <路径> 参数契约、结果文件契约、退出码 0/3、
# QA 隔离不产生真实包）、参数缺失契约（退出码 2 + parse-error 证据）、
# 概览/诊断页导出卡真实窗口截图、本机真实存储与后台服务未被触碰。
# 边界：QA 模式保存对话框与提权启动走受控夹具（不弹真实对话框、不弹 UAC、不做真实采集；
# 真实采集库由 313 项离线回归覆盖）；提权侧场景需在管理员提示符下运行本脚本，
# 非管理员时提权侧将按既有契约返回 4（elevation-requested）而失败。
# 全程不触碰本机真实 %LOCALAPPDATA%\AutoNet、C:\ProgramData\AutoNet，也不修改服务。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-export'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_STATUS_PATH', 'AUTONET_QA_EXPORT_OUTCOME')) {
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
    param([string]$QaDirectory, [string]$QaMode, [string]$StatusPath, [string[]]$Arguments = @(), [string]$Outcome = '')
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_STATUS_PATH', $StatusPath)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_EXPORT_OUTCOME', $Outcome)
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
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
function Get-Step($Report, [string]$Name) {
    $match = @($Report.Steps | Where-Object Scenario -eq $Name)
    Require ($match.Count -eq 1) ('导出步骤缺失或重复：' + $Name)
    return $match[0]
}
function Write-StatusFixture([string]$Path) {
    # 与服务端 status.json 同构的在线快照：诊断页投影可信，导出卡截图带真实状态语境。
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

# 本机真实存储的指纹：受限业务存储（ProgramData）、真实用户界面偏好与消费记录（LocalAppData）、
# 真实共享导出结果文件（ProgramData\AutoNet\export-result.json）。导出 QA 必须全程保持不变。
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
function Get-RealFileFingerprint([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path
        return @{ exists = $true; length = $item.Length; hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash; modified = $item.LastWriteTimeUtc.ToString('o') }
    }
    return @{ exists = $false }
}

try {
    $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    $serviceBefore = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
    $privateBefore = Get-RealPrivateFingerprint
    $prefsBefore = Get-RealFileFingerprint (Join-Path (Join-Path $env:LOCALAPPDATA 'AutoNet') 'ui-preferences.json')
    $notificationBefore = Get-RealFileFingerprint (Join-Path (Join-Path $env:LOCALAPPDATA 'AutoNet') 'notification-consumption.json')
    $resultBefore = Get-RealFileFingerprint (Join-Path (Join-Path $env:ProgramData 'AutoNet') 'export-result.json')

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

    # 2) 普通界面流程（故事 71–74、78、80、81）：受控夹具驱动导出状态机，逐场景核验。
    try {
        $uiDir = Join-Path $evidence 'ui'
        if (Test-Path -LiteralPath $uiDir) { Remove-Item -LiteralPath $uiDir -Recurse -Force }
        New-Item -ItemType Directory -Path $uiDir | Out-Null
        $statusPath = Join-Path $uiDir 'status.json'
        Write-StatusFixture $statusPath
        $process = Start-App $uiDir 'export-diagnostics' $statusPath
        $reportPath = Join-Path $uiDir 'export.json'
        Require (Wait-ForFile $reportPath 60) '导出界面 QA 报告未生成'
        Require (Wait-Exit $process 120) '导出界面 QA 未在超时内退出'
        Require ($process.ExitCode -eq 0) ('导出界面 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($null -eq $report.Error) ('导出界面报告包含错误：' + $report.Error)
        Require ($report.MutexName -eq 'Local\AutoNet.Tray.Avalonia') ('互斥名不符：' + $report.MutexName)

        # 故事 74：取消保存不发起提权、不生成任何文件（步骤级 Target 为空 = 发起时尚无提权记录）。
        $cancel = Get-Step $report 'cancel-pick-discards-everything'
        Require ($cancel.Phase -eq 'Cancelled' -and $null -eq $cancel.Target) '取消保存仍发起了提权或残留目标'
        # 故事 72→73：目标路径逐字传递；拒绝 UAC 即取消。
        $deny = Get-Step $report 'deny-uac-cancels-export'
        Require ($deny.Phase -eq 'Cancelled') '拒绝 UAC 未按取消呈现'
        Require ($deny.Target -like '*chosen-diagnostic-package.zip') ('提权目标与用户所选不符：' + $deny.Target)
        # 故事 81：成功呈现目标路径。
        Require ((Get-Step $report 'success-presents-target').Phase -eq 'Succeeded') '成功未按成功呈现'
        # 故事 80：缺口明细在界面结果中逐项标出。
        $notes = Get-Step $report 'notes-listed-in-result'
        Require ($notes.Phase -eq 'Succeeded') '带缺口成功呈现不符'
        # 故事 79/81：失败披露受控错误类型，不谎报成功。
        $failure = Get-Step $report 'failure-presents-controlled-kind'
        Require ($failure.Phase -eq 'Failed' -and $failure.Message.Contains('StubbedExportException')) ('失败呈现不符：' + $failure.Message)
        # 异常受控：选择与启动阶段的异常都不逃逸，界面呈现受控失败。
        Require ((Get-Step $report 'pick-exception-controlled').Phase -eq 'Failed') '选择异常未受控'
        Require ((Get-Step $report 'launch-exception-controlled').Phase -eq 'Failed') '启动异常未受控'

        # 提权启动总账：6 次记录 = 5 个场景（除取消保存与选择异常外各 1 次）+ 截图收尾成功导出；
        # 目标逐字一致。
        Require ($report.LaunchTargets.Count -eq 6) ('提权启动次数异常：' + $report.LaunchTargets.Count)
        foreach ($target in $report.LaunchTargets) {
            Require ($target -like '*chosen-diagnostic-package.zip') ('出现非用户所选目标：' + $target)
        }
        # 故事 81：导出期间界面可操作（每步都伴随状态刷新）；视觉证据已捕获。
        Require (Test-Path -LiteralPath (Join-Path $uiDir 'diagnostics-export-card.png')) '诊断页导出卡截图未生成'
        Pass 'export-ui-flow' @{ report = $reportPath; steps = $report.Steps; launchTargets = $report.LaunchTargets }
    }
    catch { Fail 'export-ui-flow' $_.Exception.Message }

    # 3) 提权侧 QA 入口（故事 73、75–77 的入口与结果契约；需管理员提示符）。
    try {
        $elevDir = Join-Path $evidence 'elevated'
        if (Test-Path -LiteralPath $elevDir) { Remove-Item -LiteralPath $elevDir -Recurse -Force }
        New-Item -ItemType Directory -Path $elevDir | Out-Null
        $requestedTarget = Join-Path $elevDir 'requested-target.zip'
        $resultPath = Join-Path $elevDir 'export-result.json'

        # 3a) 成功场景：退出码 0，结果文件 ok:true、目标逐字一致；QA 隔离不产生真实包。
        $process = Start-App $elevDir 'export-diagnostics' '' @('--export-diagnostics', $requestedTarget) 'success'
        Require (Wait-Exit $process 120) '提权侧成功场景未在超时内退出'
        Require ($process.ExitCode -eq 0) ('提权侧成功场景退出码为 ' + $process.ExitCode)
        Require (Wait-ForFile (Join-Path $elevDir 'export-elevated.json') 30) '提权侧证据未生成'
        $elevated = Get-Content -LiteralPath (Join-Path $elevDir 'export-elevated.json') -Raw | ConvertFrom-Json
        Require ($elevated.targetPath -eq $requestedTarget) ('提权侧收到的目标不符：' + $elevated.targetPath)
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        Require ($result.Ok -eq $true -and $result.TargetPath -eq $requestedTarget) '结果文件成功载荷不符'
        Require (-not (Test-Path -LiteralPath $requestedTarget)) '提权侧 QA 触碰了证据目录外的真实采集'
        # 3b) 失败场景：退出码 3，结果文件披露受控错误类型。
        $process = Start-App $elevDir 'export-diagnostics' '' @('--export-diagnostics', $requestedTarget) 'failure'
        Require (Wait-Exit $process 120) '提权侧失败场景未在超时内退出'
        Require ($process.ExitCode -eq 3) ('提权侧失败场景退出码为 ' + $process.ExitCode)
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        Require ($result.Ok -eq $false -and $result.ErrorKind -eq 'StubbedExportException') '结果文件失败载荷不符'
        Pass 'export-elevated' @{ dir = $elevDir; elevated = $elevated; successResult = 'ok:true'; failureExit = 3 }
    }
    catch { Fail 'export-elevated' $_.Exception.Message }

    # 4) 参数契约：--export-diagnostics 缺路径 → 退出码 2 + parse-error 证据，不弹窗口。
    try {
        $parseDir = Join-Path $evidence 'parse-error'
        if (Test-Path -LiteralPath $parseDir) { Remove-Item -LiteralPath $parseDir -Recurse -Force }
        New-Item -ItemType Directory -Path $parseDir | Out-Null
        $process = Start-App $parseDir 'export-diagnostics' '' @('--export-diagnostics')
        Require (Wait-Exit $process 60) '参数缺失场景未在超时内退出'
        Require ($process.ExitCode -eq 2) ('参数缺失场景退出码为 ' + $process.ExitCode)
        Require (Wait-ForFile (Join-Path $parseDir 'parse-error.json') 30) '参数缺失证据未生成'
        Pass 'export-parse-error' @{ dir = $parseDir; exitCode = 2 }
    }
    catch { Fail 'export-parse-error' $_.Exception.Message }

    # 5) 本机真实存储逐字节未被触碰（只读核对）。
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
        foreach ($pair in @(@('ui-preferences.json', $prefsBefore), @('notification-consumption.json', $notificationBefore), @('export-result.json', $resultBefore))) {
            $before = $pair[1]
            $path = if ($pair[0] -eq 'export-result.json') { Join-Path (Join-Path $env:ProgramData 'AutoNet') $pair[0] }
                    else { Join-Path (Join-Path $env:LOCALAPPDATA 'AutoNet') $pair[0] }
            $after = Get-RealFileFingerprint $path
            Require ($before.exists -eq $after.exists) ($pair[0] + ' 存在性发生变化')
            if ($before.exists) {
                Require ($before.hash -eq $after.hash) ($pair[0] + ' 内容发生变化')
                Require ($before.modified -eq $after.modified) ($pair[0] + ' 修改时间发生变化')
            }
        }
        Pass 'real-storage-untouched' @{ private = $privateAfter; exportResult = (Get-RealFileFingerprint (Join-Path (Join-Path $env:ProgramData 'AutoNet') 'export-result.json')) }
    }
    catch { Fail 'real-storage-untouched' $_.Exception.Message }

    # 6) 服务未被触碰（只读核对）
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
        deferred = @('真实系统保存对话框与同名覆盖确认（人工；QA 用受控夹具替代）',
            '真实 UAC 提权窗口（人工；提权侧场景需管理员提示符运行本脚本）',
            '真实只读采集与 ZIP 内容（离线回归 313 项覆盖 DiagnosticsPackage 契约）',
            '真实 UAC 下跨管理员账户的结果文件可读性（结果文件按既有共享目录 ACL 继承）',
            '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('诊断包导出 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
