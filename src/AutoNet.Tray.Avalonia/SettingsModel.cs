using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页内容投影（#8 / #41）：普通权限界面对已保存配置的可编辑视图。
/// 受限配置（settings.json、credentials.dpapi）位于仅管理员可读的 private 目录，
/// 普通界面绝不读取；非秘密字段由后台写进 status.json 的配置投影提供，
/// 账号只用于预填，密码不显示、不回读。保存经独立提权交接完成。
/// </summary>
internal sealed record SettingsContent : PageContent
{
    internal SettingsContent() : base(ShellPage.Settings, ShellPages.Info(ShellPage.Settings).Name) { }

    /// <summary>状态边界是否允许显示守护配置事实；不可信时相关行显示固定降级文案。</summary>
    public bool Trusted { get; init; }

    /// <summary>自动恢复开关状态（只读展示）：已开启/已关闭/待确认。</summary>
    public string RecoveryState { get; init; } = "";

    /// <summary>守护的校园 Wi-Fi（快照投影）；不可信时为固定降级文案。</summary>
    public string GuardedNetwork { get; init; } = "";

    /// <summary>后台是否提供了非秘密配置投影；旧版服务或不可信状态为 false。</summary>
    public bool ConfigProjected { get; init; }

    /// <summary>投影对应的状态更新时间；用于避免保存后旧快照回填草稿。</summary>
    public DateTimeOffset ProjectedAt { get; init; }

    /// <summary>已保存的校园网账号，仅用于预填；密码永不进入投影。</summary>
    public string Username { get; init; } = "";
    public string InterfaceId { get; init; } = "";
    public string WifiProfile { get; init; } = "";
    /// <summary>代理地址的安全展示值（完整地址或仅来源）；完整值永不进入状态。</summary>
    public string ProxyUrlDisplay { get; init; } = "";
    /// <summary>已保存代理地址含未回显的路径、查询或片段；未编辑时保存须保留原值。</summary>
    public bool ProxyUrlRedacted { get; init; }
    public string UuEndpointDisplay { get; init; } = "";
    /// <summary>已保存 UU 地址含未回显的路径；未编辑时保存须保留原值。</summary>
    public bool UuEndpointRedacted { get; init; }
    public bool Enabled { get; init; } = true;
    public bool AllowActiveDisconnect { get; init; } = true;
    public bool KeepAwakeOnAc { get; init; } = true;
    public string AuthChannelMode { get; init; } = AuthChannelModes.ManualOnly;

    /// <summary>配置字段的稳定拼接；变化时才把投影应用到草稿。</summary>
    public string ProjectionKey { get; init; } = "";
}

/// <summary>
/// 设置页投影规则（#8 / #41）。只依赖状态读取，不读受限配置、无 I/O、无副作用。
/// 只有后台明确提供了配置投影（<see cref="StatusSnapshot.SettingsProjected"/>）且状态可信时，
/// 才把非秘密字段提供给页面预填；密码永远不在投影中，账号仅用于预填。
/// </summary>
internal static class SettingsModel
{
    /// <summary>认证地址固定：与既有设置表单逐字一致。</summary>
    public const string PortalLine = "https://login.csust.edu.cn  ·  Dr.COM";

    /// <summary>凭据说明：普通界面不显示、不回读密码（故事 51）。</summary>
    public const string CredentialNote =
        "校园网账号与密码使用 Windows DPAPI 加密保存，存放于仅管理员可访问的受限目录。" +
        "本页不显示、也不回读已保存的密码；保存时请求管理员确认。";

    /// <summary>密码规则说明（故事 52）。</summary>
    public const string PasswordNote =
        "密码输入始终遮挡。账号未变且密码留空时保留已保存的密码；首次设置或更改账号必须输入新密码。";

    /// <summary>保存入口说明（故事 53、58）：取消或拒绝授权零写入；失败不宣称原子性。</summary>
    public const string SaveNote =
        "保存会请求管理员确认；取消或拒绝授权时不写入任何设置。保存设置或修改密码不会解除认证暂停。" +
        "设置与凭据分两步写入，保存失败时可能只写入其中一部分。";

