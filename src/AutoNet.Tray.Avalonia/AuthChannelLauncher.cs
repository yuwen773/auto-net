using AutoNet.Core;
using System.Diagnostics;

namespace AutoNet.Tray.Avalonia;

/// <summary>诊断页只读投影：通道模式（服务广播）、结构信号、最近结论与忙态。</summary>
internal sealed record AuthChannelLaunchState(
    string Mode, bool SignalPresent, bool Requested, string? LastConclusion, bool Busy);

/// <summary>
/// 内嵌认证通道发起器（工单 #24 / ADR-0009）：消费服务置位的 AuthChannelRequested 授予
/// （授予键 = NotBefore，进程内去重），经 UAC 提权启动辅助进程——辅助进程自身读取机器级
/// 凭据（LocalMachine DPAPI），凭据不经托盘进程、不经命令行。发起前重读可信快照；
/// ManualOnly 仅诊断页手动入口可达；Off 一律拒绝。结果文件在下一轮观察时回读
/// （路径 + 时刻双核对 → 结论映射），结论经诊断页呈现；气泡通知与可见化属工单 #25。
/// </summary>
internal sealed class AuthChannelLauncher
{
    public enum ObserveOutcome
    {
        Launched,
        NoGrant,
        ModeOff,
        AlreadyConsumed,
        UntrustedState,
        UacDeclined,
        StartFailed,
        ResultConsumed,
    }

    public const string ResultFileName = "auth-channel-result.json";

    private readonly Func<Task<StatusSnapshot?>> refresh;
    private readonly Func<string, Process?> start;
    private readonly Action<NotificationAlert>? presentAlert;
    private readonly Func<Task>? openPortalFallback;
    private readonly string resultPath;
    private DateTimeOffset? consumedGrant;
    private DateTimeOffset? launchedAt;
    private string? lastConclusion;
    private StatusSnapshot? lastReading;

    public AuthChannelLauncher(
        Func<Task<StatusSnapshot?>> refresh,
        Func<string, Process?> start,
        Action<NotificationAlert>? presentAlert = null,
        Func<Task>? openPortalFallback = null)
    {
        this.refresh = refresh;
        this.start = start;
        this.presentAlert = presentAlert;
        this.openPortalFallback = openPortalFallback;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoNet");
        Directory.CreateDirectory(dir);
        resultPath = Path.Combine(dir, ResultFileName);
    }

    public string ResultPath => resultPath;

    /// <summary>诊断页手动入口（无参）：使用最近一次管道快照；无快照视为状态不可信。</summary>
    public ObserveOutcome LaunchManually()
    {
        if (lastReading is not { } reading) return ObserveOutcome.UntrustedState;
        return LaunchManually(reading);
    }

    /// <summary>诊断页投影：模式/信号/最近结论。</summary>
    public AuthChannelLaunchState Describe() => Describe(lastReading);

    /// <summary>诊断页手动入口（ManualOnly/Automatic 可达）：信号存在即允许，Off 拒绝。</summary>
    public ObserveOutcome LaunchManually(StatusSnapshot reading)
    {
        if (reading.AuthChannelMode == AuthChannelModes.Off) return ObserveOutcome.ModeOff;
        if (!reading.PortalLayoutSuspected) return ObserveOutcome.UntrustedState;
        return Launch(reading);
    }

    /// <summary>管道观察入口：缓存快照（诊断页投影用）、顺带回读上次结果，再处理自动授予。</summary>
    public async Task<ObserveOutcome> ObserveAsync(StatusSnapshot? reading)
    {
        lastReading = reading;
        if (reading is null) { ConsumeResult(new StatusSnapshot()); return ObserveOutcome.NoGrant; }
        ConsumeResult(reading);
        if (!reading.AuthChannelRequested || reading.AuthChannelNotBefore is not { } grant) return ObserveOutcome.NoGrant;
        if (consumedGrant == grant) { TryConsumeResultFile(); return ObserveOutcome.AlreadyConsumed; }
        // 发起前重验：快照不可信、请求消失或授予时间戳已变 → 不发起（故事 68 同型）。
        var fresh = await refresh();
        if (fresh?.AuthChannelRequested != true || fresh.AuthChannelNotBefore != grant) return ObserveOutcome.UntrustedState;
        var outcome = Launch(reading);
        if (outcome == ObserveOutcome.Launched || outcome == ObserveOutcome.UacDeclined || outcome == ObserveOutcome.StartFailed)
        {
            // UAC 拒绝与启动失败同样视为本授予已消费：绝不自我重试、绝不反复打扰。
            consumedGrant = grant;
            if (outcome != ObserveOutcome.Launched) launchedAt = null;
        }
        return outcome;
    }

