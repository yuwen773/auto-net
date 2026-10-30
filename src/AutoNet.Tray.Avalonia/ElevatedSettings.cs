using AutoNet.Core;
using System.Text.Json;

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
    StoreFailed,
    /// <summary>保留未回显地址需要读取现有设置，但读取失败；本次未写入任何内容。</summary>
    SettingsUnreadable,
    /// <summary>另一个保存（本进程、其他会话或兼容提权入口）持有保存锁；本次未写入任何内容。</summary>
    SaveBusy,
    /// <summary>兼容提权编辑器打开后，设置已被其他窗口或会话改写；本次未写入任何内容。</summary>
    SettingsChanged
}

/// <summary>
/// 兼容提权编辑器的乐观并发守卫：窗口打开时读到的设置快照，保存时必须在同一把保存锁内仍然相等。
/// 兼容编辑器没有“保留原值”标记，全部字段都来自打开时的快照；没有该守卫时，
/// 另一个会话在窗口打开期间保存的新设置会被旧快照静默覆盖。
/// </summary>
internal sealed record SettingsSaveGuard(Settings? Expected);

internal sealed record SettingsSaveResult(SettingsSaveOutcome Outcome, string Message)
{
    public bool Saved => Outcome == SettingsSaveOutcome.Saved;
}

/// <summary>编辑器的一次加载结果：已保存设置、已保存凭据（仅编辑器进程内使用）与可选 Wi-Fi 配置。</summary>
internal sealed record SettingsEditorState(Settings? Settings, Credentials? Credentials, IReadOnlyList<WifiProfile> Profiles);

