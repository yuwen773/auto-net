#requires -Version 7.0
<#
回滚兼容判定校验（工单 #36）：测试里的“已发布 v0.3.1 读取规则冻结副本”必须与真实旧版给出同一判定。
- 用 git archive 取出 tag 的源码，构建真实旧版 AutoNet.Core；
- 语料由测试程序写出（tests/AutoNet.Tests），旧版与冻结副本各读一遍；
- 逐条比对状态、失败分类、已用次数与是否阻断，任何一条不一致即失败，
  防止冻结副本随当前版本漂移而无人察觉。
退出码：0 通过；2 环境或用法错误；3 判定不一致。
#>
param(
    [string]$Tag = 'v0.3.1',
    [string]$Work = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/released-reader-check')
)
$ErrorActionPreference = 'Stop'
function Fail([string]$Reason, [int]$Code = 3) {
    Write-Host "READER-FAIL $Reason"
    exit $Code
}

$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
    if (-not $dotnet) { Fail '找不到 .NET SDK：既没有 .tools/dotnet/dotnet.exe，也没有 PATH 上的 dotnet。' 2 }
    $dotnet = $dotnet.Source
}

# 1) 取 tag 源码。CI 的浅克隆默认没有 tag，先显式抓取（必要时补全历史）。
& git -C $repo rev-parse --verify "refs/tags/$Tag" *> $null
if ($LASTEXITCODE -ne 0) {
    & git -C $repo fetch --tags --unshallow --quiet origin 2>&1 *> $null
    if ($LASTEXITCODE -ne 0) { & git -C $repo fetch --tags --quiet origin 2>&1 *> $null }
    & git -C $repo rev-parse --verify "refs/tags/$Tag" *> $null
    if ($LASTEXITCODE -ne 0) { Fail "找不到 tag $Tag（CI 需要先抓取 tag）。" 2 }
}
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
$tree = Join-Path $Work 'tree'
New-Item -ItemType Directory -Path $tree -Force | Out-Null
$zip = Join-Path $Work 'tag.zip'
& git -C $repo archive --format=zip -o $zip $Tag
if ($LASTEXITCODE -ne 0) { Fail "导出 $Tag 源码失败。" 2 }
Expand-Archive -LiteralPath $zip -DestinationPath $tree -Force
if (-not (Test-Path -LiteralPath (Join-Path $tree 'src/AutoNet.Core/AutoNet.Core.csproj') -PathType Leaf)) {
    Fail "解压 $Tag 源码失败。"
}

# 2) 构建真实旧版 AutoNet.Core
$oldCore = Join-Path $Work 'oldcore'
& $dotnet build (Join-Path $tree 'src/AutoNet.Core/AutoNet.Core.csproj') -c Release -o $oldCore --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "构建 $Tag 的 AutoNet.Core 失败。" 2 }
$oldDll = Join-Path $oldCore 'AutoNet.Core.dll'
if (-not (Test-Path -LiteralPath $oldDll -PathType Leaf)) { Fail "旧版产物缺失：$oldDll" 2 }

# 3) 生成旧版探针：直接调用旧版 SafetyStateStore，输出与冻结副本同格式的判定
$probe = Join-Path $Work 'probe-old'
New-Item -ItemType Directory -Path $probe -Force | Out-Null
$probeSource = @'
using System;
using System.IO;
using System.Linq;
using AutoNet.Core;

var root = args[0];
foreach (var dir in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
{
    var name = Path.GetFileName(dir);
    var block = new SafetyStateStore(dir).LoadAuthenticationBlock();
    if (block is null) Console.WriteLine($"{name}|MISSING|-|-|-");
    else Console.WriteLine($"{name}|{block.State}|{block.Failure}|{block.Attempts}|{block.BlocksSubmissions}");
}
'@
Set-Content -LiteralPath (Join-Path $probe 'Program.cs') -Value $probeSource -Encoding UTF8
$probeProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>released-reader-probe</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="AutoNet.Core"><HintPath>$oldDll</HintPath></Reference>
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.0" />
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.0" />
  </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $probe 'probe.csproj') -Value $probeProject -Encoding UTF8
$probeBin = Join-Path $probe 'bin'
& $dotnet build (Join-Path $probe 'probe.csproj') -c Release -o $probeBin --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail '构建旧版探针失败。' 2 }

# 4) 当前版本测试程序：写语料并跑冻结副本
$tests = Join-Path $repo 'tests/AutoNet.Tests/AutoNet.Tests.csproj'
& $dotnet build $tests -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail '构建测试程序失败。' 2 }
$testDll = Join-Path $repo 'tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll'
$corpus = Join-Path $Work 'corpus'
New-Item -ItemType Directory -Path $corpus -Force | Out-Null
& $dotnet $testDll --legacy-reader-corpus $corpus
if ($LASTEXITCODE -ne 0) { Fail '写出回滚判定语料失败。' 2 }

# 5) 两边各读一遍语料
$oldLines = @(& $dotnet (Join-Path $probeBin 'released-reader-probe.dll') $corpus)
if ($LASTEXITCODE -ne 0) { Fail '旧版探针执行失败。' 2 }
$newLines = @(& $dotnet $testDll --legacy-reader-probe $corpus)
if ($LASTEXITCODE -ne 0) { Fail '冻结副本探针执行失败。' 2 }

function Read-Verdicts([string[]]$Lines) {
    $map = @{}
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '\|'
        if ($parts.Count -ne 5) { Fail "探针输出无法解析：$line" 2 }
        $map[$parts[0]] = ($parts[1..4] -join '|')
    }
    return $map
}
$oldVerdicts = Read-Verdicts $oldLines
$newVerdicts = Read-Verdicts $newLines
if ($oldVerdicts.Count -eq 0) { Fail '旧版探针没有输出任何判定。' 2 }
if ($oldVerdicts.Count -ne $newVerdicts.Count) {
    Fail "语料条数不一致：旧版 $($oldVerdicts.Count)，冻结副本 $($newVerdicts.Count)。"
}

# 6) 逐条比对
$mismatches = @()
foreach ($name in ($oldVerdicts.Keys | Sort-Object)) {
    if (-not $newVerdicts.ContainsKey($name)) { $mismatches += "$name 冻结副本缺失判定"; continue }
    if ($oldVerdicts[$name] -ne $newVerdicts[$name]) {
        $mismatches += "$name 旧版=$($oldVerdicts[$name]) 冻结副本=$($newVerdicts[$name])"
    }
}
$report = Join-Path $Work 'report.txt'
$lines = @("旧版 tag：$Tag", "语料条数：$($oldVerdicts.Count)", '')
foreach ($name in ($oldVerdicts.Keys | Sort-Object)) {
    $lines += ("{0}|旧版={1}|冻结副本={2}" -f $name, $oldVerdicts[$name], $newVerdicts[$name])
}
if ($mismatches.Count -gt 0) { $lines += ''; $lines += '不一致：'; $lines += $mismatches }
Set-Content -LiteralPath $report -Value $lines -Encoding UTF8

if ($mismatches.Count -gt 0) {
    foreach ($line in $mismatches) { Write-Host "BAD $line" }
    Fail "冻结副本与 $Tag 的判定不一致（$($mismatches.Count) 条），报告：$report"
}
Write-Host "READER-OK 冻结副本与 $Tag 在 $($oldVerdicts.Count) 条语料上判定一致；报告：$report"
exit 0
