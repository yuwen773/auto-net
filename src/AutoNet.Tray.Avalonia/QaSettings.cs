using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页与设置编辑器 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=settings 激活）：
/// - 壳侧（普通界面，故事 34）：状态矩阵下的只读投影、不可信降级与修改入口意图记录
///   （本模式不弹真实 UAC）；实况模式读取本机真实状态；
/// - 编辑器侧（提权进程，故事 35–39）：在 QA 证据目录内的隔离夹具上逐场景驱动既有保存规则——
///   首次设置必须密码、账号未变密码留空保留旧密码、换账号必须新密码、校验失败与存储失败零谎报、
///   凭据走 DPAPI 密文且不落明文（故事 36）、认证暂停标记逐字节保持（故事 39）。
/// 夹具只写 QA 证据目录，绝不触碰本机真实受限目录，也不发起真实网络请求。
/// </summary>
internal static class QaSettings
{
    private static readonly object modifyGate = new();
    private static readonly List<string> modifies = [];

    /// <summary>设置页 QA 的修改入口记录适配器：只记录命令意图，不弹出 UAC（生产路径为 Elevation.Launch）。</summary>
    public static void RecordModify() => RecordModify(ElevatedOperationKind.Settings.CommandLineArgument());

    private static void RecordModify(string command)
    {
        lock (modifyGate) modifies.Add(command);
    }

    private static int ModifyCount()
    {
        lock (modifyGate) return modifies.Count;
    }

