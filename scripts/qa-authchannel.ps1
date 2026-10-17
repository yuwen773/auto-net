#requires -Version 7.0
<#
工单 #21 QA：内嵌认证通道夹具门户页三态闭环。
场景：success（提交→跳转在线页→LoggedIn）/ failure（显式错误→NotLoggedIn）/
reject（页面拒绝勾选→NeedsHuman）。断言退出码、结果文件契约、隐私（凭据零回显）、
临时会话目录即弃。证据落 artifacts/validation/qa-authchannel/。
全程使用离线夹具，不连接真实门户；凭据为字面假值。
#>
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$sdk = Join-Path $RepoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$evidence = Join-Path $RepoRoot 'artifacts/validation/qa-authchannel'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$fixtureDir = Join-Path $RepoRoot 'src/AutoNet.AuthChannel/QaFixtures'
# 防御性清理上一轮可能残留的会话目录（不计入本轮判定）
Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'autonet-authch-*' -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$exe = Join-Path $RepoRoot 'src/AutoNet.AuthChannel/bin/Release/net10.0-windows/AutoNet.AuthChannel.exe'
$report = [System.Collections.Generic.List[string]]::new()
$report += "=== AuthChannel 夹具 QA（$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')）==="

& $sdk build (Join-Path $RepoRoot 'src/AutoNet.AuthChannel/AutoNet.AuthChannel.csproj') -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'AuthChannel build failed.' }
if (-not (Test-Path -LiteralPath $exe)) { throw 'AuthChannel exe missing.' }
# apphost 默认找全局 dotnet（本机为 6.0.5，无 10.0 运行时）——显式指向仓库自带 SDK 运行时。
$env:DOTNET_ROOT = (Split-Path $sdk -Parent)
$env:DOTNET_MULTILEVEL_LOOKUP = '0'

$cases = @(
    @{ Name = '提交成功跳转在线页'; Case = 'success'; ExpectCode = 0; Expect = 'LoggedIn' },
    @{ Name = '页面显式登录失败'; Case = 'failure'; ExpectCode = 3; Expect = 'NotLoggedIn' },
    @{ Name = '勾选被页面拒绝'; Case = 'reject'; ExpectCode = 4; Expect = 'NeedsHuman' }
)
$failed = 0
$credentialJson = '{"Username":"qa-fake-user","Password":"qa-fake-pass-DO-NOT-ECHO"}'
foreach ($c in $cases) {
    $resultPath = Join-Path $evidence ("result-" + $c.Case + ".json")
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath -Force }
    $log = Join-Path $evidence ("run-" + $c.Case + ".log")
    $p = [Diagnostics.Process]::New()
    $p.StartInfo.FileName = $exe
    $p.StartInfo.Arguments = "--start-url https://portal.fixture/login.html?case=$($c.Case) --result `"$resultPath`" --fixture-dir `"$fixtureDir`" --timeout 90"
    $p.StartInfo.UseShellExecute = $false
    $p.StartInfo.RedirectStandardInput = $true
    $p.StartInfo.RedirectStandardError = $true
    $p.StartInfo.RedirectStandardOutput = $true
    [void]$p.Start()
    $p.StandardInput.Write($credentialJson)
    $p.StandardInput.Close()
    $stderrTask = $p.StandardError.ReadToEndAsync()
    $timedOut = -not $p.WaitForExit(120000)
    if ($timedOut) { $p.Kill($true) }
    [IO.File]::WriteAllText($log, $p.StandardOutput.ReadToEnd() + $stderrTask.Result, [Text.Encoding]::UTF8)
    if ($timedOut) { throw "AuthChannel timed out on case $($c.Case)." }
    $codeOk = $p.ExitCode -eq $c.ExpectCode
    $conclusion = ''
    $privacyOk = $true
    if (Test-Path -LiteralPath $resultPath) {
        $json = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $conclusion = $json.conclusion
        $privacyOk = -not ($json.detail -match 'qa-fake-user' -or $json.detail -match 'qa-fake-pass')
    }
    $resultOk = $conclusion -eq $c.Expect
    $pass = $codeOk -and $resultOk -and $privacyOk
    if (-not $pass) { $failed++ }
    $report += ('{0}：退出码 {1}（期望 {2}）| 结论 {3}（期望 {4}）| 隐私 {5} → {6}' -f
        $c.Name, $p.ExitCode, $c.ExpectCode, $conclusion, $c.Expect, $(if ($privacyOk) { 'OK' } else { 'LEAK' }), $(if ($pass) { 'PASS' } else { 'FAIL' }))
}

# 会话即弃：临时目录应已删除（等待 WebView2 子进程释放）
Start-Sleep -Seconds 5
$leftover = @(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'autonet-authch-*' -ErrorAction SilentlyContinue)
$sessionOk = $leftover.Count -eq 0
if (-not $sessionOk) { $failed++ }
$report += ('会话即弃：残留目录 {0} 个 → {1}' -f $leftover.Count, $(if ($sessionOk) { 'PASS' } else { 'FAIL' }))

$report += ("结论：{0}/{1} 通过" -f ($cases.Count + 1 - $failed), $cases.Count + 1)
$report | Set-Content -LiteralPath (Join-Path $evidence 'report.txt') -Encoding utf8
Get-Content -LiteralPath (Join-Path $evidence 'report.txt')
exit $(if ($failed -gt 0) { 1 } else { 0 })
