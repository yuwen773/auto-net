using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 诊断包导出的一次结果载荷（#12）：提权进程写、普通界面读，两侧共用同一形状。
/// 载荷只含目标路径、受控错误类型与采集缺口说明（与包内 README 同源的受控字符串）——
/// 不含账号、密码、Cookie、Token、URL 或任何状态原文。结果文件落在 ProgramData\AutoNet
/// （提权进程可写、普通界面可读的既有共享目录），固定文件名 + 时刻 + 目标路径三重核对
/// 防止旧结果冒充本次结果。
/// </summary>
internal sealed record DiagnosticsExportOutcome(bool Ok, string TargetPath, string? ErrorKind, IReadOnlyList<string> Notes, string At)
{
    /// <summary>生产结果文件名（提权写、普通读；QA 模式重定向到证据目录，不用本字段）。</summary>
    public const string ResultFileName = "export-result.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>容错读取：缺失、损坏或形状不符返回 null（调用方退回仅凭退出码判定）。</summary>
    public static DiagnosticsExportOutcome? TryRead(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<DiagnosticsExportOutcome>(File.ReadAllText(path), Options);
            return file is null || file.At.Length == 0 ? null : file;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>原子写入（提权进程侧；QA 侧也可用，目录由调用方给定）。</summary>
    public void TryWrite(string path)
    {
        try { Storage.AtomicWrite(path, JsonSerializer.Serialize(this, Options)); }
        catch (Exception) { /* 结果文件写不进去不改变退出码裁决；界面按退出码呈现。 */ }
    }

    /// <summary>是否匹配本次导出（目标逐字一致且结果时刻不早于发起时刻，容忍少量时钟偏差）。</summary>
    public bool Matches(string targetPath, DateTimeOffset startedAt) =>
        TargetPath == targetPath
        && DateTimeOffset.TryParse(At, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at)
        && at >= startedAt.AddSeconds(-5);
}

/// <summary>一次提权导出的最终事实：退出码与（可选）结果文件中的缺口说明。</summary>
internal sealed record DiagnosticsExportLaunch(int ExitCode, string? ErrorKind, IReadOnlyList<string> Notes)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>导出流程阶段；文案由控制器组合，本枚举只承载状态机位置。</summary>
internal enum DiagnosticsExportPhase
{
    Idle,
    Picking,
    Exporting,
    Succeeded,
    Cancelled,
    Failed
}

/// <summary>导出卡片的显示状态：消息逐字受控，Running 时按钮禁用（防重复发起）。
/// 成功时携带结果文件披露的缺口明细（故事 80：缺项与采集超时在界面结果中明确标出）。</summary>
internal sealed record DiagnosticsExportState(DiagnosticsExportPhase Phase, string Message, OverviewTone Tone, IReadOnlyList<string>? Notes = null)
{
    public bool Running => Phase is DiagnosticsExportPhase.Picking or DiagnosticsExportPhase.Exporting;

    public IReadOnlyList<string> NotesList => Notes ?? [];

    public static readonly DiagnosticsExportState Initial = new(
        DiagnosticsExportPhase.Idle,
        "导出会先在这里选择保存位置（默认当前用户桌面，同名覆盖需确认），随后通过单独的管理员操作按所选位置只读采集；本界面保持普通权限，期间可继续使用。",
        OverviewTone.Neutral);
}

/// <summary>
/// 诊断包导出流程（迁移工单 #12 / T13 / 故事 71–75、78、81）：用户主动触发的普通界面侧状态机。
/// - 先选保存位置（委托注入：生产为系统保存对话框，默认桌面 + 同名覆盖确认，故事 72、78）；
///   取消即取消，不发起提权、不生成任何文件（故事 74）。
/// - 再以独立提权操作采集（委托注入：生产为 UAC 重启自身并逐字传递所选路径，故事 73）；
///   拒绝 UAC 即取消（故事 74）；等待期间本状态机不阻塞界面（调用方在后台任务上驱动）。
/// - 结束按提权进程退出码呈现成功/失败；结果文件匹配本次导出时补充受控缺口说明（故事 80）。
/// - 全程不改诊断页的只读投影语义；状态只经 <see cref="StateChanged"/> 通知视图重绘。
/// 两次导出不可并发（重入保护）。委托全部注入，本类不依赖 UI 类型，可直接受控测试。
/// </summary>
internal sealed class DiagnosticsExporter(
    Func<Task<string?>> pickTarget,
    Func<string, Task<DiagnosticsExportLaunch?>> launch)
{
    private readonly Func<Task<string?>> pickTarget = pickTarget;
    private readonly Func<string, Task<DiagnosticsExportLaunch?>> launch = launch;
    private int running;

    public DiagnosticsExportState State { get; private set; } = DiagnosticsExportState.Initial;

    /// <summary>状态变化（触发线程 = 流程任务线程；UI 侧负责调度）。</summary>
    public event Action? StateChanged;

    /// <summary>发起一次导出流程；进行中再次发起直接忽略（重入保护）。</summary>
    public async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        try
        {
            await RunCoreAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // 状态机自身的意外一律收束为受控失败：只披露异常类型，旧包与所选位置不受影响。
            Set(new DiagnosticsExportState(DiagnosticsExportPhase.Failed,
                "导出未完成（" + e.GetType().Name + "）；原有文件保持不变。", OverviewTone.Problem));
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private async Task RunCoreAsync()
    {
        Set(new DiagnosticsExportState(DiagnosticsExportPhase.Picking,
            "请选择保存位置（默认当前用户桌面，同名覆盖会先确认）；取消即放弃导出。", OverviewTone.Neutral));

        string? target;
        try
        {
            target = await pickTarget().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Set(new DiagnosticsExportState(DiagnosticsExportPhase.Failed,
                "保存位置选择未完成（" + e.GetType().Name + "）；未生成任何文件。", OverviewTone.Problem));
            return;
        }

        // 故事 74：取消保存即取消导出——不发起提权、不生成任何文件。
        if (target is null)
        {
            Set(new DiagnosticsExportState(DiagnosticsExportPhase.Cancelled,
                "已取消保存；未生成任何文件。", OverviewTone.Neutral));
            return;
        }

        Set(new DiagnosticsExportState(DiagnosticsExportPhase.Exporting,
            "已选择保存位置，等待管理员授权后开始只读采集；拒绝授权即取消。期间本界面可继续使用。",
            OverviewTone.Attention));

        DiagnosticsExportLaunch? result;
        try
        {
            result = await launch(target).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Set(new DiagnosticsExportState(DiagnosticsExportPhase.Failed,
                "导出未完成（" + e.GetType().Name + "）；原有文件保持不变。", OverviewTone.Problem));
            return;
        }

        // 故事 74：拒绝 UAC 即取消导出，不生成任何文件。
        if (result is null)
        {
            Set(new DiagnosticsExportState(DiagnosticsExportPhase.Cancelled,
                "已取消管理员授权；未生成任何文件。", OverviewTone.Neutral));
            return;
        }

        if (result.Ok)
        {
            // 故事 80：缺口与超时说明逐项在界面结果中列出（与包内 README 同源的受控字符串）。
            Set(new DiagnosticsExportState(DiagnosticsExportPhase.Succeeded,
                "诊断包已导出：" + target, OverviewTone.Good, result.Notes));
            return;
        }

        Set(new DiagnosticsExportState(DiagnosticsExportPhase.Failed,
            "导出未完成（" + (result.ErrorKind ?? "ExitCode" + result.ExitCode) + "）；原有文件保持不变。", OverviewTone.Problem));
    }

    private void Set(DiagnosticsExportState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
