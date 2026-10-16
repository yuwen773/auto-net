using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页内容投影（迁移工单 #8 / T07 / 故事 34–39）：普通权限界面对已保存配置的只读视图。
/// 受限配置（settings.json、credentials.dpapi）位于仅管理员可读的 private 目录，
/// 普通界面绝不读取，也不显示账号与密码；事实来自状态快照（status.json，公共可读）。
/// 修改入口发起独立的管理员操作（--settings），入口本身不依赖状态可信度——
/// 与既有 WinForms 托盘菜单一致：修改设置不依据页面上的网络状态。
/// </summary>
internal sealed record SettingsContent : PageContent
{
    internal SettingsContent() : base(ShellPage.Settings, ShellPages.Info(ShellPage.Settings).Name) { }

    /// <summary>状态边界是否允许显示守护配置事实；不可信时相关行显示固定降级文案。</summary>
    public bool Trusted { get; init; }

    /// <summary>自动恢复开关状态（只读展示，无动作语义；开关动作在概览页）：已开启/已关闭/待确认。</summary>
    public string RecoveryState { get; init; } = "";

    /// <summary>守护的校园 Wi-Fi（快照投影）；不可信时为固定降级文案。</summary>
    public string GuardedNetwork { get; init; } = "";

    /// <summary>修改入口标签；入口始终可用（提权窗口自行读取真实配置，UAC 即授权边界）。</summary>
    public string ModifyLabel { get; init; } = SettingsModel.ModifyLabel;

    /// <summary>渲染指纹：全部可见字段的稳定拼接；不变时视图跳过重建（不打断焦点）。</summary>
    public string ContentKey { get; init; } = "";
}

/// <summary>
/// 设置页投影规则（#8 / T07）。只依赖状态读取，不读受限配置、无 I/O、无副作用；
/// 修改入口的可用性不随状态波动（故事 34 的普通界面一侧）。
/// </summary>
internal static class SettingsModel
{
    /// <summary>认证地址固定：与既有设置表单逐字一致。</summary>
    public const string PortalLine = "https://login.csust.edu.cn  ·  Dr.COM";

    /// <summary>修改入口标签：与托盘菜单「设置认证信息…」同语义的页面侧入口。</summary>
    public const string ModifyLabel = "修改设置…";

    /// <summary>凭据说明：普通界面不显示、不复制凭据内容（故事 36 的呈现侧）。</summary>
    public const string CredentialNote =
        "校园网账号与密码使用 Windows DPAPI 加密保存，存放于仅管理员可访问的受限目录。" +
        "本页不显示、也不复制凭据内容；修改账号或密码在独立的管理员操作中完成。";

    /// <summary>修改入口说明（故事 34、38、39 的呈现侧）。</summary>
    public const string ModifyNote =
        "修改网卡、Wi-Fi、账号或密码会请求管理员确认；拒绝管理员授权时不修改任何设置。" +
        "保存设置或修改密码不会解除认证暂停。";

    /// <summary>不可信状态下的守护网络降级文案：绝不沿用上次画面（故事 15）。</summary>
    public const string UnconfirmedNetwork = "不可确认";

    /// <summary>把一次状态读取投影为设置页内容；null 表示尚未取得状态。</summary>
    public static SettingsContent From(StatusReading? reading)
    {
        var trusted = reading?.Trusted == true;
        var snapshot = trusted ? reading!.Snapshot : null;
        var content = new SettingsContent
        {
            Trusted = trusted,
            RecoveryState = trusted
                ? (snapshot!.Enabled ? "已开启" : "已关闭")
                : OverviewModel.UnconfirmedRecovery.State,
            GuardedNetwork = trusted ? snapshot!.Wifi : UnconfirmedNetwork
        };
        return content with { ContentKey = Fingerprint(content) };
    }

    private static string Fingerprint(SettingsContent content) => string.Join("\u001f",
        content.Trusted, content.RecoveryState, content.GuardedNetwork, content.ModifyLabel);
}
