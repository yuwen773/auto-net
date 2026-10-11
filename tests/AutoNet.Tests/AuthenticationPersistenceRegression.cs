using AutoNet.Core;
using AutoNet.Service;

internal static class AuthenticationPersistenceRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("认证持久安全：SubmissionPending写入拒绝时零次提交", async () =>
        {
            var fixture = new MonitorFixture { FailSubmission = true };
            using (var worker = new MonitorWorker(fixture, fixture.Clock))
            {
                var status = await worker.RunOnceAsync(default);
                Require(status.CredentialsBlocked && !status.CredentialRequestStarted && status.AuthenticationAcceptedAt is null);
            }
            using var restarted = new MonitorWorker(fixture, fixture.Clock);
            fixture.Clock.Advance(TimeSpan.FromHours(1)); await restarted.RunOnceAsync(default);
            Require(Count(fixture) == 0);
        }),
        ("认证持久安全：提交边界崩溃后新Worker从真实文件保持Pending", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var fixture = new MonitorFixture();
                fixture.Http.BeforeLogin = () => throw new SimulatedCrash();
                using (var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock))
                    Require(worker.RunOnceAsync(default).GetAwaiter().GetResult().State == "检测异常");
                Require(store.LoadAuthenticationBlock()?.State == AuthenticationState.SubmissionPending && Count(fixture) == 1);
                fixture.Http.BeforeLogin = null;
                using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, new(directory)), fixture.Clock);
                fixture.Clock.Advance(TimeSpan.FromDays(1));
                var status = restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(status.CredentialsBlocked && status.AuthenticationAcceptedAt is null && Count(fixture) == 1);
            });
            return Task.CompletedTask;
        }),
        ("认证持久安全：VLAN终态写失败保留已落盘Pending并跨重启", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var fixture = new MonitorFixture(); fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"只能绑定指定的VLAN设备\"}";
                var environment = new AcceptanceRegression.StoredEnvironment(fixture, store)
                { FailAuthenticationWrite = next => next.State == AuthenticationState.BindingRejected };
                using (var worker = new MonitorWorker(environment, fixture.Clock))
                    Require(worker.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked);
                Require(store.LoadAuthenticationBlock()?.State == AuthenticationState.SubmissionPending);
                fixture.Credentials = new("changed-test", "changed-never-real");
                using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, new(directory)), fixture.Clock);
                fixture.Clock.Advance(TimeSpan.FromHours(1));
                Require(restarted.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked && Count(fixture) == 1);
            });
            return Task.CompletedTask;
        }),
        ("认证持久安全：临时拒绝持久退避且墙钟前跳和重启不缩短", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var fixture = new MonitorFixture(); fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"ldap timeout\"}";
                using (var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock))
                    worker.RunOnceAsync(default).GetAwaiter().GetResult();
                var cooling = store.LoadAuthenticationBlock();
                Require(cooling?.State == AuthenticationState.RetryCoolingDown && cooling.RetryDelaySeconds == 2 && cooling.RetryNotBefore != default);
                fixture.Clock.Advance(TimeSpan.Zero, TimeSpan.FromDays(10));
                using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, new(directory)), fixture.Clock);
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromSeconds(1)); restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(Count(fixture) == 1);
                fixture.Clock.Advance(TimeSpan.FromSeconds(1)); restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(Count(fixture) == 2);
                fixture.Clock.Advance(TimeSpan.FromSeconds(2)); restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromHours(1));
                Require(restarted.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked && Count(fixture) == 3);
            });
            return Task.CompletedTask;
        }),
        ("认证持久安全：接受响应但Ready落盘失败不记认证成功", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var fixture = new MonitorFixture();
                using (var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store)
                    { FailAuthenticationWrite = next => next.State == AuthenticationState.Ready }, fixture.Clock))
                {
                    var status = worker.RunOnceAsync(default).GetAwaiter().GetResult();
                    Require(status.CredentialsBlocked && status.AuthenticationAcceptedAt is null);
                }
                Require(store.LoadAuthenticationBlock()?.State == AuthenticationState.SubmissionPending);
                using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, new(directory)), fixture.Clock);
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(Count(fixture) == 1);
            });
            return Task.CompletedTask;
        }),
        ("认证持久安全：旧标记损坏和未知版本均保守暂停，权限拒绝零提交", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                foreach (var content in new[] { "legacy-fingerprint", "{}", "{broken", "{\"Failure\":18}", "{\"Version\":99}", "{\"Version\":1,\"State\":0}" })
                {
                    File.WriteAllText(Path.Combine(directory, "auth-block.txt"), content);
                    var fixture = new MonitorFixture();
                    using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                    Require(worker.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked && Count(fixture) == 0);
                }
                var denied = new MonitorFixture();
                using var inaccessible = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(denied, store) { FailAuthenticationRead = true }, denied.Clock);
                Require(inaccessible.RunOnceAsync(default).GetAwaiter().GetResult().State == "检测异常" && Count(denied) == 0);
            });
            return Task.CompletedTask;
        }),
        ("认证持久安全：状态转移比较版本，人工解除先审计且拒绝过期确认", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var pending = new AuthenticationBlock() { State = AuthenticationState.SubmissionPending, Attempts = 1, Revision = Guid.NewGuid() };
                Require(store.TryTransitionAuthentication(null, pending));
                Require(!new SafetyStateStore(directory).TryTransitionAuthentication(null, pending));
                var rejected = new AuthenticationBlock(PortalFailure.BindingRejected) { Attempts = 1, Revision = Guid.NewGuid() };
                Require(store.TryTransitionAuthentication(pending, rejected));
                try { store.ClearAuthenticationBlock(pending); throw new Exception("不应允许过期确认"); }
                catch (InvalidOperationException) { }
                Require(store.LoadAuthenticationBlock() == rejected);
                store.ClearAuthenticationBlock(rejected);
                Require(store.LoadAuthenticationBlock() is null);
                Require(new FileInfo(Path.Combine(directory, "auth-release-audit.jsonl")).Length > 0);
            });
            return Task.CompletedTask;
        }),
        ("认证持久安全：解除审计写入失败不得删除阻断", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var blocked = new AuthenticationBlock(PortalFailure.BindingRejected);
                store.BlockAuthentication(blocked);
                using (var locked = new FileStream(Path.Combine(directory, "auth-release-audit.jsonl"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    try { store.ClearAuthenticationBlock(blocked); throw new Exception("审计失败不得解除"); }
                    catch (IOException) { }
                }
                Require(store.LoadAuthenticationBlock() == blocked);
            });
            return Task.CompletedTask;
        }),
        ("认证持久安全：连续接口接受但HTTPS未恢复也不会无限提交", async () =>
        {
            var fixture = new MonitorFixture();
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            for (var i = 0; i < 5; i++)
            {
                await worker.RunOnceAsync(default); fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            }
            Require(Count(fixture) == 3 && (await worker.RunOnceAsync(default)).CredentialsBlocked);
            fixture.Http.Online = true;
            Require((await worker.RunOnceAsync(default)).CredentialsBlocked); // A manual safety pause survives online recovery.
        }),
        ("认证持久安全：完整结构中矛盾的状态与拒绝原因也必须阻断", () =>
        {
            AcceptanceRegression.WithStore((store, _) =>
            {
                foreach (var state in new[] { AuthenticationState.Ready, AuthenticationState.RetryCoolingDown, AuthenticationState.SubmissionPending })
                foreach (var failure in new[] { PortalFailure.BindingRejected, PortalFailure.CredentialsRejected, PortalFailure.InteractionRequired })
                {
                    store.BlockAuthentication(new(failure)
                    {
                        State = state, Attempts = 1, Revision = Guid.NewGuid(),
                        RetryDelaySeconds = state == AuthenticationState.RetryCoolingDown ? 2 : 0,
                        RetryNotBefore = state == AuthenticationState.RetryCoolingDown ? DateTimeOffset.UtcNow.AddSeconds(2) : default
                    });
                    Require(store.LoadAuthenticationBlock()?.BlocksSubmissions == true);
                    var fixture = new MonitorFixture();
                    using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                    fixture.Clock.Advance(TimeSpan.FromHours(1));
                    Require(worker.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked && Count(fixture) == 0);
                }
            });
            return Task.CompletedTask;
        })
    ];

    private static int Count(MonitorFixture fixture) => fixture.Http.Requests.Count(u => u.AbsolutePath == "/eportal/portal/login");
    private static void Require(bool condition) { if (!condition) throw new Exception("认证持久安全不变量失败"); }
    private sealed class SimulatedCrash : Exception;
}
