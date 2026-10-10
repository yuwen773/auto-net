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

try {
    $destination = Join-Path $env:ProgramFiles 'AutoNet'
    $data = Join-Path $env:ProgramData 'AutoNet'
    $serviceExe = Join-Path $destination 'service/AutoNet.Service.exe'
    $trayExe = Join-Path $destination 'tray/AutoNet.Tray.exe'
    foreach ($part in @('service/AutoNet.Service.exe', 'tray/AutoNet.Tray.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $part))) { throw 'Use install.ps1 from the published bundle, not the source scripts directory.' }
    }
    foreach ($path in @($destination, (Join-Path $destination 'service'), (Join-Path $destination 'tray'), $data, (Join-Path $data 'private'), (Join-Path $data 'logs'))) { Assert-OrdinaryDirectory $path }
    $existing = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
    if ($existing) {
        $definition = Get-CimInstance Win32_Service -Filter "Name='AutoNet'"
        if ($definition.PathName -ne ('"' + $serviceExe + '"')) { throw 'An unrelated AutoNet service already exists.' }
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
    }
    & sc.exe failure AutoNet reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure service restart policy.' }
    $run = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-ItemProperty -LiteralPath $run -Name AutoNet -Value ('"' + $trayExe + '" --tray') -PropertyType String -Force | Out-Null
    $shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AutoNet.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut); $link.TargetPath = $trayExe; $link.WorkingDirectory = Split-Path $trayExe; $link.Save()
    Start-Service -Name AutoNet
    if ($LaunchSettings) { Start-Process -FilePath $trayExe -ArgumentList '--settings' }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show('安装完成。请保存校园 Wi-Fi 和账号密码；后台服务已启动。', 'AutoNet') | Out-Null
}
catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(('安装未完成：' + $_.Exception.Message), 'AutoNet') | Out-Null
    exit 1
}
