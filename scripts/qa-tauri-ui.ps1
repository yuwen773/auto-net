#requires -Version 7.0
<#
.SYNOPSIS
    只读预览的前端与（可选）Tauri 构建校验。

.DESCRIPTION
    安装依赖后运行 typecheck、Vitest 与 Vite 构建，证据写入 artifacts/validation/qa-tauri-ui/。
    -BuildTauri 额外执行 `pnpm tauri build --no-bundle`：需要 Rust + MSVC + WebView2，
    且只生成未打包的可执行文件，不注册服务、不写 HKLM Run、不创建安装器。
#>
param([switch]$BuildTauri, [switch]$SkipInstall)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'apps/desktop'
$evidence = Join-Path $root 'artifacts/validation/qa-tauri-ui'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

Push-Location $app
try {
    if (-not $SkipInstall) {
        Write-Host '== pnpm install --frozen-lockfile' -ForegroundColor Cyan
        & pnpm install --frozen-lockfile 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'install.log') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '依赖安装失败。' }
    }
    Write-Host '== 类型检查' -ForegroundColor Cyan
    & pnpm typecheck 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'typecheck.log') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '类型检查失败。' }
    Write-Host '== 前端单元测试' -ForegroundColor Cyan
    & pnpm test 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'vitest.log') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '前端测试失败。' }
    Write-Host '== Vite 构建' -ForegroundColor Cyan
    & pnpm build 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'vite-build.log') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Vite 构建失败。' }
    if ($BuildTauri) {
        Write-Host '== Tauri 构建（不打包）' -ForegroundColor Cyan
        & pnpm tauri build --no-bundle 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'tauri-build.log') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Tauri 构建失败。' }
    }
    'PASS 只读预览校验' | Set-Content -LiteralPath (Join-Path $evidence 'summary.txt') -Encoding utf8
    Write-Host '只读预览校验通过。' -ForegroundColor Green
}
finally {
    Pop-Location
}
