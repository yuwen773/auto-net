#requires -Version 7.0
<#
.SYNOPSIS
    构建/校验 Rust 核心与 Tauri 预览。

.DESCRIPTION
    默认只检查 P0/P1 的纯 Rust crate（contracts/core/storage）：它们不依赖 MSVC，
    可在没有 Visual Studio Build Tools 的机器上运行。传入 -Full 时再检查整个工作区
    （含 Tauri 外壳），这需要 Windows MSVC 工具链与 WebView2。
    证据写入 artifacts/validation/rust/。
#>
param([switch]$Full, [switch]$Release)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $root 'artifacts/validation/rust'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$env:Path = "$env:USERPROFILE\.cargo\bin;$env:Path"
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw '未找到 cargo；请安装 Rust 1.100.0。' }

$packages = if ($Full) { @() } else { @('-p', 'autonet-contracts', '-p', 'autonet-core', '-p', 'autonet-storage') }
# 本地没有 MSVC 时可用 AUTONET_RUST_TOOLCHAIN 指定 GNU 工具链做纯 crate 校验；CI 留空。
$toolchainArg = @()
if ($env:AUTONET_RUST_TOOLCHAIN) { $toolchainArg = @("+$($env:AUTONET_RUST_TOOLCHAIN)") }
$results = [System.Collections.Generic.List[string]]::new()

function Invoke-Step([string]$Name, [string[]]$Arguments, [string]$Log) {
    Write-Host "== $Name" -ForegroundColor Cyan
    $output = & cargo @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence $Log)
    $code = $LASTEXITCODE
    if ($code -ne 0) { $results.Add("FAIL $Name"); throw "步骤失败：$Name（见 $Log）" }
    $results.Add("PASS $Name")
}

Push-Location $root
try {
    Invoke-Step 'rustfmt' ($toolchainArg + @('fmt', '--all', '--', '--check')) 'fmt.log'
    Invoke-Step 'clippy' ($toolchainArg + @('clippy') + $packages + @('--all-targets', '--locked', '--', '-D', 'warnings')) 'clippy.log'
    Invoke-Step 'test' ($toolchainArg + @('test') + $packages + @('--locked')) 'test.log'
    if ($Release) {
        Invoke-Step 'build-release' ($toolchainArg + @('build') + $packages + @('--release', '--locked')) 'build.log'
    }
    $results | Set-Content -LiteralPath (Join-Path $evidence 'summary.txt') -Encoding utf8
    Write-Host "Rust 校验通过（$($(if ($Full) { 'workspace' } else { 'pure crates' }))）。" -ForegroundColor Green
}
finally {
    Pop-Location
}
