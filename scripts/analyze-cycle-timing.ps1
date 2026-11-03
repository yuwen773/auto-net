#requires -Version 7.0
<#
只读的监测轮次耗时分析器。读取服务写出的 autonet-YYYY-MM-DD.jsonl，汇总
SlowCycle / SlowStep 事件，不启停服务、不读取凭据、不修改任何文件。

输出只包含时间、阶段名、动作、耗时和日志中的脱敏说明；不会打印账号、密码、
URL、IP、SSID。数据来自服务自身的阶段计时，是“看到什么就报什么”，不做推断。

退出码：0 成功；2 目录或用法错误。
#>
param(
    [string]$LogDirectory = (Join-Path $env:ProgramData 'AutoNet/logs'),
    [int]$Days = 14
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
    Write-Host "找不到日志目录：$LogDirectory"
    exit 2
}
if ($Days -lt 1) {
    Write-Host "参数 -Days 必须为正整数。"
    exit 2
}

$cutoff = (Get-Date).AddDays(-$Days)
$files = Get-ChildItem -LiteralPath $LogDirectory -Filter 'autonet-????-??-??.jsonl' -File |
    Where-Object { $_.LastWriteTime -ge $cutoff } | Sort-Object Name
if ($files.Count -eq 0) {
    Write-Host "近 $Days 天没有服务日志：$LogDirectory"
    exit 0
}

$cycles = [System.Collections.Generic.List[object]]::new()
$steps = [System.Collections.Generic.List[object]]::new()
$badLines = 0
foreach ($file in $files) {
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $entry = $line | ConvertFrom-Json -ErrorAction Stop } catch { $badLines++; continue }
        $code = [string]$entry.eventCode
        if ($code -notin @('SlowCycle', 'SlowStep')) { continue }
        $item = [pscustomobject]@{
            Time         = [string]$entry.time
            Code         = $code
            Stage        = [string]$entry.diagnostics.stage
            Milliseconds = [double]$entry.diagnostics.elapsedMilliseconds
            Detail       = [string]$entry.detail
        }
        if ($code -eq 'SlowCycle') { $cycles.Add($item) } else { $steps.Add($item) }
    }
}

Write-Host "日志目录：$LogDirectory"
Write-Host ("文件数：{0}；无法解析行：{1}" -f $files.Count, $badLines)

if ($cycles.Count -eq 0 -and $steps.Count -eq 0) {
    Write-Host "近 $Days 天没有 SlowCycle / SlowStep 事件：未观察到慢轮次或慢阶段。"
    exit 0
}

Write-Host ""
Write-Host ("慢轮次（整轮 >= 5000 ms）：{0} 条" -f $cycles.Count)
foreach ($cycle in ($cycles | Sort-Object Time)) {
    Write-Host ("  {0}  总耗时 {1} ms" -f $cycle.Time, $cycle.Milliseconds)
    Write-Host ("    {0}" -f $cycle.Detail)
}

Write-Host ""
Write-Host ("慢阶段（阶段 >= 500 ms）：{0} 条" -f $steps.Count)
$steps |
    Group-Object Stage |
    ForEach-Object {
        $total = ($_.Group | Measure-Object -Property Milliseconds -Sum).Sum
        $max = ($_.Group | Measure-Object -Property Milliseconds -Maximum).Maximum
        [pscustomobject]@{
            Stage = $_.Name
            Count = $_.Count
            Total = [math]::Round($total, 1)
            Max   = [math]::Round($max, 1)
        }
    } |
    Sort-Object Total -Descending |
    Format-Table -AutoSize |
    Out-String |
    Write-Host

Write-Host "说明：阶段可能重叠，阶段耗时之和不等于整轮耗时；这里只计数和排名，不做归因结论。"
exit 0
