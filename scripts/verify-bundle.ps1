#requires -Version 7.0
<#
发布物完整性校验（工单 #19，ADR-0010）：CI 与本机共用同一判据。
- 重算包内 SHA256SUMS.txt 并逐项比对；清单必须覆盖包内全部文件（不含清单自身）。
- BUILD-ID.txt 必须存在且含源提交行；给定 -ExpectedCommit 时与之一致。
退出码：0 通过；2 用法/路径错误；3 校验失败。
#>
param(
    [string]$Bundle = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/AutoNet-win-x64'),
    [string]$ExpectedCommit = ''
)
$ErrorActionPreference = 'Stop'
function Fail([string]$Reason) {
    Write-Host "VERIFY-FAIL $Reason"
    exit 3
}

if (-not (Test-Path -LiteralPath $Bundle -PathType Container)) { Write-Host "VERIFY-FAIL 包目录不存在：$Bundle"; exit 2 }
$sumsPath = Join-Path $Bundle 'SHA256SUMS.txt'
$buildIdPath = Join-Path $Bundle 'BUILD-ID.txt'
foreach ($p in @($sumsPath, $buildIdPath)) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { Fail "缺少 $([IO.Path]::GetFileName($p))" } }

# 1) 解析哈希清单
$expected = @{}
foreach ($line in Get-Content -LiteralPath $sumsPath) {
    if ($line -match '^([0-9a-fA-F]{64})\s+(\S.+?)\s*$') {
        $rel = $matches[2].Replace('\', '/')
        if ($expected.ContainsKey($rel)) { Fail "清单重复条目：$rel" }
        $expected[$rel] = $matches[1].ToLowerInvariant()
    } elseif ($line.Trim().Length -gt 0) { Fail "清单存在无法解析的行：$line" }
}
if ($expected.Count -eq 0) { Fail '清单为空' }

# 2) 清单必须与包内文件一一对应（清单自身除外）
$actual = Get-ChildItem -LiteralPath $Bundle -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt' |
    ForEach-Object { [IO.Path]::GetRelativePath($Bundle, $_.FullName).Replace('\', '/') }
$missing = @($actual | Where-Object { -not $expected.ContainsKey($_) })
$extra = @($expected.Keys | Where-Object { $_ -notin $actual })
if ($missing.Count -gt 0) { Fail ("包内文件不在清单中：" + ($missing -join ', ')) }
if ($extra.Count -gt 0) { Fail ("清单包含不存在的文件：" + ($extra -join ', ')) }

# 3) 逐项重算比对
$failures = @()
foreach ($rel in ($expected.Keys | Sort-Object)) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $Bundle $rel) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -eq $expected[$rel]) { Write-Host "OK  $rel" }
    else { $failures += $rel; Write-Host "BAD $rel" }
}
if ($failures.Count -gt 0) { Fail ("哈希不一致：" + ($failures -join ', ')) }

# 4) BUILD-ID 源提交
$buildId = Get-Content -LiteralPath $buildIdPath -Raw
$commitMatch = [regex]::Match($buildId, '源提交：([0-9a-f]{40})')
if (-not $commitMatch.Success) { Fail 'BUILD-ID 缺少可解析的源提交行' }
Write-Host "OK  BUILD-ID 源提交 $($commitMatch.Groups[1].Value)"
if ($ExpectedCommit) {
    $want = $ExpectedCommit.ToLowerInvariant()
    if ($commitMatch.Groups[1].Value -ne $want) { Fail "BUILD-ID 源提交 $($commitMatch.Groups[1].Value) 与期望 $want 不一致" }
    Write-Host "OK  源提交与期望一致"
}

Write-Host 'VERIFY-PASS 发布包完整性校验通过'
exit 0
