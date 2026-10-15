namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 普通托盘单实例互斥。迁移期间使用独立互斥名，避免与本机已安装的 WinForms 托盘（Local\AutoNet.Tray）
/// 相互阻塞；候选包替换旧实现时（工单 #15）须沿用原名，保证同一会话只运行一个普通实例。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;

    private SingleInstance(Mutex mutex, bool acquired)
    {
        this.mutex = mutex;
        Acquired = acquired;
    }

    public bool Acquired { get; }

    public static SingleInstance Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    public void Dispose()
    {
        if (Acquired)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }

        mutex.Dispose();
    }
}
