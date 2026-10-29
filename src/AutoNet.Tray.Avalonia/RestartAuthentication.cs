using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

internal enum RestartAuthenticationOutcome { Cancelled, Changed, Reset }

internal sealed class RestartAuthentication(SafetyStateStore store, Func<Task<bool>> confirm)
{
    public async Task<RestartAuthenticationOutcome> RunAsync()
    {
        var authentication = store.LoadAuthenticationBlock();
        var budget = store.LoadSubmissionBudget();
        if (!await confirm()) return RestartAuthenticationOutcome.Cancelled;
        return store.TryRestartAuthentication(authentication, budget)
            ? RestartAuthenticationOutcome.Reset : RestartAuthenticationOutcome.Changed;
    }
}
