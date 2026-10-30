#requires -Version 7.0
# 设置页与设置编辑器 QA（迁移工单 #8 / #41）。
# 覆盖：主窗口设置页（#41）状态矩阵下的只读事实与非秘密配置投影、不可信降级，
# 以及十一条保存路径：首次设置、同账号留空保留旧密码、换账号必须新密码、校验失败交接前拒绝、
# 取消/拒绝授权零写入、结果未知不宣称零写入、存储失败如实报告凭据部分写入；密码清除与不外泄、
# 认证暂停标记逐字节保持。提权编辑器（故事 35–39，兼容入口 --settings）逐场景驱动既有保存规则。
# 边界：不安装、不替换、不重启服务；夹具只写 QA 证据目录内的隔离目录，
# 绝不触碰本机真实 Storage.Private / credentials.dpapi / auth-block.txt，也不发起真实网络请求；
# 不停止已安装托盘或其他工作树的 QA 进程：改用证据目录派生的 AUTONET_QA_MUTEX_SUFFIX 取得独立互斥。
# 真实 UAC 点击与拒绝（人工）、本机真实受限目录下的编辑（人工）不在本脚本范围内；
# 提权编辑器逐场景 QA 需要提权提示符，普通上下文只验证提权入口记录并标记 NOT_RUN。
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
foreach ($name in @('AUTONET_QA_DIR', 'AUTONET_QA_MODE', 'AUTONET_QA_STATUS_PATH', 'AUTONET_QA_MUTEX_SUFFIX')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
# 本机可能仍运行旧预览版安装（其正式实例占用固定 QA 互斥名）或并行工作树的 QA 进程；
# 用证据目录派生的受控后缀取得本次运行的独立互斥身份，绝不停止已安装托盘或其他工作树进程。
$mutexSuffix = 'settings-' + [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($evidence))).Substring(0, 12).ToLowerInvariant()
[Environment]::SetEnvironmentVariable('AUTONET_QA_MUTEX_SUFFIX', $mutexSuffix)
$expectedMutex = 'Local\AutoNet.Tray.Avalonia.' + $mutexSuffix

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
    $installedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'AutoNet'))
    return @(Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($installedRoot, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { $_.Id })
}

