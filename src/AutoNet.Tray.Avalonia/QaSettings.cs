using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页与设置编辑器 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=settings 激活）：
/// - 主窗口设置页（#41）：状态矩阵下的只读事实与非秘密配置投影、真实表单控件，
///   以及首次设置、保留密码、换账号、校验失败、取消授权、结果未知、存储失败、保留原值等十一条保存路径；
///   保存经 QA 夹具内的真实 <see cref="ElevatedSettings"/> 规则，不弹真实 UAC；
/// - 提权编辑器（#8，兼容入口 --settings）：在 QA 证据目录内的隔离夹具上逐场景驱动既有保存规则。
/// 夹具只写 QA 证据目录，绝不触碰本机真实受限目录，也不发起真实网络请求；证据不含账号或密码。
/// </summary>
internal static class QaSettings
{
    private static readonly object gate = new();
    private static string? fixtureRoot;
    private static string? currentScenario;
    private static int handoffInvocations;
    private static bool submittedKeepProxyUrl;
    private static bool submittedKeepUuEndpoint;
    private static bool submittedKeepEnabled;
    private static bool submittedKeepAllowActiveDisconnect;
    private static bool submittedKeepKeepAwakeOnAc;
    private static bool submittedKeepAuthChannelMode;
    private static bool submittedKeepProfile;
    private static bool existingSettingsFound;

    /// <summary>主窗口设置页 QA：状态矩阵 + 十一条保存路径，截图与受控结果进入报告。</summary>
    public static void Run(ShellController controller, MainWindow window, SettingsPage page, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaSettingsStep>();
            var actions = new List<QaSettingsAction>();
            var editorSteps = new List<QaSettingsEditorStep>();
            var saveSteps = new List<QaSettingsSaveStep>();
            var navigationSteps = new List<QaSettingsNavigationStep>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
            var fixtureMode = fixturePath is { Length: > 0 };

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
                saveSteps,
                navigationSteps,
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
                        await Task.Delay(220);
                        var shot = qa.SettingsShotPath("settings-" + state);
                        WindowCapture.Save(window, shot);
                        steps.Add(Describe(state, controller, page, Path.GetFileName(shot)));
                    }

                    // 选项未知说明：无投影时把选项区滚入视野，证据同时显示三态“未确定”与保存语义说明。
                    File.Delete(fixturePath!);
                    await controller.SelectAsync(ShellPage.Settings);
                    await controller.RefreshStatusAsync();
                    await Task.Delay(220);
                    var optionsNote = window.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(item => item.Text == SettingsModel.OptionsUnknownNote && item.IsVisible)
                        ?? throw new InvalidOperationException("无可信投影时未显示恢复选项说明");
                    optionsNote.BringIntoView();
                    await Task.Delay(160);
                    var unknownShot = qa.SettingsShotPath("settings-options-unknown");
                    WindowCapture.Save(window, unknownShot);
                    steps.Add(Describe("options-unknown", controller, page, Path.GetFileName(unknownShot)));

                    // 列表刷新（#41 第四轮）：系统新增配置后刷新设置页，真实下拉框必须出现新配置。
                    QaProfiles.Add(OtherProfile);
                    page.RefreshProfiles();
                    await Task.Delay(160);
                    var profileBox = window.GetVisualDescendants().OfType<ComboBox>()
                        .FirstOrDefault(box => box.Items.OfType<WifiProfile>().Any());
                    if (profileBox is null || !profileBox.Items.OfType<WifiProfile>()
                            .Any(profile => profile.Name == OtherProfile.Name))
                        throw new InvalidOperationException("刷新设置页后未显示新增的 Wi-Fi 配置");
                    var refreshShot = qa.SettingsShotPath("settings-profile-refresh");
                    WindowCapture.Save(window, refreshShot);
                    steps.Add(Describe("profile-refresh", controller, page, Path.GetFileName(refreshShot)));

                    // 跨页往返（#41 审查）：指纹缓存不得让返回的页面仍显示设置控件。
                    WriteStatus(fixturePath!, Online());
                    await controller.RefreshStatusAsync();
                    await DriveNavigationAsync(controller, window, qa, navigationSteps);

                    // 保存场景必须在设置页可见时驱动：截图与保存按钮名称都取自用户实际看到的控件。
                    await controller.SelectAsync(ShellPage.Settings);
                    await Task.Delay(220);

