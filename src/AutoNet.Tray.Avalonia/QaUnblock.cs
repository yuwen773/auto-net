using System.Net;
using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 解除认证暂停 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=unblock 激活）：
/// 在 QA 证据目录内的隔离夹具上逐场景跑完整流程，记录实际阶段序列与落盘结果：
/// - 无待解除标记：不得进入确认，不得写任何文件；
/// - 用户取消确认：只走确认阶段，阻断标记与审计均不变；
/// - 前置复核未通过：保留暂停并给出受控说明，无审计行；
/// - 诊断期间设置变化 / 校园 Wi-Fi 变化：中止并保留暂停；
/// - 成功路径：审计行先落盘，随后标记被删除，凭据文件始终未被创建。
/// 每个场景另捕获真实确认对话框与受控提示窗口作为视觉证据。
/// 夹具只写 QA 证据目录，绝不触碰本机真实受限目录，也不发起真实门户请求。
/// </summary>
internal static class QaUnblock
{
    public static void Run(QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaUnblockStep>();
            var fixtureRoot = Path.Combine(qa.Directory, "unblock-fixture");
            QaUnblockReport Build(string? error = null) => new(
                "fixture",
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                fixtureRoot,
                steps,
                error);

            try
            {
                // 安全护栏（与 QaOverview 一致）：夹具只在 QA 证据目录内创建，绝不触碰本机真实受限目录。
                var prefix = Path.GetFullPath(qa.Directory) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(fixtureRoot).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("解除 QA 夹具目录必须位于 QA 证据目录内。");
                if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
                Directory.CreateDirectory(fixtureRoot);

                foreach (var scenario in Scenarios())
                    steps.Add(await DriveAsync(qa, fixtureRoot, scenario));

                QaReports.Write(qa.UnblockReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.UnblockReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    /// <summary>
    /// 一个 QA 场景：夹具初始状态 + 用户的确认选择 + 只读复核的受控结论。
    /// 用带语义的工厂方法构造，避免五个连续布尔的位陷阱。
    /// </summary>
    private sealed record Scenario(
        string Name,
        bool SeedBlock,
        bool Confirm,
        bool RecheckFails,
        bool MutateSettingsDuringRecheck,
        bool MutateLinkDuringRecheck,
        string Intent)
    {
        /// <summary>没有待解除标记：入口前提不成立。</summary>
        public static Scenario NoBlock() => new("no-block", false, true, false, false, false,
            "无待解除标记：不进入确认、不解除、不写文件");

        /// <summary>用户取消确认。</summary>
        public static Scenario Declined() => new("declined", true, false, false, false, false,
            "用户取消确认：只走确认阶段，保留暂停且零写入");

        /// <summary>只读复核不通过（故事 42）。</summary>
        public static Scenario RecheckFailed() => new("recheck-failed", true, true, true, false, false,
            "前置复核未通过：保留暂停并给出受控说明，无审计行");

        /// <summary>诊断期间已保存设置被改写。</summary>
        public static Scenario SettingsChanged() => new("settings-changed", true, true, false, true, false,
            "诊断期间设置变化：中止并保留暂停");

        /// <summary>诊断期间校园 Wi-Fi 事实（网卡索引或 IPv4）变化。</summary>
        public static Scenario LinkChanged() => new("link-changed", true, true, false, false, true,
            "诊断期间校园 Wi-Fi 变化：中止并保留暂停");

        /// <summary>四段序列全部走通的完整路径。</summary>
        public static Scenario Released() => new("released", true, true, false, false, false,
            "成功路径：确认→复核→修订核对→审计后解除，凭据文件始终未创建");
    }

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return Scenario.NoBlock();
        yield return Scenario.Declined();
        yield return Scenario.RecheckFailed();
        yield return Scenario.SettingsChanged();
        yield return Scenario.LinkChanged();
        yield return Scenario.Released();
    }

    private static async Task<QaUnblockStep> DriveAsync(QaEnvironment qa, string fixtureRoot, Scenario scenario)
    {
        // 每个场景一个隔离目录：受限目录语义（安全状态文件 + 审计 jsonl + 设置）都在其中复现。
        var directory = Path.Combine(fixtureRoot, scenario.Name);
        Directory.CreateDirectory(directory);
        var safety = new SafetyStateStore(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var credentialPath = Path.Combine(directory, "credentials.dpapi");
        var blockPath = Path.Combine(directory, "auth-block.txt");
        var auditPath = Path.Combine(directory, "auth-release-audit.jsonl");

        if (scenario.SeedBlock)
            safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
        WriteSettings(settingsPath);

        var settingsBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : [];
        var blockBefore = File.Exists(blockPath);

        // 受控只读复核：走真实 DrcomPortal.DiagnoseAsync（无凭据路径），但 HTTP 由本地夹具应答，
        // 绝不访问真实门户。诊断期间按场景需要改写设置事实，制造修订变化。
        var portalFixture = new ReadOnlyPortalFixture
        {
            RecheckFails = scenario.RecheckFails,
            OnDiagnosed = () =>
            {
                if (scenario.MutateSettingsDuringRecheck)
                    WriteSettings(settingsPath, WifiProfile: "TEST-AP-CHANGED");
            }
        };
        var portal = new DrcomPortal(new HttpClient(portalFixture), new Uri("https://login.csust.edu.cn"));
        string? confirmAnswer = null;

        var linksRead = 0;
        var flow = new UnblockAuthentication(
            () => safety.LoadAuthenticationBlock(),
            () => File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<Settings>(Storage.ReadShared(settingsPath), Storage.Json) : null,
            _ => ReadLink(ref linksRead, scenario),
            (_, _, cancellationToken) => portal.DiagnoseAsync(cancellationToken),
            block => safety.ClearAuthenticationBlock(block),
            async _ =>
            {
                confirmAnswer = scenario.Confirm ? "确认" : "取消";
                if (!scenario.Confirm) return false;
                // 先把真实确认对话框呈现出来取视觉证据（该实例以关闭收场，等同取消），
                // 再由脚本给出本场景的确认答案；流程本身只看到后者。
                await ShowConfirmEvidenceAsync(qa, scenario);
                return true;
            });

        UnblockResult result;
        try
        {
            result = await flow.RunAsync();
        }
        catch (Exception e)
        {
            // 审计写入失败等受控异常也必须保留暂停：如实记录，不谎报成功。
            // 安全边界同生产路径——证据里只写异常类型，不写异常文本。
            result = new UnblockResult(UnblockOutcome.RecheckFailed,
                "解除未完成：" + e.GetType().Name + "（认证暂停保持不变）", []);
        }

        var settingsAfter = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : [];
        var blockAfter = File.Exists(blockPath);
        var auditLines = File.Exists(auditPath)
            ? File.ReadAllLines(auditPath).Count(line => line.Trim().Length > 0) : 0;

        // 复核未通过或修订变化时也捕获受控提示窗口，用户能看到的那段说明必须进证据。
        string? shot = null;
        if (result.RequiresNotice)
        {
            shot = qa.UnblockShotPath("unblock-" + scenario.Name + "-notice");
            await ShowNoticeEvidenceAsync(shot, UnblockAuthentication.HeadingFor(result.Outcome), result.Message);
        }

        return new QaUnblockStep(
            scenario.Name,
            result.Outcome.ToString(),
            UnblockAuthentication.HeadingFor(result.Outcome),
            result.Released,
            result.RequiresNotice,
            result.Stages.Select(stage => stage.ToString()).ToList(),
            confirmAnswer,
            blockBefore,
            blockAfter,
            auditLines > 0,
            auditLines,
            settingsBefore.SequenceEqual(settingsAfter),
            File.Exists(credentialPath),
            PortalRequestPaths: portalFixture.RequestPaths,
            result.Message,
            shot);
    }

    private static WifiLink ReadLink(ref int reads, Scenario scenario)
    {
        reads++;
        if (scenario.MutateLinkDuringRecheck && reads > 1)
            return new(true, null, IPAddress.Parse("10.0.0.99"), 7, []);
        return new(true, null, IPAddress.Parse("10.0.0.1"), 7, []);
    }

    private static void WriteSettings(string path, string WifiProfile = "TEST-AP-1") => Storage.AtomicWrite(path,
        JsonSerializer.Serialize(new Settings
        {
            InterfaceId = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString(),
            WifiProfile = WifiProfile,
            PortalOrigin = "https://login.csust.edu.cn"
        }, Storage.Json));

    private static async Task ShowConfirmEvidenceAsync(QaEnvironment qa, Scenario scenario)
    {
        var shot = qa.UnblockShotPath("unblock-" + scenario.Name + "-confirm");
        var dialog = ConfirmWindow.Create(UnblockAuthentication.ConfirmHeading, UnblockAuthentication.ConfirmBody);
        dialog.Show();
        await Task.Delay(220);
        WindowCapture.Save(dialog, shot);
        // 直接关闭等同取消：证据展示完即按“未确认”收场，随后脚本再以确认答案重跑。
        dialog.Close();
        await Task.Delay(80);
    }

    private static async Task ShowNoticeEvidenceAsync(string path, string heading, string body)
    {
        // 复核失败的说明带受控失败分类摘要，用更宽的窗口留出可读换行。
        var notice = NoticeWindow.Create(heading, body, width: 560);
        notice.Show();
        await Task.Delay(220);
        WindowCapture.Save(notice, path);
        notice.Close();
        await Task.Delay(80);
    }
}
