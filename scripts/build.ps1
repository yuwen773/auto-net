#requires -Version 7.0
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    $evidence = Join-Path $repo 'artifacts/validation'
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    $sdkVersion = & $sdk --version
    & $sdk build AutoNet.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Install a .NET 10 SDK if needed.' }
    $testOutput = & $sdk 'tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll'
    $testExit = $LASTEXITCODE
    $testOutput | Tee-Object -FilePath (Join-Path $evidence 'tests.txt')
    $commit = & git rev-parse HEAD
    & git diff --quiet HEAD
    $dirty = $LASTEXITCODE -ne 0
    @{ time = [DateTimeOffset]::UtcNow.ToString('o'); commit = $commit; trackedChanges = $dirty; sdk = $sdkVersion; testExitCode = $testExit; result = $testOutput[-1] } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'build.json') -Encoding utf8
    if ($testExit -ne 0) { throw 'Regression checks failed.' }
    if ($Publish) {
        $bundle = Join-Path $repo 'artifacts/AutoNet-win-x64'
        foreach ($project in @('Service', 'Tray')) {
            $output = Join-Path $bundle $project.ToLowerInvariant()
            & $sdk publish "src/AutoNet.$project/AutoNet.$project.csproj" -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --nologo
            if ($LASTEXITCODE -ne 0) { throw "Publish $project failed." }
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $repo 'README.md') -Destination $bundle -Force
        New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'docs') | Out-Null
        foreach ($doc in @('VALIDATION.md', 'LOGGING.md', 'IMPLEMENTATION_PLAN.md', 'PORTAL_DIAGNOSIS_20261010.md', 'CODE_REVIEW_20261010.md')) {
            Copy-Item -LiteralPath (Join-Path $repo "docs/$doc") -Destination (Join-Path $bundle 'docs') -Force
        }
        Copy-Item -LiteralPath (Join-Path $evidence 'build.json'), (Join-Path $evidence 'tests.txt') -Destination $bundle -Force
        $hashes = foreach ($file in Get-ChildItem -LiteralPath $bundle -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt') {
            $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
            $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($bundle, $file.FullName).Replace('\', '/')
        }
        $hashes | Set-Content -LiteralPath (Join-Path $bundle 'SHA256SUMS.txt') -Encoding utf8
        Write-Host "Ready: $bundle"
    }
}
finally { Pop-Location }