                    fixtureRoot = Path.Combine(qa.Directory, "settings-page-fixture");
                    var prefix = Path.GetFullPath(qa.Directory) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(fixtureRoot).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("设置页 QA 夹具目录必须位于 QA 证据目录内。");
                    if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
                    Directory.CreateDirectory(fixtureRoot);

                    await DriveClearAddressKeyboardAsync(page, window, qa);

                    foreach (var scenario in SaveScenarios())
                        saveSteps.Add(await DriveSaveAsync(page, qa, window, scenario, fixtureRoot));
                }
                else
                {
                    await controller.RefreshStatusAsync();
                    await controller.SelectAsync(ShellPage.Settings);
                    await Task.Delay(250);
                    var shot = qa.SettingsShotPath("settings-live");
                    WindowCapture.Save(window, shot);
                    steps.Add(Describe("live", controller, page, Path.GetFileName(shot)));
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

    private static async Task DriveClearAddressKeyboardAsync(SettingsPage page, MainWindow window, QaEnvironment qa)
    {
        page.ApplyForQa(new SettingsDraft(FixtureProfile, "TEST-USER-1", "", "http://127.0.0.1:7897", "https://uu.example.test",
            Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true)
        { KeepProxyUrl = true, KeepUuEndpoint = true });
        await Task.Delay(180);
        foreach (var name in new[] { "清空代理检测地址", "清空 UU 服务检测地址" })
        {
            var button = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(item => item.IsVisible && AutomationProperties.GetName(item) == name)
                ?? throw new InvalidOperationException("未找到清空地址按钮：" + name);
            if (!button.Focus(NavigationMethod.Tab, KeyModifiers.None))
                throw new InvalidOperationException("清空地址按钮不能取得键盘焦点：" + name);
            button.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None, Source = button
            });
            await Task.Delay(120);
            if (name == "清空代理检测地址" && (page.Draft.KeepProxyUrl || page.Draft.ProxyUrl != "") ||
                name == "清空 UU 服务检测地址" && (page.Draft.KeepUuEndpoint || page.Draft.UuEndpoint != ""))
                throw new InvalidOperationException("键盘未触发清空地址：" + name);
        }
        WindowCapture.Save(window, qa.SettingsShotPath("settings-clear-address-keyboard"));
    }

    /// <summary>编辑器侧 QA（提权进程，兼容入口 --settings）：隔离夹具上逐场景驱动既有保存规则。</summary>
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
                [],
                [],
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

    /// <summary>
    /// QA 保存交接：在隔离夹具内调用真实 <see cref="ElevatedSettings"/> 规则；取消与结果未知
    /// 场景直接返回受控结果，不写任何文件。生产路径从不经过这里。
    /// </summary>
    internal static async Task<HandoffResult> SaveForQaAsync(SettingsDraft draft, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) handoffInvocations++;
        var scenario = currentScenario ?? "page-first-setup";
        if (scenario == "page-uac-cancelled") return new(HandoffOutcome.Cancelled);
        if (scenario == "page-outcome-unknown") return new(HandoffOutcome.OutcomeUnknown);
        // 忙态场景：保持交接未完成，让 QA 观察保存按钮与编辑控件的忙态。
        if (scenario == "page-busy-name") await Task.Delay(700);
        var root = fixtureRoot ?? throw new InvalidOperationException("设置页 QA 夹具未初始化。");
        var directory = Path.Combine(root, scenario);
        Directory.CreateDirectory(directory);
        // 证据：提交的草稿是否带“保留原值”标记，以及接收端此刻能否读到夹具设置。
        lock (gate)
        {
            submittedKeepProxyUrl = draft.KeepProxyUrl;
            submittedKeepUuEndpoint = draft.KeepUuEndpoint;
            submittedKeepEnabled = draft.KeepEnabled;
            submittedKeepAllowActiveDisconnect = draft.KeepAllowActiveDisconnect;
            submittedKeepKeepAwakeOnAc = draft.KeepKeepAwakeOnAc;
            submittedKeepAuthChannelMode = draft.KeepAuthChannelMode;
            submittedKeepProfile = draft.KeepProfile;
            existingSettingsFound = File.Exists(Path.Combine(directory, "settings.json"));
        }
        var editor = CreateFixtureEditor(
            Path.Combine(directory, "settings.json"),
            Path.Combine(directory, "credentials.dpapi"),
            failSettingsStore: scenario == "page-store-failed");
        var result = editor.Save(draft);
        return new(HandoffOutcome.Completed, result.Outcome);
    }

    private sealed record SaveScenario(
        string Name,
        string? SeedUsername,
        string SeedPassword,
        string? SeedProxyUrl,
        string? SeedUuEndpoint,
        string Username,
        string Password,
        string ProxyUrl,
        string UuEndpoint,
        bool KeepProxyUrl,
        bool KeepUuEndpoint,
        bool DelayHandoff,
        string Intent,
        bool SeedEnabled = true,
        bool SeedAllowActiveDisconnect = true,
        bool SeedKeepAwakeOnAc = true,
        string SeedAuthChannelMode = AuthChannelModes.ManualOnly,
        bool KeepOptions = false,
        WifiProfile? SeedProfile = null,
        bool KeepProfile = false);

    private static IEnumerable<SaveScenario> SaveScenarios()
    {
        // 首次设置：账号密码齐备，设置与 DPAPI 密文产生，无明文。
        yield return new("page-first-setup", null, "", null, null, "TEST-USER-1", "TEST-PASSWORD-1", "", "",
            false, false, false, "首次设置：保存成功，密码清除，认证暂停保持");
        // 账号未变且密码留空：保留已保存密码（接收端按既有规则判定）。
        yield return new("page-keep-password", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-1", "", "",
            "", false, false, false, "账号未变且密码留空：保留旧密码");
        // 换账号且密码留空：接收端拒绝，零写入。
        yield return new("page-change-account-required", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-2", "",
            "", "", false, false, false, "换账号且密码留空：拒绝并保留原凭据");
        // 校验失败：普通进程在交接前拦截，交接未发起，零写入。
        yield return new("page-validation-failed", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-1", "",
            "http://remote.example.com:8080", "", false, false, false, "代理地址不合法：交接前拒绝，零写入");
        // 取消/拒绝授权：零写入，结果文案明确。
        yield return new("page-uac-cancelled", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-1",
            "TEST-PASSWORD-1", "", "", false, false, false, "取消管理员授权：零写入");
        // 回执丢失：结果未知，不宣称零写入。
        yield return new("page-outcome-unknown", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-1",
            "TEST-PASSWORD-1", "", "", false, false, false, "保存结果未知：如实报告可能已写入");
        // 存储失败：先凭据后设置，凭据部分写入、设置未写入，如实报告。
        yield return new("page-store-failed", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-1",
            "TEST-PASSWORD-1", "", "", false, false, false, "设置写入失败：如实报告已发生的部分写入");
        // 地址含未回显的秘密：页面只看到来源，未编辑时接收端必须保留完整原值。
        yield return new("page-keep-redacted-url", "TEST-USER-1", "TEST-PASSWORD-1",
            "http://127.0.0.1:7897/TEST-URL-SECRET?token=TEST-URL-SECRET",
            "https://uu.example.test/TEST-URL-SECRET",
            "TEST-USER-1", "", "http://127.0.0.1:7897", "https://uu.example.test",
            true, true, false, "地址含未回显秘密：未编辑时保存保留完整原值");
        // 无投影时未编辑的恢复选项：页面显示“未确定”，接收端保留受限存储中的关闭状态。
        yield return new("page-keep-options", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-1", "", "", "",
            false, false, false, "无投影未编辑选项：显示未确定并保留已保存的关闭状态",
            SeedEnabled: false, SeedAllowActiveDisconnect: false, SeedKeepAwakeOnAc: false,
            SeedAuthChannelMode: AuthChannelModes.Off, KeepOptions: true);
        // 未在页面选择 Wi-Fi（KeepProfile）：接收端保留受限存储中的守护目标，不被页面旧选择覆盖。
        yield return new("page-keep-profile", "TEST-USER-1", "TEST-PASSWORD-1", null, null, "TEST-USER-1", "", "", "",
            false, false, false, "未选择 Wi-Fi：保留受限存储中的守护目标",
            SeedProfile: OtherProfile, KeepProfile: true);
        // 保存忙态：保存期间禁用编辑，保存按钮可见文字与自动化名称同步。
        yield return new("page-busy-name", null, "", null, null, "TEST-USER-1", "TEST-PASSWORD-1", "", "",
            false, false, true, "保存期间禁用编辑且无障碍名称同步忙态");
    }

    private static async Task<QaSettingsSaveStep> DriveSaveAsync(
        SettingsPage page, QaEnvironment qa, MainWindow window, SaveScenario scenario, string root)
    {
        var directory = Path.Combine(root, scenario.Name);
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialPath = Path.Combine(directory, "credentials.dpapi");
        var blockPath = Path.Combine(directory, "auth-block.txt");
        var safety = new SafetyStateStore(directory);
        if (!File.Exists(blockPath)) safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
        if (scenario.SeedUsername is { } seed)
        {
            WriteFixtureSettings(settingsPath, scenario.SeedProxyUrl, scenario.SeedUuEndpoint,
                scenario.SeedEnabled, scenario.SeedAllowActiveDisconnect, scenario.SeedKeepAwakeOnAc,
                scenario.SeedAuthChannelMode, scenario.SeedProfile);
            WriteFixtureCredentials(credentialPath, new Credentials(seed, scenario.SeedPassword));
        }
        var blockBefore = File.ReadAllBytes(blockPath);
        var settingsBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : [];
        var credentialsBefore = File.Exists(credentialPath) ? File.ReadAllBytes(credentialPath) : [];

        lock (gate)
        {
            currentScenario = scenario.Name;
            handoffInvocations = 0;
        }

        page.ApplyForQa(new SettingsDraft(
            FixtureProfile, scenario.Username, scenario.Password, scenario.ProxyUrl, scenario.UuEndpoint,
            Enabled: true, AllowActiveDisconnect: true, KeepAwakeOnAc: true)
        {
            KeepProfile = scenario.KeepProfile,
            KeepProxyUrl = scenario.KeepProxyUrl,
            KeepUuEndpoint = scenario.KeepUuEndpoint,
            KeepEnabled = scenario.KeepOptions,
            KeepAllowActiveDisconnect = scenario.KeepOptions,
            KeepKeepAwakeOnAc = scenario.KeepOptions,
            KeepAuthChannelMode = scenario.KeepOptions
        });
        await Task.Delay(160);
        var optionsUnknown = !page.Content.ConfigProjected &&
            (page.Draft.KeepEnabled || page.Draft.KeepAllowActiveDisconnect ||
             page.Draft.KeepKeepAwakeOnAc || page.Draft.KeepAuthChannelMode);
        var saveTask = page.SaveAsync();
        var nameDuringSave = "";
        if (scenario.DelayHandoff)
        {
            // 交接故意延迟：观察保存期间按钮的可见文字/自动化名称与编辑控件禁用状态。
            await Task.Delay(240);
            nameDuringSave = SaveButtonName(window);
        }
        var result = await saveTask;
        await Task.Delay(180);
        var nameAfterSave = SaveButtonName(window);
        // 保存结果与按钮位于页面下部：截图前滚到底，让证据显示用户实际看到的受控结果文案。
        ScrollToEnd(window);
        await Task.Delay(140);

        var shot = qa.SettingsShotPath("settings-" + scenario.Name);
        WindowCapture.Save(window, shot);

        var blockAfter = File.ReadAllBytes(blockPath);
        var settingsAfter = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : [];
        var credentialsAfter = File.Exists(credentialPath) ? File.ReadAllBytes(credentialPath) : [];
        var plaintextFree = credentialsAfter.Length == 0 ||
            (!SettingsFixture.ContainsUtf8(credentialsAfter, scenario.Password) &&
             !SettingsFixture.ContainsUtf8(credentialsAfter, scenario.Username));
        int invocations;
        bool keepProxy, keepUu, hadSettings, keepEnabled, keepDisconnect, keepAwake, keepChannel, keepProfile;
        lock (gate)
        {
            invocations = handoffInvocations;
            keepProxy = submittedKeepProxyUrl;
            keepUu = submittedKeepUuEndpoint;
            keepEnabled = submittedKeepEnabled;
            keepDisconnect = submittedKeepAllowActiveDisconnect;
            keepAwake = submittedKeepKeepAwakeOnAc;
            keepChannel = submittedKeepAuthChannelMode;
            keepProfile = submittedKeepProfile;
            hadSettings = existingSettingsFound;
        }
        // 落盘事实：保留标记为真时，夹具设置里未编辑的选项必须仍是种子值。
        var saved = settingsAfter.Length == 0 ? null
            : JsonSerializer.Deserialize<Settings>(Storage.ReadShared(settingsPath), Storage.Json);

        return new QaSettingsSaveStep(
            scenario.Name,
            result.State.ToString(),
            result.Message,
            PasswordCleared: page.Draft.Password.Length == 0,
            HandoffInvoked: invocations > 0,
            SecretInMessage: scenario.Password.Length > 0 &&
                result.Message.Contains(scenario.Password, StringComparison.Ordinal),
            BlockUntouched: blockBefore.SequenceEqual(blockAfter),
            SettingsWritten: !settingsBefore.SequenceEqual(settingsAfter),
            CredentialsWritten: !credentialsBefore.SequenceEqual(credentialsAfter),
            CredentialPlaintextFree: plaintextFree,
            SaveNameDuringSave: nameDuringSave,
            SaveNameAfter: nameAfterSave,
            KeepProfile: keepProfile,
            KeepProxyUrl: keepProxy,
            KeepUuEndpoint: keepUu,
            ExistingSettingsFound: hadSettings,
            KeepEnabled: keepEnabled,
            KeepAllowActiveDisconnect: keepDisconnect,
            KeepKeepAwakeOnAc: keepAwake,
            KeepAuthChannelMode: keepChannel,
            OptionsUnknown: optionsUnknown,
            EnabledAfter: saved?.Enabled,
            AllowActiveDisconnectAfter: saved?.AllowActiveDisconnect,
            KeepAwakeOnAcAfter: saved?.KeepAwakeOnAc,
            AuthChannelModeAfter: saved?.AuthChannelMode,
            File: Path.GetFileName(shot));
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
            (!SettingsFixture.ContainsUtf8(credentialsAfter, scenario.Password) &&
             !SettingsFixture.ContainsUtf8(credentialsAfter, scenario.Username));

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

    /// <summary>滚动到页面宿主外层（TextBox 模板内部也有 ScrollViewer，不能取第一个）。</summary>
    private static void ScrollToEnd(MainWindow window)
    {
        var host = window.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Name == "pageHost");
        host?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
    }

    /// <summary>保存按钮的自动化名称（保存期间应为忙态文字，结束后恢复）。</summary>
    private static string SaveButtonName(MainWindow window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Select(AutomationProperties.GetName)
            .FirstOrDefault(name => name is "保存设置" or "正在保存…") ?? "";

    /// <summary>页面宿主实际显示的页面标题；用于发现跨页指纹缓存导致的错页。</summary>
    private static string PageTitle(MainWindow window)
    {
        var host = window.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Name == "pageHost");
        return host?.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => Math.Abs(text.FontSize - 26) < 0.1 && text.Text is { Length: > 0 })?.Text ?? "";
    }

    /// <summary>
    /// 跨页往返（#41 审查）：先访问概览/诊断/日志，再进设置，再返回同一页面；每次记录页面宿主
    /// 实际显示的标题与截图。指纹缓存若未清理，返回时会停留在设置控件上。
    /// </summary>
    private static async Task DriveNavigationAsync(
        ShellController controller, MainWindow window, QaEnvironment qa, List<QaSettingsNavigationStep> steps)
    {
        var sequence = new (ShellPage From, ShellPage To)[]
        {
            (ShellPage.Settings, ShellPage.Overview),
            (ShellPage.Overview, ShellPage.Settings),
            (ShellPage.Settings, ShellPage.Overview),
            (ShellPage.Overview, ShellPage.Settings),
            (ShellPage.Settings, ShellPage.Diagnostics),
            (ShellPage.Diagnostics, ShellPage.Settings),
            (ShellPage.Settings, ShellPage.Diagnostics),
            (ShellPage.Diagnostics, ShellPage.Settings),
            (ShellPage.Settings, ShellPage.Logs),
            (ShellPage.Logs, ShellPage.Settings),
            (ShellPage.Settings, ShellPage.Logs)
        };
        var index = 0;
        foreach (var (from, to) in sequence)
        {
            await controller.SelectAsync(to);
            await Task.Delay(180);
            var shot = qa.SettingsShotPath(
                "settings-nav-" + index++ + "-" + to.ToString().ToLowerInvariant());
            WindowCapture.Save(window, shot);
            steps.Add(new QaSettingsNavigationStep(from.ToString(), to.ToString(), PageTitle(window), Path.GetFileName(shot)));
        }
    }

    private static QaSettingsStep Describe(string state, ShellController controller, SettingsPage page, string? file)
    {
        if (controller.Content is not SettingsContent settings)
            throw new InvalidOperationException("设置页内容未就绪：" + state);
        // 无投影且未编辑的恢复选项在界面上显示为“未确定”（三态未选）并给出说明；
        // OptionsKnown=false 时脚本要求截图与说明同时存在。
        var optionsKnown = settings.ConfigProjected ||
            !(page.Draft.KeepEnabled || page.Draft.KeepAllowActiveDisconnect ||
              page.Draft.KeepKeepAwakeOnAc || page.Draft.KeepAuthChannelMode);
        return new QaSettingsStep(
            state,
            settings.Trusted,
            settings.RecoveryState,
            settings.GuardedNetwork,
            SettingsModel.PortalLine.Trim(),
            settings.ConfigProjected,
            settings.WifiProfile,
            settings.ProxyUrlDisplay,
            settings.ProxyUrlRedacted,
            settings.UuEndpointDisplay,
            settings.UuEndpointRedacted,
            settings.Enabled,
            settings.AllowActiveDisconnect,
            settings.KeepAwakeOnAc,
            settings.AuthChannelMode,
            optionsKnown,
            file);
    }

    private static IEnumerable<(string State, StatusSnapshot? Snapshot)> Matrix()
    {
        yield return ("online", Online());
        yield return ("online-disabled", Online() with { Enabled = false });
        yield return ("online-redacted", Online() with
        {
            ProxyUrlDisplay = "http://127.0.0.1:7897",
            ProxyUrlRedacted = true,
            UuEndpointDisplay = "https://uu.example.test",
            UuEndpointRedacted = true
        });
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
        Uu = "未核验",
        SettingsProjected = true,
        InterfaceId = FixtureProfile.InterfaceId,
        WifiProfile = FixtureProfile.Name,
        ProxyUrlDisplay = "http://127.0.0.1:7897",
        ProxyUrlRedacted = false,
        UuEndpointDisplay = "https://uu.example.test",
        UuEndpointRedacted = false,
        AllowActiveDisconnect = true,
        KeepAwakeOnAc = true,
        AuthChannelMode = AuthChannelModes.ManualOnly
    };

    private static readonly WifiProfile FixtureProfile = SettingsFixture.Profile;

    /// <summary>QA 可枚举的配置列表；运行中追加用于验证设置页的列表刷新。</summary>
    private static readonly List<WifiProfile> QaProfiles = [FixtureProfile];
    internal static IReadOnlyList<WifiProfile> Profiles => QaProfiles;

    /// <summary>另一个已保存配置：用于验证 KeepProfile 不会被页面的默认选择覆盖。</summary>
    private static readonly WifiProfile OtherProfile = new(
        "66666666-7777-8888-9999-AAAAAAAAAAAA", "TEST-ADAPTER-2", "TEST-AP-2", AllUsers: true, Connected: false);

    private static void WriteFixtureSettings(string path, string? proxyUrl = null, string? uuEndpoint = null,
        bool enabled = true, bool allowActiveDisconnect = true, bool keepAwakeOnAc = true,
        string authChannelMode = AuthChannelModes.ManualOnly, WifiProfile? profile = null) =>
        Storage.AtomicWrite(path, JsonSerializer.Serialize(new Settings
        {
            InterfaceId = (profile ?? FixtureProfile).InterfaceId,
            WifiProfile = (profile ?? FixtureProfile).Name,
            PortalOrigin = "https://login.csust.edu.cn",
            ProxyUrl = proxyUrl ?? "",
            UuEndpoint = uuEndpoint ?? "",
            Enabled = enabled,
            AllowActiveDisconnect = allowActiveDisconnect,
            KeepAwakeOnAc = keepAwakeOnAc,
            AuthChannelMode = authChannelMode
        }, Storage.Json));

    /// <summary>夹具凭据读写与明文扫描：共享夹具的既有 DPAPI 逻辑（路径由场景给出）。</summary>
    private static void WriteFixtureCredentials(string path, Credentials credentials) =>
        SettingsFixture.WriteCredentials(path, credentials);

    private static Credentials? ReadFixtureCredentials(string path) =>
        SettingsFixture.ReadCredentials(path);

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
