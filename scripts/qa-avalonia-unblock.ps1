#requires -Version 7.0
# 解除认证暂停 QA（迁移工单 #5 / T08）。
# 覆盖：自包含 win-x64 单文件发布契约、提权入口 --unblock-auth 逐场景完整序列
# （无标记不确认 / 用户取消 / 前置复核未通过 / 诊断期间设置变化 / 诊断期间校园 Wi-Fi 变化 /
#  成功解除），每个场景的阶段序列与「认证阻断标记 + 审计 jsonl + 设置文件」落盘结果、
# 确认对话框与受控提示窗口的真实窗口截图、解除全程零凭据请求与零凭据文件、
# 后台服务与已安装托盘未被触碰。
# 边界：不安装、不替换、不重启服务；夹具只写 QA 证据目录内的隔离目录，
# 绝不触碰本机真实 Storage.Private / auth-block.txt，也不发起真实门户请求；
# 真实 UAC 点击（人工）与真实受限目录下的解除（人工）不在本脚本范围内。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-unblock'
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
function Wait-ForFile([string]$Path, [int]$TimeoutSeconds = 25) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { Start-Sleep -Milliseconds 250; return $true }
        Start-Sleep -Milliseconds 120
    }
    return $false
}
function Get-Step($Report, [string]$Name) {
    $match = @($Report.Steps | Where-Object Scenario -eq $Name)
    Require ($match.Count -eq 1) ('解除场景缺失或重复：' + $Name)
    return $match[0]
}
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

