using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>设置页一次保存的受控结果；状态只描述页面能证实的事实。</summary>
internal enum SettingsPageSaveState
{
    Saved,
    Busy,
    ValidationFailed,
    Cancelled,
    Rejected,
    TimedOut,
    StoreFailed,
    /// <summary>保存前发现设置已被其他窗口或会话修改；本次零写入，不能与“可能已写入”混用。</summary>
    SettingsChanged,
    OutcomeUnknown
}

internal sealed record SettingsPageSaveResult(SettingsPageSaveState State, string Message)
{
    public bool Saved => State == SettingsPageSaveState.Saved;
}

/// <summary>可编辑草稿：普通 UI 进程内存中的临时值，密码不写日志、不进命令行或文件。</summary>
internal sealed class SettingsDraftValues
{
    public WifiProfile? Profile { get; set; }
    /// <summary>用户未在页面明确选择 Wi-Fi：保存端保留受限存储中的当前守护配置。</summary>
    public bool KeepProfile { get; set; } = true;
    /// <summary>用户未改动账号：保存端保留受限存储中的账号，只改其他字段时无需重输。</summary>
    public bool KeepUsername { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    /// <summary>地址含未回显部分且用户尚未编辑：保存端保留受限存储中的完整原值。</summary>
    public bool KeepProxyUrl { get; set; } = true;
    public string UuEndpoint { get; set; } = "";
    /// <summary>地址含未回显部分且用户尚未编辑：保存端保留受限存储中的完整原值。</summary>
    public bool KeepUuEndpoint { get; set; } = true;
    /// <summary>没有可信配置投影且用户尚未改动该开关：保存端保留受限存储中的原值。</summary>
    public bool KeepEnabled { get; set; } = true;
    public bool KeepAllowActiveDisconnect { get; set; } = true;
    public bool KeepAwakeOnAcKept { get; set; } = true;
    public bool KeepAuthChannelMode { get; set; } = true;
    /// <summary>从未取得已有设置时，允许受限存储确认没有配置的情况下创建首次设置。</summary>
    public bool AllowFirstTimeSetup { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool AllowActiveDisconnect { get; set; } = true;
    public bool KeepAwakeOnAc { get; set; } = true;
    public string AuthChannelMode { get; set; } = AuthChannelModes.ManualOnly;

    public SettingsDraft ToDraft() => new(
        Profile, Username, Password, ProxyUrl, UuEndpoint,
        Enabled, AllowActiveDisconnect, KeepAwakeOnAc, AuthChannelMode)
    {
        KeepProfile = KeepProfile,
        KeepUsername = KeepUsername,
        KeepProxyUrl = KeepProxyUrl,
        KeepUuEndpoint = KeepUuEndpoint,
        KeepEnabled = KeepEnabled,
        KeepAllowActiveDisconnect = KeepAllowActiveDisconnect,
        KeepAwakeOnAcKept = KeepAwakeOnAcKept,
        KeepAuthChannelMode = KeepAuthChannelMode,
        AllowFirstTimeSetup = AllowFirstTimeSetup
    };

    public void CopyFrom(SettingsDraft draft)
    {
        Profile = draft.Profile;
        KeepProfile = draft.KeepProfile;
        KeepUsername = draft.KeepUsername;
        Username = draft.Username;
        Password = draft.Password;
        ProxyUrl = draft.ProxyUrl;
        KeepProxyUrl = draft.KeepProxyUrl;
        UuEndpoint = draft.UuEndpoint;
        KeepUuEndpoint = draft.KeepUuEndpoint;
        KeepEnabled = draft.KeepEnabled;
        KeepAllowActiveDisconnect = draft.KeepAllowActiveDisconnect;
        KeepAwakeOnAcKept = draft.KeepAwakeOnAcKept;
        KeepAuthChannelMode = draft.KeepAuthChannelMode;
        AllowFirstTimeSetup = draft.AllowFirstTimeSetup;
        Enabled = draft.Enabled;
        AllowActiveDisconnect = draft.AllowActiveDisconnect;
        KeepAwakeOnAc = draft.KeepAwakeOnAc;
        AuthChannelMode = draft.AuthChannelMode;
    }
}

/// <summary>
/// 设置页模型（#41）：字段与既有提权编辑窗口一致，保存经独立提权交接。
/// 本类不依赖 Avalonia，也不读写受限存储；只持有普通 UI 进程内存里的草稿，
/// 由注入的交接委托把草稿交给提权进程（保存端的跨进程互斥见保存锁 SettingsWriteLock）。
/// 非秘密配置与账号来自配置投影；密码在每次交接结束后立即从草稿清除。
/// </summary>
internal sealed class SettingsPage
{
    public const string BusyMessage = "正在保存，请稍候。";
    public const string SavingMessage = "正在请求管理员确认…";
    public const string CancelledMessage = "已取消管理员授权，未写入任何设置。";
    public const string TimedOutMessage = "等待管理员授权超时，未写入任何设置。";
    public const string RejectedMessage = "设置交接未通过，未写入任何设置。";
    // 接收端与页面共用同一份文案，避免两处措辞漂移。
    public const string InvalidSettingsMessage = ElevatedSettings.InvalidSettingsMessage;
    public const string StoreFailedMessage = "保存未完成：存储写入失败。凭据可能已写入、设置可能未写入；请核对后重试。";
    public const string SettingsUnreadableMessage = "保存未完成：无法读取现有设置，未写入任何内容。请稍后重试。";
    public const string SaveBusyMessage = ElevatedSettings.SaveBusyMessage;
    public const string SettingsChangedMessage = ElevatedSettings.SettingsChangedMessage;
    public const string OutcomeUnknownMessage = "保存结果未知：管理员操作可能已写入部分或全部内容，请核对当前设置。";
    public const string SavedWithPendingEditsMessage = "已保存提交时的内容；保存期间的新修改尚未保存，请再次保存。";

    private readonly Func<SettingsDraft, CancellationToken, Task<HandoffResult>> handoff;
    private readonly Func<IReadOnlyList<WifiProfile>> loadProfiles;
    private readonly Func<Task>? refresh;
    private int savingFlag;
    private int draftVersion;
    private string appliedKey = "";
    private DateTimeOffset lastSavedAt = DateTimeOffset.MinValue;
    private bool hasSeenSettings;

    internal SettingsPage(
        Func<IReadOnlyList<WifiProfile>> loadProfiles,
        Func<SettingsDraft, CancellationToken, Task<HandoffResult>> handoff,
        Func<Task>? refresh = null)
    {
        this.handoff = handoff;
        this.loadProfiles = loadProfiles;
        this.refresh = refresh;
        Profiles = LoadProfiles(out var failed);
        ProfileLoadFailed = failed;
    }

    /// <summary>可选的已保存 Wi-Fi 配置；枚举失败时为空列表，保存会以受控文案拒绝。</summary>
    public IReadOnlyList<WifiProfile> Profiles { get; private set; }

    public bool ProfileLoadFailed { get; private set; }

    /// <summary>重新枚举系统 Wi-Fi 配置；页面进入或用户重试时调用，避免启动后新增的配置必须重启托盘。</summary>
    public void RefreshProfiles()
    {
        Profiles = LoadProfiles(out var failed);
        ProfileLoadFailed = failed;
        ProfilesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private IReadOnlyList<WifiProfile> LoadProfiles(out bool failed)
    {
        try { failed = false; return loadProfiles(); }
        catch (Exception) { failed = true; return []; }
    }

    internal SettingsContent Content { get; private set; } = new();

    public SettingsDraftValues Draft { get; } = new();

    public bool Saving => Volatile.Read(ref savingFlag) != 0;

    public bool Dirty { get; private set; }

    public string Message { get; private set; } = "";

    public bool MessageIsError { get; private set; }

    /// <summary>消息表示保存成功（含“保存期间有新修改”）；视图据此选择成功色调，不解析文案前缀。</summary>
    public bool MessageIsSuccess { get; private set; }

    /// <summary>消息、保存中状态或只读事实变化；UI 侧负责调度到界面线程。</summary>
    public event Action? StateChanged;

    /// <summary>草稿被程序化改写（投影应用、放弃修改、QA 驱动）；UI 侧回填控件。</summary>
    public event Action? ProjectionApplied;

    /// <summary>Wi-Fi 配置列表已重新枚举；UI 侧刷新选择项并保留当前选择。</summary>
    public event Action? ProfilesChanged;

    /// <summary>密码已从草稿清除；UI 侧立即清空密码输入框。</summary>
    public event Action? PasswordCleared;

    /// <summary>应用一次状态投影。只有未修改的草稿才接受新的配置投影，避免轮询覆盖用户输入。</summary>
    public void Update(SettingsContent content)
    {
        // 保存前写入的配置投影比页面刚保存的内容更旧：采用它会把只读事实与脱敏提示刷回
        // 保存前的值，而草稿仍保持较新的值。等下一次状态写入（ProjectedAt > lastSavedAt）再采用。
        if (content.ConfigProjected && content.ProjectedAt <= lastSavedAt) return;

        Content = content;
        var profileUpdated = false;
        var usernameUpdated = false;
        if (content.ConfigProjected)
        {
            // 草稿已修改时也必须记住已确认的现有配置，避免其消失后误当首次设置。
            hasSeenSettings = true;
            Draft.AllowFirstTimeSetup = false;
            // 草稿已修改时不整体回填（会覆盖用户输入），但用户未编辑的开关必须跟上可信投影，
            // 否则页面会把“未知”继续当成未知，而实际已保存值已经可取。
            if (Draft.KeepEnabled) Draft.Enabled = content.Enabled;
            if (Draft.KeepAllowActiveDisconnect) Draft.AllowActiveDisconnect = content.AllowActiveDisconnect;
            if (Draft.KeepAwakeOnAcKept) Draft.KeepAwakeOnAc = content.KeepAwakeOnAc;
            if (Draft.KeepAuthChannelMode && AuthChannelModes.IsKnown(content.AuthChannelMode))
                Draft.AuthChannelMode = content.AuthChannelMode;
            // 用户没有明确选择 Wi-Fi 时，草稿必须跟上其他窗口刚保存的守护配置；
            // 否则界面会继续显示旧配置，保存也会带着过期草稿（保存端仍按 KeepProfile 保留原值）。
            if (Draft.KeepProfile && FindProfile(content) is { } projected &&
                !SameProfile(projected, Draft.Profile))
            {
                Draft.Profile = projected;
                profileUpdated = true;
            }
            // 用户尚未改动账号时跟随配置投影预填；账号只用于展示，密码永不回读。
            if (Draft.KeepUsername && content.Username.Length > 0 && Draft.Username != content.Username)
            {
                Draft.Username = content.Username;
                usernameUpdated = true;
            }
        }
        if (!Dirty && content.ConfigProjected && content.ProjectionKey != appliedKey)
        {
            ApplyProjection(content);
            ProjectionApplied?.Invoke();
        }
        else if (profileUpdated || usernameUpdated) ProjectionApplied?.Invoke();

        StateChanged?.Invoke();
    }

    /// <summary>用户明确选择 Wi-Fi：清除“保留原值”，保存按选择写入；未选择时由保存端保留当前守护配置。</summary>
    public void SelectProfile(WifiProfile? profile)
    {
        if (!Draft.KeepProfile && SameProfile(profile, Draft.Profile)) return;
        Draft.Profile = profile;
        Draft.KeepProfile = false;
        MarkDirty();
        StateChanged?.Invoke();
    }

    /// <summary>用户明确改动账号：清除“保留账号”，保存按用户输入处理；密码仍须单独输入。</summary>
    public void SetUsername(string value)
    {
        if (Draft.Username == value) return;
        Draft.Username = value;
        Draft.KeepUsername = false;
        MarkDirty();
        StateChanged?.Invoke();
    }

    /// <summary>用户改动了任意字段；此后状态轮询不再覆盖草稿，直到保存成功或放弃修改。</summary>
    public void MarkDirty()
    {
        Dirty = true;
        Interlocked.Increment(ref draftVersion);
    }

    /// <summary>显式清空地址；即使没有配置投影，也不会被误认为保留原值。</summary>
    public void ClearProxyUrl()
    {
        Draft.ProxyUrl = "";
        Draft.KeepProxyUrl = false;
        MarkDirty();
        ProjectionApplied?.Invoke();
        StateChanged?.Invoke();
    }

    public void ClearUuEndpoint()
    {
        Draft.UuEndpoint = "";
        Draft.KeepUuEndpoint = false;
        MarkDirty();
        ProjectionApplied?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 用户明确改动恢复选项：清除“保留原值”标记，保存按用户选择写入。
    /// 无投影时这些开关对页面是未知值，任何明确操作都必须覆盖已保存值，而不是被当作“未编辑”。
    /// </summary>
    public void SetEnabled(bool value)
    {
        if (!Draft.KeepEnabled && Draft.Enabled == value) return;
        Draft.Enabled = value;
        Draft.KeepEnabled = false;
        MarkDirty();
        StateChanged?.Invoke();
    }

    public void SetAllowActiveDisconnect(bool value)
    {
        if (!Draft.KeepAllowActiveDisconnect && Draft.AllowActiveDisconnect == value) return;
        Draft.AllowActiveDisconnect = value;
        Draft.KeepAllowActiveDisconnect = false;
        MarkDirty();
        StateChanged?.Invoke();
    }

    public void SetKeepAwakeOnAc(bool value)
    {
        if (!Draft.KeepAwakeOnAcKept && Draft.KeepAwakeOnAc == value) return;
        Draft.KeepAwakeOnAc = value;
        Draft.KeepAwakeOnAcKept = false;
        MarkDirty();
        StateChanged?.Invoke();
    }

    /// <summary>用户明确选择认证通道模式；未知值忽略。</summary>
    public void SetAuthChannelMode(string mode)
    {
        if (!AuthChannelModes.IsKnown(mode)) return;
        if (!Draft.KeepAuthChannelMode && Draft.AuthChannelMode == mode) return;
        Draft.AuthChannelMode = mode;
        Draft.KeepAuthChannelMode = false;
        MarkDirty();
        StateChanged?.Invoke();
    }

    /// <summary>放弃修改：清空密码与账号，恢复最近一次配置投影（没有投影时恢复默认值），零写入。</summary>
    public void Discard()
    {
        Draft.Password = "";
        Draft.Username = "";
        Dirty = false;
        Message = "";
        MessageIsError = false;
        MessageIsSuccess = false;
        Interlocked.Increment(ref draftVersion);
        ResetDraft();
        PasswordCleared?.Invoke();
        ProjectionApplied?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>QA 接缝：以给定草稿驱动与用户保存相同的页面路径，并让真实控件显示该草稿。</summary>
    internal void ApplyForQa(SettingsDraft draft)
    {
        Draft.CopyFrom(draft);
        Dirty = true;
        Interlocked.Increment(ref draftVersion);
        ProjectionApplied?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 保存一次：先在普通进程做不依赖已保存凭据的校验，再交给独立提权交接。
    /// 并发调用只允许一次交接；密码在交接结束后立即清除。
    /// </summary>
    public async Task<SettingsPageSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref savingFlag, 1, 0) != 0)
            return new(SettingsPageSaveState.Busy, BusyMessage);

        var draft = Draft.ToDraft();
        var version = Volatile.Read(ref draftVersion);
        // 保存期间状态轮询可能替换 Content；保留提交时的投影事实，供保存后判断哪些值对页面仍未知。
        var projectedBeforeSave = Content.ConfigProjected;
        var proxyRedactedBeforeSave = Content.ProxyUrlRedacted;
        var uuRedactedBeforeSave = Content.UuEndpointRedacted;
        var local = ValidateLocally(draft);
        if (local is not null)
        {
            // 本地校验失败同样结束本次提交：密码不留在普通进程内存里等待下一次交接。
            ClearSubmittedPassword(draft);
            Volatile.Write(ref savingFlag, 0);
            SetMessage(local.Message, error: true);
            return local;
        }

        SetMessage(SavingMessage, error: false);
        HandoffResult result;
        try
        {
            result = await handoff(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new(HandoffOutcome.Cancelled);
        }
        catch (Exception)
        {
            // 交接异常不能把异常文本带进界面或日志；结果按未知处理，不宣称零写入。
            result = new(HandoffOutcome.OutcomeUnknown);
        }
        finally
        {
            ClearSubmittedPassword(draft);
            Volatile.Write(ref savingFlag, 0);
        }

        var editedDuringSave = Volatile.Read(ref draftVersion) != version;
        var mapped = Map(result);
        if (mapped.Saved)
        {
            hasSeenSettings = true;
            Draft.AllowFirstTimeSetup = false;
        }
        if (mapped.Saved && !editedDuringSave)
        {
            Dirty = false;
            lastSavedAt = DateTimeOffset.UtcNow;
            // 只有“页面从未显示过、由接收端保留”的值才算未知；完整显示的地址即使带保留标记，
            // 页面也知道它的值，不能因此把配置投影误标为不可用。
            var hasKeptFields =
                (draft.KeepProfile && (draft.Profile is null || !projectedBeforeSave)) ||
                (draft.KeepProxyUrl && (!projectedBeforeSave || proxyRedactedBeforeSave)) ||
                (draft.KeepUuEndpoint && (!projectedBeforeSave || uuRedactedBeforeSave)) ||
                (draft.KeepEnabled && !projectedBeforeSave) ||
                (draft.KeepAllowActiveDisconnect && !projectedBeforeSave) ||
                (draft.KeepAwakeOnAcKept && !projectedBeforeSave) ||
                (draft.KeepAuthChannelMode && !projectedBeforeSave);
            if (!hasKeptFields)
            {
                Content = WithDraft(Content, draft, lastSavedAt);
                appliedKey = Content.ProjectionKey;
            }
            else
            {
                // 接收端读取的原值可能已在投影之后变化；等待新快照再确认，不猜测保存值。
                Content = Content with { ConfigProjected = false };
                appliedKey = "";
            }
        }

        if (mapped.Saved && editedDuringSave)
        {
            // 提交的草稿确实已保存，但保存期间产生了新修改：不能把它们标成已保存。
            Dirty = true;
            SetMessage(SavedWithPendingEditsMessage, error: false, success: true);
        }
        else
        {
            SetMessage(mapped.Message, error: !mapped.Saved, success: mapped.Saved);
        }

        if (mapped.Saved && refresh is not null) _ = refresh();
        return mapped;
    }

    /// <summary>只清除本次提交的密码；保存期间若用户已输入新密码，保留新输入。</summary>
    private void ClearSubmittedPassword(SettingsDraft draft)
    {
        if (Draft.Password != draft.Password) return;
        Draft.Password = "";
        PasswordCleared?.Invoke();
    }

    private void SetMessage(string message, bool error, bool success = false)
    {
        Message = message;
        MessageIsError = error;
        MessageIsSuccess = success;
        StateChanged?.Invoke();
    }

    private WifiProfile? FindProfile(SettingsContent content) => Profiles.FirstOrDefault(profile =>
        profile.InterfaceId == content.InterfaceId && profile.Name == content.WifiProfile);

    private static bool SameProfile(WifiProfile? left, WifiProfile? right) =>
        left is null ? right is null
            : right is not null && left.InterfaceId == right.InterfaceId && left.Name == right.Name;

    private void ResetDraft()
    {
        Draft.Profile = null;
        Draft.KeepProfile = true;
        Draft.Username = "";
        Draft.KeepUsername = true;
        Draft.ProxyUrl = "";
        Draft.KeepProxyUrl = true;
        Draft.UuEndpoint = "";
        Draft.KeepUuEndpoint = true;
        Draft.AllowFirstTimeSetup = !hasSeenSettings;
        Draft.Enabled = true;
        Draft.KeepEnabled = true;
        Draft.AllowActiveDisconnect = true;
        Draft.KeepAllowActiveDisconnect = true;
        Draft.KeepAwakeOnAc = true;
        Draft.KeepAwakeOnAcKept = true;
        Draft.AuthChannelMode = AuthChannelModes.ManualOnly;
        Draft.KeepAuthChannelMode = true;
        appliedKey = "";
        if (Content.ConfigProjected) ApplyProjection(Content);
    }

    private void ApplyProjection(SettingsContent content)
    {
        hasSeenSettings = true;
        Draft.AllowFirstTimeSetup = false;
        Draft.Profile = FindProfile(content);
        // 账号由配置投影预填、密码永不回读；未编辑账号时保存端仍以受限存储为准。
        // 投影暂时没有账号（例如凭据读取失败）时保留现有预填值，不把界面刷成空白。
        if (content.Username.Length > 0) Draft.Username = content.Username;
        Draft.KeepUsername = true;
        Draft.ProxyUrl = content.ProxyUrlDisplay;
        Draft.KeepProxyUrl = true;
        Draft.UuEndpoint = content.UuEndpointDisplay;
        Draft.KeepUuEndpoint = true;
        Draft.Enabled = content.Enabled;
        Draft.KeepEnabled = true;
        Draft.AllowActiveDisconnect = content.AllowActiveDisconnect;
        Draft.KeepAllowActiveDisconnect = true;
        Draft.KeepAwakeOnAc = content.KeepAwakeOnAc;
        Draft.KeepAwakeOnAcKept = true;
        Draft.AuthChannelMode = content.AuthChannelMode;
        Draft.KeepAuthChannelMode = true;
        appliedKey = content.ProjectionKey;
    }

    private static SettingsPageSaveResult? ValidateLocally(SettingsDraft draft)
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

    private static SettingsPageSaveResult Map(HandoffResult result) => result.Outcome switch
    {
        HandoffOutcome.Completed => result.SaveOutcome switch
        {
            SettingsSaveOutcome.Saved => new(SettingsPageSaveState.Saved, ElevatedSettings.SavedMessage),
            SettingsSaveOutcome.MissingProfile => new(SettingsPageSaveState.ValidationFailed, ElevatedSettings.MissingProfileMessage),
            SettingsSaveOutcome.PasswordRequired => new(SettingsPageSaveState.ValidationFailed, ElevatedSettings.PasswordRequiredMessage),
            SettingsSaveOutcome.InvalidSettings => new(SettingsPageSaveState.ValidationFailed, InvalidSettingsMessage),
            SettingsSaveOutcome.StoreFailed => new(SettingsPageSaveState.StoreFailed, StoreFailedMessage),
            SettingsSaveOutcome.SettingsUnreadable => new(SettingsPageSaveState.StoreFailed, SettingsUnreadableMessage),
            SettingsSaveOutcome.SaveBusy => new(SettingsPageSaveState.StoreFailed, SaveBusyMessage),
            // 零写入的并发拒绝必须与“可能已写入”区分，不能共用结果未知的措辞。
            SettingsSaveOutcome.SettingsChanged => new(SettingsPageSaveState.SettingsChanged, SettingsChangedMessage),
            _ => new(SettingsPageSaveState.OutcomeUnknown, OutcomeUnknownMessage)
        },
        HandoffOutcome.Cancelled => new(SettingsPageSaveState.Cancelled, CancelledMessage),
        HandoffOutcome.TimedOut => new(SettingsPageSaveState.TimedOut, TimedOutMessage),
        HandoffOutcome.Rejected => new(SettingsPageSaveState.Rejected, RejectedMessage),
        _ => new(SettingsPageSaveState.OutcomeUnknown, OutcomeUnknownMessage)
    };

    private static SettingsContent WithDraft(SettingsContent content, SettingsDraft draft, DateTimeOffset at)
    {
        var updated = content with
        {
            ConfigProjected = true,
            ProjectedAt = at,
            Username = draft.Username,
            InterfaceId = draft.Profile?.InterfaceId ?? "",
            WifiProfile = draft.Profile?.Name ?? "",
            ProxyUrlDisplay = ConfigProjection.Display(draft.ProxyUrl),
            // 页面刚保存的是完整已知值：只有该值本身仍含隐藏部分时才需要提示，不再沿用“保留”标记。
            ProxyUrlRedacted = ConfigProjection.HasHiddenParts(draft.ProxyUrl),
            UuEndpointDisplay = ConfigProjection.Display(draft.UuEndpoint),
            UuEndpointRedacted = ConfigProjection.HasHiddenParts(draft.UuEndpoint),
            Enabled = draft.Enabled,
            AllowActiveDisconnect = draft.AllowActiveDisconnect,
            KeepAwakeOnAc = draft.KeepAwakeOnAc,
            AuthChannelMode = draft.AuthChannelMode
        };
        return updated with { ProjectionKey = SettingsModel.ConfigFingerprint(updated) };
    }
}
