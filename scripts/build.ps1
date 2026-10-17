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
        # T18（故事 85）：候选包必须与唯一提交一一对应。脏树或未跟踪源文件发布的包无法追溯，
        # 直接拒绝而不是把不可追溯的包交给审阅者。
        $statusLines = & git status --porcelain
        if ($statusLines) { throw "候选包要求干净工作树（故事 85 可追溯）：$($statusLines -join '; ')" }
        $bundle = Join-Path $repo 'artifacts/AutoNet-win-x64'
        # The previous bundle is scratch, not a rollback asset — the installed program and
        # git history are. Shipping leftover files (an old nested package with stale
        # binaries once rode along) inside a fresh package is a delivery defect.
        if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
        # 候选托盘是 Avalonia 新界面（工单 #15）；-p:AutoNetPublishIdentity 在 csproj 内把 EXE
        # 身份保持为 AutoNet.Tray.exe（工单 #3 安装契约），输出目录仍为 tray/，安装脚本无需改动。
        $publishTargets = @(
            @{ Project = 'AutoNet.Service'; Output = 'service'; Identity = $false },
            @{ Project = 'AutoNet.Tray.Avalonia'; Output = 'tray'; Identity = $true },
            # 内嵌认证通道辅助进程（工单 #26 / ADR-0009）：独立 exe，进 authchannel/ 子目录，
            # 由托盘 UAC 逐字传参启动（Elevation.ResolveHelperExe 探测该布局）。
            @{ Project = 'AutoNet.AuthChannel'; Output = 'authchannel'; Identity = $false }
        )
        foreach ($target in $publishTargets) {
            $output = Join-Path $bundle $target.Output
            # 空数组 splatting 在所有 PowerShell 7.x 版本都不传参；$null 实参在 7.0 会绑定为空串。
            $identityArg = @()
            if ($target.Identity) { $identityArg = @('-p:AutoNetPublishIdentity=true') }
            & $sdk publish "src/$($target.Project)/$($target.Project).csproj" -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true @identityArg --nologo
            if ($LASTEXITCODE -ne 0) { throw "Publish $($target.Project) failed." }
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $repo 'README.md') -Destination $bundle -Force
        New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'docs') | Out-Null
        foreach ($doc in @('VALIDATION.md', 'LOGGING.md', 'IMPLEMENTATION_PLAN.md', 'PORTAL_DIAGNOSIS_20261010.md', 'CODE_REVIEW_20261010.md', 'UI-MIGRATION-ACCEPTANCE.md')) {
            Copy-Item -LiteralPath (Join-Path $repo "docs/$doc") -Destination (Join-Path $bundle 'docs') -Force
        }
        Copy-Item -LiteralPath (Join-Path $evidence 'build.json'), (Join-Path $evidence 'tests.txt') -Destination $bundle -Force
        # T18（故事 85/86）：构建标识与安装/预览边界写进包内，与 build.json / tests.txt /
        # SHA256SUMS 互相印证；程序版本取自发布产物本身，而不是独立推导。
        $trayExe = Get-Item -LiteralPath (Join-Path $bundle 'tray/AutoNet.Tray.exe')
        @(
            'AutoNet 发布包构建标识'
            '================================'
            ''
            "程序版本（ProductVersion）：$($trayExe.VersionInfo.ProductVersion)"
            "源提交：$commit（发布门槛：干净工作树，包与提交一一对应）"
            "构建时刻（UTC）：$([DateTimeOffset]::UtcNow.ToString('o'))"
            "SDK：$sdkVersion"
            "回归结果：$(Get-Content -LiteralPath (Join-Path $evidence 'tests.txt') -Tail 1)（退出码 $testExit；完整输出见 tests.txt，元数据见 build.json）"
            '完整性：SHA256SUMS.txt 覆盖包内全部文件（清单不含其自身；BUILD-ID.txt 在列）'
            ''
            '使用方式：覆盖安装运行 Install.cmd / install.ps1（需管理员，脚本自带失败回滚并'
            '保留加密设置、凭据与日志）；仅预览界面可直接运行 tray\AutoNet.Tray.exe，不安装、'
            '不替换已安装程序、不停止现有服务。验收状态见 docs/UI-MIGRATION-ACCEPTANCE.md：'
            '真实网络场景与人工视觉项为显式待验收项，独立于发布结论。'
        ) | Set-Content -LiteralPath (Join-Path $bundle 'BUILD-ID.txt') -Encoding utf8
        $hashes = foreach ($file in Get-ChildItem -LiteralPath $bundle -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt') {
            $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
            $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($bundle, $file.FullName).Replace('\', '/')
        }
        $hashes | Set-Content -LiteralPath (Join-Path $bundle 'SHA256SUMS.txt') -Encoding utf8
        Write-Host "Ready: $bundle"
    }
}
finally { Pop-Location }
