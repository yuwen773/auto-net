using System.Diagnostics;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 设置保存的跨进程一致性回归（#41 审查修复）：所有保存入口共享同一保存锁，
/// 用真实独立进程证明“读取既有值 → 写凭据 → 写设置”被串行化，锁竞争按受控结果拒绝且零写入。
/// 全部在隔离目录与虚构凭据上进行；不触碰真实受限存储、校园网、UAC 或已安装服务。
/// 锁只串行化保存，不提供 settings.json 与 credentials.dpapi 的跨文件原子性（由 StoreFailed 如实披露）。
/// </summary>
internal static class SettingsSaveRegression
{
    private const string Username = "TEST-USER-1";
    private const string Password = "TEST-PASSWORD-1";
    private const string OldProxy = "http://127.0.0.1:7897/old";
    private const string NewProxy = "http://127.0.0.1:7897/new?token=TEST-URL-SECRET";

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("设置保存：独立进程在锁内读取最新值而不是打开时的旧值", SerializedAsync),
        ("设置保存：跨进程锁被占用时受控拒绝且零写入", BusyAsync),
        ("设置保存：锁文件残留不阻塞后续保存", StaleLockFileAsync)
    ];

    /// <summary>
    /// 父进程持锁期间写入新地址与关闭的选项，独立保存进程必须在锁内读到它们：
    /// 若保存端先读后锁（或无锁），后写者会用旧地址和页面默认值覆盖。
    /// </summary>
    private static async Task SerializedAsync()
    {
        var directory = NewDirectory();
        try
        {
            var paths = Seed(directory);
            // 先持锁再启动保存进程：保证“读取既有值”一定发生在父进程写入之后。
            var held = SettingsWriteLock.Acquire(LockPath(directory), TimeSpan.FromSeconds(5));
            using var child = StartPeer("--settings-save-peer", directory);
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(ready == "READY", "保存进程未就绪：" + ready);

            Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(new Settings
            {
                InterfaceId = SettingsFixture.Profile.InterfaceId,
                WifiProfile = SettingsFixture.Profile.Name,
                ProxyUrl = NewProxy,
                UuEndpoint = "https://uu.example.test/kept",
                Enabled = false,
                AllowActiveDisconnect = false,
                KeepAwakeOnAc = false,
                AuthChannelMode = AuthChannelModes.Off
            }, Storage.Json));
            held.Dispose();

            var output = await child.StandardOutput.ReadToEndAsync();
            var error = await child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(child.ExitCode == 0 && output.Contains("OUTCOME=Saved", StringComparison.Ordinal),
                "保存进程未成功：" + output + error);

            var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
            Require(actual.ProxyUrl == NewProxy, "后写者用旧地址覆盖了新地址：" + actual.ProxyUrl);
            Require(actual.UuEndpoint == "https://uu.example.test/kept", "后写者覆盖了 UU 地址：" + actual.UuEndpoint);
            Require(!actual.Enabled && !actual.AllowActiveDisconnect && !actual.KeepAwakeOnAc &&
                actual.AuthChannelMode == AuthChannelModes.Off, "后写者用页面默认值覆盖了锁内读到的最新选项");
            Require(SettingsFixture.ReadCredentials(paths.Credentials)?.Password == Password, "保存进程改写了密码");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>另一个进程持有保存锁时，保存必须按受控结果拒绝并零写入；锁释放后同一保存成功。</summary>
    private static async Task BusyAsync()
    {
        var directory = NewDirectory();
        try
        {
            var paths = Seed(directory);
            using var child = StartPeer("--settings-lock-peer", directory, "4000");
            var locked = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(locked == "LOCKED", "锁进程未就绪：" + locked);

            var settingsBefore = File.ReadAllBytes(paths.Settings);
            var credentialsBefore = File.ReadAllBytes(paths.Credentials);
            var editor = Editor(directory, TimeSpan.FromMilliseconds(400));
            var result = editor.Save(Draft());
            Require(result.Outcome == SettingsSaveOutcome.SaveBusy, "锁竞争未被受控拒绝：" + result.Outcome);
            Require(result.Message.Contains("未写入", StringComparison.Ordinal), "锁竞争未说明零写入：" + result.Message);
            Require(File.ReadAllBytes(paths.Settings).SequenceEqual(settingsBefore), "锁竞争仍改写了设置");
            Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), "锁竞争仍改写了凭据");

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(editor.Save(Draft()).Outcome == SettingsSaveOutcome.Saved, "锁释放后保存仍失败");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>持有者退出后锁文件仍在磁盘上：残留的零字节文件本身不得阻塞下一次获取。</summary>
    private static Task StaleLockFileAsync()
    {
        var directory = NewDirectory();
        try
        {
            File.WriteAllText(LockPath(directory), "");
            using (SettingsWriteLock.Acquire(LockPath(directory), TimeSpan.FromSeconds(1))) { }
            using (SettingsWriteLock.Acquire(LockPath(directory), TimeSpan.FromSeconds(1))) { }
            return Task.CompletedTask;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>独立进程入口：以真实保存规则在锁内读取并保存，锁内看到的必须是父进程刚写入的值。</summary>
    public static int RunSavePeer(string[] args)
    {
        var directory = args[1];
        Console.WriteLine("READY");
        Console.Out.Flush();
        var result = Editor(directory, TimeSpan.FromSeconds(30)).Save(Draft());
        Console.WriteLine("OUTCOME=" + result.Outcome);
        return result.Saved ? 0 : 1;
    }

    /// <summary>独立进程入口：持有保存锁指定毫秒数，用于证明锁在真实进程之间生效。</summary>
    public static int RunLockPeer(string[] args)
    {
        using var held = SettingsWriteLock.Acquire(LockPath(args[1]), TimeSpan.FromSeconds(15));
        Console.WriteLine("LOCKED");
        Console.Out.Flush();
        Thread.Sleep(int.Parse(args[2]));
        return 0;
    }

    private static SettingsDraft Draft() => new(
        SettingsFixture.Profile, Username, "", "", "",
        Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true)
    {
        KeepProxyUrl = true,
        KeepUuEndpoint = true,
        KeepEnabled = true,
        KeepAllowActiveDisconnect = true,
        KeepAwakeOnAcKept = true,
        KeepAuthChannelMode = true
    };

    private static ElevatedSettings Editor(string directory, TimeSpan lockTimeout)
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialsPath = Path.Combine(directory, "credentials.dpapi");
        return new ElevatedSettings(
            () => ElevatedSettings.ReadSettings(settingsPath),
            () => SettingsFixture.ReadCredentials(credentialsPath),
            () => [SettingsFixture.Profile],
            credentials => SettingsFixture.WriteCredentials(credentialsPath, credentials),
            settings => Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(settings, Storage.Json)),
            () => SettingsWriteLock.Acquire(LockPath(directory), lockTimeout));
    }

    private static (string Settings, string Credentials) Seed(string directory)
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialsPath = Path.Combine(directory, "credentials.dpapi");
        SettingsFixture.WriteCredentials(credentialsPath, new Credentials(Username, Password));
        Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(new Settings
        {
            InterfaceId = SettingsFixture.Profile.InterfaceId,
            WifiProfile = SettingsFixture.Profile.Name,
            ProxyUrl = OldProxy
        }, Storage.Json));
        return (settingsPath, credentialsPath);
    }

    private static Process StartPeer(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(SettingsSaveRegression).Assembly.Location);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new Exception("无法启动保存进程");
    }

    private static string LockPath(string directory) => Path.Combine(directory, "settings.lock");

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "autonet-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
