using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray;

internal static class TrayRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("NEW-01：主窗口已暂停、过期和缺失状态的恢复操作语义一致", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.WriteStatus(Online() with { Enabled = false });
            var commands = new List<string>();
            using var form = new MainForm(commands.Add) { AllowExit = true };
            var action = Field<Button>(form, "toggle");
            Require(action.Enabled && action.Text == "恢复自动恢复");
            fixture.WriteStatus(Online() with { Enabled = false, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            form.RefreshStatus();
            Require(!action.Enabled && action.Text == "自动恢复状态待确认", "过期状态仍显示可点击的暂停按钮");
            action.PerformClick();
            form.Toggle();
            File.Delete(fixture.StatusPath);
            form.RefreshStatus();
            Require(!action.Enabled && action.Text == "自动恢复状态待确认", "缺失状态仍允许改变自动恢复");
            action.PerformClick();
            form.Toggle();
            foreach (var snapshot in new[] { Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(30) }, Online() with { Tunnel = null! } })
            {
                fixture.WriteStatus(snapshot);
                form.RefreshStatus();
                action.PerformClick();
                form.Toggle();
                Require(!action.Enabled && action.Text == "自动恢复状态待确认");
            }
            var missingEnabled = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Online()))!.AsObject();
            missingEnabled.Remove("Enabled");
            File.WriteAllText(fixture.StatusPath, missingEnabled.ToJsonString());
            form.RefreshStatus();
            Require(!action.Enabled, "没有明确 Enabled 字段时不得猜测当前恢复设置");
            Require(commands.Count == 0, "不可信状态不应产生任何设置命令");
            Capture(form, "recovery-action-unconfirmed.png");
        })),
        ("NEW-01：托盘入口同步已暂停、过期和缺失状态且禁止绕过", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.WriteStatus(Online() with { Enabled = false });
            var commands = new List<string>();
            using var form = new MainForm(commands.Add);
            using var context = new TrayContext(true, form);
            try
            {
                var menu = Field<NotifyIcon>(context, "tray").ContextMenuStrip!;
                var action = (ToolStripMenuItem)menu.Items[2];
                Require(action.Enabled && action.Text == "恢复自动恢复");
                fixture.WriteStatus(Online() with { Enabled = false, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
                // Opening the actual tray menu refreshes trust before an operation becomes selectable.
                Method(menu, "OnOpening").Invoke(menu, [new System.ComponentModel.CancelEventArgs()]);
                Require(!action.Enabled && action.Text == "自动恢复状态待确认", "过期状态的托盘入口未禁用");
                action.PerformClick();
                File.Delete(fixture.StatusPath);
                Method(menu, "OnOpening").Invoke(menu, [new System.ComponentModel.CancelEventArgs()]);
                Require(!action.Enabled && action.Text == "自动恢复状态待确认", "托盘入口仍可绕过主窗口的不可信状态保护");
                action.PerformClick();
                Require(commands.Count == 0);
            }
            finally { context.ExitThread(); }
        })),
        ("NEW-01：主窗口与托盘发送明确意图且点击时重新验证快照", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.WriteStatus(Online());
            var commands = new List<string>();
            var settings = new Settings { Enabled = false, WifiProfile = "虚构配置" };
            using var form = new MainForm(command =>
            {
                commands.Add(command);
                AutoNet.Tray.Program.SetRecoveryEnabled(AutoNet.Tray.Program.RecoveryTarget([command])!.Value,
                    () => settings, value => settings = value);
            });
            ShowForQa(form);
            using var context = new TrayContext(true, form);
            try
            {
                var button = Field<Button>(form, "toggle");
                var menu = (ToolStripMenuItem)Field<NotifyIcon>(context, "tray").ContextMenuStrip!.Items[2];
                Require(button.Text == "暂停自动恢复" && menu.Text == button.Text);
                // The user saw "pause", but the latest snapshot and configuration have already paused.
                fixture.WriteStatus(Online() with { Enabled = false });
                button.PerformClick();
                Require(commands.SequenceEqual(["--disable-recovery"]) && !settings.Enabled, "暂停操作反向开启了恢复");
                Require(menu.Enabled && menu.Text == "恢复自动恢复");
                // A deliberate resume remains explicit and idempotent even if another actor enabled it first.
                settings = settings with { Enabled = true };
                menu.PerformClick();
                Require(commands[^1] == "--enable-recovery" && settings.Enabled);
                fixture.WriteStatus(Online());
                form.RefreshStatus();
                fixture.WriteStatus(Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
                button.PerformClick();
                Require(commands.Count == 2 && !button.Enabled && !menu.Enabled, "点击时快照过期仍执行了命令");
                fixture.WriteStatus(Online());
                form.RefreshStatus();
                File.Delete(fixture.StatusPath);
                menu.PerformClick();
                Require(commands.Count == 2 && !button.Enabled && !menu.Enabled, "托盘点击时快照缺失仍执行了命令");
            }
            finally { context.ExitThread(); }
        })),
        ("NEW-01：管理员执行明确状态命令不会反转配置且保留最新其他设置", () => RunSta(() =>
        {
            Require(AutoNet.Tray.Program.RecoveryTarget(["--enable-recovery"]) == true);
            Require(AutoNet.Tray.Program.RecoveryTarget(["--disable-recovery"]) == false);
            Require(AutoNet.Tray.Program.RecoveryTarget(["--tray"]) is null);
            foreach (var args in new[] { new[] { "--toggle" }, new[] { "--enable-recovery", "--disable-recovery" } })
            {
                try { AutoNet.Tray.Program.RecoveryTarget(args); throw new Exception("危险或矛盾命令未被拒绝"); }
                catch (ArgumentException) { }
            }
            foreach (var initial in new[] { false, true })
                foreach (var target in new[] { false, true })
                {
                    var current = new Settings { Enabled = initial, WifiProfile = "授权期间更新的配置", AllowActiveDisconnect = true };
                    var saved = current;
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        AutoNet.Tray.Program.SetRecoveryEnabled(target, () => saved, value => saved = value);
                        Require(saved.Enabled == target && saved == current with { Enabled = target });
                    }
                }
        })),
        ("托盘加固：五万次固定标志刷新复用控件且资源稳定", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.WriteStatus(Online());
            using var form = new MainForm { AllowExit = true };
            ShowForQa(form);
            form.RefreshStatus();
            var strip = Field<Panel>(form, "additionalStrip");
            var first = strip.Controls[0];
            var chip = first.Controls[0];
            var render = Method(form, "RenderAdditional");
            var display = NetworkDisplayMap.Map(Online(), TimeSpan.Zero);
            void Refresh(int count)
            {
                for (var i = 0; i < count; i++) render.Invoke(form, [display]);
                Application.DoEvents();
            }
            Refresh(1000);
            var baseline = Resources();
            var samples = new List<object>();
            for (var batch = 0; batch < 5; batch++)
            {
                Refresh(10_000);
                var sample = Resources();
                samples.Add(new { refreshes = (batch + 1) * 10_000, sample.Gdi, sample.Managed, sample.Private });
                Require(strip.Controls.Count == 1 && ReferenceEquals(first, strip.Controls[0]));
                Require(first.Controls.Count == 1 && ReferenceEquals(chip, first.Controls[0]));
                Require(sample.Gdi <= baseline.Gdi + 4, "固定状态 GDI 持续增长");
                Require(sample.Managed <= baseline.Managed + 2 * 1024 * 1024, "固定状态托管内存持续增长");
                Require(sample.Private <= baseline.Private + 16 * 1024 * 1024, "固定状态私有内存超出暖机余量");
            }
            // Exercise the real JSON -> timer refresh path as well, without relying on wall-clock freshness.
            for (var i = 0; i < 500; i++) { fixture.WriteStatus(Online()); form.RefreshStatus(); }
            Require(ReferenceEquals(first, strip.Controls[0]));
            for (var i = 0; i < 1000; i++)
            {
                var old = strip.Controls[0];
                render.Invoke(form, [display with { Additional = i % 2 == 0
                    ? NetworkAdditional.AuthenticationSuspended : NetworkAdditional.EgressUnverified }]);
                Require(old.IsDisposed, "标志变化时旧控件必须释放");
                Require(strip.Controls.Count == 1 && strip.Controls[0].Controls.Count == 1);
            }
            Require(ReferenceEquals(Theme.Font(9), Theme.Font(9)), "相同字体应共享生命周期");
            var afterChanges = Resources();
            Require(afterChanges.Gdi <= baseline.Gdi + 8, "切换标志后 GDI 未释放");
            Evidence("resources.json", new { baseline, samples, afterChanges, fixedRefreshes = 50_000, fullRefreshes = 500, transitions = 1000 });
        })),
        ("托盘加固：空值、未来、过期状态在标题服务诊断区域一致降级", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.WriteStatus(Online() with { CredentialsBlocked = true, AuthenticationFailure = "BindingRejected" });
            using var form = new MainForm { AllowExit = true };
            _ = form.Handle;
            Require(form.RefreshStatus() is not null);
            Require(Field<Label>(form, "stateTitle").Text == "网络在线");
            Require(Field<Panel>(form, "additionalStrip").Controls[0].Controls.Cast<Label>().Any(c => c.Text == "认证提交已暂停"));
            Capture(form, "online-binding-blocked.png");
            foreach (var (name, snapshot) in new[]
            {
                ("future", Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(30) }),
                ("stale", Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }),
                ("null-tunnel", Online() with { Tunnel = null! }),
                ("missing-time", Online() with { UpdatedAt = default })
            })
            {
                fixture.WriteStatus(snapshot);
                Require(form.RefreshStatus() is null);
                Require(Field<Label>(form, "stateTitle").Text != "网络在线");
                Require(!Field<Label>(form, "serviceStatus").Text.Contains("运行中"));
                Require(Field<Dictionary<string, Label>>(form, "values")["校园网直连"].Text == "待检测");
                Capture(form, name + ".png");
            }
            File.WriteAllText(fixture.StatusPath, "{not json");
            Require(form.RefreshStatus() is null);
        })),
        ("托盘加固：两千次日志切换合并为单一读取且仅最新结果可见", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var calls = 0;
            var active = 0;
            var maxActive = 0;
            IReadOnlyList<ActivityLog> Read(string path, CancellationToken token)
            {
                var count = Interlocked.Increment(ref calls);
                maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
                try
                {
                    if (count == 1)
                    {
                        started.Set();
                        Require(release.Wait(TimeSpan.FromSeconds(10)), "读取等待超时");
                        // Deliberately ignore cancellation: even a slow reader must not overlap its replacement.
                    }
                    return [new() { Time = DateTimeOffset.UtcNow, Action = Path.GetFileName(path), Detail = "夹具事件" }];
                }
                finally { Interlocked.Decrement(ref active); }
            }
            using var form = new LogForm(Read);
            _ = form.Handle;
            var pending = Field<Task>(form, "pending");
            PumpUntil(() => started.IsSet);
            var dates = Field<ComboBox>(form, "date");
            try
            {
                for (var i = 0; i < 2000; i++)
                {
                    dates.SelectedIndex = i % 2;
                    Method(form, "LoadEntries").Invoke(form, null);
                }
                dates.SelectedIndex = 1;
                Require(calls == 1 && maxActive == 1, "刷新产生了并行读取");
            }
            finally { release.Set(); }
            PumpUntil(() => pending.IsCompleted);
            Require(!pending.IsFaulted && calls == 2 && maxActive == 1, "请求未合并为当前读取加最新请求");
            Require(Field<List<ActivityLog>>(form, "loaded").Single().Action == "autonet-2026-10-10.jsonl", "旧结果覆盖了最新日期");
            Evidence("log-loading.json", new { requests = 2000, reads = calls, maxConcurrentReads = maxActive });
        })),
        ("托盘加固：日志关闭取消读取且读取失败后仍可刷新", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            using var started = new ManualResetEventSlim();
            using (var form = new LogForm((_, token) =>
            {
                started.Set();
                Require(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "关闭未取消读取");
                token.ThrowIfCancellationRequested();
                return [];
            }))
            {
                _ = form.Handle;
                var pending = Field<Task>(form, "pending");
                PumpUntil(() => started.IsSet);
                form.Close();
                Require(form.IsDisposed);
                PumpUntil(() => pending.IsCompleted);
                Require(!pending.IsFaulted, "窗口关闭后读取异常逃逸");
            }
            var calls = 0;
            using var retry = new LogForm((_, _) => ++calls == 1
                ? throw new IOException("夹具读取错误") : [new() { Action = "恢复联网" }]);
            _ = retry.Handle;
            PumpUntil(() => Field<Task>(retry, "pending").IsCompleted);
            Require(Field<TextBox>(retry, "detail").Text.Contains("暂时无法读取"));
            Method(retry, "LoadEntries").Invoke(retry, null);
            PumpUntil(() => Field<Task>(retry, "pending").IsCompleted);
            Require(Field<List<ActivityLog>>(retry, "loaded").Count == 1 && calls == 2);
        })),
        ("托盘加固：日志界面按全部结构化代码筛选并可渲染", () => RunSta(() =>
        {
            using var fixture = new Fixture();
            var label = typeof(AutoNet.Service.MonitorWorker).Assembly.GetType("AutoNet.Service.RecoveryJournal")!
                .GetMethod("DisplayAction", BindingFlags.NonPublic | BindingFlags.Static)!;
            var records = LogCentreRegression.Events().Select(e => new ActivityLog
            {
                SchemaVersion = 2, EventCode = e.Code, Time = DateTimeOffset.UtcNow,
                Action = (string)label.Invoke(null, [e.Code])!, Detail = "虚构事件；不代表现场验证通过。"
            }).ToArray();
            using var form = new LogForm((_, _) => records);
            ShowForQa(form);
            PumpUntil(() => Field<Task>(form, "pending").IsCompleted);
            var filter = Field<ComboBox>(form, "filter");
            var list = Field<ListView>(form, "list");
            foreach (var (index, count) in new[] { (0, 21), (1, 11), (2, 7), (3, 3) })
            {
                filter.SelectedIndex = index;
                Require(list.Items.Count == count, $"筛选 {index} 预期 {count} 条，实际 {list.Items.Count}");
                Capture(form, $"log-filter-{index}.png");
            }
        }))
    ];

    private static StatusSnapshot Online() => new()
    {
        Enabled = true, State = "校园网在线", Direct = "外网可达", Wifi = "已连接",
        Portal = "未查询（外网检查已通过）", Tunnel = "系统路径可达，出口未核验", Uu = "未核验"
    };

    private static Task RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                test();
            }
            catch (Exception e) { failure = e; }
            finally { Theme.ReleaseFonts(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(60)), "UI 专项测试超时");
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(10), "等待 UI 读取完成超时");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static MethodInfo Method(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static string EvidenceDirectory => Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ui-fix");
    private static void Evidence(string name, object value)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        File.WriteAllText(Path.Combine(EvidenceDirectory, name), JsonSerializer.Serialize(value, Storage.Json));
    }

    private static void Capture(Form form, string name)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        if (!form.Visible) ShowForQa(form);
        void Settle(Control control)
        {
            foreach (Control child in control.Controls) Settle(child);
            control.PerformLayout();
        }
        Settle(form);
        Application.DoEvents();
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(Path.Combine(EvidenceDirectory, name));
    }

    private static void ShowForQa(Form form)
    {
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();
    }

    private sealed record ResourceSample(int Gdi, long Managed, long Private);

    private static ResourceSample Resources()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        return new(GetGuiResources(process.Handle, 0), GC.GetTotalMemory(true), process.PrivateMemorySize64);
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "autonet-ui-fix-" + Guid.NewGuid().ToString("N"));
        private readonly string? previousStatus = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
        private readonly string? previousLogs = Environment.GetEnvironmentVariable("AUTONET_QA_LOG_DIRECTORY");
        public string StatusPath => Path.Combine(directory, "status.json");

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("AUTONET_QA_STATUS_PATH", StatusPath);
            Environment.SetEnvironmentVariable("AUTONET_QA_LOG_DIRECTORY", directory);
            File.WriteAllText(Path.Combine(directory, "autonet-2026-10-11.jsonl"), "");
            File.WriteAllText(Path.Combine(directory, "autonet-2026-10-10.jsonl"), "");
        }

        public void WriteStatus(StatusSnapshot status) => File.WriteAllText(StatusPath, JsonSerializer.Serialize(status, Storage.Json));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("AUTONET_QA_STATUS_PATH", previousStatus);
            Environment.SetEnvironmentVariable("AUTONET_QA_LOG_DIRECTORY", previousLogs);
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
