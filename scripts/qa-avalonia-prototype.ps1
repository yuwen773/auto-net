#requires -Version 7.0
# Avalonia 兼容原型 QA（迁移工单 #2 / tickets 01）。
# 覆盖：自包含 win-x64 单文件发布与隔离运行、真机生命周期（隐藏启动/关闭隐藏/退出不停服务）、
# 普通实例单实例与独立提权入口并存、官方 Fluent 明暗主题真实窗口渲染、安装器复制与回滚文件布局镜像。
# 边界：不安装、不替换、不重启服务；不读取或修改任何业务配置；仅通过 AUTONET_QA_* 环境变量驱动原型自身。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-prototype'
$publishDir = Join-Path $evidence 'publish'
$exe = Join-Path $publishDir 'AutoNet.Tray.exe'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$steps = [ordered]@{}
$failures = [System.Collections.Generic.List[string]]::new()
$tracked = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$savedEnvironment = @{}
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }

function Pass([string]$Name, $Detail) { $steps[$Name] = @{ result = 'pass'; detail = $Detail }; Write-Host ('PASS ' + $Name) }
function Fail([string]$Name, [string]$Message) {
    $steps[$Name] = @{ result = 'fail'; detail = $Message }
    $failures.Add($Name + '：' + $Message)
    Write-Host ('FAIL ' + $Name + '：' + $Message)
}
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Start-Prototype {
    param([string]$Executable, [string[]]$Arguments, [string]$QaDirectory, [string]$QaMode)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $QaDirectory)
    [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', $QaMode)
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
function Wait-Exit($Process, [int]$TimeoutSeconds = 40) {
    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) { return $false }
    return $true
}
function New-QaDirectory([string]$Name) {
    $path = Join-Path $evidence $Name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
# 已安装托盘（C:\Program Files\AutoNet）的进程快照：只排除本证据目录下的原型进程，绝不触碰已安装实例。
function Get-InstalledTrayPids {
    return @(Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and -not [IO.Path]::GetFullPath($_.Path).StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { $_.Id })
}

# 只清理本证据目录下的原型进程，绝不触碰 C:\Program Files\AutoNet 下已安装的托盘。
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
            # pdb 是调试符号不是运行时依赖（0.2.1 发布包同样携带），安装器只复制 exe；其余外置文件才违反单文件契约。
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

    if (-not (Test-Path -LiteralPath $exe)) { throw '没有可运行的原型发布产物，后续核验无法进行。' }

    # 2) 隔离运行：只复制一个 exe 到全新目录运行完整生命周期（证明不依赖外置框架文件）
    try {
        $isolated = New-QaDirectory 'isolated'
        $isolatedExe = Join-Path $isolated 'AutoNet.Tray.exe'
        Copy-Item -LiteralPath $exe -Destination $isolatedExe
        $qa = Join-Path $isolated 'qa'
        $process = Start-Prototype -Executable $isolatedExe -Arguments @('--tray') -QaDirectory $qa -QaMode 'lifecycle'
        $reportPath = Join-Path $qa 'lifecycle.json'
        Require (Wait-ForFile $reportPath) '隔离运行的 QA 报告未生成'
        Require (Wait-Exit $process) '隔离运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('隔离运行退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $stepMap = @{}; foreach ($step in $report.Steps) { $stepMap[$step.Step] = $step }
        Require ($report.Phase -eq 'exit-requested') ('生命周期报告阶段异常：' + $report.Phase)
        Require ($report.StartHidden) '未记录隐藏启动（--tray）'
        Require (-not $stepMap['started'].WindowVisible) '隐藏启动时窗口可见'
        Require ($stepMap['shown'].WindowVisible) '显示操作后窗口不可见'
        Require (-not $stepMap['closed'].WindowVisible) '关闭窗口后未隐藏'
        Require ($stepMap.ContainsKey('alive-after-close')) '关闭后未保持运行'
        Require ([IO.Path]::GetFullPath($report.ProcessPath) -eq [IO.Path]::GetFullPath($isolatedExe)) '报告进程路径不是隔离目录中的发布 exe'
        Pass 'isolated-run' @{ exe = $isolatedExe; report = $reportPath; steps = $report.Steps }
    }
    catch { Fail 'isolated-run' $_.Exception.Message }

    # 3) 单实例与独立提权入口并存：A 持有普通实例，B 被拦截，C 提权入口不被拦截
    $instanceA = $null
    try {
        $runtimeA = New-QaDirectory 'runtime-a'
        $instanceA = Start-Prototype -Executable $exe -Arguments @('--tray') -QaDirectory $runtimeA -QaMode 'hold'
        Require (Wait-ForFile (Join-Path $runtimeA 'lifecycle.json')) '实例 A（hold）未启动'
        $holdReport = Get-Content -LiteralPath (Join-Path $runtimeA 'lifecycle.json') -Raw | ConvertFrom-Json
        Require ($holdReport.Phase -eq 'started' -and -not $holdReport.Steps[0].WindowVisible) '实例 A 未按隐藏启动运行'
        Require (-not $instanceA.HasExited) '实例 A 意外退出'
        Pass 'instance-hold' @{ report = (Join-Path $runtimeA 'lifecycle.json'); processId = $instanceA.Id }

        $runtimeB = New-QaDirectory 'runtime-b'
        $instanceB = Start-Prototype -Executable $exe -Arguments @('--tray') -QaDirectory $runtimeB -QaMode ''
        Require (Wait-Exit $instanceB 20) '第二个普通实例未退出'
        Require ($instanceB.ExitCode -eq 3) ('第二个普通实例退出码为 ' + $instanceB.ExitCode + '（预期 3=已在运行）')
        $alreadyRunning = Join-Path $runtimeB 'already-running.json'
        Require (Test-Path -LiteralPath $alreadyRunning) '第二个普通实例未写入已在运行报告'
        Require (-not $instanceA.HasExited) '第二个实例影响了实例 A'
        Pass 'single-instance' @{ exitCode = $instanceB.ExitCode; report = $alreadyRunning }

        $runtimeC = New-QaDirectory 'runtime-c'
        $instanceC = Start-Prototype -Executable $exe -Arguments @('--settings') -QaDirectory $runtimeC -QaMode ''
        $elevatedInline = Join-Path $runtimeC 'elevated-inline.json'
        $elevationRequest = Join-Path $runtimeC 'elevation.json'
        # 两种合法分支：管理员上下文直接执行（elevated-inline.json）；普通上下文记录提权请求后退出（elevation.json，退出码 4）。
        # 共同点是提权入口先于单实例检查，绝不能被普通实例互斥锁拦成 already-running.json。
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path -LiteralPath $elevatedInline) -and -not (Test-Path -LiteralPath $elevationRequest)) {
            Start-Sleep -Milliseconds 150
        }
        $blockedForC = Join-Path $runtimeC 'already-running.json'
        Require (-not (Test-Path -LiteralPath $blockedForC)) '管理员操作被普通实例互斥锁拦截'
        if (Test-Path -LiteralPath $elevatedInline) {
            $branch = 'elevated-inline'
            $recordPath = $elevatedInline
        }
        elseif (Test-Path -LiteralPath $elevationRequest) {
            $branch = 'elevation-request'
            $recordPath = $elevationRequest
            Require (Wait-Exit $instanceC 20) '提权请求进程未退出'
            Require ($instanceC.ExitCode -eq 4) ('提权请求退出码为 ' + $instanceC.ExitCode + '（预期 4）')
        }
        else { throw '管理员操作既未直接执行也未记录提权请求' }
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
        Require ($record.operation -eq '--settings') ('提权操作参数被改写：' + $record.operation)
        Require (-not $instanceA.HasExited) '提权操作影响了实例 A'
        Pass 'elevated-entry' @{ branch = $branch; report = $recordPath; operation = $record.operation }
        if (-not $instanceC.HasExited) { $instanceC.Kill($true); [void]$instanceC.WaitForExit(5000) }
    }
    catch { Fail 'single-instance-and-elevated' $_.Exception.Message }
    finally { if ($instanceA -and -not $instanceA.HasExited) { $instanceA.Kill($true); [void]$instanceA.WaitForExit(5000) } }

    # 4) 明暗主题真实窗口渲染（窗口会短暂显示一次；证据来自真实渲染）
    try {
        $theme = New-QaDirectory 'theme'
        $process = Start-Prototype -Executable $exe -Arguments @() -QaDirectory $theme -QaMode 'theme'
        Require (Wait-Exit $process) '主题渲染未在超时内退出'
        Require ($process.ExitCode -eq 0) ('主题渲染退出码为 ' + $process.ExitCode)
        foreach ($name in @('theme-light.png', 'theme-dark.png')) {
            $path = Join-Path $theme $name
            Require (Test-Path -LiteralPath $path) ('缺少渲染证据：' + $name)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($name + ' 内容过小，疑似空白渲染')
        }
        Pass 'theme-render' @{ light = (Join-Path $theme 'theme-light.png'); dark = (Join-Path $theme 'theme-dark.png') }
    }
    catch { Fail 'theme-render' $_.Exception.Message }

    # 5) 安装器复制与回滚文件布局镜像（不安装：在证据目录复现 install.ps1 的文件布局子集）
    try {
        $layout = New-QaDirectory 'layout'
        $bundleTray = Join-Path $layout 'bundle/tray'
        New-Item -ItemType Directory -Force -Path $bundleTray | Out-Null
        Copy-Item -LiteralPath $exe -Destination (Join-Path $bundleTray 'AutoNet.Tray.exe')
        $bundleService = Join-Path $layout 'bundle/service'
        New-Item -ItemType Directory -Force -Path $bundleService | Out-Null
        $currentServiceExe = Join-Path $repo 'artifacts/AutoNet-win-x64/service/AutoNet.Service.exe'
        if (Test-Path -LiteralPath $currentServiceExe) { Copy-Item -LiteralPath $currentServiceExe -Destination $bundleService }
        # install.ps1 的发布前置检查：bundle 必须含 tray/AutoNet.Tray.exe 与 service/AutoNet.Service.exe
        Require (Test-Path -LiteralPath (Join-Path $bundleTray 'AutoNet.Tray.exe')) 'bundle 布局缺少 tray/AutoNet.Tray.exe'
        Require (Test-Path -LiteralPath (Join-Path $bundleService 'AutoNet.Service.exe')) 'bundle 布局缺少 service/AutoNet.Service.exe（0.2.1 发布包缺失）'

        # 与 install.ps1 相同的安装目标路径与备份布局（.rollback-<guid> + manifest.json + 随机备份名）
        $destination = Join-Path $layout 'ProgramFiles/AutoNet/tray'
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        $installed = Join-Path $destination 'AutoNet.Tray.exe'
        $oldSource = Join-Path $repo 'artifacts/AutoNet-win-x64/tray/AutoNet.Tray.exe'
        $oldKind = 'current-release'
        if (-not (Test-Path -LiteralPath $oldSource)) {
            $oldSource = Join-Path $layout 'old-version-standin.bin'
            [IO.File]::WriteAllBytes($oldSource, [byte[]](0..255))
            $oldKind = 'synthetic-standin'
        }
        Copy-Item -LiteralPath $oldSource -Destination $installed
        $backup = Join-Path (Split-Path $destination -Parent) ('.rollback-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $backup | Out-Null
        $saved = Join-Path $backup ([Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $installed -Destination $saved
        @{ files = @{ $installed = $saved }; wasRunning = $false; previousAutostart = $null } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding utf8

        $oldHash = (Get-FileHash -LiteralPath $oldSource -Algorithm SHA256).Hash
        Copy-Item -LiteralPath (Join-Path $bundleTray 'AutoNet.Tray.exe') -Destination $installed -Force
        $candidateHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        $installedHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        Require ($installedHash -eq $candidateHash) '复制到安装目标路径后哈希与候选不符'
        Copy-Item -LiteralPath $saved -Destination $installed -Force
        $restoredHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        Require ($restoredHash -eq $oldHash) '回滚未恢复旧文件字节'
        Pass 'installer-layout' @{
            bundle = $layout; oldVersionSource = $oldKind
            candidateSha256 = $candidateHash; restoredSha256 = $restoredHash
            notExecuted = @('服务停止/启动', 'ACL 设置', 'HKLM 自启写入', '快捷方式', '真实安装')
        }
    }
    catch { Fail 'installer-layout' $_.Exception.Message }

    # 6) 服务与已安装托盘未被触碰（只读核对）
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
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('兼容原型 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
