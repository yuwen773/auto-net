namespace AutoNet.Core;

public enum LogEventCategory { Other, State, Recovery, Authentication }

/// <summary>Structured event codes take precedence; legacy text is matched only against known actions.</summary>
public static class LogEventClassifier
{
    public static LogEventCategory Classify(ActivityLog entry)
    {
        if (entry.SchemaVersion >= 2 || !string.IsNullOrEmpty(entry.EventCode))
            return entry.EventCode switch
            {
                "SessionStarted" or "SessionStopped" or "IncidentStarted" or "IncidentRecovered"
                    or "IncidentResumed" or "IncidentSuspended" or "OperationFailed" or "OperationRecovered"
                    or "CycleInterrupted" or "LoggingGap" or "StatusChanged" => LogEventCategory.State,
                "RecoveryDecision" or "RecoveryActionStarted" or "RecoveryActionCompleted" or "WifiRequestResult"
                    or "SafetyStateWriteStarted" or "SafetyStateWritten" or "DisconnectRecheck" => LogEventCategory.Recovery,
                "PortalStep" or "SubmissionAuthorized" or "SubmissionNotAuthorized" => LogEventCategory.Authentication,
                _ => LogEventCategory.Other
            };

        return entry.Action switch
        {
            "重连校园 Wi-Fi" or "连接校园 Wi-Fi" or "开始恢复动作" or "取消主动断线" or "主动断线请求失败" => LogEventCategory.Recovery,
            "认证结果" or "认证已接受" or "需要处理认证" or "重新认证" => LogEventCategory.Authentication,
            "恢复联网" or "观测到联网恢复" or "检测异常" or "状态写入异常" or "日志写入异常"
                or "设置已更改" or "服务启动" or "服务停止" or "安全状态已恢复" => LogEventCategory.State,
            _ when NetworkDisplayMap.KnownStates.Contains(entry.Action) => LogEventCategory.State,
            _ => LogEventCategory.Other
        };
    }
}
