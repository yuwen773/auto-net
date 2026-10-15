namespace AutoNet.Tray.Avalonia;

internal enum StartupActionKind
{
    RunTray,
    RequestElevation,
    RunElevatedOperation,
    ReportAlreadyRunning
}

internal sealed record StartupDecision(StartupActionKind Kind, ElevatedOperationKind? Operation);

/// <summary>
/// 启动路由：管理员操作永远先于单实例检查。普通托盘互斥锁不得拦截提权入口（规格故事 8），
/// 因此同一个请求在“已有普通实例运行”时仍然升级到提权路径。
/// </summary>
internal static class StartupRouter
{
    public static StartupDecision Decide(StartupRequest request, bool isAdministrator, bool anotherInstanceRunning)
    {
        if (request.ElevatedOperation is { } operation)
        {
            return isAdministrator
                ? new StartupDecision(StartupActionKind.RunElevatedOperation, operation)
                : new StartupDecision(StartupActionKind.RequestElevation, operation);
        }

        return anotherInstanceRunning
            ? new StartupDecision(StartupActionKind.ReportAlreadyRunning, null)
            : new StartupDecision(StartupActionKind.RunTray, null);
    }
}
