#requires -Version 7.0
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    & $sdk build AutoNet.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Install a .NET 10 SDK if needed.' }
    & $sdk 'tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll'
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
    if ($Publish) {
        $bundle = Join-Path $repo 'artifacts/AutoNet-win-x64'
        foreach ($project in @('Service', 'Tray')) {
            $output = Join-Path $bundle $project.ToLowerInvariant()
            & $sdk publish "src/AutoNet.$project/AutoNet.$project.csproj" -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --nologo
            if ($LASTEXITCODE -ne 0) { throw "Publish $project failed." }
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $repo 'README.md') -Destination $bundle -Force
        New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'docs') | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo 'docs/VALIDATION.md') -Destination (Join-Path $bundle 'docs') -Force
        Write-Host "Ready: $bundle"
    }
}
finally { Pop-Location }
