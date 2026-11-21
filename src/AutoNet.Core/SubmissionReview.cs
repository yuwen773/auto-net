namespace AutoNet.Core;

/// <summary>
/// 提交前刚刚重读到的事实。协议载体与内嵌通道载体都先重读设置、链路、认证状态与额度，
/// 再把这些事实交给 <see cref="SubmissionReview"/> 比较；本记录不持有授权时的旧副本，
/// 旧副本由调用方另行传入，避免「用同一份对象和自己比较」的假通过。
/// </summary>
public readonly record struct SubmissionReviewFacts(
    Settings? Settings,
    WifiLink Link,
    AuthenticationBlock? Authentication,
    SubmissionBudget? Budget,
    Credentials? Credentials = null);

/// <summary>
/// 凭据提交前的复核规则（ADR-0004 / 0011 / 0014 / 0015）：协议载体与内嵌通道载体
/// 各自在提交前后重读事实，但「哪些差异必须拒绝」只在这里定义一次。
///
/// 纯比较，不读文件、不访问网络；返回 null 表示仍然一致，否则返回受控原因。
/// 每个方法的检查顺序固定并写在注释里，调用方只负责按载体选择对应入口与记录阶段。
/// </summary>
public static class SubmissionReview
{
    /// <summary>
    /// 协议载体的首次复核：设置（含 null）→ 恢复开关 → 链路就绪与身份。
    /// 对应拒绝原因 SettingsChanged / RecoveryDisabled / WifiChanged。
    /// </summary>
    public static string? ProtocolInitial(Settings? currentSettings, Settings authorizedSettings,
        WifiLink currentLink, WifiLink authorizedLink)
    {
        if (!SettingsUnchanged(currentSettings, authorizedSettings)) return "SettingsChanged";
        if (!currentSettings!.Enabled) return "RecoveryDisabled";
        if (!LinkUnchanged(currentLink, authorizedLink)) return "WifiChanged";
        return null;
    }

    /// <summary>
    /// 协议载体的最终复核：认证状态与额度 → 时钟可信 → 设置与链路。
    /// 设置或链路变化统一报告 FinalSettingsOrWifiChanged，与既有日志口径一致。
    /// </summary>
    public static string? ProtocolFinal(SubmissionReviewFacts current, SubmissionReviewFacts authorized,
        bool clockTrusted)
    {
        if (current.Authentication != authorized.Authentication || current.Budget != authorized.Budget)
            return "FinalAuthenticationOrBudgetChanged";
        if (!clockTrusted) return "FinalClockUntrusted";
        if (!SettingsUnchanged(current.Settings, authorized.Settings) || !LinkUnchanged(current.Link, authorized.Link))
            return "FinalSettingsOrWifiChanged";
        return null;
    }

    /// <summary>
    /// 内嵌通道载体的复核：设置 → 链路 → 凭据 → 浏览器绑定 → 认证状态 → 额度 →
    /// 复核窗口 → 时钟。任一不一致都返回受控原因，调用方只把它当作拒绝。
    /// </summary>
    public static string? ChannelStillValid(SubmissionReviewFacts current, SubmissionReviewFacts authorized,
        bool browserBound, bool reviewWindowOpen, bool clockTrusted)
    {
        if (!SettingsUnchanged(current.Settings, authorized.Settings)) return "SettingsChanged";
        if (!LinkUnchanged(current.Link, authorized.Link)) return "WifiChanged";
        if (authorized.Credentials is not null && current.Credentials != authorized.Credentials)
            return "CredentialsChanged";
        if (!browserBound) return "BrowserUnbound";
        if (current.Authentication != authorized.Authentication) return "AuthenticationStateChanged";
        if (current.Budget != authorized.Budget) return "SubmissionBudgetChanged";
        if (!reviewWindowOpen) return "ReviewChainTimeout";
        if (!clockTrusted) return "FinalClockUntrusted";
        return null;
    }

    /// <summary>设置未被删除、替换或改写；null 与授权值不同同样视为变化。</summary>
    public static bool SettingsUnchanged(Settings? current, Settings? authorized) =>
        current is not null && current == authorized;

    /// <summary>链路仍就绪，且仍是授权时的那一个（索引与地址都一致）。</summary>
    public static bool LinkUnchanged(WifiLink current, WifiLink authorized) =>
        current.State == WifiState.Ready && current.Index == authorized.Index &&
        Equals(current.Address, authorized.Address);

    /// <summary>
    /// 墙钟与单调时钟在提交窗口内保持一致：两者之差不超过允许偏移，
    /// 防止墙钟被调整后把「已经过去很久」误当成仍在窗口内。
    /// </summary>
    public static bool ClockTrusted(TimeProvider clock, DateTimeOffset utc, long tick, TimeSpan maxSkew) =>
        ((clock.GetUtcNow() - utc) - clock.GetElapsedTime(tick)).Duration() <= maxSkew;
}
