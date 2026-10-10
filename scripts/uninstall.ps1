#requires -Version 7.0
# Removes the service and tray autostart; retains encrypted settings and logs.
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $start = [Diagnostics.ProcessStartInfo]::new('pwsh.exe')
    $start.UseShellExecute = $true; $start.Verb = 'runas'; $start.WindowStyle = 'Hidden'
    foreach ($arg in @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)) { $start.ArgumentList.Add($arg) }
    $child = [Diagnostics.Process]::Start($start); $child.WaitForExit(); exit $child.ExitCode
}
$serviceExe = Join-Path $env:ProgramFiles 'AutoNet/service/AutoNet.Service.exe'
$trayExe = Join-Path $env:ProgramFiles 'AutoNet/tray/AutoNet.Tray.exe'
Get-Process -Name AutoNet.Tray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $trayExe } | Stop-Process
$service = Get-Service -Name AutoNet -ErrorAction SilentlyContinue
if ($service) {
    $definition = Get-CimInstance Win32_Service -Filter "Name='AutoNet'"
    if ($definition.PathName -ne ('"' + $serviceExe + '"')) { throw 'Refusing to remove an unrelated service.' }
    Stop-Service -Name AutoNet
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    & sc.exe delete AutoNet
    if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
}
Remove-ItemProperty -LiteralPath 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name AutoNet -ErrorAction SilentlyContinue
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AutoNet.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
Write-Host 'AutoNet service and autostart removed. Encrypted settings, logs and binaries are retained.'
