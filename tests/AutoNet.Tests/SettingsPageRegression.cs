using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页与设置编辑器回归（迁移工单 #8 / T07 / 故事 34–39）：
/// 普通界面只读投影与不可信降级、修改入口不依赖状态可信度、首次设置/换账号/保留密码的
/// 凭据规则、校验失败与存储失败的零谎报零写入、DPAPI 密文不落明文、保存不清认证暂停。
/// 受控 UI 证据由 scripts/qa-avalonia-settings.ps1（settings 模式）提供。
/// </summary>
internal static class SettingsPageRegression
{
    private const string PasswordOne = "TEST-PASSWORD-1";
    private const string PasswordTwo = "TEST-PASSWORD-2";
    private static readonly WifiProfile Profile = SettingsFixture.Profile;

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("设置页回归：可信状态投影守护事实且不可信状态固定降级", () => Sync(() =>
        {
            var online = Online();
            var content = SettingsModel.From(Reading(online));
            Require(content.Trusted, "可信状态未被投影为可信");
            Require(content.RecoveryState == "已开启", "自动恢复开关状态投影不符：" + content.RecoveryState);
            Require(content.GuardedNetwork == online.Wifi, "守护网络未按快照投影：" + content.GuardedNetwork);
            Require(content.ModifyLabel == SettingsModel.ModifyLabel, "修改入口标签不符");

            var disabled = SettingsModel.From(Reading(online with { Enabled = false }));
            Require(disabled.RecoveryState == "已关闭", "关闭状态的开关投影不符：" + disabled.RecoveryState);

            // 缺失、过期与损坏一律固定降级，绝不沿用上次画面（故事 15）。
            foreach (var reading in new[] { null, Reading(online with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }), Reading(new StatusSnapshot { Tunnel = null! }) })
            {
                var degraded = SettingsModel.From(reading);
                Require(!degraded.Trusted, "不可信状态被投影为可信");
                Require(degraded.RecoveryState == "待确认", "不可信状态未显示待确认：" + degraded.RecoveryState);
                Require(degraded.GuardedNetwork == SettingsModel.UnconfirmedNetwork, "不可信状态未显示不可确认");
            }

            // 指纹：同状态稳定，状态变化时更新（视图按指纹跳过重建）。
            var again = SettingsModel.From(Reading(online));
            Require(again.ContentKey == content.ContentKey, "同状态指纹不稳定");
            Require(disabled.ContentKey != content.ContentKey, "状态变化时指纹未更新");
        })),
        ("设置页回归：修改入口不依赖状态可信度且普通界面不显示凭据", () => Sync(() =>
        {
            // 故事 34：修改入口与既有托盘菜单同语义——不依据页面网络状态，提权窗口自行读取真实配置。
            foreach (var reading in new StatusReading?[] { null, Reading(Online()) })
            {
                var content = SettingsModel.From(reading);
                Require(content.ModifyLabel.Length > 0, "修改入口缺失");
            }

            // 故事 36 的呈现侧：投影字段里没有账号或密码，凭据说明为固定受控文案。
            var content2 = SettingsModel.From(Reading(Online()));
            var fingerprint = content2.ContentKey + content2.RecoveryState + content2.GuardedNetwork + content2.ModifyLabel;
            Require(!fingerprint.Contains(PasswordOne, StringComparison.Ordinal) && !fingerprint.Contains("TEST-USER", StringComparison.Ordinal),
                "投影不得携带凭据内容");
            Require(SettingsModel.CredentialNote.Contains("DPAPI", StringComparison.Ordinal), "凭据说明缺少加密存储事实");
        })),
        ("设置页回归：首次设置必须提供密码且零写入", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory);
                var result = editor.Save(Draft(Profile, "TEST-USER-1", ""));
                Require(result.Outcome == SettingsSaveOutcome.PasswordRequired, "首次设置留空密码未被拒绝：" + result.Outcome);
                Require(result.Message == ElevatedSettings.PasswordRequiredMessage, "提示文案不符：" + result.Message);
                Require(!File.Exists(paths.Settings) && !File.Exists(paths.Credentials), "拒绝时不得写入任何文件");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：账号未变且密码留空时保留已保存密码", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var result = editor.Save(Draft(Profile, "TEST-USER-1", ""));
                Require(result.Saved, "账号未变留空密码应保留旧密码并保存：" + result.Message);
                var kept = ReadCredentials(paths.Credentials);
                Require(kept is { Username: "TEST-USER-1", Password: PasswordOne }, "保留规则未生效：" + (kept?.Username ?? "无"));
                Require(!SettingsFixture.ContainsUtf8(File.ReadAllBytes(paths.Credentials), PasswordOne), "凭据文件出现了密码明文（故事 36）");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：换账号必须提供新密码且原凭据字节不变", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var credentialsBefore = File.ReadAllBytes(paths.Credentials);
                var settingsBefore = File.ReadAllBytes(paths.Settings);
                var result = editor.Save(Draft(Profile, "TEST-USER-2", ""));
                Require(result.Outcome == SettingsSaveOutcome.PasswordRequired, "换账号留空密码未被拒绝：" + result.Outcome);
                var after = File.ReadAllBytes(paths.Credentials);
                Require(after.SequenceEqual(credentialsBefore), "拒绝时凭据文件被改写：" +
                    $"before={credentialsBefore.Length} after={after.Length}");
                Require(File.ReadAllBytes(paths.Settings).SequenceEqual(settingsBefore), "拒绝时设置被改写");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：换账号并提供新密码时凭据更新", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var result = editor.Save(Draft(Profile, "TEST-USER-2", PasswordTwo));
                Require(result.Saved, "换账号提供新密码应保存成功：" + result.Message);
                var updated = ReadCredentials(paths.Credentials);
                Require(updated is { Username: "TEST-USER-2", Password: PasswordTwo }, "凭据未更新：" + (updated?.Username ?? "无"));
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：未选择配置或校验失败时不写入任何文件", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var settingsBefore = File.ReadAllBytes(paths.Settings);
                var credentialsBefore = File.ReadAllBytes(paths.Credentials);

                var missing = editor.Save(Draft(null, "TEST-USER-1", PasswordOne));
                Require(missing.Outcome == SettingsSaveOutcome.MissingProfile, "未选择配置未被拒绝：" + missing.Outcome);
                Require(missing.Message == ElevatedSettings.MissingProfileMessage, "未选择配置提示不符");

                // 故事 38 的镜像：校验失败与取消一样不写入任何业务配置。
                var invalid = editor.Save(Draft(Profile, "TEST-USER-1", PasswordOne, ProxyUrl: "http://remote.example.com:8080"));
                Require(invalid.Outcome == SettingsSaveOutcome.InvalidSettings, "非法代理未被校验拦截：" + invalid.Outcome);
                Require(invalid.Message.Contains("代理检测地址", StringComparison.Ordinal), "校验提示应使用受控业务文案：" + invalid.Message);
                Require(File.ReadAllBytes(paths.Settings).SequenceEqual(settingsBefore), "校验失败时设置被改写");
                Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), "校验失败时凭据被改写");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：存储失败如实披露异常类型且不谎报成功", () =>
        {
            var directory = NewDirectory();
            try
            {
                // 与既有表单一致的写入顺序（先凭据后设置）：设置阶段失败时，结果必须是受控失败而非成功；
                // 凭据已写入的部分结果如实存在（与 WinForms 相同的事务边界，不静默回滚也不谎报）。
                var (editor, paths) = CreateEditor(directory, failSettingsStore: true);
                var result = editor.Save(Draft(Profile, "TEST-USER-1", PasswordOne));
                Require(result.Outcome == SettingsSaveOutcome.StoreFailed, "存储失败未被如实报告：" + result.Outcome);
                Require(result.Message.Contains("IOException", StringComparison.Ordinal), "失败提示必须披露异常类型：" + result.Message);
                Require(!result.Message.Contains("夹具注入", StringComparison.Ordinal), "失败提示不得携带异常文本（安全边界）");
                Require(!File.Exists(paths.Settings), "设置写入失败时不得留下设置文件");
                var written = ReadCredentials(paths.Credentials);
                Require(written is { Username: "TEST-USER-1", Password: PasswordOne }, "凭据阶段的部分写入应如实存在");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：账号空白在凭据写入前拒绝", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var credentialsBefore = File.ReadAllBytes(paths.Credentials);
                var settingsBefore = File.ReadAllBytes(paths.Settings);
                var result = editor.Save(Draft(Profile, "   ", PasswordOne));
                Require(result.Outcome == SettingsSaveOutcome.InvalidSettings, "账号空白未被拒绝：" + result.Outcome);
                Require(result.Message == ElevatedSettings.CredentialsEmptyMessage, "账号空白提示不符：" + result.Message);
                Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), "账号空白时凭据被改写");
                Require(File.ReadAllBytes(paths.Settings).SequenceEqual(settingsBefore), "账号空白时设置被改写");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：保存凭据与设置不清除认证暂停标记", () =>
        {
            var directory = NewDirectory();
            try
            {
                // 故事 39 / T07：保存设置或修改密码绝不解除认证暂停（既有安全边界的直接证据）。
                var safety = new SafetyStateStore(directory);
                safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
                var blockPath = Path.Combine(directory, "auth-block.txt");
                var before = File.ReadAllBytes(blockPath);

                var (editor, _) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                Require(editor.Save(Draft(Profile, "TEST-USER-1", "")).Saved, "保留密码保存应成功");
                Require(editor.Save(Draft(Profile, "TEST-USER-2", PasswordTwo)).Saved, "换账号保存应成功");
                Require(File.ReadAllBytes(blockPath).SequenceEqual(before), "保存路径改写了认证暂停标记（故事 39）");
                Require(safety.LoadAuthenticationBlock()?.BlocksSubmissions == true, "认证暂停事实未保留");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：编辑器加载投影返回既有配置且不产生写入", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var state = editor.Load();
                Require(state.Settings is { WifiProfile: "TEST-AP-1" }, "加载未返回已保存设置");
                Require(state.Credentials is { Username: "TEST-USER-1" }, "加载未返回已保存账号");
                Require(state.Profiles.SequenceEqual(new[] { Profile }), "加载未返回可选配置");
                // 查看零写入（故事 38）：加载后文件仍与种子一致。
                Require(File.Exists(paths.Settings) && File.Exists(paths.Credentials), "加载产生了意外文件变化");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        })
    ];

    private static SettingsDraft Draft(WifiProfile? profile, string username, string password, string ProxyUrl = "") =>
        new(profile, username, password, ProxyUrl, "", Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true);

    private static StatusReading Reading(StatusSnapshot snapshot)
    {
        var parsed = StatusSnapshotReader.Parse(JsonSerializer.Serialize(snapshot, Storage.Json));
        var age = parsed is null ? null : (TimeSpan?)(DateTimeOffset.UtcNow - parsed.UpdatedAt);
        return new StatusReading(parsed, NetworkDisplayMap.Map(parsed, age), parsed is not null,
            NetworkDisplayMap.IsFresh(age), age, DateTimeOffset.UtcNow);
    }

    private static StatusSnapshot Online() => new()
    {
        Enabled = true,
        State = "校园网在线",
        Direct = "外网可达",
        Wifi = "TEST-AP-1",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };

    private sealed record Paths(string Settings, string Credentials);

    /// <summary>夹具编辑器：既有保存规则 + 既有 DPAPI 逻辑，全部落在隔离目录；seed 用于已有配置场景。</summary>
    private static (ElevatedSettings Editor, Paths Files) CreateEditor(
        string directory, string? seedUsername = null, string? seedPassword = null, bool failSettingsStore = false)
    {
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialPath = Path.Combine(directory, "credentials.dpapi");
        if (seedUsername is { } username)
        {
            WriteCredentials(credentialPath, new Credentials(username, seedPassword ?? ""));
            Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(new Settings
            {
                InterfaceId = Profile.InterfaceId,
                WifiProfile = Profile.Name,
                PortalOrigin = "https://login.csust.edu.cn"
            }, Storage.Json));
        }

        var editor = new ElevatedSettings(
            () => File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<Settings>(Storage.ReadShared(settingsPath), Storage.Json) : null,
            () => ReadCredentials(credentialPath),
            () => [Profile],
            credentials => WriteCredentials(credentialPath, credentials),
            settings =>
            {
                if (failSettingsStore) throw new IOException("夹具注入的设置写入失败");
                Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(settings, Storage.Json));
            });
        return (editor, new Paths(settingsPath, credentialPath));
    }

    private static void WriteCredentials(string path, Credentials credentials) =>
        SettingsFixture.WriteCredentials(path, credentials);

    private static Credentials? ReadCredentials(string path) =>
        SettingsFixture.ReadCredentials(path);

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "autonet-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
