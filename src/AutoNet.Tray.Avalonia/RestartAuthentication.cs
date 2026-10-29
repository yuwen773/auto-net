using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

internal enum RestartAuthenticationOutcome { Cancelled, Changed, Reset, StorageFailure }

internal sealed class RestartAuthentication(SafetyStateStore store, Func<Task<bool>> confirm)
{
    public async Task<RestartAuthenticationOutcome> RunAsync()
    {
        try
        {
            var authentication = store.LoadAuthenticationBlock();
            var budget = store.LoadSubmissionBudget();
            if (!await confirm()) return RestartAuthenticationOutcome.Cancelled;
            return store.TryRestartAuthentication(authentication, budget)
                ? RestartAuthenticationOutcome.Reset : RestartAuthenticationOutcome.Changed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return RestartAuthenticationOutcome.StorageFailure;
        }
    }
}
