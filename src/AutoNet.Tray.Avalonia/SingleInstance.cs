namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 普通托盘单实例互斥。入口为正式运行传入既有安装身份，为显式 QA 传入独立身份，
/// 保证同一会话只运行一个普通实例，同时允许受控验证与已安装托盘并存。
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