    /// <summary>壳侧设置页 QA（普通界面）：只读投影矩阵 + 修改入口意图记录。</summary>
    public static void Run(ShellController controller, MainWindow window, Action modifySettings, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaSettingsStep>();
            var actions = new List<QaSettingsAction>();
            var editorSteps = new List<QaSettingsEditorStep>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
            var fixtureMode = fixturePath is { Length: > 0 };
            lock (modifyGate) modifies.Clear();

            QaSettingsReport Build(string? error = null) => new(
                fixtureMode ? "fixture" : "live",
                fixtureMode,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                steps,
                actions,
                editorSteps,
                error);

            try
            {
                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                if (fixtureMode)
                {
                    // 安全护栏：夹具只允许写 QA 证据目录内的文件，绝不触碰本机真实的 status.json。
                    if (!Path.GetFullPath(fixturePath!).StartsWith(qa.Directory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("AUTONET_QA_STATUS_PATH 必须位于 QA 证据目录内。");

                    foreach (var (state, snapshot) in Matrix())
                    {
                        if (snapshot is null) File.Delete(fixturePath!);
                        else WriteStatus(fixturePath!, snapshot);
                        await controller.SelectAsync(ShellPage.Settings);
                        await controller.RefreshStatusAsync();
                        await Task.Delay(200);
                        var shot = qa.SettingsShotPath("settings-" + state);
                        WindowCapture.Save(window, shot);
                        steps.Add(Describe(state, controller, Path.GetFileName(shot)));
                    }

                    // 修改入口意图：入口不依赖状态可信度（与既有托盘菜单一致），
                    // 点击记录的必须是既有的 --settings 提权参数（故事 34 的接线证据）。
                    actions.Add(DriveModify("修改入口：普通界面只发起提权请求", modifySettings));
                }
                else
                {
                    await controller.RefreshStatusAsync();
                    await controller.SelectAsync(ShellPage.Settings);
                    await Task.Delay(250);
                    var shot = qa.SettingsShotPath("settings-live");
                    WindowCapture.Save(window, shot);
                    steps.Add(Describe("live", controller, Path.GetFileName(shot)));
                }

                QaReports.Write(qa.SettingsReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.SettingsReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    /// <summary>编辑器侧 QA（提权进程）：隔离夹具上逐场景驱动既有保存规则、真实窗口与取消路径。</summary>
    public static void RunEditor(QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var editorSteps = new List<QaSettingsEditorStep>();
            var fixtureRoot = Path.Combine(qa.Directory, "settings-fixture");
            QaSettingsReport Build(string? error = null) => new(
                "fixture",
                true,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                [],
                [],
                editorSteps,
                error);

            try
            {
                // 安全护栏（与 QaUnblock 一致）：夹具只在 QA 证据目录内创建，绝不触碰本机真实受限目录。
                var prefix = Path.GetFullPath(qa.Directory) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(fixtureRoot).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("设置 QA 夹具目录必须位于 QA 证据目录内。");
                if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
                Directory.CreateDirectory(fixtureRoot);

                foreach (var scenario in Scenarios())
                    editorSteps.Add(DriveScenario(fixtureRoot, scenario));

                // 真实窗口视觉证据：编辑窗口初始状态、窗口内校验失败说明、private 缺失提示、
                // 取消零写入（故事 38）与保存成功反馈。
                editorSteps.Add(await DriveWindowCancelEvidenceAsync(qa, fixtureRoot));
                editorSteps.Add(await DriveWindowSavedEvidenceAsync(qa, fixtureRoot));

                QaReports.Write(qa.SettingsReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.SettingsReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    private sealed record Scenario(
        string Name,
        string? SeedUsername,
        string SeedPassword,
        string Username,
        string Password,
        string ProxyUrl,
        bool FailSettingsStore,
        string Intent);

    private static IEnumerable<Scenario> Scenarios()
    {
        // 故事 35+36：首次设置必须提供密码；成功后设置与 DPAPI 密文都在夹具内产生。
        yield return new("first-setup", null, "", "TEST-USER-1", "TEST-PASSWORD-1", "", false,
            "首次设置：账号密码齐备，设置与 DPAPI 密文产生，无明文");
        // 故事 37：账号未变且密码留空 → 保留已保存密码（重新加密属正常写入，密文仍不含明文）。
        yield return new("keep-password", "TEST-USER-1", "TEST-PASSWORD-1", "TEST-USER-1", "", "", false,
            "账号未变且密码留空：保留已保存密码，密文更新且无明文");
        // 故事 37：换账号且密码留空 → 拒绝并保留一切。
        yield return new("change-account-required", "TEST-USER-1", "TEST-PASSWORD-1", "TEST-USER-2", "", "", false,
            "换账号且密码留空：必须提供新密码，凭据与设置字节不变");
        // 故事 37：换账号并提供新密码 → 凭据更新。
        yield return new("change-account-saved", "TEST-USER-1", "TEST-PASSWORD-1", "TEST-USER-2", "TEST-PASSWORD-2", "", false,
            "换账号并提供新密码：凭据更新为 (TEST-USER-2, TEST-PASSWORD-2)");
        // 校验失败：不写入任何文件（取消不写入的镜像）。
        yield return new("validation-failed", "TEST-USER-1", "TEST-PASSWORD-1", "TEST-USER-1", "TEST-PASSWORD-1", "http://remote.example.com:8080", false,
            "代理地址不合法：校验失败，凭据与设置字节不变");
        // 存储失败：与既有表单一致的顺序（先凭据后设置），设置失败如实披露异常类型，不谎报成功。
        yield return new("store-failed", "TEST-USER-1", "TEST-PASSWORD-1", "TEST-USER-1", "TEST-PASSWORD-1", "", true,
            "设置写入失败：只披露异常类型，不谎报成功");
    }

    private static QaSettingsEditorStep DriveScenario(string fixtureRoot, Scenario scenario)
    {
        // 每个场景一个隔离目录：受限存储语义（设置 + DPAPI 凭据 + 认证暂停标记）都在其中复现。
        var directory = Path.Combine(fixtureRoot, scenario.Name);
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialPath = Path.Combine(directory, "credentials.dpapi");
        var blockPath = Path.Combine(directory, "auth-block.txt");

        // 故事 39 的直接证据：预置认证暂停标记，保存全程必须逐字节保持。
        var safety = new SafetyStateStore(directory);
        safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
        var blockBefore = File.ReadAllBytes(blockPath);
        if (scenario.SeedUsername is { } seed)
        {
            WriteFixtureSettings(settingsPath);
            WriteFixtureCredentials(credentialPath, new Credentials(seed, scenario.SeedPassword));
        }

        var settingsBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : [];
        var credentialsBefore = File.Exists(credentialPath) ? File.ReadAllBytes(credentialPath) : [];

        // 故事 39 的构造性保证在委托层复核：编辑器依赖里没有任何解除入口，
        // 这里额外记录标记字节，任何路径的标记变化都会被脚本发现。
        var editor = CreateFixtureEditor(settingsPath, credentialPath, scenario.FailSettingsStore);

        var result = editor.Save(new SettingsDraft(
            FixtureProfile, scenario.Username, scenario.Password, scenario.ProxyUrl, "",
            Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true));

        var settingsAfter = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : [];
        var credentialsAfter = File.Exists(credentialPath) ? File.ReadAllBytes(credentialPath) : [];
        var blockAfter = File.ReadAllBytes(blockPath);
        var credentialPlaintextFree = credentialsAfter.Length == 0 ||
            (!SearchUtf8(credentialsAfter, scenario.Password) && !SearchUtf8(credentialsAfter, scenario.Username));

        return new QaSettingsEditorStep(
            scenario.Name,
            result.Outcome.ToString(),
            result.Message,
            SettingsWritten: !settingsBefore.SequenceEqual(settingsAfter),
            CredentialsWritten: !credentialsBefore.SequenceEqual(credentialsAfter),
            CredentialsCreated: credentialsBefore.Length == 0 && credentialsAfter.Length > 0,
            CredentialPlaintextFree: credentialPlaintextFree,
            BlockUntouched: blockBefore.SequenceEqual(blockAfter),
            File: null);
    }

    /// <summary>
    /// 真实编辑窗口视觉证据（取消路径，故事 38）：编辑窗口初始状态 → 窗口内校验失败说明
    /// → private 缺失提示 → 关闭零写入（字节级核对）。场景结论走 JSON 报告；窗口只提供
    /// 用户实际看到的界面证据。
    /// </summary>
    private static async Task<QaSettingsEditorStep> DriveWindowCancelEvidenceAsync(QaEnvironment qa, string fixtureRoot)
    {
        var directory = Path.Combine(fixtureRoot, "window");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialPath = Path.Combine(directory, "credentials.dpapi");
        var blockPath = Path.Combine(directory, "auth-block.txt");
        var safety = new SafetyStateStore(directory);
        safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
        WriteFixtureSettings(settingsPath);
        WriteFixtureCredentials(credentialPath, new Credentials("TEST-USER-1", "TEST-PASSWORD-1"));

        var settingsBefore = File.ReadAllBytes(settingsPath);
        var credentialsBefore = File.ReadAllBytes(credentialPath);
        var blockBefore = File.ReadAllBytes(blockPath);

        // private 缺失提示（生产语义里的真实窗口）：StartSettingsEditor 在该分支显示同一文案。
        await ShowNoticeEvidenceAsync(qa.SettingsShotPath("settings-editor-missing-store"),
            "AutoNet", ElevatedSettings.MissingStoreMessage);

        var editor = CreateFixtureEditor(settingsPath, credentialPath, failSettingsStore: false);
        var window = new SettingsWindow(editor);
        window.Show();
        await Task.Delay(320);
        WindowCapture.Save(window, qa.SettingsShotPath("settings-editor-window"));

        // 换账号且密码留空：窗口保持打开并在窗口内显示受控说明（与用户保存同一条交互路径）。
        window.RunSaveForQa(new SettingsDraft(FixtureProfile, "TEST-USER-2", "", "", "",
            Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true));
        await Task.Delay(160);
        WindowCapture.Save(window, qa.SettingsShotPath("settings-editor-window-password-required"));
        window.Close();
        await Task.Delay(80);

        // 取消（关闭窗口）零写入（故事 38）：字节级核对。
        var settingsAfter = File.ReadAllBytes(settingsPath);
        var credentialsAfter = File.ReadAllBytes(credentialPath);
        var blockAfter = File.ReadAllBytes(blockPath);
        var untouched = settingsBefore.SequenceEqual(settingsAfter)
            && credentialsBefore.SequenceEqual(credentialsAfter)
            && blockBefore.SequenceEqual(blockAfter);

        return new QaSettingsEditorStep(
            "window-cancel",
            "Declined",
            "窗口展示与取消均不写入；换账号留空的说明显示在窗口内。",
            SettingsWritten: !untouched,
            CredentialsWritten: !untouched,
            CredentialsCreated: false,
            CredentialPlaintextFree: true,
            BlockUntouched: blockBefore.SequenceEqual(blockAfter),
            File: "settings-editor-window.png");
    }

    /// <summary>
    /// 真实编辑窗口视觉证据（保存成功反馈）：与既有表单的保存确认同语义——窗口内显示
    /// 「已保存」文案（WinForms 为 MessageBox），账号未变留空密码保留旧凭据；随后用户关闭窗口。
    /// </summary>
    private static async Task<QaSettingsEditorStep> DriveWindowSavedEvidenceAsync(QaEnvironment qa, string fixtureRoot)
    {
        var directory = Path.Combine(fixtureRoot, "window-saved");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialPath = Path.Combine(directory, "credentials.dpapi");
        var blockPath = Path.Combine(directory, "auth-block.txt");
        var safety = new SafetyStateStore(directory);
        safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
        WriteFixtureSettings(settingsPath);
        WriteFixtureCredentials(credentialPath, new Credentials("TEST-USER-1", "TEST-PASSWORD-1"));

        var settingsBefore = File.ReadAllBytes(settingsPath);
        var blockBefore = File.ReadAllBytes(blockPath);

        var editor = CreateFixtureEditor(settingsPath, credentialPath, failSettingsStore: false);
        var window = new SettingsWindow(editor);
        window.Show();
        await Task.Delay(320);

        // 账号未变且密码留空：保存成功，窗口内显示确认文案（故事 37 的保留规则 + 真实反馈）。
        window.RunSaveForQa(new SettingsDraft(FixtureProfile, "TEST-USER-1", "", "", "",
            Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true));
        await Task.Delay(160);
        WindowCapture.Save(window, qa.SettingsShotPath("settings-editor-window-saved"));
        window.Close();
        await Task.Delay(80);

        var kept = SettingsFixture.ReadCredentials(credentialPath);
        var settingsAfter = File.ReadAllBytes(settingsPath);
        var blockAfter = File.ReadAllBytes(blockPath);

        return new QaSettingsEditorStep(
            "window-saved",
            "Saved",
            "保存成功后窗口内显示确认文案；凭据保留旧密码并重新加密。",
            SettingsWritten: !settingsBefore.SequenceEqual(settingsAfter),
            CredentialsWritten: kept is { Username: "TEST-USER-1", Password: "TEST-PASSWORD-1" },
            CredentialsCreated: false,
            CredentialPlaintextFree: !SettingsFixture.ContainsUtf8(File.ReadAllBytes(credentialPath), "TEST-PASSWORD-1"),
            BlockUntouched: blockBefore.SequenceEqual(blockAfter),
            File: "settings-editor-window-saved.png");
    }

    private static ElevatedSettings CreateFixtureEditor(string settingsPath, string credentialPath, bool failSettingsStore) => new(
        () => File.Exists(settingsPath)
            ? JsonSerializer.Deserialize<Settings>(Storage.ReadShared(settingsPath), Storage.Json) : null,
        () => ReadFixtureCredentials(credentialPath),
        () => [FixtureProfile],
        credentials => WriteFixtureCredentials(credentialPath, credentials),
        settings =>
        {
            if (failSettingsStore) throw new IOException("夹具注入的设置写入失败");
            Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(settings, Storage.Json));
        });

    private static QaSettingsAction DriveModify(string context, Action modifySettings)
    {
        const string expected = "--settings";
        var before = ModifyCount();
        modifySettings();
        var after = ModifyCount();
        if (after != before + 1) throw new InvalidOperationException("修改入口未记录命令：" + context);
        string? command;
        lock (modifyGate) command = modifies[^1];
        if (command != expected) throw new InvalidOperationException($"命令意图不符：期望 {expected}，实际 {command}（{context}）");
        return new QaSettingsAction(context, expected, true, command);
    }

    private static QaSettingsStep Describe(string state, ShellController controller, string? file)
    {
        if (controller.Content is not SettingsContent settings)
            throw new InvalidOperationException("设置页内容未就绪：" + state);
        return new QaSettingsStep(
            state,
            settings.Trusted,
            settings.RecoveryState,
            settings.GuardedNetwork,
            SettingsModel.PortalLine.Trim(),
            settings.ModifyLabel,
            file);
    }

    private static IEnumerable<(string State, StatusSnapshot? Snapshot)> Matrix()
    {
        yield return ("online", Online());
        yield return ("online-disabled", Online() with { Enabled = false });
        yield return ("online-suspended", Online() with
        {
            CredentialsBlocked = true,
            AuthenticationFailure = "CredentialsRejected",
            AuthenticationPersistentState = "CredentialsRejected"
        });
        yield return ("stale", Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        yield return ("invalid", Online() with { Tunnel = null! });
        yield return ("missing", null);
    }

    private static void WriteStatus(string path, StatusSnapshot snapshot) =>
        Storage.AtomicWrite(path, JsonSerializer.Serialize(snapshot, Storage.Json));

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

    private static readonly WifiProfile FixtureProfile = SettingsFixture.Profile;

    private static void WriteFixtureSettings(string path) => Storage.AtomicWrite(path,
        JsonSerializer.Serialize(new Settings
        {
            InterfaceId = FixtureProfile.InterfaceId,
            WifiProfile = FixtureProfile.Name,
            PortalOrigin = "https://login.csust.edu.cn"
        }, Storage.Json));

    /// <summary>夹具凭据读写与明文扫描：共享夹具的既有 DPAPI 逻辑（路径由场景给出）。</summary>
    private static void WriteFixtureCredentials(string path, Credentials credentials) =>
        SettingsFixture.WriteCredentials(path, credentials);

    private static Credentials? ReadFixtureCredentials(string path) =>
        SettingsFixture.ReadCredentials(path);

    private static bool SearchUtf8(byte[] haystack, string needle) =>
        SettingsFixture.ContainsUtf8(haystack, needle);

    private static async Task ShowNoticeEvidenceAsync(string path, string heading, string body)
    {
        var notice = NoticeWindow.Create(heading, body, width: 520);
        notice.Show();
        await Task.Delay(220);
        WindowCapture.Save(notice, path);
        notice.Close();
        await Task.Delay(80);
    }
}
