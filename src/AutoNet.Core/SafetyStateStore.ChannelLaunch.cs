namespace AutoNet.Core;

public sealed partial class SafetyStateStore
{
    // Held by the elevated channel helper for its whole lifetime. A tray restart must not
    // launch a second helper while the first is still running: the OS releases the handle
    // when the process exits, so a leftover file never blocks a later launch.
    public IDisposable? TryAcquireChannelLaunch()
    {
        try
        {
            return new FileStream(ChannelLaunchPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>只读探测：共享冲突说明辅助进程仍持有启动锁；无法读取时按不忙处理，
    /// 避免 ACL 异常让通道永久无法启动——真实提交仍由共享执行锁与持久额度兜底。</summary>
    public bool IsChannelLaunchActive()
    {
        if (!Exists(ChannelLaunchPath)) return false;
        try
        {
            using var probe = new FileStream(ChannelLaunchPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string ChannelLaunchPath => Path.Combine(directory, "auth-channel-launch.lock");
}
