using AutoNet.Core;

namespace AutoNet.Service;

public interface IAuthChannelBrowser
{
    bool HasTrustedNetworkBoundary { get; }
    bool IsBoundTo(Settings settings, WifiLink link);
    int BrowserProcessId { get; }
    int HelperProcessId { get; }
    string ProcessJobName { get; }

    /// <summary>页面是否仍是固定门户来源且包含登录表单；未就绪时不填表、不提交（工单 #40）。</summary>
    AuthPageReadiness PageReadiness { get; }

    /// <summary>用户是否正在手动操作，或接管后尚未完成恢复复核（工单 #40）。</summary>
    bool UserTakeoverActive { get; }

    /// <summary>
    /// 接管空闲满 15 秒后，结合调用方本轮核对的事实（网络、认证、额度）与浏览器刚刚
    /// 重新核对的页面来源/表单，决定是否恢复自动操作。页面事实只由浏览器提供，
    /// 不接受调用方的旧快照。返回 true 表示已恢复。
    /// </summary>
    Task<bool> TryResumeAutoAsync(bool network, bool authentication, bool budget, CancellationToken ct);

    Task<AuthChannelConclusion> SubmitAsync(Credentials credentials, CancellationToken ct);
}

public enum ChannelWorkflowOutcome { Exhausted, Waiting, Blocked, Submitted }

public sealed class AuthChannelWorkflow(IMonitorEnvironment environment, IAuthChannelBrowser browser, TimeProvider clock)
{
    private readonly SubmissionRetryPolicy policy = new();
    private readonly long started = clock.GetTimestamp();

    public async Task<ChannelWorkflowOutcome> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!browser.HasTrustedNetworkBoundary) return ChannelWorkflowOutcome.Blocked;
            var budget = environment.LoadSubmissionBudget();
            var decision = policy.Evaluate(budget, clock.GetUtcNow(), clock.GetElapsedTime(started));
            if (decision == SubmissionRetryDecision.Exhausted) return ChannelWorkflowOutcome.Exhausted;
            if (decision is SubmissionRetryDecision.Waiting or SubmissionRetryDecision.WaitClock) return ChannelWorkflowOutcome.Waiting;
            if (decision != SubmissionRetryDecision.Authorized) return ChannelWorkflowOutcome.Blocked;
            var settings = environment.LoadSettings();
            var credentials = environment.LoadCredentials();
            if (settings is null || credentials is null || !settings.Enabled || settings.AuthChannelMode == AuthChannelModes.Off)
                return ChannelWorkflowOutcome.Blocked;
            settings.Validate();
            var block = environment.LoadAuthenticationBlock();
            var link = environment.ReadWifi(settings);
            if (link.State != WifiState.Ready || !browser.IsBoundTo(settings, link)) return ChannelWorkflowOutcome.Blocked;
            // 用户手动操作期间不填表、不提交。空闲满 15 秒也只是恢复的前提之一：
            // 网络、认证、页面来源、表单与额度必须在本轮重新核对全部成立。
            if (browser.UserTakeoverActive)
            {
                if (!await browser.TryResumeAutoAsync(
                    network: link.State == WifiState.Ready,
                    authentication: block?.State == AuthenticationState.InteractionRequired,
                    budget: decision == SubmissionRetryDecision.Authorized, ct))
                    return ChannelWorkflowOutcome.Waiting;
            }
            // 工单 #40：页面不是固定门户来源或没有登录表单时，自动填表与提交都不成立。
            if (!browser.PageReadiness.Ready) return ChannelWorkflowOutcome.Blocked;
            var reviewStarted = clock.GetTimestamp();
            using var review = CancellationTokenSource.CreateLinkedTokenSource(ct);
            review.CancelAfter(TimeSpan.FromSeconds(5));
            using var http = environment.CreateDirectClient(link);
            if ((await HealthChecks.InternetDetailedAsync(http, review.Token)).Online) return ChannelWorkflowOutcome.Blocked;
            var inspection = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).DiagnoseAsync(review.Token);
            if (inspection.ObservedState == PortalState.LoggedIn || !PortalLayoutSignal.IsStructural(inspection.Failure))
                return ChannelWorkflowOutcome.Blocked;
            var utc = clock.GetUtcNow();
            var tick = clock.GetTimestamp();
            using var lease = environment.TryBeginChannelSubmission(block, budget, inspection.Failure, policy, utc, clock.GetElapsedTime(started));
            if (lease is null) return ChannelWorkflowOutcome.Blocked;
            var nextBudgetState = SubmissionBudgetState.PossiblySent;
            try
            {
                bool StillValid()
                {
                    review.Token.ThrowIfCancellationRequested();
                    // 事实一次读全再交给纯规则比较：任一读取失败都会落到外层受控 catch 并返回
                    // Blocked，因此提前读取不会把原先短路拒绝的路径变成放行；每次只读五个本地
                    // 小文件，复核窗口仍由下面的 5 秒与 2 秒时钟约束把住。
                    var currentSettings = environment.LoadSettings();
                    var currentLink = environment.ReadWifi(settings);
                    var currentCredentials = environment.LoadCredentials();
                    var currentAuthentication = environment.LoadAuthenticationBlock();
                    var currentBudget = environment.LoadSubmissionBudget();
                    return SubmissionReview.ChannelStillValid(
                        new(currentSettings, currentLink, currentAuthentication, currentBudget, currentCredentials),
                        new(settings, link, lease.Authentication, lease.Budget, credentials),
                        browser.IsBoundTo(settings, currentLink),
                        clock.GetElapsedTime(reviewStarted) < TimeSpan.FromSeconds(5),
                        SubmissionReview.ClockTrusted(clock, utc, tick, TimeSpan.FromSeconds(2))) is null;
                }
                if (!StillValid()) return ChannelWorkflowOutcome.Blocked;
                if (!environment.TryRegisterChannelSession(lease, browser.BrowserProcessId, browser.HelperProcessId, browser.ProcessJobName))
                    return ChannelWorkflowOutcome.Blocked;
                if (!StillValid()) return ChannelWorkflowOutcome.Blocked;
                review.Token.ThrowIfCancellationRequested();
                var conclusion = await browser.SubmitAsync(credentials, ct);
                if (conclusion is AuthChannelConclusion.NeedsHuman or AuthChannelConclusion.NotLoggedIn &&
                    environment.TryTransitionAuthentication(lease.Authentication, new AuthenticationBlock(PortalFailure.InteractionRequired)
                    { Attempts = lease.Authentication.Attempts, Revision = Guid.NewGuid() }))
                    nextBudgetState = SubmissionBudgetState.Concluded;
                return ChannelWorkflowOutcome.Submitted;
            }
            finally
            {
                environment.TryTransitionSubmissionBudget(lease.Budget, nextBudgetState);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ChannelWorkflowOutcome.Blocked; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return ChannelWorkflowOutcome.Blocked; }
    }
}
