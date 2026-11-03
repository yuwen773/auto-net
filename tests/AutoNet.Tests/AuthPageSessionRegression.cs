using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页 WebView2 会话代次（工单 #38）：旧会话事件不得改写新会话、销毁后不得再导航、
/// 临时目录按进程退出等待并在启动时清扫过期目录。真实 WebView2 生命周期仍属实机验收。
/// </summary>
internal static class AuthPageSessionRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("认证页会话：只有当前代次可以继续写状态", () =>
        {
            var first = new AuthPageBrowserSession(1, Path.GetTempPath());
            var second = new AuthPageBrowserSession(2, Path.GetTempPath());
            Require(AuthPageWebViewHost.IsCurrentSession(0, 2, second, second));
            Require(!AuthPageWebViewHost.IsCurrentSession(0, 2, second, first), "旧代次仍被当作当前会话");
            Require(!AuthPageWebViewHost.IsCurrentSession(0, 3, second, second), "代次号不匹配仍被接受");
            Require(!AuthPageWebViewHost.IsCurrentSession(1, 2, second, second), "销毁后的宿主仍接受会话");
            Require(!AuthPageWebViewHost.IsCurrentSession(0, 2, null, second), "已替换/摘除的会话仍被接受");
            second.Dispose();
            Require(!AuthPageWebViewHost.IsCurrentSession(0, 2, second, second), "已销毁的会话仍被接受");
            first.Dispose();
            second.Dispose();
            return Task.CompletedTask;
        }),
        ("认证页会话：迟到的成功、崩溃与退出事件只能作用于当前会话", () =>
        {
            // 旧浏览器的创建在 await 之后才完成：新会话已经开始，旧对象即使自称成功也不得导航或发布。
            var previous = new AuthPageBrowserSession(4, Path.GetTempPath());
            var current = new AuthPageBrowserSession(5, Path.GetTempPath());
            previous.MarkExited();
            Require(!AuthPageWebViewHost.IsCurrentSession(0, 5, current, previous),
                "旧会话创建完成后仍被允许修改新会话");

            // 首个导航失败会拆除会话（active 变为 null）；后到的成功事件同样被丢弃。
            Require(!AuthPageWebViewHost.IsCurrentSession(0, 5, null, previous),
                "导航失败拆除会话后，迟到的成功事件仍被接受");
            // 导航完成只由第一个结果决定：确认成功后迟到的成功/失败都不再重复发布。
            Require(AuthPageNavigationGate.Decide(false, true) == AuthPageNavigationGate.Decision.Loaded);
            Require(AuthPageNavigationGate.Decide(false, false) == AuthPageNavigationGate.Decision.Failed);
            Require(AuthPageNavigationGate.Decide(true, true) == AuthPageNavigationGate.Decision.Ignore);
            Require(AuthPageNavigationGate.Decide(true, false) == AuthPageNavigationGate.Decision.Ignore);

            // 崩溃回调在界面线程排队期间宿主已 Dispose：代次校验必须拒绝。
            current.Dispose();
            Require(!AuthPageWebViewHost.IsCurrentSession(1, 5, current, current), "Dispose 后的崩溃回调仍被接受");
            Require(!AuthPageWebViewHost.IsCurrentSession(0, 5, current, current), "已销毁的会话仍被接受");
            previous.Dispose();
            return Task.CompletedTask;
        }),
        ("认证页会话：取消与销毁幂等且进程退出信号只完成一次", () =>
        {
            var session = new AuthPageBrowserSession(7, Path.GetTempPath());
            Require(!session.Cancellation.IsCancellationRequested && !session.BrowserExited.IsCompleted);
            session.Cancel();
            Require(session.Cancellation.IsCancellationRequested);
            session.Cancel();
            session.MarkExited();
            session.MarkExited();
            Require(session.BrowserExited.IsCompletedSuccessfully);
            Require(!session.IsDisposed);
            session.Dispose();
            Require(session.IsDisposed);
            session.Dispose();
            session.Cancel();
            return Task.CompletedTask;
        }),
        ("认证页会话：加载中挂载表面但不显示重试，只有成功后才算附着", () =>
        {
            var attaching = AuthPageModel.From(null,
                new AuthPageHostSnapshot(AuthPageHostStatus.Attaching, AuthPageModel.PortalAddress, false, ""));
            Require(attaching.HostStatus == AuthPageHostStatus.Attaching);
            Require(attaching.HostAvailable, "加载中没有挂载原生表面，浏览器无法创建 HWND");
            Require(!attaching.HostRetryVisible, "加载中不应出现重试入口");
            Require(attaching.HostTitle.Contains("加载") && attaching.HostDetail.Contains("代理"));

            var attached = AuthPageModel.From(null,
                new AuthPageHostSnapshot(AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true, ""));
            Require(attached.HostAvailable && !attached.HostRetryVisible);
            Require(attached.HostTitle == "门户页面");

            var failed = AuthPageModel.From(null,
                new AuthPageHostSnapshot(AuthPageHostStatus.Failed, AuthPageModel.PortalAddress, false, ""));
            Require(!failed.HostAvailable && failed.HostRetryVisible);
            return Task.CompletedTask;
        }),
        ("认证页：手动修复入口投影到本页且忙态参与指纹", () =>
        {
            var state = new AuthChannelLaunchState(AuthChannelModes.ManualOnly, SignalPresent: true,
                Requested: false, LastConclusion: null, Busy: false);
            var content = AuthPageModel.From(null, AuthPageHostSnapshot.NotAttached, state);
            Require(content.Channel is not null && content.Channel.SignalPresent);
            var busy = AuthPageModel.From(null, AuthPageHostSnapshot.NotAttached, state with { Busy = true });
            Require(busy.ContentKey != content.ContentKey, "忙态变化没有进入渲染指纹");
            var hidden = AuthPageModel.From(null, AuthPageHostSnapshot.NotAttached);
            Require(hidden.Channel is null, "未装配发起器时仍显示手动修复入口");
            return Task.CompletedTask;
        }),
        ("诊断页：不再携带提权提交入口", () =>
        {
            var build = typeof(DiagnosticsView).GetMethod("Build");
            Require(build is not null && build.GetParameters().Length == 3);
            Require(typeof(DiagnosticsContent).GetProperties().All(property => !property.Name.Contains("Channel")),
                "只读诊断内容仍携带认证通道入口");
            return Task.CompletedTask;
        }),
        ("认证页会话：导航永不完成时按当前代次超时，成功先到则忽略超时", () =>
        {
            Require(AuthPageWebViewHost.ShouldFailNavigation(true, false), "当前会话导航未完成时未判超时");
            Require(!AuthPageWebViewHost.ShouldFailNavigation(true, true), "导航已成功仍判为超时");
            Require(!AuthPageWebViewHost.ShouldFailNavigation(false, false), "旧会话或销毁后的宿主仍判超时");
            Require(AuthPageWebViewHost.NavigationTimeout > TimeSpan.Zero);
            return Task.CompletedTask;
        }),
        ("认证页会话：浏览器未确认退出时保留目录，不按等待超时清理", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "autonet-delete-wait-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "marker.txt"), "x");
            try
            {
                var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await AuthPageWebViewHost.DeleteSessionDirectoryAsync(path, pending.Task, browserAttempted: true,
                    exitTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(1));
                Require(Directory.Exists(path), "浏览器未确认退出就删除了会话目录");

                pending.SetResult(true);
                await AuthPageWebViewHost.DeleteSessionDirectoryAsync(path, pending.Task, browserAttempted: true,
                    exitTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(1));
                Require(!Directory.Exists(path), "确认浏览器退出后目录未清理");
            }
            finally { try { Directory.Delete(path, recursive: true); } catch (Exception) { } }
        }),
        ("认证页会话：从未创建浏览器的目录可直接清理", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "autonet-delete-never-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await AuthPageWebViewHost.DeleteSessionDirectoryAsync(path, never.Task, browserAttempted: false,
                    exitTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(1));
                Require(!Directory.Exists(path), "未启动浏览器的目录未清理");
            }
            finally { try { Directory.Delete(path, recursive: true); } catch (Exception) { } }
        }),
        ("认证页会话：被其他进程占用的会话目录保留给启动清扫", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "autonet-delete-locked-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                var held = Path.Combine(path, "held.txt");
                File.WriteAllText(held, "x");
                using (var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await AuthPageWebViewHost.DeleteSessionDirectoryAsync(path, Task.CompletedTask, browserAttempted: true,
                        exitTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(1));
                    Require(Directory.Exists(path), "占用中的会话目录被删除");
                }
                await AuthPageWebViewHost.DeleteSessionDirectoryAsync(path, Task.CompletedTask, browserAttempted: true,
                    exitTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(1));
                Require(!Directory.Exists(path), "释放后的会话目录未清理");
            }
            finally { try { Directory.Delete(path, recursive: true); } catch (Exception) { } }
        }),
        ("认证页会话：启动清扫只删本应用创建且过期的目录", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "autonet-sweep-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var link = (string?)null;
            try
            {
                var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
                string Owned(string suffix) => Path.Combine(root, "autonet-authpage-" + suffix);
                var stale = Owned(Guid.NewGuid().ToString("N"));
                var fresh = Owned(Guid.NewGuid().ToString("N"));
                var active = Owned(Guid.NewGuid().ToString("N"));
                var wrongLength = Owned(Guid.NewGuid().ToString("N")[..31]);
                var wrongShape = Owned("notes");
                var unrelated = Path.Combine(root, "other-directory");
                foreach (var directory in new[] { stale, fresh, active, wrongLength, wrongShape, unrelated })
                    Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(stale, "marker.txt"), "x");
                // 写入子文件会刷新目录时间，因此在写入之后再固定过期时间。
                foreach (var directory in new[] { stale, active, wrongLength, wrongShape, unrelated })
                    Directory.SetLastWriteTimeUtc(directory, now.AddDays(-2).UtcDateTime);
                Directory.SetLastWriteTimeUtc(fresh, now.AddMinutes(-5).UtcDateTime);

                // 重解析点必须整体跳过，绝不跟随删除目标目录内容；建立失败时跳过该断言。
                try
                {
                    link = Owned(Guid.NewGuid().ToString("N"));
                    Directory.CreateSymbolicLink(link, unrelated);
                    Directory.SetLastWriteTimeUtc(link, now.AddDays(-2).UtcDateTime);
                }
                catch (Exception) { link = null; }

                using (var session = new AuthPageBrowserSession(1, active))
                {
                    Require(AuthPageWebViewHost.SweepStaleSessions(root, now) == 1, "清扫数量与过期会话目录不符");
                    Require(!Directory.Exists(stale), "过期会话目录未被清理");
                    Require(Directory.Exists(active), "活跃会话目录被误删");
                    Require(Directory.Exists(fresh), "未过期会话目录被误删");
                    Require(Directory.Exists(wrongLength), "长度不足的会话名被误删");
                    Require(Directory.Exists(wrongShape), "非十六进制会话名被误删");
                    Require(Directory.Exists(unrelated), "非前缀目录被误删");
                    if (link is not null) Require(Directory.Exists(link), "重解析点被跟随删除");
                }

                // 会话销毁后活跃登记随之释放；仍过期的目录才允许清理。
                Require(AuthPageWebViewHost.SweepStaleSessions(root, now) == 1, "销毁后的过期目录未被清理");
                Require(!Directory.Exists(active), "销毁后的活跃目录仍被错误保留");
                Require(AuthPageWebViewHost.SweepStaleSessions(root, now) == 0, "重复清扫仍有删除");
            }
            finally
            {
                if (link is not null) { try { Directory.Delete(link); } catch (Exception) { } }
                try { Directory.Delete(root, recursive: true); } catch (Exception) { }
            }
            return Task.CompletedTask;
        }),
        ("认证页会话：被其他进程占用的过期目录保留给下次清扫", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "autonet-sweep-lock-" + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "autonet-authpage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var held = Path.Combine(directory, "held.txt");
                File.WriteAllText(held, "x");
                var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
                Directory.SetLastWriteTimeUtc(directory, now.AddDays(-2).UtcDateTime);
                using (var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Require(AuthPageWebViewHost.SweepStaleSessions(root, now) == 0, "占用中的目录被计为已清理");
                    Require(Directory.Exists(directory), "占用中的目录被删除");
                }
                Require(AuthPageWebViewHost.SweepStaleSessions(root, now) == 1, "释放后过期目录未被清理");
                Require(!Directory.Exists(directory), "释放后目录仍残留");
            }
            finally { try { Directory.Delete(root, recursive: true); } catch (Exception) { } }
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition, string? detail = null)
    {
        if (!condition) throw new Exception("认证页会话行为不符合预期" + (detail is null ? "" : "：" + detail));
    }
}
