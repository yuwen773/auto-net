using AutoNet.Core;

namespace AutoNet.Service;

public interface IAuthChannelBrowser
{
    bool HasTrustedNetworkBoundary { get; }
    bool IsBoundTo(Settings settings, WifiLink link);
    int BrowserProcessId { get; }
    int HelperProcessId { get; }
    string ProcessJobName { get; }
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
                    var currentSettings = environment.LoadSettings();
                    var currentLink = environment.ReadWifi(settings);
                    return currentSettings == settings && environment.LoadCredentials() == credentials && currentLink.State == WifiState.Ready &&
                        currentLink.Index == link.Index && Equals(currentLink.Address, link.Address) && browser.IsBoundTo(settings, currentLink) &&
                        environment.LoadAuthenticationBlock() == lease.Authentication && environment.LoadSubmissionBudget() == lease.Budget &&
                        clock.GetElapsedTime(reviewStarted) < TimeSpan.FromSeconds(5) &&
                        ((clock.GetUtcNow() - utc) - clock.GetElapsedTime(tick)).Duration() <= TimeSpan.FromSeconds(2);
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