    /// <summary>诊断页投影：模式/信号/最近结论。</summary>
    public AuthChannelLaunchState Describe(StatusSnapshot? reading) => new(
        Mode: reading?.AuthChannelMode ?? AuthChannelModes.ManualOnly,
        SignalPresent: reading?.PortalLayoutSuspected ?? false,
        Requested: reading?.AuthChannelRequested ?? false,
        LastConclusion: lastConclusion,
        Busy: launchedAt is not null && lastConclusion is null);
    private ObserveOutcome Launch(StatusSnapshot reading)
    {
        if (reading.AuthChannelMode == AuthChannelModes.Off) return ObserveOutcome.ModeOff;
        launchedAt = DateTimeOffset.UtcNow;
        try { File.Delete(resultPath); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            var process = start(resultPath);
            if (process is null)
            {
                // UAC 拒绝 = 用户此刻不要通道；消费标记由调用方置位（同授予不重试）。
                return ObserveOutcome.UacDeclined;
            }
            return ObserveOutcome.Launched;
        }
        catch (Exception ex)
        {
            // 进程隔离（工单 #25）：启动异常不外溢到管道事件流；受控呈现并回退浏览器兜底。
            Present(NotificationCodes.AuthChannelNeedsHuman,
                "内嵌认证通道启动失败（" + ex.GetType().Name + "），已回退浏览器兜底；请人工完成认证。");
            _ = openPortalFallback?.Invoke();
            return ObserveOutcome.StartFailed;
        }
    }

    private void ConsumeResult(StatusSnapshot reading)
    {
        if (launchedAt is null || lastConclusion is not null) return;
        if (!AuthChannelResultFile.TryLoad(resultPath, out var result, out _) || result is null) return;
        // 回执双核对（工单 #23）：结果文件必须就是发起时约定的路径（同机同用户），且
        // 判定时刻不早于发起时刻——伪造或上一轮的陈旧结果在此被拒。
        if (!AuthChannelReceipt.Validate(result, resultPath, resultPath, launchedAt.Value, out _)) return;
        lastConclusion = AuthChannelReceipt.MapToSubmissionOutcome(result.Conclusion)?.ToString()
            ?? "Unknown";
        // 工单 #25：受控关键通知（不静默）。失败回退系统浏览器兜底；需人工呈现窗口提示。
        if (result.Detail.Contains("error=", StringComparison.Ordinal))
        {
            Present(NotificationCodes.AuthChannelNeedsHuman,
                "内嵌认证通道无法运行（" + result.Detail + "），已回退浏览器兜底；请人工完成认证。");
            _ = openPortalFallback?.Invoke();
        }
        else if (result.Conclusion == AuthChannelConclusion.NeedsHuman)
        {
            Present(NotificationCodes.AuthChannelNeedsHuman,
                "内嵌认证通道遇到验证码或页面限制，窗口保持门户页打开，请人工完成剩余步骤。");
        }
        else
        {
            Present(NotificationCodes.AuthChannelResult,
                "内嵌认证通道结论：" + lastConclusion + "。");
        }
        try { File.Delete(resultPath); } catch (IOException) { }
    }

    private void Present(string code, string body) =>
        presentAlert?.Invoke(new NotificationAlert(code, "AutoNet · 内嵌认证通道", body, Warning: true));

    private void TryConsumeResultFile()
    {
        if (launchedAt is not null && lastConclusion is null) ConsumeResult(new StatusSnapshot());
    }
}