/// <summary>
/// 用户提交的编辑内容；密码是否留空决定凭据保留规则。
/// <see cref="KeepProxyUrl"/> / <see cref="KeepUuEndpoint"/> 表示普通界面只看到安全展示值、
/// 用户未编辑该字段：接收端必须从受限存储读取完整原值，绝不以投影值覆盖。
/// </summary>
internal sealed record SettingsDraft(
    WifiProfile? Profile,
    string Username,
    string Password,
    string ProxyUrl,
    string UuEndpoint,
    bool Enabled,
    bool AllowActiveDisconnect,
    bool KeepAwakeOnAc,
    string AuthChannelMode = AuthChannelModes.ManualOnly)
{
    /// <summary>普通界面没有明确选择 Wi-Fi：接收端保留受限存储中的当前守护配置，避免过期草稿覆盖。</summary>
    public bool KeepProfile { get; init; }
    /// <summary>用户未改动账号：接收端保留受限存储中的账号；无投影时只改其他字段也无需重输账号。</summary>
    public bool KeepUsername { get; init; }
    public bool KeepProxyUrl { get; init; }
    public bool KeepUuEndpoint { get; init; }
    /// <summary>普通界面没有可信配置投影、用户未改动该开关：接收端保留受限存储中的原值。</summary>
    public bool KeepEnabled { get; init; }
    public bool KeepAllowActiveDisconnect { get; init; }
    public bool KeepAwakeOnAcKept { get; init; }
    public bool KeepAuthChannelMode { get; init; }
    /// <summary>从未取得已有配置时，若受限存储确认没有配置，允许以空地址创建首次设置。</summary>
    public bool AllowFirstTimeSetup { get; init; }
}

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
    Action<Settings> saveSettings,
    Func<IDisposable>? acquireLock = null)
{
    public const string MissingProfileMessage = "请选择校园 Wi-Fi 配置。";
    public const string PasswordRequiredMessage = "首次设置或更改账号时，请输入校园网密码。";
    public const string CredentialsEmptyMessage = "请输入校园网账号和密码。";
    public const string SavedMessage = "已保存。后台服务将在下一轮检测中使用新设置。";
    public const string MissingStoreMessage = "请先安装后台服务，然后设置认证信息。";
    public const string SaveBusyMessage = "保存未完成：另一个设置保存正在进行，未写入任何内容。请稍后重试。";
    public const string SettingsChangedMessage = "保存未完成：设置已被其他窗口或会话修改，未写入任何内容。请关闭后重新打开设置再保存。";
    /// <summary>
    /// 接收端拒绝保存的统一文案。拒绝原因经 <see cref="SettingsSaveOutcome"/> 枚举跨进程传递，
    /// 具体文本不会进入界面，所以这句话必须同时覆盖账号、代理、UU 地址与无法识别的认证通道模式。
    /// </summary>
    public const string InvalidSettingsMessage = "设置校验未通过，未写入任何设置。请检查账号、密码、代理、UU 地址与认证通道设置。";

    /// <summary>生产依赖：既有受限存储与真实 Wi-Fi 配置枚举（只列所有用户可用的配置，与既有表单一致）。</summary>
    public static ElevatedSettings Production() => new(
        () => ReadSettings(Storage.SettingsPath),
        Storage.LoadCredentials,
        () => Wifi.Profiles().Where(profile => profile.AllUsers).ToList(),
        Storage.SaveCredentials,
        Storage.SaveSettings,
        SettingsWriteLock.Production);

    /// <summary>只把确实不存在的设置视作首次配置；读取失败或损坏不能降级为空配置。</summary>
    internal static Settings? ReadSettings(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(Storage.ReadShared(path), Storage.Json)
                ?? throw new InvalidDataException("设置记录为空。");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>加载当前编辑状态；只读，不产生任何写入（故事 38 的查看侧）。</summary>
    public SettingsEditorState Load() => new(loadSettings(), loadCredentials(), loadProfiles());

    /// <summary>
    /// 按既有规则保存一次编辑；返回受控结果，绝不抛出异常。
    /// 整个“读取既有值 → 写凭据 → 写设置”在保存锁内完成：多个 Windows 会话或兼容提权入口
    /// 并发保存时，较早读取的旧值不会覆盖另一方刚保存的新地址或新密码。
    /// 锁只串行化保存，不提供 settings.json 与 credentials.dpapi 之间的跨文件原子性。
    /// </summary>
    public SettingsSaveResult Save(SettingsDraft draft, SettingsSaveGuard? guard = null)
    {
        // 故事 35：必须选择网卡与已保存 Wi-Fi 配置，守护目标不允许为空。
        // 例外：普通界面没有可信投影时保留受限存储中的当前目标（KeepProfile），由 SaveLocked 在锁内解析。
        if (draft.Profile is null && !draft.KeepProfile)
            return new(SettingsSaveOutcome.MissingProfile, MissingProfileMessage);

        IDisposable? writeLock = null;
        if (acquireLock is not null)
        {
            try { writeLock = acquireLock(); }
            catch (TimeoutException) { return new(SettingsSaveOutcome.SaveBusy, SaveBusyMessage); }
            catch (Exception e) { return new(SettingsSaveOutcome.StoreFailed, "保存未完成：" + e.GetType().Name); }
        }

        try { return SaveLocked(draft, guard); }
        finally { writeLock?.Dispose(); }
    }

    private SettingsSaveResult SaveLocked(SettingsDraft draft, SettingsSaveGuard? guard)
    {
        // 故事 37：账号未变且密码留空 → 保留已保存密码；首次设置或更改账号 → 必须提供新密码。
        var existing = loadCredentials();
        // #41 审查修复：普通界面只看到地址的安全展示值，未编辑的开关也可能对页面未知。
        // 这些字段必须保留受限存储中的完整原值，不能以展示值或页面默认值覆盖。
        var needsExisting = draft.KeepProfile || draft.KeepProxyUrl || draft.KeepUuEndpoint || draft.KeepEnabled ||
            draft.KeepAllowActiveDisconnect || draft.KeepAwakeOnAcKept || draft.KeepAuthChannelMode;
        Settings? existingSettings = null;
        if (needsExisting || guard is not null)
        {
            try { existingSettings = loadSettings(); }
            catch (Exception e)
            {
                // 读取失败绝不退回展示值：那会把地址中未回显的部分静默截断。此处尚未写入任何内容。
                return new(SettingsSaveOutcome.SettingsUnreadable,
                    "保存未完成：无法读取现有设置（" + e.GetType().Name + "），未写入任何内容。");
            }
            // 兼容提权编辑器没有“保留原值”标记，必须在锁内确认打开窗口时的快照仍然有效；
            // 否则另一个会话刚保存的设置会被旧快照静默覆盖。
            if (guard is not null && !Equals(existingSettings, guard.Expected))
                return new(SettingsSaveOutcome.SettingsChanged, SettingsChangedMessage);
            if (needsExisting && existingSettings is null && (!draft.AllowFirstTimeSetup ||
                draft.KeepProxyUrl && draft.ProxyUrl.Length != 0 ||
                draft.KeepUuEndpoint && draft.UuEndpoint.Length != 0))
                return new(SettingsSaveOutcome.SettingsUnreadable, "保存未完成：现有设置不存在，未写入任何内容。");
        }

        // 页面没有可信投影且受限存储也没有可保留的守护目标时，无法确定要保存哪个网络。
        if (draft.Profile is null && existingSettings is null)
            return new(SettingsSaveOutcome.MissingProfile, MissingProfileMessage);

        // #41 审查修复：无法识别的既有通道模式不能被页面默认值静默改写；未编辑时明确拒绝。
        if (draft.KeepAuthChannelMode && existingSettings is not null &&
            !AuthChannelModes.IsKnown(existingSettings.AuthChannelMode))
            return new(SettingsSaveOutcome.InvalidSettings, InvalidSettingsMessage);

        // 用户未改动账号时以受限存储为准：无投影或投影过期都不会把旧草稿账号写回。
        var username = draft.Username.Trim();
        if (draft.KeepUsername && existing is not null) username = existing.Username.Trim();
        var keepsPassword = draft.Password.Length == 0;
        if (keepsPassword && (existing is null || existing.Username != username))
            return new(SettingsSaveOutcome.PasswordRequired, PasswordRequiredMessage);

        // 与既有表单同语义：账号空白在凭据写入前拒绝（文案与 Storage.SaveCredentials 逐字一致）。
        if (username.Length == 0) return new(SettingsSaveOutcome.InvalidSettings, CredentialsEmptyMessage);

        // 用户没有在页面明确选择 Wi-Fi 时，守护目标以受限存储中的当前值为准：
        // 另一个窗口刚改成网络 B 后，本窗口的旧草稿不能把它改回网络 A。
        // profile 为 null 时 KeepProfile 必为 true，且上面的检查已确认 existingSettings 非空。
        var profile = draft.Profile;
        var guardTarget = draft.KeepProfile ? existingSettings : null;
        var settings = new Settings
        {
            InterfaceId = guardTarget?.InterfaceId ?? profile!.InterfaceId,
            WifiProfile = guardTarget?.WifiProfile ?? profile!.Name,
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
