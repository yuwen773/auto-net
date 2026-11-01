#requires -Version 7.0
<#
工单 #19 QA：verify-bundle.ps1 的负向夹具自测。
构造临时夹具包，验证完整性失败与合法文件名、嵌套清单的退出码。
证据落 artifacts/validation/qa-verify-bundle/。不读取凭据或真实校园网配置。
#>
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$evidence = Join-Path $RepoRoot 'artifacts/validation/qa-verify-bundle'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$verify = Join-Path $PSScriptRoot 'verify-bundle.ps1'
$report = [System.Collections.Generic.List[string]]::new()
$report += "=== verify-bundle QA（$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')）==="

function Write-FixtureManifest([string]$Dir) {
    $manifest = Join-Path $Dir 'SHA256SUMS.txt'
    $sums = foreach ($f in Get-ChildItem -LiteralPath $Dir -File -Recurse | Where-Object FullName -ne $manifest) {
        $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$h  $([IO.Path]::GetRelativePath($Dir, $f.FullName).Replace('\','/'))"
    }
    $sums | Set-Content -LiteralPath (Join-Path $Dir 'SHA256SUMS.txt') -Encoding utf8
}
function New-FixtureBundle([string]$Dir, [string]$SourceCommit) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'service') | Out-Null
    Set-Content -LiteralPath (Join-Path $Dir 'service\A.exe') -Value 'payload-A'
    Set-Content -LiteralPath (Join-Path $Dir 'service\B.exe') -Value 'payload-B'
    Set-Content -LiteralPath (Join-Path $Dir 'BUILD-ID.txt') -Value "AutoNet 发布包构建标识`n源提交：$SourceCommit"
    Write-FixtureManifest $Dir
}
$good = 'a' * 40
$base = Join-Path ([IO.Path]::GetTempPath()) ("verify-bundle-qa-" + [guid]::NewGuid().ToString('N'))
$cases = @()

# 场景 1：干净包 + 提交一致 → 0
$dir = Join-Path $base 'good'; New-FixtureBundle $dir $good
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir -ExpectedCommit $good *> ($out = Join-Path $evidence 'case1-good.txt')
$cases += ,@('干净包+提交一致', $LASTEXITCODE, 0, $out)

# 场景 2：篡改文件 → 3
$dir = Join-Path $base 'tampered'; New-FixtureBundle $dir $good
Set-Content -LiteralPath (Join-Path $dir 'service\B.exe') -Value 'tampered'
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir *> ($out = Join-Path $evidence 'case2-tampered.txt')
$cases += ,@('篡改文件', $LASTEXITCODE, 3, $out)

# 场景 3：源提交不符 → 3
$dir = Join-Path $base 'commit'; New-FixtureBundle $dir $good
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir -ExpectedCommit ('b' * 40) *> ($out = Join-Path $evidence 'case3-commit.txt')
$cases += ,@('源提交不符', $LASTEXITCODE, 3, $out)

# 场景 4：清单缺项（新增文件未入清单）→ 3
$dir = Join-Path $base 'missing'; New-FixtureBundle $dir $good
Set-Content -LiteralPath (Join-Path $dir 'stray.txt') -Value 'stray'
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir *> ($out = Join-Path $evidence 'case4-missing.txt')
$cases += ,@('包内文件不在清单', $LASTEXITCODE, 3, $out)

# 场景 5：清单多余项 → 3
$dir = Join-Path $base 'extra'; New-FixtureBundle $dir $good
$sums = Get-Content -LiteralPath (Join-Path $dir 'SHA256SUMS.txt')
$sums += ('0' * 64 + '  ghost/missing.exe')
$sums | Set-Content -LiteralPath (Join-Path $dir 'SHA256SUMS.txt') -Encoding utf8
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir *> ($out = Join-Path $evidence 'case5-extra.txt')
$cases += ,@('清单多余项', $LASTEXITCODE, 3, $out)

# 场景 6：嵌套的同名清单也是普通包内文件，不得绕过覆盖检查 → 3
$dir = Join-Path $base 'nested-unlisted'; New-FixtureBundle $dir $good
Set-Content -LiteralPath (Join-Path $dir 'service/SHA256SUMS.txt') -Value 'unlisted payload'
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir *> ($out = Join-Path $evidence 'case6-nested-unlisted.txt')
$cases += ,@('嵌套清单未入根清单', $LASTEXITCODE, 3, $out)

# 场景 7：嵌套清单纳入根清单并核验哈希 → 0
Write-FixtureManifest $dir
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir *> ($out = Join-Path $evidence 'case7-nested-listed.txt')
$cases += ,@('嵌套清单已入根清单', $LASTEXITCODE, 0, $out)

# 场景 8：合法的单字符文件名 → 0
$dir = Join-Path $base 'single-character'; New-FixtureBundle $dir $good
Set-Content -LiteralPath (Join-Path $dir 'x') -Value 'single-character payload'
Write-FixtureManifest $dir
& pwsh.exe -NoLogo -NoProfile -File $verify -Bundle $dir *> ($out = Join-Path $evidence 'case8-single-character.txt')
$cases += ,@('单字符文件名', $LASTEXITCODE, 0, $out)

$failed = 0
foreach ($c in $cases) {
    $ok = $c[1] -eq $c[2]
    if (-not $ok) { $failed++ }
    $report += ('{0}：退出码 {1}（期望 {2}）→ {3}' -f $c[0], $c[1], $c[2], $(if ($ok) { 'PASS' } else { 'FAIL' }))
}
$report += ("结论：{0}/{1} 通过" -f ($cases.Count - $failed), $cases.Count)
$report | Set-Content -LiteralPath (Join-Path $evidence 'report.txt') -Encoding utf8
Get-Content -LiteralPath (Join-Path $evidence 'report.txt')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$cleanupTarget = [IO.Path]::GetFullPath($base)
if (-not $cleanupTarget.StartsWith($tempRoot + 'verify-bundle-qa-', [StringComparison]::OrdinalIgnoreCase)) {
    throw '拒绝清理临时夹具目录范围外的路径'
}
Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
exit $(if ($failed -gt 0) { 1 } else { 0 })
