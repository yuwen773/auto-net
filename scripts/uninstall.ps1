#requires -Version 7.0
# Removes the service and tray autostart; retains encrypted settings and logs.
param([switch]$PurgeData)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $start = [Diagnostics.ProcessStartInfo]::new('pwsh.exe')
    $start.UseShellExecute = $true; $start.Verb = 'runas'; $start.WindowStyle = 'Hidden'
    foreach ($arg in @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)) { $start.ArgumentList.Add($arg) }
    if ($PurgeData) { $start.ArgumentList.Add('-PurgeData') }
    $child = [Diagnostics.Process]::Start($start); $child.WaitForExit(); exit $child.ExitCode
}
$data = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AutoNet'))
if ($PurgeData) {
    Add-Type -AssemblyName System.Windows.Forms
    $answer = [System.Windows.Forms.MessageBox]::Show('将卸载 AutoNet 并永久清除本机加密凭据、设置和日志。是否继续？', 'AutoNet 清除本地数据', 'YesNo', 'Warning', 'Button2')
    if ($answer -ne 'Yes') { Write-Host '已取消，未更改服务或数据。'; exit 0 }
    if (Test-Path -LiteralPath $data) {
        $resolved = (Resolve-Path -LiteralPath $data).ProviderPath
        if (-not [string]::Equals($resolved, $data, [StringComparison]::OrdinalIgnoreCase) -or
            ((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '数据路径不符合预期，已拒绝清除。' }
        if (Get-ChildItem -LiteralPath $data -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw '数据目录包含重解析点，已拒绝清除。' }
    }
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
if ($PurgeData -and (Test-Path -LiteralPath $data)) {
    # Recheck the absolute target immediately before recursive deletion.
    $expected = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AutoNet'))
    if (-not [string]::Equals($data, $expected, [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '数据路径已变化，已拒绝删除。' }
    Remove-Item -LiteralPath $data -Recurse -Force
    Write-Host '已移除 AutoNet 服务、自启及本机数据；程序文件保留。'
}
else { Write-Host '已移除 AutoNet 服务及自启；加密设置、日志和程序文件保留。清除数据需显式使用 -PurgeData 并确认。' }
