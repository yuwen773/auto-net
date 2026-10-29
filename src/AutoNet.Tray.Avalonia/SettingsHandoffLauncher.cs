using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;

namespace AutoNet.Tray.Avalonia;

internal static class SettingsHandoffLauncher
{
    public const string Command = "--save-settings-handoff";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>只处理一次交接，不开 UI、不参与单实例、不输出参数或异常，不重新请求 UAC。</summary>
    public static async Task<int> RunReceiverAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != Command || !Guid.TryParseExact(args[1], "N", out var id) || id == Guid.Empty ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var senderId) || senderId <= 0 ||
            !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var startedAt) || startedAt <= 0)
            return 2;
        try
        {
            var receiver = HandoffPeer.Capture(Environment.ProcessId);
            if (!receiver.Elevated) return 2;
            var sender = HandoffPeer.Capture(senderId);
            if (sender.StartedAt != startedAt || sender.SessionId != receiver.SessionId ||
                !sender.ImagePath.Equals(receiver.ImagePath, StringComparison.OrdinalIgnoreCase))
                return 2;
            var request = new HandoffRequest(id, senderId, startedAt);
            var result = await SettingsHandoff.ReceiveAsync(request, sender, ElevatedSettings.Production().Save, Timeout);
            return result.Outcome switch
            {
                HandoffOutcome.Completed => result.SaveOutcome == SettingsSaveOutcome.Saved ? 0 : 3,
                HandoffOutcome.TimedOut => 4,
                HandoffOutcome.Cancelled => 5,
                HandoffOutcome.OutcomeUnknown => 6,
                _ => 2
            };
        }
        catch (Win32Exception) { return 2; }
        catch (UnauthorizedAccessException) { return 2; }
        catch (InvalidOperationException) { return 2; }
    }

    public static Task<HandoffResult> SaveAsync(SettingsDraft draft, CancellationToken cancellationToken = default) =>
        SaveAsync(draft, Elevation.StartProcess, cancellationToken);

    public static async Task<HandoffResult> SaveAsync(SettingsDraft draft, Func<ProcessStartInfo, Process?> start,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new(HandoffOutcome.Cancelled);
        var channel = new SettingsHandoff(Timeout);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(Command);
        info.ArgumentList.Add(channel.Request.Id.ToString("N"));
        info.ArgumentList.Add(channel.Request.SenderProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(channel.Request.SenderStartedAt.ToString(CultureInfo.InvariantCulture));
        try
        {
            // ShellExecute 的 UAC 等待不阻塞主窗口；取消之后即便提权进程启动也不交出载荷。
            using var process = await Task.Run(() => start(info));
            if (process is null || cancellationToken.IsCancellationRequested) return new(HandoffOutcome.Cancelled);
            var receiver = HandoffPeer.Capture(process);
            var sender = HandoffPeer.Capture(Environment.ProcessId);
            if (!receiver.Elevated || receiver.SessionId != sender.SessionId ||
                !receiver.ImagePath.Equals(sender.ImagePath, StringComparison.OrdinalIgnoreCase))
                return new(HandoffOutcome.Rejected);
            return await channel.SendAsync(draft, receiver, cancellationToken);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return new(HandoffOutcome.Cancelled); }
        catch (Win32Exception) { return new(HandoffOutcome.Rejected); }
        catch (InvalidOperationException) { return new(HandoffOutcome.Rejected); }
        catch (UnauthorizedAccessException) { return new(HandoffOutcome.Rejected); }
    }
}