# 本机真实安全状态的指纹：解除 QA 必须在全程保持逐字节不变。
function Get-RealSafetyFingerprint {
    $private = Join-Path $env:ProgramData 'AutoNet/private'
    $files = @('auth-block.txt', 'auth-release-audit.jsonl', 'settings.json')
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

try {
    $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    $serviceBefore = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
    $installedTrayBefore = Get-InstalledTrayPids
    $safetyBefore = Get-RealSafetyFingerprint

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

    # 2) 解除认证暂停逐场景序列（--unblock-auth 提权入口 + QA 隔离夹具）
    try {
        $unblockDir = Join-Path $evidence 'unblock'
        if (Test-Path -LiteralPath $unblockDir) { Remove-Item -LiteralPath $unblockDir -Recurse -Force }
        New-Item -ItemType Directory -Path $unblockDir | Out-Null

        [Environment]::SetEnvironmentVariable('AUTONET_QA_DIR', $unblockDir)
        [Environment]::SetEnvironmentVariable('AUTONET_QA_MODE', 'unblock')
        [Environment]::SetEnvironmentVariable('AUTONET_QA_STATUS_PATH', '')
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.ArgumentList.Add('--unblock-auth')
        $process = [Diagnostics.Process]::Start($start)
        $tracked.Add($process)

        $reportPath = Join-Path $unblockDir 'unblock.json'
        Require (Wait-ForFile $reportPath 90) '解除 QA 报告未生成'
        Require ($process.WaitForExit(180000)) '解除 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('解除 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture') '夹具模式标记不符'
        Require ($null -eq $report.Error) ('解除 QA 报告包含错误：' + $report.Error)
        Require ($report.Steps.Count -eq 6) ('场景数不符：' + $report.Steps.Count)
        # 提权入口独立于普通托盘互斥锁（故事 8）：报告里的互斥名必须是迁移期独立名。
        Require ($report.MutexName -eq 'Local\AutoNet.Tray.Avalonia') ('互斥名不符：' + $report.MutexName)

        # 通用不变式：解除全程不得出现凭据请求，也不得创建凭据文件（故事 23 交叉）。
        foreach ($step in $report.Steps) {
            Require (-not $step.CredentialFileCreated) ('解除流程创建了凭据文件：' + $step.Scenario)
            Require (@($step.PortalRequestPaths | Where-Object { $_ -like '*CREDENTIAL*' }).Count -eq 0) ('解除流程出现凭据请求：' + $step.Scenario)
            # settings-changed 场景由 QA 夹具在诊断期间主动改写设置（这正是被核对出来的那种变化），
            # 因此只有它允许设置文件变化；其余场景必须证明解除流程自身从不写设置。
            if ($step.Scenario -ne 'settings-changed') {
                Require ($step.SettingsFileUntouched) ('解除流程改写了已保存设置：' + $step.Scenario)
            }
            # 到达过复核阶段的场景必须真的发起过只读请求；未到达的（无标记、用户取消）
            # 反而必须一次请求都没有——那正是「不确认就不复核」的证据。
            $reachedRecheck = @($step.Stages) -contains 'Recheck'
            if ($reachedRecheck) { Require (@($step.PortalRequestPaths).Count -gt 0) ('解除流程没有发起任何只读请求：' + $step.Scenario) }
            else { Require (@($step.PortalRequestPaths).Count -eq 0) ('未确认却发起了只读请求：' + $step.Scenario) }
        }

        # 无待解除标记：不得进入确认、不得复核、不得解除、不得写任何文件。
        $noBlock = Get-Step $report 'no-block'
        Require ($noBlock.Outcome -eq 'NoBlock') ('无标记场景结果不符：' + $noBlock.Outcome)
        Require ($noBlock.ConfirmAnswer -eq $null) '无标记时不应出现确认答案'
        Require (@($noBlock.Stages).Count -eq 0) ('无标记时不应进入任何阶段：' + (@($noBlock.Stages) -join ','))
        Require (-not $noBlock.BlockFileBefore -and -not $noBlock.BlockFileAfter) '无标记时不应存在认证阻断标记'
        Require (-not $noBlock.AuditWritten) '无标记时不应写审计行'

        # 用户取消：只走确认阶段，保留暂停且零写入。
        $declined = Get-Step $report 'declined'
        Require ($declined.Outcome -eq 'Declined') ('取消场景结果不符：' + $declined.Outcome)
        Require (@($declined.Stages) -join ',' -eq 'Confirm') ('取消场景阶段序列不符：' + (@($declined.Stages) -join ','))
        Require ($declined.ConfirmAnswer -eq '取消') '取消场景未记录取消答案'
        Require ($declined.BlockFileBefore -and $declined.BlockFileAfter) '取消后认证暂停未保留'
        Require (-not $declined.AuditWritten) '取消后不应写审计行'
        Require (-not $declined.RequiresNotice) '用户自己取消不应再弹受控说明'

        # 前置复核未通过：保留暂停 + 受控说明，无审计行（故事 42）。
        $recheck = Get-Step $report 'recheck-failed'
        Require ($recheck.Outcome -eq 'RecheckFailed') ('复核场景结果不符：' + $recheck.Outcome)
        Require (@($recheck.Stages) -join ',' -eq 'Confirm,Recheck') ('复核场景阶段序列不符：' + (@($recheck.Stages) -join ','))
        Require ($recheck.BlockFileBefore -and $recheck.BlockFileAfter) '复核未通过时认证暂停未保留'
        Require (-not $recheck.AuditWritten) '复核未通过不应写审计行'
        Require ($recheck.RequiresNotice) '复核未通过必须给出受控说明'
        Require ($recheck.Message.Contains('前置复核未通过，保留认证暂停')) ('受控说明缺少保留暂停的结论：' + $recheck.Message)
        Require ($recheck.Heading -eq '前置复核未通过，保留认证暂停') ('受控标题不符：' + $recheck.Heading)

        # 诊断期间事实变化：中止并保留暂停，绝不按过期事实解除。
        foreach ($name in @('settings-changed', 'link-changed')) {
            $changed = Get-Step $report $name
            Require ($changed.Outcome -eq 'RevisionChanged') ($name + ' 场景结果不符：' + $changed.Outcome)
            Require (@($changed.Stages) -join ',' -eq 'Confirm,Recheck,RevisionCheck') ($name + ' 阶段序列不符：' + (@($changed.Stages) -join ','))
            Require (-not (@($changed.Stages) -contains 'Audit')) ($name + ' 中止后不应进入审计解除')
            Require ($changed.BlockFileAfter) ($name + ' 中止后认证暂停未保留')
            Require (-not $changed.AuditWritten) ($name + ' 中止后不应写审计行')
        }

        # 成功路径：四段序列完整、审计先落盘、标记后删除。
        $released = Get-Step $report 'released'
        Require ($released.Released) ('成功场景未解除：' + $released.Outcome)
        Require (@($released.Stages) -join ',' -eq 'Confirm,Recheck,RevisionCheck,Audit') ('成功场景阶段序列不符：' + (@($released.Stages) -join ','))
        Require ($released.BlockFileBefore -and -not $released.BlockFileAfter) '解除后标记文件应被删除'
        Require ($released.AuditWritten -and $released.AuditLines -eq 1) ('解除应留下恰好一行审计：' + $released.AuditLines)
        Require (-not $released.RequiresNotice) '成功应保持既有静默语义'

        # 截图：确认对话框 + 受控提示窗口均来自真实窗口捕获且非空白。
        $shots = @('unblock-released-confirm', 'unblock-recheck-failed-notice',
            'unblock-settings-changed-confirm', 'unblock-settings-changed-notice',
            'unblock-link-changed-confirm', 'unblock-link-changed-notice')
        foreach ($shot in $shots) {
            $path = Join-Path $unblockDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少解除截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }

        # 夹具目录审计文件逐行核对：解除动作与解除前状态事实都被持久记录。
        $auditPath = Join-Path $unblockDir 'unblock-fixture/released/auth-release-audit.jsonl'
        Require (Test-Path -LiteralPath $auditPath) '成功场景缺少审计文件'
        $audit = @(Get-Content -LiteralPath $auditPath | Where-Object { $_.Trim().Length -gt 0 })
        Require ($audit.Count -eq 1) ('审计行数不符：' + $audit.Count)
        $entry = $audit[0] | ConvertFrom-Json
        Require ($entry.Action -eq '人工确认解除认证暂停') ('审计动作不符：' + $entry.Action)
        Require ($entry.State -eq 'BindingRejected' -and $entry.Failure -eq 'BindingRejected') '审计缺少解除前的状态事实'
        Require (-not $entry.PSObject.Properties['Password']) '审计行出现了密码字段'

        # 除成功场景外，其余场景目录都不得留下审计文件。
        foreach ($name in @('no-block', 'declined', 'recheck-failed', 'settings-changed', 'link-changed')) {
            $path = Join-Path $unblockDir ('unblock-fixture/' + $name + '/auth-release-audit.jsonl')
            Require (-not (Test-Path -LiteralPath $path)) ($name + ' 场景不应留下审计文件')
            Require (-not (Test-Path -LiteralPath (Join-Path $unblockDir ('unblock-fixture/' + $name + '/credentials.dpapi')))) ($name + ' 场景不应出现凭据文件')
        }

        Pass 'unblock-fixture' @{ report = $reportPath; steps = $report.Steps }
    }
    catch { Fail 'unblock-fixture' $_.Exception.Message }

    # 3) 本机真实安全状态逐字节未被触碰（只读核对）
    try {
        $safetyAfter = Get-RealSafetyFingerprint
        foreach ($file in @('auth-block.txt', 'auth-release-audit.jsonl', 'settings.json')) {
            $before = $safetyBefore[$file]
            $after = $safetyAfter[$file]
            Require ($before.exists -eq $after.exists) ($file + ' 存在性发生变化')
            if ($before.exists) {
                Require ($before.hash -eq $after.hash) ($file + ' 内容发生变化')
                Require ($before.length -eq $after.length) ($file + ' 长度发生变化')
                Require ($before.modified -eq $after.modified) ($file + ' 修改时间发生变化')
            }
        }
        Pass 'real-safety-untouched' $safetyAfter
    }
    catch { Fail 'real-safety-untouched' $_.Exception.Message }

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
        deferred = @('真实 UAC 点击与拒绝（人工，需单独授权）',
            '本机真实受限目录下的解除（人工，需存在待解除阻断）',
            '明暗主题（#9）', '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('解除认证暂停 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
