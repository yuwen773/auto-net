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
        Busy,
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
    private readonly Func<bool>? launchActive;
    private readonly string resultPath;
    private DateTimeOffset? consumedGrant;
    private DateTimeOffset? launchedAt;
    private DateTimeOffset? lastConsumedAt;
    private string? lastConclusion;
    private Process? activeProcess;
    private StatusSnapshot? lastReading;

    public AuthChannelLauncher(
        Func<Task<StatusSnapshot?>> refresh,
        Func<string, Process?> start,
        Action<NotificationAlert>? presentAlert = null,
        Func<Task>? openPortalFallback = null,
        string? resultPath = null,
        Func<bool>? launchActive = null)
    {
        this.refresh = refresh;
        this.start = start;
        this.presentAlert = presentAlert;
        this.openPortalFallback = openPortalFallback;
        this.launchActive = launchActive;
        this.resultPath = resultPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoNet", ResultFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(this.resultPath))!);
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
        if (outcome == ObserveOutcome.Launched || outcome == ObserveOutcome.UacDeclined || outcome == ObserveOutcome.StartFailed || outcome == ObserveOutcome.Busy)
        {
            // UAC 拒绝与启动失败同样视为本授予已消费：绝不自我重试、绝不反复打扰。
            consumedGrant = grant;
            if (outcome is ObserveOutcome.UacDeclined or ObserveOutcome.StartFailed) launchedAt = null;
        }
        return outcome;
    }

    /// <summary>
    /// 内嵌认证通道会话是否正在进行（工单 #39）：本进程发起的辅助进程仍存活，或外部启动锁
    /// 仍被持有（例如托盘重启后由上一实例启动的通道）。认证页据此不另起第二个浏览器。
    /// </summary>
    public bool SessionActive => IsProcessActive() || launchActive?.Invoke() == true;

    /// <summary>诊断页投影：模式/信号/最近结论。</summary>
    public AuthChannelLaunchState Describe(StatusSnapshot? reading) => new(
        Mode: reading?.AuthChannelMode ?? AuthChannelModes.ManualOnly,
        SignalPresent: reading?.PortalLayoutSuspected ?? false,
        Requested: reading?.AuthChannelRequested ?? false,
        LastConclusion: lastConclusion,
        Busy: SessionActive);
    private ObserveOutcome Launch(StatusSnapshot reading)
    {
        if (reading.AuthChannelMode == AuthChannelModes.Off) return ObserveOutcome.ModeOff;
        lastConclusion = null;
        lastConsumedAt = null;
        if (SessionActive) return ObserveOutcome.Busy;
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
            activeProcess = process;
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

    private bool IsProcessActive()
    {
        if (activeProcess is null) return false;
        try
        {
            if (!activeProcess.HasExited) return true;
            activeProcess.Dispose();
            activeProcess = null;
            return false;
        }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private void ConsumeResult(StatusSnapshot reading)
    {
        if (launchedAt is null) return;
        if (!AuthChannelResultFile.TryLoad(resultPath, out var result, out _) || result is null) return;
        // 回执双核对（工单 #23）：结果文件必须就是发起时约定的路径（同机同用户），且
        // 判定时刻不早于发起时刻——伪造或上一轮的陈旧结果在此被拒。
        if (!AuthChannelReceipt.Validate(result, resultPath, resultPath, launchedAt.Value, out _)) return;
        // 工单 #40 / ADR-0019：人工等待期间允许同一会话补写更新的结果（例如手动登录成功）。
        // 只按判定时刻去重，不因已消费过第一条就永久拒绝；重复或更旧的判定直接清理。
        if (lastConsumedAt is { } last && result.DetectedAt <= last)
        {
            try { File.Delete(resultPath); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return;
        }
        lastConsumedAt = result.DetectedAt;
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
        if (launchedAt is not null) ConsumeResult(new StatusSnapshot());
    }
}
