using System.IO.Compression;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页与设置编辑器回归（迁移工单 #8 / #41）：
/// 普通界面的非秘密配置投影与不可信降级、投影不含账号密码；合并页的凭据规则
/// （首次设置/换账号必须密码、同账号留空保留旧密码）、取消/拒绝授权零写入、
/// 并发保存只发起一次交接、存储失败与结果未知的如实报告、密码清除与不外泄、
/// 保存不清认证暂停。受控 UI 证据由 scripts/qa-avalonia-settings.ps1 提供。
/// </summary>
internal static class SettingsPageRegression
{
    private const string PasswordOne = "TEST-PASSWORD-1";
    private const string PasswordTwo = "TEST-PASSWORD-2";
    private static readonly WifiProfile Profile = SettingsFixture.Profile;
    private static readonly WifiProfile ProfileB = new(
        "66666666-7777-8888-9999-AAAAAAAAAAAA", "TEST-ADAPTER", "TEST-AP-2", AllUsers: true, Connected: false);

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("设置页回归：保留隐藏地址时原设置消失必须零写入", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var credentialsBefore = File.ReadAllBytes(paths.Credentials);
                var draft = Draft(Profile, "TEST-USER-1", "", "http://127.0.0.1:7897") with { KeepProxyUrl = true };
                File.Delete(paths.Settings);
                var result = editor.Save(draft);
                Require(result.Outcome == SettingsSaveOutcome.SettingsUnreadable, "原设置消失时不能使用脱敏地址保存");
                Require(!File.Exists(paths.Settings), "原设置消失后创建了设置文件");
                Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), "原设置消失后改写了凭据");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：无可信投影时未编辑地址保留受限原值", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var initial = new Settings { InterfaceId = Profile.InterfaceId, WifiProfile = Profile.Name,
                    ProxyUrl = "http://127.0.0.1:7897/path?token=TEST-URL-SECRET", UuEndpoint = "https://uu.example.test/TEST-URL-SECRET" };
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(initial, Storage.Json));
                var page = CreatePage(editor);
                page.Update(SettingsModel.From(null));
                page.Draft.Profile = Profile;
                page.Draft.Username = "TEST-USER-1";
                page.MarkDirty();
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "保留原值的保存未成功");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(actual.ProxyUrl == initial.ProxyUrl && actual.UuEndpoint == initial.UuEndpoint, "未编辑地址被覆盖");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：无投影读取异常零写入且不把地址当成空值", () => Sync(() =>
        {
            var writes = 0;
            var editor = new ElevatedSettings(
                () => throw new IOException("synthetic"),
                () => new Credentials("TEST-USER-1", PasswordOne),
                () => [Profile], _ => writes++, _ => writes++);
            var page = CreatePage(editor);
            page.Draft.Profile = Profile;
            page.Draft.Username = "TEST-USER-1";
            page.MarkDirty();
            var result = page.SaveAsync().GetAwaiter().GetResult();
            Require(!result.Saved && result.Message.Contains("未写入", StringComparison.Ordinal), "读取故障未受控拒绝");
            Require(writes == 0, "读取故障仍写入凭据或设置");
        })),
        ("设置页回归：缺失配置与读取失败在真实文件读取路径中区分", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var path = Path.Combine(directory, "settings.json");
                Require(ElevatedSettings.ReadSettings(path) is null, "真正缺失的配置未识别为首次设置");
                File.WriteAllText(path, "{}");
                var writes = 0;
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var editor = new ElevatedSettings(() => ElevatedSettings.ReadSettings(path),
                    () => new Credentials("TEST-USER-1", PasswordOne), () => [Profile], _ => writes++, _ => writes++);
                var page = CreatePage(editor);
                page.Draft.Profile = Profile;
                page.Draft.Username = "TEST-USER-1";
                page.MarkDirty();
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(!result.Saved && result.Message.Contains("未写入", StringComparison.Ordinal) && writes == 0,
                    "读取被占用的文件被误当作首次配置或产生写入");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：投影过期后未编辑的地址保留最新受限原值", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var page = CreatePage(editor);
                page.Update(SettingsModel.From(Reading(Online())));
                page.Update(SettingsModel.From(null));
                var changed = new Settings { InterfaceId = Profile.InterfaceId, WifiProfile = Profile.Name,
                    ProxyUrl = "http://127.0.0.1:7897/path?token=TEST-URL-SECRET", UuEndpoint = "https://uu.example.test/TEST-URL-SECRET" };
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(changed, Storage.Json));
                page.Draft.Username = "TEST-USER-1";
                page.MarkDirty();
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "保留最新地址的保存失败");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(actual.ProxyUrl == changed.ProxyUrl && actual.UuEndpoint == changed.UuEndpoint, "旧投影覆盖了最新受限地址");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：曾取得投影后原设置消失不能当作首次设置", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var page = CreatePage(editor);
                var snapshot = Online() with { ProxyUrlRedacted = true };
                page.Update(SettingsModel.From(Reading(snapshot)));
                page.Update(SettingsModel.From(null));
                page.Draft.Username = "TEST-USER-1";
                page.MarkDirty();
                File.Delete(paths.Settings);
                var before = File.ReadAllBytes(paths.Credentials);
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(!result.Saved && result.Message.Contains("未写入", StringComparison.Ordinal), "已知的旧配置消失时不应按首次创建");
                Require(!File.Exists(paths.Settings) && File.ReadAllBytes(paths.Credentials).SequenceEqual(before), "配置消失后产生写入");

                // 用户先开始编辑，再收到可信投影：不能为保护草稿而忽略“已有设置”事实。
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(new Settings
                { InterfaceId = Profile.InterfaceId, WifiProfile = Profile.Name }, Storage.Json));
                var edited = CreatePage(editor);
                edited.Draft.Profile = Profile;
                edited.Draft.Username = "TEST-USER-1";
                edited.MarkDirty();
                edited.Update(SettingsModel.From(Reading(Online())));
                File.Delete(paths.Settings);
                Require(!edited.SaveAsync().GetAwaiter().GetResult().Saved && !File.Exists(paths.Settings),
                    "编辑期间收到已有配置后仍按首次创建");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：无投影首次设置可创建且明确清空地址生效", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory);
                var page = CreatePage(editor);
                page.Draft.Profile = Profile;
                page.Draft.Username = "TEST-USER-1";
                page.Draft.Password = PasswordOne;
                page.MarkDirty();
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "无现有设置的首次保存失败");
                var initial = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(initial.ProxyUrl == "" && initial.UuEndpoint == "", "首次空地址不符");
                Require(initial.Enabled && initial.AllowActiveDisconnect && initial.KeepAwakeOnAc &&
                    initial.AuthChannelMode == AuthChannelModes.ManualOnly, "首次设置的恢复选项未按默认值保存");

                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(initial with
                { ProxyUrl = "http://127.0.0.1:7897/path?token=TEST-URL-SECRET", UuEndpoint = "https://uu.example.test/TEST-URL-SECRET" }, Storage.Json));
                var second = CreatePage(editor);
                second.Draft.Profile = Profile;
                second.Draft.Username = "TEST-USER-1";
                second.ClearProxyUrl();
                second.Draft.UuEndpoint = "https://uu.example.test/new";
                second.Draft.KeepUuEndpoint = false;
                second.MarkDirty();
                Require(second.SaveAsync().GetAwaiter().GetResult().Saved, "明确编辑地址的保存失败");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(actual.ProxyUrl == "" && actual.UuEndpoint == "https://uu.example.test/new", "明确清空或编辑未生效");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：可信状态投影非秘密配置且不可信状态固定降级", () => Sync(() =>
        {
            var online = Online();
            var content = SettingsModel.From(Reading(online));
            Require(content.Trusted, "可信状态未被投影为可信");
            Require(content.RecoveryState == "已开启", "自动恢复开关状态投影不符：" + content.RecoveryState);
            Require(content.GuardedNetwork == online.Wifi, "守护网络未按快照投影：" + content.GuardedNetwork);
            Require(content.ConfigProjected, "后台已投影配置但页面未识别");
            Require(content.WifiProfile == Profile.Name, "Wi-Fi 配置投影不符：" + content.WifiProfile);
            Require(content.ProxyUrlDisplay == online.ProxyUrlDisplay, "代理投影不符：" + content.ProxyUrlDisplay);
            Require(content.UuEndpointDisplay == online.UuEndpointDisplay, "UU 地址投影不符：" + content.UuEndpointDisplay);
            Require(!content.ProxyUrlRedacted && !content.UuEndpointRedacted, "安全地址不应标记为已隐藏");
            Require(content.AllowActiveDisconnect && content.KeepAwakeOnAc, "电源/断线投影不符");
            Require(content.AuthChannelMode == AuthChannelModes.ManualOnly, "通道模式投影不符：" + content.AuthChannelMode);

            var disabled = SettingsModel.From(Reading(online with { Enabled = false }));
            Require(disabled.RecoveryState == "已关闭", "关闭状态的开关投影不符");
            Require(!disabled.Enabled, "关闭状态未投影到可编辑字段");

            // 缺失、过期与损坏一律固定降级，绝不沿用上次画面（故事 15）。
            foreach (var reading in new[] { null, Reading(online with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }), Reading(new StatusSnapshot { Tunnel = null! }) })
            {
                var degraded = SettingsModel.From(reading);
                Require(!degraded.Trusted, "不可信状态被投影为可信");
                Require(degraded.RecoveryState == "待确认", "不可信状态未显示待确认：" + degraded.RecoveryState);
                Require(degraded.GuardedNetwork == SettingsModel.UnconfirmedNetwork, "不可信状态未显示不可确认");
                Require(!degraded.ConfigProjected, "不可信状态仍宣称配置投影可用");
                Require(degraded.WifiProfile.Length == 0 && degraded.ProxyUrlDisplay.Length == 0, "不可信状态泄露了配置投影");
            }

            // 旧版服务不提供配置投影：字段默认值不能被当成已保存配置。
            var legacy = SettingsModel.From(Reading(online with { SettingsProjected = false }));
            Require(legacy.Trusted && !legacy.ConfigProjected, "旧版服务状态被误报为配置投影可用");
            Require(legacy.WifiProfile.Length == 0 && legacy.ProxyUrlDisplay.Length == 0, "旧版服务状态填充了未提供的配置");

            // 指纹：同状态稳定，状态变化时更新。
            var again = SettingsModel.From(Reading(online));
            Require(again.ContentKey == content.ContentKey, "同状态指纹不稳定");
            Require(disabled.ContentKey != content.ContentKey, "状态变化时指纹未更新");
        })),
        ("设置页回归：投影不含账号与密码", () => Sync(() =>
        {
            var content = SettingsModel.From(Reading(Online()));
            var text = content.ContentKey + content.ProjectionKey + content.RecoveryState + content.GuardedNetwork +
                content.WifiProfile + content.ProxyUrlDisplay + content.UuEndpointDisplay + content.AuthChannelMode;
            Require(!text.Contains(PasswordOne, StringComparison.Ordinal) && !text.Contains("TEST-USER", StringComparison.Ordinal),
                "投影不得携带凭据内容");
            Require(SettingsModel.CredentialNote.Contains("DPAPI", StringComparison.Ordinal), "凭据说明缺少加密存储事实");
            Require(SettingsModel.PasswordNote.Contains("遮挡", StringComparison.Ordinal), "密码说明缺少遮挡事实");
        })),
        ("设置页回归：首次设置必须提供密码且零写入", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory);
                var page = CreatePage(editor);
                page.ApplyForQa(Draft(Profile, "TEST-USER-1", ""));
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.State == SettingsPageSaveState.ValidationFailed, "首次设置留空密码未被拒绝：" + result.State);
                Require(result.Message == ElevatedSettings.PasswordRequiredMessage, "提示文案不符：" + result.Message);
                Require(!File.Exists(paths.Settings) && !File.Exists(paths.Credentials), "拒绝时不得写入任何文件");
                Require(page.Draft.Password.Length == 0, "交接结束后密码未从草稿清除");
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
                var page = CreatePage(editor);
                page.ApplyForQa(Draft(Profile, "TEST-USER-1", ""));
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.Saved, "账号未变留空密码应保留旧密码并保存：" + result.Message);
                var kept = ReadCredentials(paths.Credentials);
                Require(kept is { Username: "TEST-USER-1", Password: PasswordOne }, "保留规则未生效：" + (kept?.Username ?? "无"));
                Require(!SettingsFixture.ContainsUtf8(File.ReadAllBytes(paths.Credentials), PasswordOne), "凭据文件出现了密码明文（故事 55）");
                Require(page.Draft.Password.Length == 0, "成功后密码未从草稿清除");
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
                var page = CreatePage(editor);
                page.ApplyForQa(Draft(Profile, "TEST-USER-2", ""));
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.State == SettingsPageSaveState.ValidationFailed, "换账号留空密码未被拒绝：" + result.State);
                Require(result.Message == ElevatedSettings.PasswordRequiredMessage, "换账号提示不符：" + result.Message);
                Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), "拒绝时凭据文件被改写");
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
                var page = CreatePage(editor);
                page.ApplyForQa(Draft(Profile, "TEST-USER-2", PasswordTwo));
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.Saved, "换账号提供新密码应保存成功：" + result.Message);
                var updated = ReadCredentials(paths.Credentials);
                Require(updated is { Username: "TEST-USER-2", Password: PasswordTwo }, "凭据未更新：" + (updated?.Username ?? "无"));
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：未选择配置或校验失败时在交接前拒绝且零写入", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var settingsBefore = File.ReadAllBytes(paths.Settings);
                var credentialsBefore = File.ReadAllBytes(paths.Credentials);
                var calls = 0;
                var page = new SettingsPage(() => [Profile], (draft, _) =>
                {
                    calls++;
                    return Task.FromResult(new HandoffResult(HandoffOutcome.Completed, editor.Save(draft).Outcome));
                });

                page.ApplyForQa(Draft(null, "TEST-USER-1", PasswordOne));
                var missing = page.SaveAsync().GetAwaiter().GetResult();
                Require(missing.State == SettingsPageSaveState.ValidationFailed, "未选择配置未被拒绝：" + missing.State);
                Require(missing.Message == ElevatedSettings.MissingProfileMessage, "未选择配置提示不符");
                Require(calls == 0, "未选择配置仍发起了提权交接");

                // 故事 58 的镜像：校验失败与取消一样不写入任何业务配置。
                page.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordOne, ProxyUrl: "http://remote.example.com:8080"));
                var invalid = page.SaveAsync().GetAwaiter().GetResult();
                Require(invalid.State == SettingsPageSaveState.ValidationFailed, "非法代理未被校验拦截：" + invalid.State);
                Require(invalid.Message.Contains("代理检测地址", StringComparison.Ordinal), "校验提示应使用受控业务文案：" + invalid.Message);
                Require(calls == 0, "校验失败仍发起了提权交接");
                Require(File.ReadAllBytes(paths.Settings).SequenceEqual(settingsBefore), "校验失败时设置被改写");
                Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), "校验失败时凭据被改写");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：存储失败如实披露部分写入且不谎报成功", () =>
        {
            var directory = NewDirectory();
            try
            {
                // 与既有表单一致的写入顺序（先凭据后设置）：设置阶段失败时，结果必须是受控失败而非成功；
                // 凭据已写入的部分结果如实存在，页面明确说明可能只写入了一部分。
                var (editor, paths) = CreateEditor(directory, failSettingsStore: true);
                var page = CreatePage(editor);
                page.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordOne));
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.State == SettingsPageSaveState.StoreFailed, "存储失败未被如实报告：" + result.State);
                Require(result.Message.Contains("可能", StringComparison.Ordinal), "存储失败未说明可能的部分写入：" + result.Message);
                Require(!result.Message.Contains("未写入任何设置", StringComparison.Ordinal), "存储失败被误报为零写入");
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
                var calls = 0;
                var page = new SettingsPage(() => [Profile], (draft, _) =>
                {
                    calls++;
                    return Task.FromResult(new HandoffResult(HandoffOutcome.Completed, editor.Save(draft).Outcome));
                });
                page.ApplyForQa(Draft(Profile, "   ", PasswordOne));
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.State == SettingsPageSaveState.ValidationFailed, "账号空白未被拒绝：" + result.State);
                Require(result.Message == ElevatedSettings.CredentialsEmptyMessage, "账号空白提示不符：" + result.Message);
                Require(calls == 0, "账号空白仍发起了提权交接");
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
                // 故事 39 / #41：保存设置或修改密码绝不解除认证暂停（既有安全边界的直接证据）。
                var safety = new SafetyStateStore(directory);
                safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
                var blockPath = Path.Combine(directory, "auth-block.txt");
                var before = File.ReadAllBytes(blockPath);

                var (editor, _) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var page = CreatePage(editor);
                page.ApplyForQa(Draft(Profile, "TEST-USER-1", ""));
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "保留密码保存应成功");
                page.ApplyForQa(Draft(Profile, "TEST-USER-2", PasswordTwo));
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "换账号保存应成功");
                Require(File.ReadAllBytes(blockPath).SequenceEqual(before), "保存路径改写了认证暂停标记（故事 39）");
                Require(safety.LoadAuthenticationBlock()?.BlocksSubmissions == true, "认证暂停事实未保留");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：取消或拒绝授权零写入且不宣称成功", () =>
        {
            foreach (var outcome in new[] { HandoffOutcome.Cancelled, HandoffOutcome.Rejected, HandoffOutcome.TimedOut })
            {
                var directory = NewDirectory();
                try
                {
                    var (_, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                    var credentialsBefore = File.ReadAllBytes(paths.Credentials);
                    var settingsBefore = File.ReadAllBytes(paths.Settings);
                    var page = new SettingsPage(() => [Profile], (_, _) => Task.FromResult(new HandoffResult(outcome)));
                    page.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordOne));
                    var result = page.SaveAsync().GetAwaiter().GetResult();
                    Require(!result.Saved, outcome + " 被误报为成功");
                    Require(result.Message.Contains("未写入任何设置", StringComparison.Ordinal), outcome + " 未说明零写入：" + result.Message);
                    Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), outcome + " 改写了凭据");
                    Require(File.ReadAllBytes(paths.Settings).SequenceEqual(settingsBefore), outcome + " 改写了设置");
                    Require(page.Draft.Password.Length == 0, outcome + " 后密码未清除");
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
            return Task.CompletedTask;
        }),
        ("设置页回归：并发保存只发起一次交接", () =>
        {
            var gate = new TaskCompletionSource<HandoffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var page = new SettingsPage(() => [Profile], (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return gate.Task;
            });
            page.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordOne));
            var first = page.SaveAsync();
            var second = page.SaveAsync().GetAwaiter().GetResult();
            Require(second.State == SettingsPageSaveState.Busy, "并发保存未被拒绝：" + second.State);
            Require(calls == 1, "并发保存发起了多次交接：" + calls);
            gate.SetResult(new HandoffResult(HandoffOutcome.Completed, SettingsSaveOutcome.Saved));
            var completed = first.GetAwaiter().GetResult();
            Require(completed.Saved, "首个保存未完成：" + completed.State);
            Require(calls == 1, "首个保存完成后重复发起交接");
            Require(page.Draft.Password.Length == 0, "保存结束后密码未清除");
            return Task.CompletedTask;
        }),
        ("设置页回归：保存成功后清除密码且结果不含密码", () =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var page = CreatePage(editor);
                page.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordTwo));
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.Saved, "保存应成功：" + result.Message);
                Require(page.Draft.Password.Length == 0, "成功后密码未从草稿清除");
                Require(!result.Message.Contains(PasswordTwo, StringComparison.Ordinal) &&
                    !page.Message.Contains(PasswordTwo, StringComparison.Ordinal), "结果文本泄漏密码");
                var files = Directory.EnumerateFiles(directory).ToArray();
                Require(files.All(file => file.EndsWith("credentials.dpapi", StringComparison.OrdinalIgnoreCase) ||
                    !SettingsFixture.ContainsUtf8(File.ReadAllBytes(file), PasswordTwo)), "隔离目录出现密码明文");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：结果未知不宣称零写入", () =>
        {
            var page = new SettingsPage(() => [Profile], (_, _) =>
                Task.FromResult(new HandoffResult(HandoffOutcome.OutcomeUnknown)));
            page.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordOne));
            var result = page.SaveAsync().GetAwaiter().GetResult();
            Require(result.State == SettingsPageSaveState.OutcomeUnknown, "回执丢失未按结果未知处理：" + result.State);
            Require(result.Message.Contains("结果未知", StringComparison.Ordinal), "结果未知文案不符：" + result.Message);
            Require(!result.Message.Contains("未写入任何设置", StringComparison.Ordinal), "结果未知被误报为零写入");
            Require(page.Draft.Password.Length == 0, "结果未知后密码未清除");
            return Task.CompletedTask;
        }),
        ("设置页回归：状态轮询不覆盖已修改的草稿", () =>
        {
            var page = new SettingsPage(() => [Profile], (_, _) =>
                Task.FromResult(new HandoffResult(HandoffOutcome.Rejected)));
            page.Update(SettingsModel.From(Reading(Online())));
            Require(page.Draft.ProxyUrl == Online().ProxyUrlDisplay, "初始投影未应用：" + page.Draft.ProxyUrl);
            page.Draft.ProxyUrl = "http://127.0.0.1:9999";
            page.Draft.Username = "TEST-USER-1";
            page.MarkDirty();
            page.Update(SettingsModel.From(Reading(Online() with { ProxyUrlDisplay = "http://127.0.0.1:8888" })));
            Require(page.Draft.ProxyUrl == "http://127.0.0.1:9999", "状态轮询覆盖了已修改的草稿");
            Require(page.Draft.Username == "TEST-USER-1", "状态轮询覆盖了账号输入");
            return Task.CompletedTask;
        }),
        ("设置页回归：放弃修改清除密码并恢复投影", () =>
        {
            var page = new SettingsPage(() => [Profile], (_, _) =>
                Task.FromResult(new HandoffResult(HandoffOutcome.Rejected)));
            var content = SettingsModel.From(Reading(Online()));
            page.Update(content);
            page.Draft.Password = PasswordOne;
            page.Draft.Username = "TEST-USER-1";
            page.Draft.ProxyUrl = "http://127.0.0.1:9999";
            page.MarkDirty();
            page.Discard();
            Require(page.Draft.Password.Length == 0, "放弃修改未清除密码");
            Require(page.Draft.Username.Length == 0, "放弃修改未清除账号");
            Require(page.Draft.ProxyUrl == content.ProxyUrlDisplay, "放弃修改未恢复配置投影：" + page.Draft.ProxyUrl);
            Require(!page.Dirty, "放弃修改后仍标记为已修改");
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
        }),
        ("设置页回归：QA 互斥后缀隔离旧预览安装与并行工作树", () => Sync(() =>
        {
            var previousDirectory = Environment.GetEnvironmentVariable("AUTONET_QA_DIR");
            var previousSuffix = Environment.GetEnvironmentVariable("AUTONET_QA_MUTEX_SUFFIX");
            try
            {
                Environment.SetEnvironmentVariable("AUTONET_QA_DIR", @"C:\虚构\qa");
                Environment.SetEnvironmentVariable("AUTONET_QA_MUTEX_SUFFIX", null);
                Require(AutoNet.Tray.Avalonia.Program.ResolveMutexName(QaEnvironment.FromProcess()) == @"Local\AutoNet.Tray.Avalonia",
                    "未设置后缀时互斥名应保持既有固定名");
                Environment.SetEnvironmentVariable("AUTONET_QA_MUTEX_SUFFIX", "settings-abc123");
                Require(AutoNet.Tray.Avalonia.Program.ResolveMutexName(QaEnvironment.FromProcess()) == @"Local\AutoNet.Tray.Avalonia.settings-abc123",
                    "设置后缀后未取得独立互斥身份");
                Environment.SetEnvironmentVariable("AUTONET_QA_MUTEX_SUFFIX", @"bad\name");
                Require(AutoNet.Tray.Avalonia.Program.ResolveMutexName(QaEnvironment.FromProcess()) == @"Local\AutoNet.Tray.Avalonia",
                    "非法后缀未被忽略");
                Environment.SetEnvironmentVariable("AUTONET_QA_DIR", null);
                Require(AutoNet.Tray.Avalonia.Program.ResolveMutexName(QaEnvironment.FromProcess()) == @"Local\AutoNet.Tray",
                    "非 QA 上下文不应使用 QA 互斥名");
            }
            finally
            {
                Environment.SetEnvironmentVariable("AUTONET_QA_DIR", previousDirectory);
                Environment.SetEnvironmentVariable("AUTONET_QA_MUTEX_SUFFIX", previousSuffix);
            }
        })),
        ("设置页回归：点段与编码路径不得绕过地址脱敏", () => Sync(() =>
        {
            const string secret = "TEST-URL-SECRET";
            // Uri 会把点段规范化掉：只看规范化结果会把 "…/秘密/../" 当成根路径并原样公开。
            string[] hidden =
            [
                "http://127.0.0.1:7897/" + secret + "/../",
                "http://127.0.0.1:7897/" + secret + "/..",
                "http://127.0.0.1:7897/./" + secret + "/..",
                "http://127.0.0.1:7897/%2E%2E/" + secret,
                "http://127.0.0.1:7897/%54%45%53%54-URL-SECRET",
                "http://127.0.0.1:7897/?" + secret + "=1",
                "http://127.0.0.1:7897/?" + secret,
                "http://127.0.0.1:7897/#" + secret,
                "http://127.0.0.1:7897/?" + secret + "=1#" + secret,
                "http://127.0.0.1:7897\\" + secret,
                "http://127.0.0.1:7897/" + secret + "?q=1#f"
            ];
            foreach (var url in hidden)
            {
                var display = ConfigProjection.Display(url);
                Require(!display.Contains(secret, StringComparison.Ordinal), "展示值泄露原始秘密：" + url + " → " + display);
                Require(ConfigProjection.HasHiddenParts(url), "未识别隐藏部分：" + url);
            }
            // userinfo 与无法可靠切分的输入一律不投影任何内容。
            Require(ConfigProjection.Display("http://user:" + secret + "@127.0.0.1:7897/") == "", "userinfo 地址不应投影");
            Require(ConfigProjection.HasHiddenParts("http://user:" + secret + "@127.0.0.1:7897/"), "userinfo 地址应视为隐藏");
            Require(ConfigProjection.Display("127.0.0.1:7897/" + secret) == "", "相对地址不应投影");
            // 正常地址仍原样投影，字节不变。
            foreach (var url in new[] { "http://127.0.0.1:7897", "https://uu.example.test/", "https://uu.example.test", "http://[::1]:8080/" })
            {
                Require(ConfigProjection.Display(url) == url, "安全地址被改写：" + url);
                Require(!ConfigProjection.HasHiddenParts(url), "安全地址被误判为隐藏：" + url);
            }
        })),
        ("设置页回归：点段地址的秘密不进入公开状态与诊断包", async () =>
        {
            const string secret = "TEST-URL-SECRET";
            var directory = NewDirectory();
            try
            {
                var fixture = new MonitorFixture();
                fixture.Settings = fixture.Settings with
                {
                    ProxyUrl = "http://127.0.0.1:7897/" + secret + "/../",
                    UuEndpoint = "https://uu.example.test/" + secret + "/../"
                };
                using var worker = new MonitorWorker(fixture, fixture.Clock);
                var status = await worker.RunOnceAsync(default);
                Require(status.SettingsProjected, "读取成功应投影配置");
                Require(status.ProxyUrlDisplay == "http://127.0.0.1:7897" && status.ProxyUrlRedacted,
                    "点段代理地址未被隐藏：" + status.ProxyUrlDisplay);
                Require(status.UuEndpointDisplay == "https://uu.example.test" && status.UuEndpointRedacted,
                    "点段 UU 地址未被隐藏：" + status.UuEndpointDisplay);
                var json = JsonSerializer.Serialize(status, Storage.Json);
                Require(!json.Contains(secret, StringComparison.Ordinal), "状态快照出现点段地址秘密");

                var statusPath = Path.Combine(directory, "status.json");
                File.WriteAllText(statusPath, json);
                var logs = Path.Combine(directory, "logs");
                Directory.CreateDirectory(logs);
                var zip = Path.Combine(directory, "diag.zip");
                await DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                {
                    OutputPath = zip,
                    StatusPath = statusPath,
                    LogDirectory = logs,
                    LoadSettings = () => null,
                    Profiles = () => [],
                    ExportedAt = DateTimeOffset.UtcNow
                });
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    Require(!reader.ReadToEnd().Contains(secret, StringComparison.Ordinal),
                        "诊断包条目泄露点段地址秘密：" + entry.FullName);
                }
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("设置页回归：点段地址未编辑时保留受限存储的完整原值", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var stored = new Settings
                {
                    InterfaceId = Profile.InterfaceId,
                    WifiProfile = Profile.Name,
                    ProxyUrl = "http://127.0.0.1:7897/TEST-URL-SECRET/../",
                    UuEndpoint = "https://uu.example.test/TEST-URL-SECRET/../"
                };
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(stored, Storage.Json));
                var page = CreatePage(editor);
                page.Update(SettingsModel.From(null));
                page.Draft.Profile = Profile;
                page.Draft.Username = "TEST-USER-1";
                page.MarkDirty();
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "保留点段原值的保存未成功");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(actual.ProxyUrl == stored.ProxyUrl && actual.UuEndpoint == stored.UuEndpoint,
                    "未编辑的点段地址被展示值覆盖");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：无可信投影时未编辑的恢复选项保留受限原值", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var stored = new Settings
                {
                    InterfaceId = Profile.InterfaceId,
                    WifiProfile = Profile.Name,
                    Enabled = false,
                    AllowActiveDisconnect = false,
                    KeepAwakeOnAc = false,
                    AuthChannelMode = AuthChannelModes.Off
                };
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(stored, Storage.Json));
                var page = CreatePage(editor);
                page.Update(SettingsModel.From(null));
                Require(!page.Content.ConfigProjected, "无可信投影时不应宣称配置已投影");
                page.Draft.Profile = Profile;
                page.Draft.Username = "TEST-USER-1";
                page.MarkDirty();
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "保留恢复选项的保存未成功");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(!actual.Enabled, "未编辑的自动恢复开关被默认值重新开启");
                Require(!actual.AllowActiveDisconnect, "未编辑的主动断线开关被默认值重新开启");
                Require(!actual.KeepAwakeOnAc, "未编辑的保持唤醒开关被默认值覆盖");
                Require(actual.AuthChannelMode == AuthChannelModes.Off, "未编辑的通道模式被默认值覆盖：" + actual.AuthChannelMode);
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：无投影时明确修改的恢复选项按用户选择保存", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(new Settings
                {
                    InterfaceId = Profile.InterfaceId,
                    WifiProfile = Profile.Name,
                    Enabled = false,
                    AllowActiveDisconnect = false,
                    KeepAwakeOnAc = false,
                    AuthChannelMode = AuthChannelModes.Off
                }, Storage.Json));
                var page = CreatePage(editor);
                page.Update(SettingsModel.From(null));
                page.Draft.Profile = Profile;
                page.Draft.Username = "TEST-USER-1";
                // 用户明确勾选：必须覆盖已保存的关闭状态，且不能连带改动未编辑的选项。
                page.SetEnabled(true);
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "明确修改选项的保存未成功");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(actual.Enabled, "用户明确开启的自动恢复未保存");
                Require(!actual.AllowActiveDisconnect && !actual.KeepAwakeOnAc && actual.AuthChannelMode == AuthChannelModes.Off,
                    "明确修改一个选项时改动了其他未编辑选项");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：选项未知状态与配置投影区分", () => Sync(() =>
        {
            // 未投影时未编辑的选项对页面未知；有投影或用户已修改时才是已知值。
            Require(!SettingsModel.OptionKnown(keep: true, configProjected: false), "无投影时未编辑的选项被当成已知值");
            Require(SettingsModel.OptionKnown(keep: true, configProjected: true), "有投影时选项未被视为已知");
            Require(SettingsModel.OptionKnown(keep: false, configProjected: false), "用户明确修改后选项仍被视为未知");
            Require(SettingsModel.OptionsUnknownNote.Contains("首次设置", StringComparison.Ordinal) &&
                SettingsModel.OptionsUnknownNote.Contains("保留原值", StringComparison.Ordinal),
                "选项未知说明未区分首次设置与保留原值");
        })),
        ("设置页回归：地址安全投影保留安全值并隐藏路径与查询秘密", () => Sync(() =>
        {
            const string secret = "TEST-URL-SECRET";
            Require(ConfigProjection.Display("http://127.0.0.1:7897") == "http://127.0.0.1:7897", "安全代理地址被改写");
            Require(!ConfigProjection.HasHiddenParts("http://127.0.0.1:7897"), "安全代理地址被误判为隐藏");
            Require(ConfigProjection.Display("https://uu.example.test/") == "https://uu.example.test/", "安全地址的尾斜杠被改写");
            Require(ConfigProjection.Display("http://127.0.0.1:7897/" + secret + "?token=" + secret) == "http://127.0.0.1:7897",
                "代理秘密未从展示值移除");
            Require(ConfigProjection.HasHiddenParts("http://127.0.0.1:7897/" + secret), "代理路径未识别为隐藏");
            Require(ConfigProjection.Display("https://uu.example.test/" + secret) == "https://uu.example.test",
                "UU 秘密未从展示值移除");
            Require(ConfigProjection.Display("") == "", "空地址应投影为空");
        })),
        ("设置页回归：地址中的秘密不进入公开状态", async () =>
        {
            const string secret = "TEST-URL-SECRET";
            var fixture = new MonitorFixture();
            fixture.Settings = fixture.Settings with
            {
                ProxyUrl = "http://127.0.0.1:7897/" + secret + "?token=" + secret,
                UuEndpoint = "https://uu.example.test/" + secret
            };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var status = await worker.RunOnceAsync(default);
            Require(status.SettingsProjected, "读取成功应投影配置");
            Require(status.ProxyUrlDisplay == "http://127.0.0.1:7897" && status.ProxyUrlRedacted,
                "代理秘密未被隐藏：" + status.ProxyUrlDisplay);
            Require(status.UuEndpointDisplay == "https://uu.example.test" && status.UuEndpointRedacted,
                "UU 秘密未被隐藏：" + status.UuEndpointDisplay);
            var json = JsonSerializer.Serialize(status, Storage.Json);
            Require(!json.Contains(secret, StringComparison.Ordinal), "状态快照出现地址秘密");
            var content = SettingsModel.From(Reading(status));
            Require(!(content.ProxyUrlDisplay + content.UuEndpointDisplay).Contains(secret, StringComparison.Ordinal),
                "设置页投影出现地址秘密");
        }),
        ("设置页回归：地址中的秘密不进入诊断包", async () =>
        {
            const string secret = "TEST-URL-SECRET";
            var directory = NewDirectory();
            try
            {
                var fixture = new MonitorFixture();
                fixture.Settings = fixture.Settings with
                {
                    ProxyUrl = "http://127.0.0.1:7897/" + secret,
                    UuEndpoint = "https://uu.example.test/" + secret
                };
                using var worker = new MonitorWorker(fixture, fixture.Clock);
                var status = await worker.RunOnceAsync(default);
                var statusPath = Path.Combine(directory, "status.json");
                File.WriteAllText(statusPath, JsonSerializer.Serialize(status, Storage.Json));
                var logs = Path.Combine(directory, "logs");
                Directory.CreateDirectory(logs);
                var zip = Path.Combine(directory, "diag.zip");
                var result = await DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                {
                    OutputPath = zip,
                    StatusPath = statusPath,
                    LogDirectory = logs,
                    LoadSettings = () => null,
                    Profiles = () => [],
                    ExportedAt = DateTimeOffset.UtcNow
                });
                Require(result.Entries.Contains("status.json"), "诊断包未收录状态文件");
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    Require(!reader.ReadToEnd().Contains(secret, StringComparison.Ordinal),
                        "诊断包条目泄露地址秘密：" + entry.FullName);
                }
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("设置页回归：配置读取失败不发布旧投影或复位通道模式", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Settings = fixture.Settings with
            {
                AuthChannelMode = AuthChannelModes.Automatic,
                ProxyUrl = "http://127.0.0.1:7897"
            };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var first = await worker.RunOnceAsync(default);
            Require(first.SettingsProjected && first.AuthChannelMode == AuthChannelModes.Automatic, "首轮投影未建立");
            fixture.BeforeSettingsRead = () => throw new IOException("fixture-settings-read-failed");
            var failed = await worker.RunOnceAsync(default);
            Require(failed.State == "检测异常", "读取失败应进入检测异常：" + failed.State);
            Require(!failed.SettingsProjected, "读取失败仍宣称配置投影可信");
            Require(failed.ProxyUrlDisplay.Length == 0 && failed.UuEndpointDisplay.Length == 0, "读取失败泄露上一轮配置");
            Require(failed.AuthChannelMode.Length == 0, "读取失败仍发布复位后的通道模式：" + failed.AuthChannelMode);
            fixture.BeforeSettingsRead = null;
            var recovered = await worker.RunOnceAsync(default);
            Require(recovered.SettingsProjected && recovered.AuthChannelMode == AuthChannelModes.Automatic, "恢复后投影未重建");
        }),
        ("设置页回归：读取成功后其他检测失败仍保留可信投影", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Settings = fixture.Settings with
            {
                AuthChannelMode = AuthChannelModes.Off,
                ProxyUrl = "http://127.0.0.1:7897"
            };
            fixture.BeforeWifiRead = () => throw new IOException("fixture-wifi-read-failed");
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var status = await worker.RunOnceAsync(default);
            Require(status.State == "检测异常" && status.SettingsProjected, "读取成功后的检测失败不应撤销投影");
            Require(status.AuthChannelMode == AuthChannelModes.Off, "读取成功后的检测失败应保留本轮模式");
            Require(status.ProxyUrlDisplay == "http://127.0.0.1:7897", "读取成功后的检测失败应保留安全投影");
        }),
        ("设置页回归：保存期间的新修改不被标成已保存", async () =>
        {
            var gate = new TaskCompletionSource<HandoffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = new SettingsPage(() => [Profile], (_, _) => gate.Task);
            page.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordOne));
            var save = page.SaveAsync();
            page.ApplyForQa(new SettingsDraft(Profile, "TEST-USER-1", PasswordTwo, "", "",
                Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true));
            gate.SetResult(new HandoffResult(HandoffOutcome.Completed, SettingsSaveOutcome.Saved));
            var result = await save;
            Require(result.Saved, "提交的草稿应保存成功");
            Require(page.Dirty, "保存期间的新修改被误标为已保存");
            Require(page.Message.Contains("尚未保存", StringComparison.Ordinal), "未提示保存期间的新修改：" + page.Message);
            Require(page.Draft.Password == PasswordTwo, "保存期间输入的新密码被清除");
        }),
        ("设置页回归：未编辑的隐藏地址保存时保留完整原值", () =>
        {
            const string secret = "TEST-URL-SECRET";
            var directory = NewDirectory();
            try
            {
                var secretProxy = "http://127.0.0.1:7897/" + secret + "?token=" + secret;
                var secretUu = "https://uu.example.test/" + secret;
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(new Settings
                {
                    InterfaceId = Profile.InterfaceId,
                    WifiProfile = Profile.Name,
                    PortalOrigin = "https://login.csust.edu.cn",
                    ProxyUrl = secretProxy,
                    UuEndpoint = secretUu
                }, Storage.Json));
                var page = CreatePage(editor);
                page.Update(SettingsModel.From(Reading(Online() with
                {
                    ProxyUrlDisplay = "http://127.0.0.1:7897",
                    ProxyUrlRedacted = true,
                    UuEndpointDisplay = "https://uu.example.test",
                    UuEndpointRedacted = true
                })));
                Require(page.Draft.KeepProxyUrl && page.Draft.KeepUuEndpoint, "投影未标记隐藏地址");
                Require(!(page.Draft.ProxyUrl + page.Draft.UuEndpoint).Contains(secret, StringComparison.Ordinal),
                    "草稿回显了隐藏地址");
                page.ApplyForQa(new SettingsDraft(
                    Profile, "TEST-USER-1", "", "http://127.0.0.1:7897", "https://uu.example.test",
                    Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true)
                {
                    KeepProxyUrl = true,
                    KeepUuEndpoint = true
                });
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(result.Saved, "保留隐藏地址的保存失败：" + result.Message);
                var saved = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(saved.ProxyUrl == secretProxy && saved.UuEndpoint == secretUu,
                    "保存覆盖了隐藏地址的完整原值");
                Require(!page.Content.ProxyUrlDisplay.Contains(secret, StringComparison.Ordinal), "页面内容回显了隐藏地址");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：保留隐藏地址时读取现有设置失败不写入也不截断", () =>
        {
            var directory = NewDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var settingsPath = Path.Combine(directory, "settings.json");
                var credentialPath = Path.Combine(directory, "credentials.dpapi");
                var writes = 0;
                var editor = new ElevatedSettings(
                    () => throw new IOException("fixture-settings-read-failed"),
                    () => new Credentials("TEST-USER-1", PasswordOne),
                    () => [Profile],
                    _ => writes++,
                    _ => writes++);
                var page = CreatePage(editor);
                page.ApplyForQa(new SettingsDraft(
                    Profile, "TEST-USER-1", "", "http://127.0.0.1:7897", "https://uu.example.test",
                    Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true)
                {
                    KeepProxyUrl = true,
                    KeepUuEndpoint = true
                });
                var result = page.SaveAsync().GetAwaiter().GetResult();
                Require(!result.Saved, "读取现有设置失败仍报告保存成功");
                Require(result.Message.Contains("未写入", StringComparison.Ordinal), "读取失败未说明零写入：" + result.Message);
                Require(writes == 0, "读取失败后仍执行了写入");
                Require(!File.Exists(settingsPath) && !File.Exists(credentialPath), "读取失败时产生了写入文件");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("设置页回归：保存按钮自动化名称与忙态同步", () => Sync(() =>
        {
            Require(ShellAccessibility.SettingsSave(false) == "保存设置", "空闲名称不符");
            Require(ShellAccessibility.SettingsSave(true) == "正在保存…", "忙态名称不符");
            Require(SettingsModel.RedactedUrlNote(true, true).Contains("未回显", StringComparison.Ordinal),
                "隐藏地址说明缺失");
            Require(SettingsModel.RedactedUrlNote(false, false).Length == 0, "无隐藏地址时不应显示说明");
        })),
        ("设置页回归：并发保存不会把守护 Wi-Fi 改回旧草稿", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var settingsPath = Path.Combine(directory, "settings.json");
                var credentialPath = Path.Combine(directory, "credentials.dpapi");
                SettingsFixture.WriteCredentials(credentialPath, new Credentials("TEST-USER-1", PasswordOne));
                Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(new Settings
                { InterfaceId = Profile.InterfaceId, WifiProfile = Profile.Name }, Storage.Json));
                var editor = new ElevatedSettings(
                    () => JsonSerializer.Deserialize<Settings>(Storage.ReadShared(settingsPath), Storage.Json),
                    () => SettingsFixture.ReadCredentials(credentialPath),
                    () => [Profile, ProfileB],
                    credentials => SettingsFixture.WriteCredentials(credentialPath, credentials),
                    settings => Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(settings, Storage.Json)));
                var page = new SettingsPage(() => [Profile, ProfileB], (draft, _) =>
                    Task.FromResult(new HandoffResult(HandoffOutcome.Completed, editor.Save(draft).Outcome)));
                page.Update(SettingsModel.From(Reading(Online())));
                Require(page.Draft.Profile is { } projected && projected.Name == Profile.Name, "初始投影未选中网络 A");
                page.Draft.Username = "TEST-USER-1";
                page.MarkDirty();

                // 另一个窗口改成网络 B 并保存成功；本窗口只改了账号，草稿里的 Wi-Fi 仍是旧投影。
                Require(editor.Save(new SettingsDraft(ProfileB, "TEST-USER-1", "", "", "", true, true, true)).Saved,
                    "另一个窗口保存失败");
                Require(page.SaveAsync().GetAwaiter().GetResult().Saved, "本窗口保存失败");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(settingsPath), Storage.Json)!;
                Require(actual.InterfaceId == ProfileB.InterfaceId && actual.WifiProfile == ProfileB.Name,
                    "旧草稿把守护 Wi-Fi 改回了网络 A");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：Wi-Fi 列表可刷新且枚举失败有受控状态", () => Sync(() =>
        {
            var profiles = new List<WifiProfile> { Profile };
            var page = new SettingsPage(() => profiles, (_, _) => Task.FromResult(new HandoffResult(HandoffOutcome.Rejected)));
            Require(page.Profiles.Count == 1 && !page.ProfileLoadFailed, "初始配置列表不符");
            var refreshed = 0;
            page.ProfilesChanged += () => refreshed++;
            profiles.Add(ProfileB);
            page.RefreshProfiles();
            Require(page.Profiles.Count == 2 && refreshed == 1 && !page.ProfileLoadFailed, "刷新未取得新增配置");
            page.Update(SettingsModel.From(Reading(Online())));
            Require(page.Draft.Profile?.Name == Profile.Name, "刷新后投影未按网卡与名称重新匹配");

            var failing = new SettingsPage(() => throw new IOException("synthetic"),
                (_, _) => Task.FromResult(new HandoffResult(HandoffOutcome.Rejected)));
            Require(failing.ProfileLoadFailed && failing.Profiles.Count == 0, "枚举失败未标记且列表未清空");
            failing.RefreshProfiles();
            Require(failing.ProfileLoadFailed && failing.Profiles.Count == 0, "重试失败状态未保持");
        })),
        ("设置页回归：本地校验失败也立即清除已输入密码", () => Sync(() =>
        {
            var emptyDirectory = NewDirectory();
            try
            {
                var cleared = 0;
                var page = CreatePage(CreateEditor(emptyDirectory).Editor);
                page.PasswordCleared += () => cleared++;
                page.ApplyForQa(Draft(null, "TEST-USER-1", PasswordOne));
                var missing = page.SaveAsync().GetAwaiter().GetResult();
                Require(missing.State == SettingsPageSaveState.ValidationFailed, "未选择 Wi-Fi 未被本地拦截");
                Require(page.Draft.Password.Length == 0 && cleared == 1, "本地校验失败后密码仍留在草稿");
            }
            finally { Directory.Delete(emptyDirectory, recursive: true); }

            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var invalid = CreatePage(editor);
                invalid.ApplyForQa(Draft(Profile, "TEST-USER-1", PasswordTwo, "http://10.0.0.1:7897"));
                var result = invalid.SaveAsync().GetAwaiter().GetResult();
                Require(result.State == SettingsPageSaveState.ValidationFailed, "非法代理未被本地拦截");
                Require(invalid.Draft.Password.Length == 0, "地址校验失败后密码仍留在草稿");
                Require(SettingsFixture.ReadCredentials(paths.Credentials)?.Password == PasswordOne, "本地校验失败仍改写了凭据");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：兼容编辑器快照在保存前变化时零写入", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var expected = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                var credentialsBefore = File.ReadAllBytes(paths.Credentials);

                // 另一个会话在窗口打开后改写了设置（例如代理地址）。
                var changed = expected with { ProxyUrl = "http://127.0.0.1:7898" };
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(changed, Storage.Json));

                var result = editor.Save(Draft(Profile, "TEST-USER-1", PasswordTwo), new SettingsSaveGuard(expected));
                Require(result.Outcome == SettingsSaveOutcome.SettingsChanged, "旧快照未被拒绝：" + result.Outcome);
                Require(result.Message.Contains("未写入", StringComparison.Ordinal), "旧快照拒绝未说明零写入");
                Require(File.ReadAllBytes(paths.Credentials).SequenceEqual(credentialsBefore), "旧快照覆盖了凭据");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(actual == changed, "旧快照覆盖了另一会话刚保存的设置");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：兼容编辑器首次设置期间出现新设置时零写入", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory);
                var created = new Settings { InterfaceId = Profile.InterfaceId, WifiProfile = Profile.Name };
                Storage.AtomicWrite(paths.Settings, JsonSerializer.Serialize(created, Storage.Json));
                var result = editor.Save(Draft(Profile, "TEST-USER-1", PasswordOne), new SettingsSaveGuard(null));
                Require(result.Outcome == SettingsSaveOutcome.SettingsChanged, "首次设置期间的新设置未被拒绝");
                Require(!File.Exists(paths.Credentials), "被拒绝的保存仍写入了凭据");
                var actual = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                Require(actual == created, "被拒绝的保存改写了新设置");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：兼容编辑器快照未变化时按原规则保存", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var (editor, paths) = CreateEditor(directory, seedUsername: "TEST-USER-1", seedPassword: PasswordOne);
                var expected = JsonSerializer.Deserialize<Settings>(Storage.ReadShared(paths.Settings), Storage.Json)!;
                var result = editor.Save(Draft(Profile, "TEST-USER-1", PasswordTwo), new SettingsSaveGuard(expected));
                Require(result.Saved, "快照未变化时保存失败：" + result.Message);
                Require(SettingsFixture.ReadCredentials(paths.Credentials)?.Password == PasswordTwo, "快照未变化时未写入新密码");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("设置页回归：兼容编辑器快照守卫读取失败时零写入", () => Sync(() =>
        {
            var writes = 0;
            var editor = new ElevatedSettings(
                () => throw new IOException("fixture-settings-read-failed"),
                () => new Credentials("TEST-USER-1", PasswordOne),
                () => [Profile], _ => writes++, _ => writes++);
            var result = editor.Save(Draft(Profile, "TEST-USER-1", PasswordTwo), new SettingsSaveGuard(null));
            Require(result.Outcome == SettingsSaveOutcome.SettingsUnreadable, "守卫读取失败未受控拒绝");
            Require(writes == 0, "守卫读取失败仍执行了写入");
        }))
    ];

    private static SettingsPage CreatePage(ElevatedSettings editor) =>
        new(() => [Profile], (draft, _) =>
            Task.FromResult(new HandoffResult(HandoffOutcome.Completed, editor.Save(draft).Outcome)));

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
        Uu = "未核验",
        SettingsProjected = true,
        InterfaceId = Profile.InterfaceId,
        WifiProfile = Profile.Name,
        ProxyUrlDisplay = "http://127.0.0.1:7897",
        ProxyUrlRedacted = false,
        UuEndpointDisplay = "https://uu.example.test",
        UuEndpointRedacted = false,
        AllowActiveDisconnect = true,
        KeepAwakeOnAc = true,
        AuthChannelMode = AuthChannelModes.ManualOnly
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
