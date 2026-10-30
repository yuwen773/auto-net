namespace AutoNet.Core;

/// <summary>
/// 保存锁（SettingsWriteLock，#41 审查修复）：设置保存的跨进程互斥。settings.json 与 credentials.dpapi 的保存都是
/// “读取既有值 → 写凭据 → 写设置”；多个 Windows 会话、兼容提权入口或新的设置页同时保存时，
/// 较早读取的旧值会覆盖另一方刚保存的新地址或新密码。这里用受限目录内的锁文件把整段读改写串行化：
/// - 锁文件以 <see cref="FileShare.None"/> 打开，Windows 共享模式在整机范围内生效，跨进程、跨会话都互斥；
/// - 持有者退出或崩溃时系统关闭句柄，锁随之释放；残留的零字节锁文件本身不阻塞下一次获取；
/// - 只串行化保存，**不提供** settings.json 与 credentials.dpapi 之间的跨文件原子性：
///   写入阶段崩溃仍可能只写入其中一个文件，调用方必须如实报告部分写入。
/// </summary>
public static class SettingsWriteLock
{
    /// <summary>生产保存路径：与受限配置同一目录，随目录 ACL 只允许管理员与 SYSTEM 访问。</summary>
    public static string ProductionPath => Path.Combine(Storage.Private, "settings.lock");

    /// <summary>生产获取方式：最多等待 5 秒，超时以 <see cref="TimeoutException"/> 报告竞争。</summary>
    public static IDisposable Production() => Acquire(ProductionPath, TimeSpan.FromSeconds(5));

    /// <summary>
    /// 获取保存锁；锁被其他进程或会话持有时按 25 毫秒重试，直到超时抛出 <see cref="TimeoutException"/>。
    /// 其他 I/O 错误（目录缺失、ACL 拒绝等）立即抛出，不伪装成锁竞争。
    /// </summary>
    public static IDisposable Acquire(string path, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException e) when (IsSharingViolation(e))
            {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("设置保存锁在超时内未取得。", e);
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Windows 共享/锁定冲突（ERROR_SHARING_VIOLATION、ERROR_LOCK_VIOLATION）。</summary>
    private static bool IsSharingViolation(IOException e) =>
        e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
}