    /// <summary>地址含未回显部分时的受控说明：不编辑即保留受限存储中的完整原值。</summary>
    public static string RedactedUrlNote(bool proxy, bool uu) => (proxy, uu) switch
    {
        (true, true) => "已保存的代理与 UU 地址包含未回显的路径或查询参数；不改动对应输入框将保留原值。",
        (true, false) => "已保存的代理地址包含未回显的路径或查询参数；不改动该输入框将保留原值。",
        (false, true) => "已保存的 UU 地址包含未回显的路径；不改动该输入框将保留原值。",
        _ => ""
    };

    /// <summary>没有配置投影时的受控说明：不把空白当成已保存值。</summary>
    public const string ProjectionUnavailableNote =
        "暂时无法从后台读取已保存的配置。表单内容可能不是当前已保存值；未改动的账号与地址会保留原值，首次设置没有原值时保持为空。可使用清空按钮明确删除地址。";

    /// <summary>恢复选项在无投影且未编辑时对页面是未知值；说明与保存语义逐条对应。</summary>
    public const string OptionsUnknownNote =
        "恢复选项显示为“未确定”：尚未读取到已保存的开关状态。已有设置时，未改动的选项会保留原值；" +
        "首次设置时按默认值保存（自动恢复、主动断线与保持唤醒开启，内嵌通道仅手动）。勾选或选择即表示明确修改。";

    /// <summary>
    /// 开关值是否已知：没有可信配置投影且用户尚未改动时为未知，界面必须显示为“未确定”，
    /// 不能把页面默认值伪装成已保存配置。
    /// </summary>
    public static bool OptionKnown(bool keep, bool configProjected) => !(keep && !configProjected);

    /// <summary>外观分区说明（#9 / 故事 70）：界面偏好不含秘密，按 Windows 用户隔离保存。</summary>
    public const string AppearanceNote =
        "选择立即生效，并保存到当前 Windows 用户的界面偏好；主题设置不包含账号、密码或其他凭据信息。";

    /// <summary>不可信状态下的守护网络降级文案：绝不沿用上次画面（故事 15）。</summary>
    public const string UnconfirmedNetwork = "不可确认";

    /// <summary>把一次状态读取投影为设置页内容；null 表示尚未取得状态。</summary>
    public static SettingsContent From(StatusReading? reading)
    {
        var trusted = reading?.Trusted == true;
        var snapshot = trusted ? reading!.Snapshot : null;
        var projected = snapshot?.SettingsProjected == true;
        var content = new SettingsContent
        {
            Trusted = trusted,
            RecoveryState = trusted
                ? (snapshot!.Enabled ? "已开启" : "已关闭")
                : OverviewModel.UnconfirmedRecovery.State,
            GuardedNetwork = trusted ? snapshot!.Wifi : UnconfirmedNetwork,
            ConfigProjected = projected,
            ProjectedAt = projected ? snapshot!.UpdatedAt : default,
            Username = projected ? snapshot!.Username : "",
            InterfaceId = projected ? snapshot!.InterfaceId : "",
            WifiProfile = projected ? snapshot!.WifiProfile : "",
            ProxyUrlDisplay = projected ? snapshot!.ProxyUrlDisplay : "",
            ProxyUrlRedacted = projected && snapshot!.ProxyUrlRedacted,
            UuEndpointDisplay = projected ? snapshot!.UuEndpointDisplay : "",
            UuEndpointRedacted = projected && snapshot!.UuEndpointRedacted,
            Enabled = projected ? snapshot!.Enabled : true,
            AllowActiveDisconnect = projected ? snapshot!.AllowActiveDisconnect : true,
            KeepAwakeOnAc = projected ? snapshot!.KeepAwakeOnAc : true,
            AuthChannelMode = projected && AuthChannelModes.IsKnown(snapshot!.AuthChannelMode)
                ? snapshot.AuthChannelMode
                : AuthChannelModes.ManualOnly
        };
        return content with { ProjectionKey = projected ? ConfigFingerprint(content) : "" };
    }

    /// <summary>配置字段指纹：不含密码或任何凭据内容；账号仅作为预填值参与变化检测。</summary>
    public static string ConfigFingerprint(SettingsContent content) => string.Join("\u001f",
        content.Username, content.InterfaceId, content.WifiProfile, content.ProxyUrlDisplay, content.ProxyUrlRedacted,
        content.UuEndpointDisplay, content.UuEndpointRedacted,
        content.Enabled, content.AllowActiveDisconnect, content.KeepAwakeOnAc, content.AuthChannelMode);
}
