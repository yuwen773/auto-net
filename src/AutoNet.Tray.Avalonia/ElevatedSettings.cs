using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>一次保存的可观察结果。成功静默关闭窗口，其余必须给出受控说明。</summary>
internal enum SettingsSaveOutcome
{
    /// <summary>凭据与设置都已按既有规则写入。</summary>
    Saved,
    /// <summary>未选择校园 Wi-Fi 配置。</summary>
    MissingProfile,
    /// <summary>首次设置或更改账号时未提供密码（故事 37）。</summary>
    PasswordRequired,
    /// <summary>业务校验未通过（设置、账号或地址不合法）；消息是受控校验文案。</summary>
    InvalidSettings,
    /// <summary>存储写入失败；只披露异常类型，不携带异常文本（安全边界）。</summary>
    StoreFailed
}

internal sealed record SettingsSaveResult(SettingsSaveOutcome Outcome, string Message)
{
    public bool Saved => Outcome == SettingsSaveOutcome.Saved;
}

/// <summary>编辑器的一次加载结果：已保存设置、已保存凭据（仅编辑器进程内使用）与可选 Wi-Fi 配置。</summary>
internal sealed record SettingsEditorState(Settings? Settings, Credentials? Credentials, IReadOnlyList<WifiProfile> Profiles);

/// <summary>用户提交的编辑内容；密码是否留空决定凭据保留规则。</summary>
internal sealed record SettingsDraft(
    WifiProfile? Profile,
    string Username,
    string Password,
    string ProxyUrl,
    string UuEndpoint,
    bool Enabled,
    bool AllowActiveDisconnect,
    bool KeepAwakeOnAc,
    string AuthChannelMode = AuthChannelModes.ManualOnly);

/// <summary>
/// 管理员设置编辑（迁移工单 #8 / T07 / 故事 35–39）：与既有 WinForms 设置表单同语义。
/// - 网卡与已保存 Wi-Fi 选择（故事 35）：从调用方提供的可选配置中选择，选中项决定守护目标；
/// - 凭据规则（故事 37）：账号未变且密码留空 → 保留已保存密码；首次设置或更改账号 → 必须提供新密码；
/// - 存储（故事 36）：凭据经调用方写入既有 DPAPI 受限存储，本类不引入任何新的存储路径或明文文件；
/// - 取消（故事 38）：取消不是本类的路径——不调用 <see cref="Save"/> 就没有任何写入；
/// - 认证暂停（故事 39）：本类没有任何解除依赖，保存绝不触碰认证暂停标记（构造性保证）。
/// 写入顺序与既有表单一致：先凭据后设置；任何失败如实报告，不谎报成功。
/// 所有依赖以委托注入，便于受控测试；本类不依赖任何 UI 类型。
/// </summary>
internal sealed class ElevatedSettings(
    Func<Settings?> loadSettings,
    Func<Credentials?> loadCredentials,
    Func<IReadOnlyList<WifiProfile>> loadProfiles,
    Action<Credentials> saveCredentials,
    Action<Settings> saveSettings)
{
    public const string MissingProfileMessage = "请选择校园 Wi-Fi 配置。";
    public const string PasswordRequiredMessage = "首次设置或更改账号时，请输入校园网密码。";
    public const string CredentialsEmptyMessage = "请输入校园网账号和密码。";
    public const string SavedMessage = "已保存。后台服务将在下一轮检测中使用新设置。";
    public const string MissingStoreMessage = "请先安装后台服务，然后设置认证信息。";

    /// <summary>生产依赖：既有受限存储与真实 Wi-Fi 配置枚举（只列所有用户可用的配置，与既有表单一致）。</summary>
    public static ElevatedSettings Production() => new(
        Storage.LoadSettings,
        Storage.LoadCredentials,
        () => Wifi.Profiles().Where(profile => profile.AllUsers).ToList(),
        Storage.SaveCredentials,
        Storage.SaveSettings);

    /// <summary>加载当前编辑状态；只读，不产生任何写入（故事 38 的查看侧）。</summary>
    public SettingsEditorState Load() => new(loadSettings(), loadCredentials(), loadProfiles());

    /// <summary>按既有规则保存一次编辑；返回受控结果，绝不抛出异常。</summary>
    public SettingsSaveResult Save(SettingsDraft draft)
    {
        // 故事 35：必须选择网卡与已保存 Wi-Fi 配置，守护目标不允许为空。
        if (draft.Profile is not { } profile) return new(SettingsSaveOutcome.MissingProfile, MissingProfileMessage);

        var username = draft.Username.Trim();
        // 故事 37：账号未变且密码留空 → 保留已保存密码；首次设置或更改账号 → 必须提供新密码。
        var existing = loadCredentials();
        var keepsPassword = draft.Password.Length == 0;
        if (keepsPassword && (existing is null || existing.Username != username))
            return new(SettingsSaveOutcome.PasswordRequired, PasswordRequiredMessage);

        // 与既有表单同语义：账号空白在凭据写入前拒绝（文案与 Storage.SaveCredentials 逐字一致）。
        if (username.Length == 0) return new(SettingsSaveOutcome.InvalidSettings, CredentialsEmptyMessage);

        var settings = new Settings
        {
            InterfaceId = profile.InterfaceId,
            WifiProfile = profile.Name,
            Enabled = draft.Enabled,
            AllowActiveDisconnect = draft.AllowActiveDisconnect,
            KeepAwakeOnAc = draft.KeepAwakeOnAc,
            ProxyUrl = draft.ProxyUrl.Trim(),
            UuEndpoint = draft.UuEndpoint.Trim(),
            AuthChannelMode = draft.AuthChannelMode
        };

        try
        {
            settings.Validate();
        }
        catch (ArgumentException e)
        {
            // 业务校验失败：Message 来自 Core 的固定文案，可以呈现。
            return new(SettingsSaveOutcome.InvalidSettings, e.Message);
        }

        try
        {
            // 与既有表单一致的顺序：先凭据后设置。存储阶段的一切异常（含 ArgumentException）
            // 只披露异常类型，绝不回显异常文本（安全边界）；失败如实呈现，不谎报成功。
            saveCredentials(new(username, keepsPassword ? existing!.Password : draft.Password));
            saveSettings(settings);
        }
        catch (Exception e)
        {
            return new(SettingsSaveOutcome.StoreFailed, "保存未完成：" + e.GetType().Name);
        }

        return new(SettingsSaveOutcome.Saved, SavedMessage);
    }
}
