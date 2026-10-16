#requires -Version 7.0
# 设置页与设置编辑器 QA（迁移工单 #8 / T07）。
# 覆盖：普通界面（故事 34）状态矩阵下的只读投影与不可信降级、修改入口逐字记录 --settings 提权意图；
# 提权编辑器（故事 35–39）逐场景驱动既有保存规则：首次设置必须密码、账号未变密码留空保留旧密码、
# 换账号必须新密码、校验失败零写入、存储失败只披露异常类型、DPAPI 密文不落明文（脚本侧解密核对）、
# 认证暂停标记逐字节保持、真实编辑窗口与取消零写入、真实编辑窗口截图。
# 边界：不安装、不替换、不重启服务；夹具只写 QA 证据目录内的隔离目录，
# 绝不触碰本机真实 Storage.Private / credentials.dpapi / auth-block.txt，也不发起真实网络请求；
# 真实 UAC 点击与拒绝（人工）、本机真实受限目录下的编辑（人工）不在本脚本范围内。
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $repo 'artifacts/validation/ui-avalonia-settings'
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
function Wait-Exit($Process, [int]$TimeoutSeconds = 120) { return $Process.WaitForExit($TimeoutSeconds * 1000) }
function New-QaDirectory([string]$Name) {
    $path = Join-Path $evidence $Name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
function Get-EditorStep($Report, [string]$Name) {
    $match = @($Report.EditorSteps | Where-Object Scenario -eq $Name)
    Require ($match.Count -eq 1) ('编辑器场景缺失或重复：' + $Name)
    return $match[0]
}
function Get-ShellStep($Report, [string]$State) {
    $match = @($Report.Steps | Where-Object State -eq $State)
    Require ($match.Count -eq 1) ('设置页步骤缺失或重复：' + $State)
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

# 本机真实受限存储的指纹：设置 QA 必须全程保持逐字节不变（凭据、设置、认证暂停、审计）。
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

# 脚本侧 DPAPI 解密（与 Core Storage 同逻辑，仅作用于夹具文件）：核验保留/变更凭据规则的最终密文。
function Read-FixtureCredentials([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $encrypted = [IO.File]::ReadAllBytes($Path)
    $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $encrypted, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    try {
        $text = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        return @{ username = $text.Username; password = $text.Password }
    }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

try {
    $service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    $serviceBefore = if ($service) { @{ status = [string]$service.Status; processId = (Get-CimInstance Win32_Service -Filter "Name='AutoNet'").ProcessId } } else { @{ status = 'absent'; processId = 0 } }
    $installedTrayBefore = Get-InstalledTrayPids
    $privateBefore = Get-RealPrivateFingerprint

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

    # 2) 普通界面（故事 34）：状态矩阵下的只读投影、不可信降级与修改入口意图记录
    try {
        $shellDir = New-QaDirectory 'shell'
        $statusPath = Join-Path $shellDir 'status.json'
        @{ Enabled = $true; UpdatedAt = [DateTimeOffset]::Now.ToString('o'); State = '校园网在线'; Detail = ''
           Direct = '外网可达'; Wifi = 'TEST-AP-1'; Portal = '未查询（外网检查已通过）'; Tunnel = '系统路径可达，出口未核验'
           Uu = '未核验'; Attempts = 0; DisconnectAttempts = 0; AuthenticationStage = 'Unknown'; AuthenticationFailure = 'None'
           AuthenticationPersistentState = 'Ready' } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statusPath -Encoding utf8
        $process = Start-App $exe @() $shellDir 'settings' $statusPath
        $reportPath = Join-Path $shellDir 'settings.json'
        Require (Wait-ForFile $reportPath 60) '设置页 QA 报告未生成'
        Require (Wait-Exit $process 120) '设置页 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('设置页 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture') '夹具模式标记不符'
        Require ($null -eq $report.Error) ('设置页 QA 报告包含错误：' + $report.Error)
        Require ($report.MutexName -eq 'Local\AutoNet.Tray.Avalonia') ('互斥名不符：' + $report.MutexName)

        foreach ($state in @('online', 'online-disabled', 'online-suspended')) {
            $step = Get-ShellStep $report $state
            Require ($step.Trusted) ($state + ' 应为可信投影')
            Require ($step.RecoveryState -in @('已开启', '已关闭')) ($state + ' 开关状态不符：' + $step.RecoveryState)
            Require ($step.GuardedNetwork.Length -gt 0) ($state + ' 缺少守护网络')
            Require ($step.PortalLine -eq 'https://login.csust.edu.cn  ·  Dr.COM') ($state + ' 认证地址不符')
            Require ($step.ModifyLabel -eq '修改设置…') ($state + ' 修改入口标签不符')
        }
        $online = Get-ShellStep $report 'online'
        Require ($online.RecoveryState -eq '已开启') '在线夹具应显示已开启'
        Require ($online.GuardedNetwork -eq 'TEST-AP-1') '守护网络应按快照投影'
        $disabled = Get-ShellStep $report 'online-disabled'
        Require ($disabled.RecoveryState -eq '已关闭') '关闭夹具应显示已关闭'

        foreach ($state in @('stale', 'invalid', 'missing')) {
            $step = Get-ShellStep $report $state
            Require (-not $step.Trusted) ($state + ' 应为不可信降级')
            Require ($step.RecoveryState -eq '待确认') ($state + ' 未显示待确认：' + $step.RecoveryState)
            Require ($step.GuardedNetwork -eq '不可确认') ($state + ' 未显示不可确认')
            Require ($step.ModifyLabel -eq '修改设置…') ($state + ' 修改入口标签不符（入口不依赖状态可信度）')
        }

        Require ($report.Actions.Count -eq 1) ('修改入口动作记录数不符：' + $report.Actions.Count)
        $modify = $report.Actions[0]
        Require ($modify.Launched -and $modify.Command -eq '--settings' -and $modify.Expected -eq '--settings') ('修改入口意图不符：' + $modify.Command)

        foreach ($shot in @('settings-online', 'settings-online-disabled', 'settings-online-suspended',
            'settings-stale', 'settings-invalid', 'settings-missing')) {
            $path = Join-Path $shellDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少设置页截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }
        Pass 'shell-readonly' @{ report = $reportPath; steps = $report.Steps; actions = $report.Actions }
    }
    catch { Fail 'shell-readonly' $_.Exception.Message }

    # 3) 提权编辑器（故事 35–39）：--settings 提权入口 + QA 隔离夹具逐场景
    try {
        $editorDir = New-QaDirectory 'editor'
        $process = Start-App $exe @('--settings') $editorDir 'settings'
        $reportPath = Join-Path $editorDir 'settings.json'
        Require (Wait-ForFile $reportPath 60) '编辑器 QA 报告未生成'
        Require (Wait-Exit $process 180) '编辑器 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('编辑器 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture') '夹具模式标记不符'
        Require ($null -eq $report.Error) ('编辑器 QA 报告包含错误：' + $report.Error)
        Require ($report.Administrator) '编辑器 QA 必须在提权路径内运行'
        Require ($report.MutexName -eq 'Local\AutoNet.Tray.Avalonia') ('互斥名不符：' + $report.MutexName)

        $fixture = Join-Path $editorDir 'settings-fixture'

        # 故事 35+36：首次设置成功，设置与 DPAPI 密文产生，密文无明文，认证暂停保持。
        $first = Get-EditorStep $report 'first-setup'
        Require ($first.Outcome -eq 'Saved') ('首次设置结果不符：' + $first.Outcome)
        Require ($first.Message -eq '已保存。后台服务将在下一轮检测中使用新设置。') ('首次设置提示不符：' + $first.Message)
        Require ($first.SettingsWritten -and $first.CredentialsWritten -and $first.CredentialsCreated) '首次设置应产生设置与凭据文件'
        Require ($first.CredentialPlaintextFree) '首次设置凭据密文出现明文（故事 36）'
        Require ($first.BlockUntouched) '首次设置改写了认证暂停标记（故事 39）'
        $kept = Read-FixtureCredentials (Join-Path $fixture 'first-setup/credentials.dpapi')
        Require ($kept.username -eq 'TEST-USER-1' -and $kept.password -eq 'TEST-PASSWORD-1') '首次设置的凭据密文无法按 DPAPI 解密还原'

        # 故事 37：账号未变且密码留空 → 保留已保存密码（密文重新加密属正常写入；
        # 设置为同值重写，字节本就相同，不作为写入证据）。
        $keep = Get-EditorStep $report 'keep-password'
        Require ($keep.Outcome -eq 'Saved') ('保留密码场景结果不符：' + $keep.Outcome)
        Require ($keep.CredentialsWritten) '保留密码场景应重写凭据密文'
        Require ($keep.CredentialPlaintextFree) '保留密码场景凭据密文出现明文'
        $keepCreds = Read-FixtureCredentials (Join-Path $fixture 'keep-password/credentials.dpapi')
        Require ($keepCreds.username -eq 'TEST-USER-1' -and $keepCreds.password -eq 'TEST-PASSWORD-1') '留空密码未保留已保存密码（故事 37）'

        # 故事 37：换账号且密码留空 → 拒绝且凭据、设置字节不变。
        $required = Get-EditorStep $report 'change-account-required'
        Require ($required.Outcome -eq 'PasswordRequired') ('换账号场景结果不符：' + $required.Outcome)
        Require ($required.Message -eq '首次设置或更改账号时，请输入校园网密码。') ('换账号提示不符：' + $required.Message)
        Require (-not $required.SettingsWritten -and -not $required.CredentialsWritten) '换账号被拒时不应有任何写入'

        # 故事 37：换账号并提供新密码 → 凭据更新为新账号新密码。
        $changed = Get-EditorStep $report 'change-account-saved'
        Require ($changed.Outcome -eq 'Saved') ('换账号保存场景结果不符：' + $changed.Outcome)
        Require ($changed.CredentialsWritten) '换账号场景应重写凭据密文'
        Require ($changed.CredentialPlaintextFree) '换账号场景凭据密文出现明文'
        $changedCreds = Read-FixtureCredentials (Join-Path $fixture 'change-account-saved/credentials.dpapi')
        Require ($changedCreds.username -eq 'TEST-USER-2' -and $changedCreds.password -eq 'TEST-PASSWORD-2') '换账号后凭据未更新（故事 37）'

        # 校验失败：零写入（取消不写入的镜像，故事 38）。
        $invalid = Get-EditorStep $report 'validation-failed'
        Require ($invalid.Outcome -eq 'InvalidSettings') ('校验失败场景结果不符：' + $invalid.Outcome)
        Require (-not $invalid.SettingsWritten -and -not $invalid.CredentialsWritten) '校验失败时不应有任何写入'

        # 存储失败：只披露异常类型，不谎报成功（安全边界）。
        $store = Get-EditorStep $report 'store-failed'
        Require ($store.Outcome -eq 'StoreFailed') ('存储失败场景结果不符：' + $store.Outcome)
        Require ($store.Message.Contains('IOException') -and -not $store.Message.Contains('夹具注入')) ('存储失败提示不符：' + $store.Message)
        Require (-not $store.SettingsWritten) '设置写入失败时设置文件不应变化'
        Require ($store.BlockUntouched) '存储失败改写了认证暂停标记'

        # 取消路径：真实窗口展示与关闭零写入（故事 38）。
        $cancel = Get-EditorStep $report 'window-cancel'
        Require ($cancel.Outcome -eq 'Declined') ('取消场景结果不符：' + $cancel.Outcome)
        Require (-not $cancel.SettingsWritten -and -not $cancel.CredentialsWritten) '取消时窗口路径产生了写入（故事 38）'
        Require ($cancel.BlockUntouched) '取消场景改写了认证暂停标记'

        # 保存成功反馈：窗口内显示「已保存」确认文案（与既有表单的 MessageBox 确认同语义），
        # 凭据按保留规则重写（账号未变留空密码）。
        $saved = Get-EditorStep $report 'window-saved'
        Require ($saved.Outcome -eq 'Saved') ('保存反馈场景结果不符：' + $saved.Outcome)
        Require ($saved.CredentialsWritten) '保存反馈场景凭据未按保留规则重写'
        Require ($saved.CredentialPlaintextFree) '保存反馈场景凭据密文出现明文'
        Require ($saved.BlockUntouched) '保存反馈场景改写了认证暂停标记'

        # 全场景不变式：认证暂停标记逐字节保持（故事 39 / T07）。
        foreach ($step in $report.EditorSteps) {
            Require ($step.BlockUntouched) ('保存路径改写了认证暂停标记：' + $step.Scenario)
        }

        # 截图：编辑窗口、窗口内校验说明、保存成功反馈、private 缺失提示均来自真实窗口捕获且非空白
        # （缺失提示是小窗口，正常渲染约 6KB；窗口截图必须显著大于该值）。
        foreach ($shot in @('settings-editor-window', 'settings-editor-window-password-required', 'settings-editor-window-saved')) {
            $path = Join-Path $editorDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少编辑器截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }
        $missingStore = Join-Path $editorDir 'settings-editor-missing-store.png'
        Require (Test-Path -LiteralPath $missingStore) '缺少 private 缺失提示截图'
        Require ((Get-Item -LiteralPath $missingStore).Length -gt 3KB) 'private 缺失提示内容过小，疑似空白渲染'

        # 明文扫描：夹具目录内不得出现任何密码明文文件（故事 36）。
        $plaintextHits = @(Get-ChildItem -LiteralPath $fixture -Recurse -File |
            Where-Object { $_.Name -ne 'credentials.dpapi' -and (Select-String -LiteralPath $_.FullName -Pattern 'TEST-PASSWORD' -SimpleMatch -Quiet) })
        Require ($plaintextHits.Count -eq 0) ('夹具内出现密码明文文件：' + (($plaintextHits | ForEach-Object Name) -join '、'))

        Pass 'editor-rules' @{ report = $reportPath; steps = $report.EditorSteps }
    }
    catch { Fail 'editor-rules' $_.Exception.Message }

    # 4) 本机真实受限存储逐字节未被触碰（只读核对）
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
        Pass 'real-private-untouched' $privateAfter
    }
    catch { Fail 'real-private-untouched' $_.Exception.Message }

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
        deferred = @('真实 UAC 点击与拒绝（人工，需单独授权）',
            '本机真实受限目录下的设置编辑（人工，需单独授权）',
            '明暗主题（#9）', '真实安装/回退（#15/#16）')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('设置页 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
