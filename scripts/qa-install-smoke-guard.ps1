#requires -Version 7.0
# 只测试 CI 环境守卫；始终传入不存在的包路径，绝不进入真装流程。
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $repo 'artifacts/validation/qa-install-smoke-guard'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$missingBundle = Join-Path $evidence ('missing-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $missingBundle) { throw '守卫夹具包路径必须不存在' }
$cases = @(
    @{ Name = 'local'; Actions = ''; Runner = ''; Reject = $true },
    @{ Name = 'self-hosted'; Actions = 'true'; Runner = 'self-hosted'; Reject = $true },
    @{ Name = 'github-hosted'; Actions = 'true'; Runner = 'github-hosted'; Reject = $false }
)
$failed = 0
foreach ($case in $cases) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh.exe -ErrorAction Stop).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-File', (Join-Path $PSScriptRoot 'qa-install-smoke.ps1'), '-Bundle', $missingBundle)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['GITHUB_ACTIONS'] = $case.Actions
    $start.Environment['RUNNER_ENVIRONMENT'] = $case.Runner
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw 'CI 守卫测试超时' }
        $output = $stdout.Result + $stderr.Result
        $output | Set-Content -LiteralPath (Join-Path $evidence ($case.Name + '.txt')) -Encoding utf8
        $rejected = $output.Contains('SMOKE-FAIL CI_ONLY')
        $ok = $process.ExitCode -eq 2 -and $rejected -eq $case.Reject
        if (-not $ok) { $failed++ }
        Write-Host ('{0} {1}（退出码 {2}，CI 守卫拒绝 {3}）' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $case.Name, $process.ExitCode, $rejected)
    }
    finally { $process.Dispose() }
}
Write-Host ('{0}/{1} passed' -f ($cases.Count - $failed), $cases.Count)
exit $(if ($failed) { 1 } else { 0 })
