using System.IO.Compression;
using System.Text.RegularExpressions;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

internal static class DiagnosticsExporterRegression
{
    private static readonly DateTimeOffset Base = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private const string FixtureTarget = "C:\\Users\\fixture\\Desktop\\autonet-diagnostics.zip";
    // 与 DiagnosticsPackageRegression 同源的值形态扫描器：T15 对结果文件与缺口说明复用同一契约。
    private static readonly Regex PrivacyMacShape = new("[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}", RegexOptions.None);
    private static readonly Regex PrivacyCredentialShape =
        new(@"user_password\s*=|user_account\s*=|password\s*=|phpsessid\s*=|set-cookie|cookie\s*[:=]", RegexOptions.IgnoreCase);

    /// <summary>导出流程夹具：可编排的选择/启动委托 + 状态收集。</summary>
    private sealed class Flow
    {
        public string? Picked = "";
        public IOException? PickFailure;
        public DiagnosticsExportLaunch? LaunchResult;
        public Exception? LaunchFailure;
        public readonly List<string> LaunchCalls = [];
        public readonly List<DiagnosticsExportState> States = [];

        public DiagnosticsExporter Build()
        {
            var exporter = new DiagnosticsExporter(
                () =>
                {
                    if (PickFailure is { } failure) throw failure;
                    return Task.FromResult(Picked);
                },
                target =>
                {
                    LaunchCalls.Add(target);
                    if (LaunchFailure is { } failure) throw failure;
                    return Task.FromResult(LaunchResult);
                });
            exporter.StateChanged += () => States.Add(exporter.State);
            return exporter;
        }
    }

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("导出：启动请求解析 --export-diagnostics 目标路径且与其他操作互斥", () =>
        {
            var parsed = StartupRequest.Parse(["--export-diagnostics", @"C:\pkg\diag.zip"]);
            Require(parsed.ElevatedOperation == ElevatedOperationKind.DiagnosticsExport, "应识别导出操作");
            Require(parsed.ExportTargetPath == @"C:\pkg\diag.zip", "目标路径应逐字解析");
            var withTray = StartupRequest.Parse(["--tray", "--export-diagnostics", "relative.zip"]);
            Require(withTray.StartHidden && withTray.ExportTargetPath == "relative.zip", "--tray 可与导出共存且路径原样保留");
            foreach (var invalid in new[]
            {
                new[] { "--export-diagnostics" },
                new[] { "--export-diagnostics", "a.zip", "--settings" },
                new[] { "--export-diagnostics", "a.zip", "--unblock-auth" },
                new[] { "--export-diagnostics", "a.zip", "--export-diagnostics", "b.zip" }
            })
            {
                try
                {
                    StartupRequest.Parse(invalid);
                    throw new Exception("非法参数组合未被拒绝：" + string.Join(" ", invalid));
                }
                catch (ArgumentException) { }
            }

            Require(StartupRequest.Parse([]).ExportTargetPath is null, "无参数时不得臆造导出操作");
            Require(ElevatedOperationKind.DiagnosticsExport.CommandLineArgument() == "--export-diagnostics", "参数名契约");
            return Task.CompletedTask;
        }),
        ("导出：普通界面流程——取消保存不发起提权（故事74）", () =>
        {
            var flow = new Flow { Picked = null };
            var exporter = flow.Build();
            exporter.RunAsync().GetAwaiter().GetResult();
            Require(flow.LaunchCalls.Count == 0, "取消保存不得发起提权");
            Require(exporter.State.Phase == DiagnosticsExportPhase.Cancelled
                && exporter.State.Message.Contains("已取消保存", StringComparison.Ordinal), "取消保存应呈现受控取消文案");
            Require(!exporter.State.Running, "取消后不应处于运行中");
            return Task.CompletedTask;
        }),
        ("导出：目标路径逐字传递，拒绝 UAC 即取消（故事72-74）", () =>
        {
            var flow = new Flow { Picked = FixtureTarget, LaunchResult = null };
            var exporter = flow.Build();
            exporter.RunAsync().GetAwaiter().GetResult();
            Require(flow.LaunchCalls is [var only] && only == FixtureTarget, "提权启动应逐字携带用户所选路径");
            Require(exporter.State.Phase == DiagnosticsExportPhase.Cancelled
                && exporter.State.Message.Contains("已取消管理员授权", StringComparison.Ordinal), "拒绝 UAC 应呈现受控取消文案");
            return Task.CompletedTask;
        }),
        ("导出：成功呈现目标路径与缺口明细（故事80、81）", () =>
        {
            var flow = new Flow
            {
                Picked = FixtureTarget,
                LaunchResult = new DiagnosticsExportLaunch(0, null, ["近 3 天没有日志文件。", "status.json 不存在。"])
            };
            var exporter = flow.Build();
            exporter.RunAsync().GetAwaiter().GetResult();
            Require(exporter.State.Phase == DiagnosticsExportPhase.Succeeded
                && exporter.State.Message.Contains(FixtureTarget, StringComparison.Ordinal), "成功应呈现目标路径");
            Require(exporter.State.NotesList.Count == 2, "缺口明细应逐项呈现");
            Require(exporter.State.Tone == OverviewTone.Good, "成功用语义色标注");
            return Task.CompletedTask;
        }),
        ("导出：失败与异常呈现受控错误且不谎报成功（故事79、81）", () =>
        {
            var failed = new Flow { Picked = "C:\\t.zip", LaunchResult = new DiagnosticsExportLaunch(3, "StubbedExportException", []) };
            var exporter = failed.Build();
            exporter.RunAsync().GetAwaiter().GetResult();
            Require(exporter.State.Phase == DiagnosticsExportPhase.Failed
                && exporter.State.Message.Contains("StubbedExportException", StringComparison.Ordinal)
                && exporter.State.Message.Contains("原有文件保持不变", StringComparison.Ordinal), "失败应披露受控错误类型");

            var launchThrow = new Flow { Picked = "C:\\t.zip", LaunchFailure = new IOException("synthetic") };
            var exporter2 = launchThrow.Build();
            exporter2.RunAsync().GetAwaiter().GetResult();
            Require(exporter2.State.Phase == DiagnosticsExportPhase.Failed
                && exporter2.State.Message.Contains("IOException", StringComparison.Ordinal), "启动异常应披露类型");

            var pickThrow = new Flow { Picked = null, PickFailure = new IOException("synthetic pick") };
            var exporter3 = pickThrow.Build();
            exporter3.RunAsync().GetAwaiter().GetResult();
            Require(exporter3.State.Phase == DiagnosticsExportPhase.Failed
                && exporter3.State.Message.Contains("未生成任何文件", StringComparison.Ordinal), "选择异常不得生成文件");

            // 无缺口成功：明细为空，不得编造说明。
            var clean = new Flow { Picked = "C:\\t.zip", LaunchResult = new DiagnosticsExportLaunch(0, null, []) };
            var exporter4 = clean.Build();
            exporter4.RunAsync().GetAwaiter().GetResult();
            Require(exporter4.State.Phase == DiagnosticsExportPhase.Succeeded && exporter4.State.NotesList.Count == 0, "无缺口成功不列明细");

            // 结果文件未回传：成功但不得谎称无缺口，只引导查看包内 README（故事 80）。
            var unconfirmed = new Flow { Picked = "C:\\t.zip", LaunchResult = new DiagnosticsExportLaunch(0, null, [], DetailsConfirmed: false) };
            var exporter5 = unconfirmed.Build();
            exporter5.RunAsync().GetAwaiter().GetResult();
            Require(exporter5.State.Phase == DiagnosticsExportPhase.Succeeded
                && exporter5.State.Message.Contains("README.txt", StringComparison.Ordinal)
                && exporter5.State.NotesList.Count == 0, "明细未确认时不得谎称无缺口");

            // 等待超时：如实说明未确认完成，不得伪报确定性失败或成功（决策 13）。
            var timedOut = new Flow { Picked = "C:\\t.zip", LaunchResult = new DiagnosticsExportLaunch(int.MinValue, "Timeout", [], DetailsConfirmed: false) };
            var exporter6 = timedOut.Build();
            exporter6.RunAsync().GetAwaiter().GetResult();
            Require(exporter6.State.Phase == DiagnosticsExportPhase.Failed
                && exporter6.State.Message.Contains("以实际文件为准", StringComparison.Ordinal), "超时呈现应说明以实际文件为准");
            return Task.CompletedTask;
        }),
        ("导出：结果文件 roundtrip 与匹配校验（防旧结果冒充本次）", () =>
        {
            var root = Directory.CreateTempSubdirectory("AutoNet-exportflow-").FullName;
            try
            {
                var path = Path.Combine(root, "result.json");
                var outcome = new DiagnosticsExportOutcome(true, @"C:\pkg\diag.zip", null, ["近 3 天没有日志文件。"], Base.ToString("O"));
                outcome.TryWrite(path);
                var read = DiagnosticsExportOutcome.TryRead(path);
                Require(read is { } ok && ok.Ok && ok.TargetPath == outcome.TargetPath && ok.Notes.Count == 1, "结果文件应按原样读回");
                Require(read!.Matches(@"C:\pkg\diag.zip", Base.AddSeconds(-1)), "同目标同刻应匹配");
                Require(!read.Matches(@"C:\pkg\other.zip", Base.AddSeconds(-1)), "不同目标不得采信");
                Require(!read.Matches(@"C:\pkg\diag.zip", Base.AddMinutes(10)), "早于发起时刻的旧结果不得采信");
                File.WriteAllText(path, "{{{ broken");
                Require(DiagnosticsExportOutcome.TryRead(path) is null, "损坏结果应回退为空");
                Require(DiagnosticsExportOutcome.TryRead(Path.Combine(root, "missing.json")) is null, "缺失结果应回退为空");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }),
        ("导出：提权任务成功写结果文件并返回退出码（故事73、77、80）", () =>
        {
            var root = Directory.CreateTempSubdirectory("AutoNet-exportjob-").FullName;
            try
            {
                var target = Path.Combine(root, "pkg.zip");
                var resultPath = Path.Combine(root, "export-result.json");
                var logs = Path.Combine(root, "logs");
                Directory.CreateDirectory(logs);
                File.WriteAllText(Path.Combine(logs, "autonet-2026-10-16.jsonl"), "{\"schemaVersion\":2,\"action\":\"today\"}\n");
                var code = DiagnosticsExportJob.Run(target, resultPath, options => options with
                {
                    LogDirectory = logs,
                    StatusPath = Path.Combine(root, "missing-status.json"),
                    LoadSettings = () => null,
                    Profiles = () => [],
                    Collect = (_, _) => Task.FromResult(new AutoNet.Core.LiveDiagnostics(null, null)),
                    ExportedAt = Base
                });
                Require(code == 0, "离线成功采集应返回退出码 0");
                Require(File.Exists(target), "应产出诊断包");
                Require(Directory.GetFiles(root, "*.tmp").Length == 0, "不得留下临时残留");
                var outcome = DiagnosticsExportOutcome.TryRead(resultPath);
                Require(outcome is { } ok && ok.Ok && ok.TargetPath == target, "结果文件应记录目标路径");
                // README 记录界面导出来源（与 Service CLI 区分）。
                using var zip = System.IO.Compression.ZipFile.OpenRead(target);
                var readme = new StreamReader(zip.GetEntry("README.txt")!.Open()).ReadToEnd();
                Require(readme.Contains("独立提权采集", StringComparison.Ordinal), "README 应记录界面导出来源");
                // 缺失状态文件 → 缺口说明同时进 README 与结果文件（故事 80）。
                Require(outcome!.Notes.Any(n => n.Contains("status.json", StringComparison.Ordinal)), "结果文件应披露状态缺口");
                Require(readme.Contains("status.json", StringComparison.Ordinal), "README 应披露状态缺口");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }),
        ("导出：提权任务失败受控返回退出码 3 且不留半包（故事79）", () =>
        {
            var root = Directory.CreateTempSubdirectory("AutoNet-exportjob-").FullName;
            try
            {
                var target = Path.Combine(root, "pkg.zip");
                const string oldBytes = "previous evidence";
                File.WriteAllText(target, oldBytes);
                var resultPath = Path.Combine(root, "export-result.json");
                // 目标被独占占用 → 最终发布失败：这是“导出失败”的受控模拟。
                using var exclusive = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
                var code = DiagnosticsExportJob.Run(target, resultPath, options => options with
                {
                    LogDirectory = Path.Combine(root, "no-logs"),
                    StatusPath = Path.Combine(root, "missing-status.json"),
                    LoadSettings = () => null,
                    Profiles = () => [],
                    Collect = (_, _) => Task.FromResult(new AutoNet.Core.LiveDiagnostics(null, null)),
                    ExportedAt = Base
                });
                exclusive.Dispose();
                Require(code == 3, "采集发布失败应返回退出码 3");
                Require(File.ReadAllText(target) == oldBytes, "旧包字节应保持不变");
                var outcome = DiagnosticsExportOutcome.TryRead(resultPath);
                Require(outcome is { } bad && !bad.Ok && bad.ErrorKind is { Length: > 0 }, "结果文件应披露受控错误类型");
                // 参数缺失：退出码 2，不产包。
                var missing = DiagnosticsExportJob.Run("  ", Path.Combine(root, "missing-result.json"));
                Require(missing == 2, "空目标应返回退出码 2");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }),
        ("导出：结果文件与缺口说明遵守隐私契约——脏输入零泄漏（故事77、T15）", () =>
        {
            var root = Directory.CreateTempSubdirectory("AutoNet-exportpriv-").FullName;
            try
            {
                // 无设置、无状态、无日志的离线导出：Notes 为最丰富的受控缺口词表输出，
                // 结果文件是界面侧「结果」呈现的全部来源——逐字段扫描敏感形态。
                var target = Path.Combine(root, "pkg.zip");
                var resultPath = Path.Combine(root, "export-result.json");
                var code = DiagnosticsExportJob.Run(target, resultPath, options => options with
                {
                    LogDirectory = Path.Combine(root, "no-logs"),
                    StatusPath = Path.Combine(root, "missing-status.json"),
                    LoadSettings = () => null,
                    Profiles = () => [],
                    Collect = (_, _) => Task.FromResult(new AutoNet.Core.LiveDiagnostics(null, null)),
                    ExportedAt = Base
                });
                Require(code == 0, "离线导出应成功");
                var outcome = DiagnosticsExportOutcome.TryRead(resultPath);
                if (outcome is not { } ok || !ok.Ok) throw new Exception("结果文件应记录成功");
                Require(ok.Notes.Count >= 2, "缺口说明应逐项存在");
                foreach (var note in ok.Notes)
                    Require(!PrivacyMacShape.IsMatch(note) && !PrivacyCredentialShape.IsMatch(note), "缺口说明携带敏感形态：" + note);
                // 结果文件原文（含 TargetPath 与 ErrorKind 字段）整体扫描；受控错误类型只允许类型名。
                var raw = File.ReadAllText(resultPath);
                Require(!PrivacyMacShape.IsMatch(raw) && !PrivacyCredentialShape.IsMatch(raw), "结果文件出现 MAC 或凭据形态");
                Require(!raw.Contains("secret-token-value", StringComparison.Ordinal) && !raw.Contains("user_password", StringComparison.OrdinalIgnoreCase),
                    "结果文件出现 Token 或凭据参数形态");
                // LoadSettings 返回 null 是受控「无设置」信号：绝不回退读真实存储（隐私边界）。
                Require(ok.Notes.Any(n => n.Contains("未找到可用设置", StringComparison.Ordinal)),
                    "受控无设置应产生回退说明而非读取真实存储");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