# 只清理本工作树 artifacts/validation 下的迁移进程，绝不触碰 C:\Program Files\AutoNet 下已安装的托盘，
# 也不触碰其他工作树（并行工单）的 QA 进程。其他工作树的迁移进程共用 Local\AutoNet.Tray.Avalonia 互斥，
# 若它仍在运行，本次设置页 QA 会得到 already-running 证据而不是报告；因此这里显式列出占用者。
$validationRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/validation')) + [IO.Path]::DirectorySeparatorChar
Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
Start-Sleep -Milliseconds 200
$foreignQa = @(Get-Process -Name 'AutoNet.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and -not [IO.Path]::GetFullPath($_.Path).StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not [IO.Path]::GetFullPath($_.Path).StartsWith((Join-Path $env:ProgramFiles 'AutoNet'), [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { $_.Id.ToString() + ':' + $_.Path })
if ($foreignQa.Count -gt 0) {
    Write-Host ('WARN 检测到其他位置的迁移进程（可能来自并行工作树），共享 QA 互斥：' + ($foreignQa -join '；'))
}
$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

# 本机真实受限存储的指纹：设置 QA 必须全程保持逐字节不变（凭据、设置、认证暂停、审计）。
# 普通权限进程无法读取仅管理员可访问的 private 目录时，明确返回“未验证”，绝不谎报已核对。
function Get-RealPrivateFingerprint {
    $private = Join-Path $env:ProgramData 'AutoNet/private'
    $files = @('settings.json', 'credentials.dpapi', 'auth-block.txt', 'auth-release-audit.jsonl')
    $snapshot = [ordered]@{ accessible = $false; reason = '' }
    try {
        if (-not (Test-Path -LiteralPath $private)) { $snapshot.reason = 'PrivateDirectoryMissing'; return $snapshot }
        $snapshot.accessible = $true
        foreach ($file in $files) {
            $path = Join-Path $private $file
            if (Test-Path -LiteralPath $path) {
                $item = Get-Item -LiteralPath $path
                $snapshot[$file] = @{ exists = $true; length = $item.Length; hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; modified = $item.LastWriteTimeUtc.ToString('o') }
            }
            else { $snapshot[$file] = @{ exists = $false } }
        }
    }
    catch [System.UnauthorizedAccessException] {
        $snapshot = [ordered]@{ accessible = $false; reason = 'AccessDenied' }
    }
    catch [System.IO.IOException] {
        $snapshot = [ordered]@{ accessible = $false; reason = 'ReadFailed' }
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

    # 2) 主窗口设置页（#41）：状态矩阵下的只读事实与非秘密配置投影、真实表单，
    #    以及十一条保存路径（首次设置、保留密码、换账号、校验失败、取消授权、结果未知、存储失败、保留原值等）。
    try {
        $shellDir = New-QaDirectory 'shell'
        $statusPath = Join-Path $shellDir 'status.json'
        @{ Enabled = $true; UpdatedAt = [DateTimeOffset]::Now.ToString('o'); State = '校园网在线'; Detail = ''
           Direct = '外网可达'; Wifi = 'TEST-AP-1'; Portal = '未查询（外网检查已通过）'; Tunnel = '系统路径可达，出口未核验'
           Uu = '未核验'; Attempts = 0; DisconnectAttempts = 0; AuthenticationStage = 'Unknown'; AuthenticationFailure = 'None'
           AuthenticationPersistentState = 'Ready'; SettingsProjected = $true
           InterfaceId = '11111111-2222-3333-4444-555555555555'; WifiProfile = 'TEST-AP-1'
           ProxyUrlDisplay = 'http://127.0.0.1:7897'; ProxyUrlRedacted = $false
           UuEndpointDisplay = 'https://uu.example.test'; UuEndpointRedacted = $false
           AllowActiveDisconnect = $true; KeepAwakeOnAc = $true; AuthChannelMode = 'ManualOnly' } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statusPath -Encoding utf8
        $process = Start-App $exe @() $shellDir 'settings' $statusPath
        $reportPath = Join-Path $shellDir 'settings.json'
        Require (Wait-ForFile $reportPath 60) '设置页 QA 报告未生成'
        Require (Wait-Exit $process 120) '设置页 QA 运行未在超时内退出'
        Require ($process.ExitCode -eq 0) ('设置页 QA 退出码为 ' + $process.ExitCode)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Require ($report.Mode -eq 'fixture') '夹具模式标记不符'
        Require ($null -eq $report.Error) ('设置页 QA 报告包含错误：' + $report.Error)
        Require ($report.MutexName -eq $expectedMutex) ('互斥名不符：' + $report.MutexName)

        foreach ($state in @('online', 'online-disabled', 'online-suspended', 'online-redacted')) {
            $step = Get-ShellStep $report $state
            Require ($step.Trusted) ($state + ' 应为可信投影')
            Require ($step.RecoveryState -in @('已开启', '已关闭')) ($state + ' 开关状态不符：' + $step.RecoveryState)
            Require ($step.GuardedNetwork.Length -gt 0) ($state + ' 缺少守护网络')
            Require ($step.PortalLine -eq 'https://login.csust.edu.cn  ·  Dr.COM') ($state + ' 认证地址不符')
            Require ($step.ConfigProjected) ($state + ' 未识别后台配置投影')
            Require ($step.WifiProfile -eq 'TEST-AP-1') ($state + ' Wi-Fi 配置投影不符：' + $step.WifiProfile)
            Require ($step.ProxyUrlDisplay -eq 'http://127.0.0.1:7897') ($state + ' 代理展示值不符：' + $step.ProxyUrlDisplay)
            Require ($step.UuEndpointDisplay -eq 'https://uu.example.test') ($state + ' UU 展示值不符：' + $step.UuEndpointDisplay)
            Require ($step.AllowActiveDisconnect -and $step.KeepAwakeOnAc) ($state + ' 电源/断线投影不符')
            Require ($step.AuthChannelMode -eq 'ManualOnly') ($state + ' 通道模式投影不符：' + $step.AuthChannelMode)
            Require ($step.OptionsKnown) ($state + ' 有可信投影时恢复选项应显示为确定值')
            if ($state -eq 'online-redacted') {
                Require ($step.ProxyUrlRedacted -and $step.UuEndpointRedacted) '隐藏地址夹具未标记为已隐藏'
            }
            else {
                Require (-not $step.ProxyUrlRedacted -and -not $step.UuEndpointRedacted) ($state + ' 安全地址被误标为已隐藏')
            }
        }
        $online = Get-ShellStep $report 'online'
        Require ($online.RecoveryState -eq '已开启') '在线夹具应显示已开启'
        Require ($online.GuardedNetwork -eq 'TEST-AP-1') '守护网络应按快照投影'
        Require ($online.Enabled) '在线夹具的自动恢复投影应为开启'
        $disabled = Get-ShellStep $report 'online-disabled'
        Require ($disabled.RecoveryState -eq '已关闭') '关闭夹具应显示已关闭'
        Require (-not $disabled.Enabled) '关闭夹具的可编辑开关投影应为关闭'

        foreach ($state in @('stale', 'invalid', 'missing')) {
            $step = Get-ShellStep $report $state
            Require (-not $step.Trusted) ($state + ' 应为不可信降级')
            Require ($step.RecoveryState -eq '待确认') ($state + ' 未显示待确认：' + $step.RecoveryState)
            Require ($step.GuardedNetwork -eq '不可确认') ($state + ' 未显示不可确认')
            Require (-not $step.ConfigProjected) ($state + ' 仍宣称配置投影可用')
            Require ($step.WifiProfile.Length -eq 0 -and $step.ProxyUrlDisplay.Length -eq 0) ($state + ' 泄露了配置投影')
            Require (-not $step.OptionsKnown) ($state + ' 无可信投影时恢复选项应显示为未确定')
        }

        # 十一条保存路径：结果、密码清除、交接发起、落盘事实与认证暂停保持。
        function Get-SaveStep($Report, [string]$Name) {
            $match = @($Report.SaveSteps | Where-Object Scenario -eq $Name)
            Require ($match.Count -eq 1) ('保存场景缺失或重复：' + $Name)
            return $match[0]
        }
        foreach ($step in $report.SaveSteps) {
            Require (-not $step.SecretInMessage) ('保存结果文本出现密码明文：' + $step.Scenario)
            Require ($step.PasswordCleared) ('交接结束后密码未清除：' + $step.Scenario)
            Require ($step.BlockUntouched) ('保存路径改写了认证暂停标记：' + $step.Scenario)
        }

        $first = Get-SaveStep $report 'page-first-setup'
        Require ($first.State -eq 'Saved') ('首次设置结果不符：' + $first.State)
        Require ($first.HandoffInvoked -and $first.SettingsWritten -and $first.CredentialsWritten) '首次设置应发起交接并写入设置与凭据'
        Require ($first.CredentialPlaintextFree) '首次设置凭据密文出现明文'

        $keep = Get-SaveStep $report 'page-keep-password'
        Require ($keep.State -eq 'Saved') ('保留密码场景结果不符：' + $keep.State)
        Require ($keep.CredentialsWritten -and $keep.CredentialPlaintextFree) '保留密码场景应重写凭据密文且无明文'

        $required = Get-SaveStep $report 'page-change-account-required'
        Require ($required.State -eq 'ValidationFailed') ('换账号场景结果不符：' + $required.State)
        Require ($required.Message.Contains('请输入校园网密码')) ('换账号提示不符：' + $required.Message)
        Require (-not $required.SettingsWritten -and -not $required.CredentialsWritten) '换账号被拒时不应有任何写入'

        $invalid = Get-SaveStep $report 'page-validation-failed'
        Require ($invalid.State -eq 'ValidationFailed') ('校验失败场景结果不符：' + $invalid.State)
        Require (-not $invalid.HandoffInvoked) '校验失败仍发起了提权交接'
        Require (-not $invalid.SettingsWritten -and -not $invalid.CredentialsWritten) '校验失败时不应有任何写入'

        $cancelled = Get-SaveStep $report 'page-uac-cancelled'
        Require ($cancelled.State -eq 'Cancelled') ('取消授权结果不符：' + $cancelled.State)
        Require ($cancelled.Message.Contains('未写入任何设置')) ('取消授权文案不符：' + $cancelled.Message)
        Require (-not $cancelled.SettingsWritten -and -not $cancelled.CredentialsWritten) '取消授权时不应有任何写入'

        $unknown = Get-SaveStep $report 'page-outcome-unknown'
        Require ($unknown.State -eq 'OutcomeUnknown') ('结果未知状态不符：' + $unknown.State)
        Require ($unknown.Message.Contains('结果未知')) ('结果未知文案不符：' + $unknown.Message)
        Require (-not $unknown.Message.Contains('未写入任何设置')) '结果未知被误报为零写入'

        $store = Get-SaveStep $report 'page-store-failed'
        Require ($store.State -eq 'StoreFailed') ('存储失败结果不符：' + $store.State)
        Require (-not $store.SettingsWritten -and $store.CredentialsWritten) '存储失败应如实呈现凭据部分写入'
        Require ($store.Message.Contains('可能')) ('存储失败未说明可能的部分写入：' + $store.Message)

        # 隐藏地址：页面只看到来源；未编辑保存必须保留受限存储中的完整原值。
        $redactedSave = Get-SaveStep $report 'page-keep-redacted-url'
        Require ($redactedSave.State -eq 'Saved') ('隐藏地址保存结果不符：' + $redactedSave.State)
        Require ($redactedSave.KeepProxyUrl -and $redactedSave.KeepUuEndpoint) '隐藏地址场景未提交保留原值标记'
        Require ($redactedSave.ExistingSettingsFound) '隐藏地址场景接收端未读到夹具设置'
        $redactedSettingsPath = Join-Path $shellDir 'settings-page-fixture/page-keep-redacted-url/settings.json'
        Require (Test-Path -LiteralPath $redactedSettingsPath) '隐藏地址场景缺少夹具设置文件'
        $redactedSettings = Get-Content -LiteralPath $redactedSettingsPath -Raw | ConvertFrom-Json
        Require ($redactedSettings.ProxyUrl -eq 'http://127.0.0.1:7897/TEST-URL-SECRET?token=TEST-URL-SECRET') `
            ('未编辑的代理地址被覆盖：' + $redactedSettings.ProxyUrl)
        Require ($redactedSettings.UuEndpoint -eq 'https://uu.example.test/TEST-URL-SECRET') `
            ('未编辑的 UU 地址被覆盖：' + $redactedSettings.UuEndpoint)
        Require (-not (Select-String -LiteralPath $reportPath -Pattern 'TEST-URL-SECRET' -SimpleMatch -Quiet)) `
            '设置页 QA 报告出现地址秘密'
        Require (-not (Select-String -LiteralPath $statusPath -Pattern 'TEST-URL-SECRET' -SimpleMatch -Quiet)) `
            '状态夹具出现地址秘密'

        # 无投影时未编辑的恢复选项：页面提交全部保留标记，接收端保留夹具里的关闭状态。
        $optionsSave = Get-SaveStep $report 'page-keep-options'
        Require ($optionsSave.State -eq 'Saved') ('保留选项保存结果不符：' + $optionsSave.State)
        Require ($optionsSave.KeepEnabled -and $optionsSave.KeepAllowActiveDisconnect -and
            $optionsSave.KeepKeepAwakeOnAc -and $optionsSave.KeepAuthChannelMode) '保留选项场景未提交全部保留标记'
        Require ($optionsSave.ExistingSettingsFound) '保留选项场景接收端未读到夹具设置'
        Require ($null -ne $optionsSave.EnabledAfter) '保留选项场景缺少落盘事实'
        Require (-not $optionsSave.EnabledAfter -and -not $optionsSave.AllowActiveDisconnectAfter -and
            -not $optionsSave.KeepAwakeOnAcAfter) '未编辑的恢复选项被页面默认值重新开启'
        Require ($optionsSave.AuthChannelModeAfter -eq 'Off') ('未编辑的通道模式被覆盖：' + $optionsSave.AuthChannelModeAfter)

        # 未选择 Wi-Fi：接收端保留受限存储中的守护目标，而不是页面默认选择。
        $profileSave = Get-SaveStep $report 'page-keep-profile'
        Require ($profileSave.State -eq 'Saved') ('保留守护目标保存结果不符：' + $profileSave.State)
        Require ($profileSave.KeepProfile) '保留守护目标场景未提交 KeepProfile 标记'
        Require ($profileSave.ExistingSettingsFound) '保留守护目标场景接收端未读到夹具设置'
        $profileSettingsPath = Join-Path $shellDir 'settings-page-fixture/page-keep-profile/settings.json'
        Require (Test-Path -LiteralPath $profileSettingsPath) '保留守护目标场景缺少夹具设置文件'
        $profileSettings = Get-Content -LiteralPath $profileSettingsPath -Raw | ConvertFrom-Json
        Require ($profileSettings.WifiProfile -eq 'TEST-AP-2') ('未选择的守护目标被页面默认值覆盖：' + $profileSettings.WifiProfile)

        # 列表刷新：新增系统配置后设置页下拉框必须出现新配置（驱动侧已断言，截图留证）。
        Require ($null -ne (Get-ShellStep $report 'profile-refresh')) '缺少设置页列表刷新证据步骤'

        # 保存忙态：编辑控件禁用由视图实现；按钮可见文字与自动化名称同步。
        $busy = Get-SaveStep $report 'page-busy-name'
        Require ($busy.State -eq 'Saved') ('忙态保存结果不符：' + $busy.State)
        Require ($busy.SaveNameDuringSave -eq '正在保存…') ('保存期间自动化名称不符：' + $busy.SaveNameDuringSave)
        Require ($busy.SaveNameAfter -eq '保存设置') ('保存结束后自动化名称未恢复：' + $busy.SaveNameAfter)

        # 跨页往返：每次返回都必须显示目标页面，不能停留在设置控件。
        $expectedTitles = @('概览', '设置', '概览', '设置', '网络诊断', '设置', '网络诊断', '设置', '日志', '设置', '日志')
        $navigation = @($report.NavigationSteps)
        Require ($navigation.Count -eq $expectedTitles.Count) ('跨页往返步骤数不符：' + $navigation.Count)
        for ($index = 0; $index -lt $navigation.Count; $index++) {
            Require ($navigation[$index].PageTitle -eq $expectedTitles[$index]) `
                ('跨页往返第 ' + $index + ' 步显示“' + $navigation[$index].PageTitle + '”，应为“' + $expectedTitles[$index] + '”')
            $navShot = Join-Path $shellDir $navigation[$index].File
            Require (Test-Path -LiteralPath $navShot) ('缺少跨页截图：' + $navigation[$index].File)
            Require ((Get-Item -LiteralPath $navShot).Length -gt 10KB) ($navigation[$index].File + ' 内容过小，疑似空白渲染')
        }

        $optionsUnknown = Get-ShellStep $report 'options-unknown'
        Require (-not $optionsUnknown.ConfigProjected -and -not $optionsUnknown.OptionsKnown) `
            '选项未知证据步骤未处于无投影的未确定状态'

        foreach ($shot in @('settings-online', 'settings-online-disabled', 'settings-online-suspended',
            'settings-online-redacted', 'settings-stale', 'settings-invalid', 'settings-missing',
            'settings-options-unknown', 'settings-profile-refresh',
            'settings-page-first-setup', 'settings-page-keep-password',
            'settings-page-change-account-required', 'settings-page-validation-failed',
            'settings-page-uac-cancelled', 'settings-page-outcome-unknown',
            'settings-page-store-failed', 'settings-page-keep-redacted-url', 'settings-page-keep-options',
            'settings-page-keep-profile', 'settings-page-busy-name')) {
            $path = Join-Path $shellDir ($shot + '.png')
            Require (Test-Path -LiteralPath $path) ('缺少设置页截图：' + $shot)
            Require ((Get-Item -LiteralPath $path).Length -gt 10KB) ($shot + ' 内容过小，疑似空白渲染')
        }

        # 页面夹具明文扫描：除 DPAPI 密文外不得出现密码或账号明文。
        $pageFixture = Join-Path $shellDir 'settings-page-fixture'
        $plaintextHits = @(Get-ChildItem -LiteralPath $pageFixture -Recurse -File |
            Where-Object { $_.Name -ne 'credentials.dpapi' -and (Select-String -LiteralPath $_.FullName -Pattern 'TEST-PASSWORD' -SimpleMatch -Quiet) })
        Require ($plaintextHits.Count -eq 0) ('页面夹具内出现密码明文文件：' + (($plaintextHits | ForEach-Object Name) -join '、'))

        Pass 'shell-page' @{ report = $reportPath; steps = $report.Steps; saveSteps = $report.SaveSteps }
    }
    catch { Fail 'shell-page' $_.Exception.Message }

    # 3) 提权编辑器（故事 35–39）：--settings 提权入口 + QA 隔离夹具逐场景
    try {
        if (-not $isAdministrator) { throw 'editor-qa-requires-elevation' }
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
        Require ($report.MutexName -eq $expectedMutex) ('互斥名不符：' + $report.MutexName)

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
    catch {
        if ($_.Exception.Message -eq 'editor-qa-requires-elevation') {
            # 非提权上下文：--settings 按既有契约只记录提权请求并以 4 退出，编辑器规则无法在此上下文驱动。
            # 不弹真实 UAC、不伪造通过；如实记为未验证（NOT_RUN），提权提示符下重跑即可覆盖。
            try {
                $editorDir = New-QaDirectory 'editor'
                $process = Start-App $exe @('--settings') $editorDir 'settings'
                Require (Wait-Exit $process 60) '提权入口未在超时内退出'
                Require ($process.ExitCode -eq 4) ('非提权上下文退出码应为 4，实际 ' + $process.ExitCode)
                $elevationPath = Join-Path $editorDir 'elevation.json'
                Require (Test-Path -LiteralPath $elevationPath) '提权入口未记录提权请求'
                $elevation = Get-Content -LiteralPath $elevationPath -Raw | ConvertFrom-Json
                Require ($elevation.operation -eq '--settings') ('提权操作不符：' + $elevation.operation)
                $steps['editor-rules'] = @{ result = 'not-verified'; detail = @{
                    reason = '当前进程非管理员；编辑器 QA 需在提权提示符下运行。普通上下文已验证 --settings 记录提权请求并以 4 退出。'
                    elevation = $elevationPath } }
                Write-Host 'NOT-VERIFIED editor-rules：编辑器 QA 需提权提示符；已记录普通上下文的提权入口证据'
            }
            catch { Fail 'editor-rules' $_.Exception.Message }
        }
        else { Fail 'editor-rules' $_.Exception.Message }
    }

    # 4) 本机真实受限存储逐字节未被触碰（只读核对；普通权限无法读取时明确记为未验证）
    try {
        $privateAfter = Get-RealPrivateFingerprint
        if (-not $privateBefore.accessible -or -not $privateAfter.accessible) {
            $steps['real-private-untouched'] = @{
                result = 'not-verified'
                detail = @{ before = $privateBefore; after = $privateAfter
                    reason = '当前进程无法读取仅管理员可访问的 private 目录；未声称真实存储未被触碰。' }
            }
            Write-Host 'NOT-VERIFIED real-private-untouched：当前进程无法读取真实 private 目录'
        }
        else {
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
    $notVerified = @($steps.Keys | Where-Object { $steps[$_].result -eq 'not-verified' })
    $deferred = @('真实 UAC 点击与拒绝（人工，需单独授权）',
        '跨管理员账户 UAC 成功路径（本机无法构造，NOT_RUN）',
        '本机真实受限目录下的设置编辑（人工，需单独授权）',
        '明暗主题（#9）', '真实安装/回退（#15/#16）')
    if (-not $isAdministrator) { $deferred += '提权编辑器逐场景 QA（需提权提示符，NOT_RUN）' }
    $summary = [ordered]@{
        time = [DateTimeOffset]::UtcNow.ToString('o')
        exe = $exe
        administrator = $isAdministrator
        steps = $steps
        failures = @($failures)
        notVerified = $notVerified
        result = if ($failures.Count -eq 0) { 'pass' } else { 'fail' }
        deferred = $deferred
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'qa-result.json') -Encoding utf8
    Write-Host ''
    Write-Host ('设置页 QA：' + $summary.result + '；证据目录 ' + $evidence)
    if ($failures.Count -gt 0) { foreach ($failure in $failures) { Write-Host ('- ' + $failure) }; exit 1 }
}
