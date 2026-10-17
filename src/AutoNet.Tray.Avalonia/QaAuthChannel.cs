using System.Diagnostics;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 可访问性 QA 模式的通道动作记录器（工单 #24）：只记录意图与约定结果路径，
/// 不弹 UAC、不启动辅助进程——与 QaOverview.RecordLaunch / QaAccessibility.RecordPortalOpen
/// 同一模式。记录内容进 QA 报告供逐字核验。
/// </summary>
internal static class QaAuthChannel
{
    public static readonly List<string> Launched = [];

    public static void Reset() => Launched.Clear();

    public static Process? RecordLaunch(string resultPath)
    {
        Launched.Add("uac-launch --from-settings --result " + resultPath);
        return null;
    }
}
