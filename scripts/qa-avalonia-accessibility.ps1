#requires -Version 7.0
# 可访问性与缩放 QA（迁移工单 #14 / T05+T06）。
# 覆盖：键盘导航/焦点/主要操作（Enter 触发恢复开关、方向键切换页面、Tab 前进，受控键注入走真实路由）、
# 可访问名称随状态动态更新（恢复开关三态、解除入口、兜底开页、导出入口）、
# 100%/125%/150% 受控缩放（真实窗口 LayoutTransform + PrintWindow 捕获，程序化核验关键控件
# 「视口内完整可见或位于可滚动区域」）、每页 Tab 停留点序列。
# 边界：真实 Windows DPI 与系统键盘/辅助技术交互不在本脚本内，结果分列记录为待验收；
# 不安装、不替换、不重启服务；夹具只写 QA 证据目录；不订阅自动开页，不触碰剪贴板与资源管理器。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-accessibility'
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
function Wait-ForFile([string]$Path, [int]$TimeoutSeconds = 40) {
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
# 只清理本证据目录下的迁移进程，绝不触碰 C:\Program Files\AutoNet 下已安装的托盘。
$evidencePrefix = [IO.Path]::GetFullPath($evidence) + [IO.Path]::DirectorySeparatorChar
Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

try {
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

    if (-not (Test-Path -LiteralPath $exe)) { throw '没有可运行的发布产物，后续核验无法进行。' }

    # 2) 可访问性与受控缩放（夹具模式）
    try {
        $qaDir = New-QaDirectory 'accessibility'
        $fixture = Join-Path $qaDir 'status-fixture.json'
        $process = Start-App -Executable $exe -Arguments @() -QaDirectory $qaDir -QaMode 'accessibility' -StatusPath $fixture
        $reportPath = Join-Path $qaDir 'accessibility.json'
        Require (Wait-ForFile $reportPath) '可访问性 QA 报告未生成'
        Require (Wait-Exit $process 90) '可访问性 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('可访问性 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture' -and $report.Fixture) '夹具模式标记不符'
        Require ($null -eq $report.Error) ('可访问性 QA 报告包含错误：' + $report.Error)

        # 键盘（故事 32 / T06）：Enter 触发主要操作（恢复开关 + 解除入口）、Tab 绕环遍历、方向键切换页面。
        $enter = $report.Keyboard | Where-Object { $_.Key -eq 'Enter' -and $_.Context -like '恢复开关*' }
        Require ($enter.Count -ge 1) '缺少 Enter 触发恢复开关的键盘证据'
        Require ($enter[0].FocusSet -and $enter[0].Triggered -and $enter[0].Recorded -eq '--disable-recovery') ('Enter 键盘驱动证据不符：' + ($enter[0] | ConvertTo-Json -Compress))
        $unblockEnter = $report.Keyboard | Where-Object { $_.Key -eq 'Enter' -and $_.Context -like '解除入口*' }
        Require ($unblockEnter.Count -ge 1 -and $unblockEnter[0].Triggered -and $unblockEnter[0].Recorded -eq '--unblock-auth') '缺少解除入口的键盘触发证据'
        $tab = $report.Keyboard | Where-Object { $_.Key -like 'Tab*' }
        Require ($tab.Count -ge 1 -and $tab[0].Triggered -and $tab[0].Recorded -like '*→*') ('Tab 绕环遍历证据不符：' + ($tab[0] | ConvertTo-Json -Compress))
        $down = $report.Keyboard | Where-Object { $_.Key -eq 'Down' -and $_.Context -like '左侧导航*' }
        Require ($down.Count -ge 1 -and $down[0].Triggered -and $down[0].Recorded -eq 'Diagnostics') '方向键切换页面证据不符'

        # 可访问名称（故事 33）：恢复开关随状态动态更新；解除/开页/导出携带当前事实。
        $namesMap = @{}
        foreach ($record in $report.Names) { $namesMap[$record.Scenario] = $record }
        Require $namesMap.ContainsKey('恢复开关·已开启') '缺少已开启名称证据'
        Require $namesMap.ContainsKey('恢复开关·已关闭') '缺少已关闭名称证据'
        Require ($namesMap['恢复开关·已开启'].RecoveryName -ne $namesMap['恢复开关·已关闭'].RecoveryName) '恢复开关名称未随状态变化'
        Require ($namesMap['恢复开关·已开启'].RecoveryName -like '*已开启*关闭自动恢复*') ('已开启名称不符：' + $namesMap['恢复开关·已开启'].RecoveryName)
        Require ($namesMap['恢复开关·已关闭'].RecoveryName -like '*已关闭*开启自动恢复*') ('已关闭名称不符：' + $namesMap['恢复开关·已关闭'].RecoveryName)
        Require ($namesMap['解除入口·认证暂停'].UnblockName -like '*认证提交已暂停*') ('解除入口名称未携带认证事实：' + $namesMap['解除入口·认证暂停'].UnblockName)
        Require ($namesMap['兜底开页·可见'].PortalOpenName -like '需要浏览器认证*') ('兜底开页名称不符：' + $namesMap['兜底开页·可见'].PortalOpenName)
        Require ($namesMap['导出入口·诊断页'].ExportName -eq '导出诊断包…') ('导出入口名称不符：' + $namesMap['导出入口·诊断页'].ExportName)

        # Tab 停留点（T06）：每页序列非空且导航项名称与页面名一致。
        Require ($report.TabSequences.Count -eq 4) ('页面 Tab 序列数不符：' + $report.TabSequences.Count)
        foreach ($pageStops in $report.TabSequences) {
            Require ($pageStops.Stops.Count -gt 0) ($pageStops.Page + ' 页无可键盘到达控件')
            $navNames = @($pageStops.Stops | Where-Object Kind -eq 'ListBoxItem' | ForEach-Object Name)
            Require ($navNames.Count -ge 4) ($pageStops.Page + ' 页导航项不足：' + ($navNames -join '、'))
        }

        # 受控缩放（T05 / 故事 31）：四页 × 三档全部通过，无关键裁切。
        Require ($report.Scaling.Count -eq 12) ('受控缩放记录数不符：' + $report.Scaling.Count)
        foreach ($scale in $report.Scaling) {
            Require ($scale.Pass) ($scale.Page + ' ' + $scale.Scale + '%：' + ($scale.Clipped -join '；'))
            $shot = Join-Path $qaDir ('scale-' + $scale.Page.ToLowerInvariant() + '-' + $scale.Scale + '.png')
            Require (Test-Path -LiteralPath $shot) ('缺少缩放截图：' + $scale.Page + '-' + $scale.Scale)
            Require ((Get-Item -LiteralPath $shot).Length -gt 10KB) ($scale.Page + '-' + $scale.Scale + ' 截图内容过小，疑似空白渲染')
        }

        # 真实 DPI 分列（T05）：显式待验收，不以模拟值补为通过。
        Require ($report.RealDpiVerification -match '待验收') '真实 DPI 验收结论必须显式标记为待验收'

        Pass 'accessibility-fixture' @{
            report = $reportPath; keyboard = $report.Keyboard; names = $report.Names
            tabSequences = $report.TabSequences; scaling = $report.Scaling
            realDpi = $report.RealDpiVerification
        }
    }
    catch { Fail 'accessibility-fixture' $_.Exception.Message }
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
        deferred = @('真实 Windows DPI 环境逐项验收（T05 分列，人工）', '真实系统键盘/辅助技术交互与焦点视觉（T06 桌面验收，人工）', '真实 UAC 交互（人工）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('可访问性与缩放 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
