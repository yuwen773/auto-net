#requires -Version 7.0
<#
.SYNOPSIS
    P0 跨语言兼容校验：C# 导出夹具 → 与仓库逐字节比对 → Rust golden 测试 → 安装契约核对。

.DESCRIPTION
    只使用隔离临时目录与离线夹具；不安装、不启动/停止服务、不接触真实状态或凭据。
    DPAPI 夹具不参与逐字节比对（密文随机且绑定本机）。
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixtures = Join-Path $root 'fixtures/cross-language'
$evidence = Join-Path $root 'artifacts/validation/crosslang'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$env:Path = "$env:USERPROFILE\.cargo\bin;$env:Path"

$dotnet = @(
    $env:AUTONET_DOTNET,
    (Join-Path $root '.tools/dotnet/dotnet.exe'),
    (Join-Path $env:USERPROFILE '.dotnet10/dotnet.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source }
if (-not $dotnet) { throw '未找到 dotnet；请安装 .NET SDK 10.0.401。' }

Write-Host '== 构建 C# 测试驱动器' -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'tests/AutoNet.Tests/AutoNet.Tests.csproj') -c Release --nologo 2>&1 |
    Tee-Object -FilePath (Join-Path $evidence 'csharp-build.log') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'C# 测试驱动器构建失败。' }
$tests = Join-Path $root 'tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll'

$temp = Join-Path ([IO.Path]::GetTempPath()) ('autonet-crosslang-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    Write-Host '== 导出夹具' -ForegroundColor Cyan
    # 带 --dpapi 只为让 manifest 与仓库一致；dpapi-credential.json 本身随机且绑定本机，不参与比对。
    & $dotnet $tests --export-golden-fixtures $temp --dpapi 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'export.log') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '夹具导出失败。' }

    $files = @('recovery-policy.json', 'authentication-block.json', 'submission-budget.json', 'status-log-projection.json', 'manifest.json')
    foreach ($name in $files) {
        $expected = Get-Content -LiteralPath (Join-Path $fixtures $name) -Raw
        $actual = Get-Content -LiteralPath (Join-Path $temp $name) -Raw
        if ($expected -ne $actual) {
            throw "夹具漂移：$name。C# 行为变化必须显式更新夹具并评审。"
        }
        Write-Host "PASS 夹具一致：$name"
    }

    Write-Host '== 安装契约核对（install.ps1）' -ForegroundColor Cyan
    $install = Get-Content -LiteralPath (Join-Path $root 'scripts/install.ps1') -Raw
    $contract = Get-Content -LiteralPath (Join-Path $fixtures 'install-lifecycle.json') -Raw | ConvertFrom-Json
    $patterns = @(
        "New-Service -Name AutoNet",
        "$($contract.service.displayName)",
        "-StartupType Automatic",
        "-DependsOn @('WlanSvc', 'Tcpip')",
        "sc.exe failure AutoNet reset= 86400 actions= restart/5000/restart/15000/restart/60000",
        "service/AutoNet.Service.exe",
        "tray/AutoNet.Tray.exe",
        "authchannel/AutoNet.AuthChannel.exe",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run",
        "Set-DataAcl `$private `$false",
        "Set-DataAcl `$path `$true"
    )
    foreach ($pattern in $patterns) {
        if (-not $install.Contains($pattern)) { throw "install.ps1 缺少契约片段：$pattern" }
    }
    if ($contract.acl.private.Count -ne 2) { throw '安装契约中 private 的 ACL 条目数量不符。' }
    Write-Host 'PASS 安装契约与脚本一致'

    Write-Host '== Rust golden 测试' -ForegroundColor Cyan
    $toolchainArg = @()
    if ($env:AUTONET_RUST_TOOLCHAIN) { $toolchainArg = @("+$($env:AUTONET_RUST_TOOLCHAIN)") }
    & cargo ($toolchainArg + @('test', '-p', 'autonet-contracts', '-p', 'autonet-core', '-p', 'autonet-storage', '--locked')) 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'rust-golden.log') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Rust golden 测试失败。' }
    Write-Host 'PASS Rust golden 测试通过'
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host '跨语言兼容校验通过。' -ForegroundColor Green
