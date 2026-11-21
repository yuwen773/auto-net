using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置草稿契约（#41）：一处拥有“用户是否编辑”“接收端必须保留哪些原值”“这份草稿能否发送”三类规则。
/// 页面只报告哪个字段变了；接收端只按 <see cref="Resolve"/> 合成最终设置；显示侧只问
/// <see cref="OptionKnown"/>。这样覆盖规则不再分散在页面、视图、提权编辑器与 QA 夹具里。
/// </summary>
internal static class SettingsDraftContract
{
    /// <summary>
    /// 接收端是否需要读取受限存储中的既有设置。地址、守护目标与恢复选项只要有一个
    /// 仍处于“未编辑”状态，就必须在保存锁内读取原值，不能以展示值或页面默认值覆盖。
    /// </summary>
    public static bool NeedsExistingSettings(SettingsDraft draft) =>
        draft.KeepProfile || draft.KeepProxyUrl || draft.KeepUuEndpoint || draft.KeepEnabled ||
        draft.KeepAllowActiveDisconnect || draft.KeepAwakeOnAcKept || draft.KeepAuthChannelMode;

    /// <summary>
    /// 保存成功后页面是否仍有“接收端保留、页面不知道”的字段。只有页面从未显示过、
    /// 由接收端保留的值才算未知；完整显示的地址即使带保留标记，页面也知道它的值。
    /// </summary>
    public static bool HasKeptFields(SettingsDraft draft, bool projected, bool proxyRedacted, bool uuRedacted) =>
        (draft.KeepProfile && (draft.Profile is null || !projected)) ||
        (draft.KeepProxyUrl && (!projected || proxyRedacted)) ||
        (draft.KeepUuEndpoint && (!projected || uuRedacted)) ||
        (draft.KeepEnabled && !projected) ||
        (draft.KeepAllowActiveDisconnect && !projected) ||
        (draft.KeepAwakeOnAcKept && !projected) ||
        (draft.KeepAuthChannelMode && !projected);

    /// <summary>
    /// 没有可信配置投影且用户尚未改动时为未知：界面显示“未确定”，保存端保留原值。
    /// 用户明确改动后即为已知，即使改动值恰好等于页面默认值。
    /// </summary>
    public static bool OptionKnown(bool keep, bool configProjected) => !(keep && !configProjected);

    /// <summary>页面发送前的本地校验：只使用草稿本身，不读取受限存储。</summary>
    public static SettingsPageSaveResult? Validate(SettingsDraft draft)
    {
        // 没有明确选择 Wi-Fi 时，KeepProfile 表示由保存端保留受限存储中的当前目标；
        // 只有既没有选择、也没有保留语义时才是真正的“未选择配置”。
        if (draft.Profile is null && !draft.KeepProfile)
            return new(SettingsPageSaveState.ValidationFailed, ElevatedSettings.MissingProfileMessage);

        // 未改动账号（KeepUsername）时由保存端读取受限存储中的账号；只有明确改动后留空才拒绝。
        if (!draft.KeepUsername && draft.Username.Trim().Length == 0)
            return new(SettingsPageSaveState.ValidationFailed, ElevatedSettings.CredentialsEmptyMessage);

        try
        {
            // 与保存端共用同一套地址与通道规则，避免两处校验漂移。
            Settings.ValidateConnectionOptions(
                draft.ProxyUrl.Trim(), draft.UuEndpoint.Trim(), draft.AuthChannelMode);
        }
        catch (ArgumentException e)
        {
            // Core 的固定业务文案可以呈现；这里不涉及存储或凭据内容。
            return new(SettingsPageSaveState.ValidationFailed, e.Message);
        }

        return null;
    }

    /// <summary>
    /// 按“保留 / 用户编辑”事实合成最终设置。调用方必须已确认：需要读取时既有设置可用，
    /// 且 <see cref="SettingsDraft.Profile"/> 在未保留守护目标时非空。
    /// </summary>
    public static Settings Resolve(SettingsDraft draft, Settings? existingSettings)
    {
        var profile = draft.Profile;
        var preserved = draft.KeepProfile ? existingSettings : null;
        return new Settings
        {
            InterfaceId = preserved?.InterfaceId ?? profile!.InterfaceId,
            WifiProfile = preserved?.WifiProfile ?? profile!.Name,
            Enabled = draft.KeepEnabled && existingSettings is not null
                ? existingSettings.Enabled
                : draft.Enabled,
            AllowActiveDisconnect = draft.KeepAllowActiveDisconnect && existingSettings is not null
                ? existingSettings.AllowActiveDisconnect
                : draft.AllowActiveDisconnect,
            KeepAwakeOnAc = draft.KeepAwakeOnAcKept && existingSettings is not null
                ? existingSettings.KeepAwakeOnAc
                : draft.KeepAwakeOnAc,
            ProxyUrl = draft.KeepProxyUrl && existingSettings is not null
                ? existingSettings.ProxyUrl
                : draft.ProxyUrl.Trim(),
            UuEndpoint = draft.KeepUuEndpoint && existingSettings is not null
                ? existingSettings.UuEndpoint
                : draft.UuEndpoint.Trim(),
            AuthChannelMode = draft.KeepAuthChannelMode && existingSettings is not null
                ? existingSettings.AuthChannelMode
                : draft.AuthChannelMode
        };
    }
}

/// <summary>
/// 页面草稿的显示语义：把八个“保留”标志翻译成控件需要的三态与提示可见性。
/// 视图不再逐字段重复判断“未投影且未编辑即未知”。
/// </summary>
internal sealed partial class SettingsDraftValues
{
    public bool? EnabledForDisplay(bool configProjected) =>
        SettingsDraftContract.OptionKnown(KeepEnabled, configProjected) ? Enabled : null;

    public bool? AllowActiveDisconnectForDisplay(bool configProjected) =>
        SettingsDraftContract.OptionKnown(KeepAllowActiveDisconnect, configProjected) ? AllowActiveDisconnect : null;

    public bool? KeepAwakeOnAcForDisplay(bool configProjected) =>
        SettingsDraftContract.OptionKnown(KeepAwakeOnAcKept, configProjected) ? KeepAwakeOnAc : null;

    public bool ChannelModeKnown(bool configProjected) =>
        SettingsDraftContract.OptionKnown(KeepAuthChannelMode, configProjected);

    public bool AnyOptionUnknown(bool configProjected) =>
        !SettingsDraftContract.OptionKnown(KeepEnabled, configProjected) ||
        !SettingsDraftContract.OptionKnown(KeepAllowActiveDisconnect, configProjected) ||
        !SettingsDraftContract.OptionKnown(KeepAwakeOnAcKept, configProjected) ||
        !SettingsDraftContract.OptionKnown(KeepAuthChannelMode, configProjected);
}
