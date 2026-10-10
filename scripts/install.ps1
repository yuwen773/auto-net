#requires -Version 7.0
param([switch]$LaunchSettings)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $start = [Diagnostics.ProcessStartInfo]::new('pwsh.exe')
    $start.UseShellExecute = $true; $start.Verb = 'runas'; $start.WindowStyle = 'Hidden'
    foreach ($arg in @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)) { $start.ArgumentList.Add($arg) }
    if ($LaunchSettings) { $start.ArgumentList.Add('-LaunchSettings') }
    $child = [Diagnostics.Process]::Start($start)
    $child.WaitForExit()
    if ($child.ExitCode -eq 0 -and $LaunchSettings) {
        Start-Process -FilePath (Join-Path $env:ProgramFiles 'AutoNet/tray/AutoNet.Tray.exe') -ArgumentList '--tray' -WindowStyle Hidden
    }
    exit $child.ExitCode
}

function Assert-OrdinaryDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation path is not an ordinary directory.' }
        $owner = (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-32-544', 'S-1-5-18')) { throw 'An installation directory is owned by an untrusted account. Choose a clean installation location.' }
    }
}
function Set-DataAcl([string]$Path, [bool]$PublicRead) {
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false); $acl.SetOwner($admins)
    foreach ($sid in @($admins, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    if ($PublicRead) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')) }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

$backup = $null
$rollbackFiles = @{}
$originalAcls = @{}
$wasRunning = $false
$createdService = $false
$serviceTouched = $false
$changed = $false
$run = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$oldRun = Get-ItemPropertyValue -LiteralPath $run -Name AutoNet -ErrorAction SilentlyContinue
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AutoNet.lnk'
try {
    $destination = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AutoNet'
    $data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AutoNet'
    $serviceExe = Join-Path $destination 'service/AutoNet.Service.exe'
    $trayExe = Join-Path $destination 'tray/AutoNet.Tray.exe'
    foreach ($part in @('service/AutoNet.Service.exe', 'tray/AutoNet.Tray.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $part))) { throw 'Use install.ps1 from the published bundle, not the source scripts directory.' }
    }
    foreach ($path in @($destination, (Join-Path $destination 'service'), (Join-Path $destination 'tray'), $data, (Join-Path $data 'private'), (Join-Path $data 'logs'))) {
        Assert-OrdinaryDirectory $path
        if (Test-Path -LiteralPath $path) { $originalAcls[$path] = Get-Acl -LiteralPath $path }
    }
    foreach ($target in @($serviceExe, $trayExe, (Join-Path $destination 'uninstall.ps1'), $shortcut)) {
        if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation target is a reparse point.' }
    }
    $existing = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    if ($existing) {
        $definition = Get-CimInstance Win32_Service -Filter "Name='AutoNet'"
        if ($definition.PathName -ne ('"' + $serviceExe + '"')) { throw 'An unrelated AutoNet service already exists.' }
        $wasRunning = $existing.Status -eq 'Running'
    }
    $changed = $true
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Set-DataAcl $destination $true
    $backup = Join-Path $destination ('.rollback-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup | Out-Null
    Set-DataAcl $backup $false
    foreach ($target in @($serviceExe, $trayExe, (Join-Path $destination 'uninstall.ps1'), $shortcut)) {
        $saved = $null
        if (Test-Path -LiteralPath $target) {
            $saved = Join-Path $backup ([Guid]::NewGuid().ToString('N'))
            Copy-Item -LiteralPath $target -Destination $saved
        }
        $rollbackFiles[$target] = $saved
    }
    @{ files = $rollbackFiles; wasRunning = $wasRunning; previousAutostart = $oldRun } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding utf8
    if ($existing) {
        $serviceTouched = $true
        Stop-Service -Name AutoNet
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    Get-Process -Name AutoNet.Tray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $trayExe } | Stop-Process
    # Executables run as SYSTEM, so ordinary users must never be able to replace them.
    foreach ($path in @($destination, (Join-Path $destination 'service'), (Join-Path $destination 'tray'))) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Set-DataAcl $path $true
    }
    foreach ($path in @($data, (Join-Path $data 'logs'))) { New-Item -ItemType Directory -Path $path -Force | Out-Null; Set-DataAcl $path $true }
    $private = Join-Path $data 'private'
    New-Item -ItemType Directory -Path $private -Force | Out-Null; Set-DataAcl $private $false
    foreach ($part in @('service/AutoNet.Service.exe', 'tray/AutoNet.Tray.exe')) {
        $target = Join-Path $destination $part
        if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Executable target is a reparse point.' }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $part) -Destination $target -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $destination -Force
    if (-not $existing) {
        New-Service -Name AutoNet -DisplayName 'AutoNet 校园网连接守护' -BinaryPathName ('"' + $serviceExe + '"') -StartupType Automatic -DependsOn @('WlanSvc', 'Tcpip') -Description '自动恢复已配置的校园 Wi-Fi 和 Dr.COM 认证，不重置网卡。' | Out-Null
        $createdService = $true
        $serviceTouched = $true
        & sc.exe failure AutoNet reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not configure service restart policy.' }
    }
    New-ItemProperty -LiteralPath $run -Name AutoNet -Value ('"' + $trayExe + '" --tray') -PropertyType String -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut); $link.TargetPath = $trayExe; $link.WorkingDirectory = Split-Path $trayExe; $link.Save()
    Start-Service -Name AutoNet
    (Get-Service -Name AutoNet).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    if ($LaunchSettings) { Start-Process -FilePath $trayExe -ArgumentList '--settings' }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(('安装完成。请保存校园 Wi-Fi 和账号密码；后台服务已启动。旧程序备份保留于：' + $backup), 'AutoNet') | Out-Null
}
catch {
    $failure = $_.Exception.Message
    $rollback = '未更改服务或程序。'
    if ($changed) {
        try {
            $currentService = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
            if ($serviceTouched -and $currentService -and $currentService.Status -ne 'Stopped') {
                Stop-Service -Name AutoNet
                $currentService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
            }
            if ($createdService) {
                & sc.exe delete AutoNet | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not remove the newly created service.' }
            }
            foreach ($target in $rollbackFiles.Keys) {
                if ($rollbackFiles[$target]) { Copy-Item -LiteralPath $rollbackFiles[$target] -Destination $target -Force }
                elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
            }
            if ($null -ne $oldRun) { New-ItemProperty -LiteralPath $run -Name AutoNet -Value $oldRun -PropertyType String -Force | Out-Null }
            else { Remove-ItemProperty -LiteralPath $run -Name AutoNet -ErrorAction SilentlyContinue }
            foreach ($path in $originalAcls.Keys) { Set-Acl -LiteralPath $path -AclObject $originalAcls[$path] }
            if ($serviceTouched -and $existing -and $wasRunning) {
                Start-Service -Name AutoNet
                (Get-Service -Name AutoNet).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
            }
            $rollback = '已恢复旧程序、自启和服务原运行状态；配置及凭据未改动。'
        }
        catch { $rollback = '自动回滚未完成，请在本机接管并使用备份恢复：' + $backup + '；错误类型：' + $_.Exception.GetType().Name }
    }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(('安装未完成：' + $failure + [Environment]::NewLine + $rollback), 'AutoNet') | Out-Null
    exit 1
}
