using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoNet.Core;

internal static class DiagnosticsPackageRegression
{
    private static readonly DateTimeOffset FixtureNow = new(2026, 10, 15, 12, 44, 37, TimeSpan.FromHours(8));
    private static readonly DateTime FixtureToday = new(2026, 10, 15);
    // A real MAC never reaches the package; neither do credential-bearing parameter shapes.
    // Value-bearing forms only: README's privacy statement names Cookie/Token/MAC by design, so a
    // bare keyword sweep would flag the contract text itself.
    private static readonly Regex MacShape = new("[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}", RegexOptions.None);
    private static readonly Regex CredentialShape = new(@"user_password\s*=|user_account\s*=|password\s*=|phpsessid\s*=|set-cookie|cookie\s*[:=]", RegexOptions.IgnoreCase);

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("诊断包：设置快照剔除代理与UU地址并保留网卡与配置名", () =>
        {
            var settings = new Settings
            {
                InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "CSUST-Staff",
                PortalOrigin = "https://login.csust.edu.cn", ProxyUrl = "http://127.0.0.1:7897",
                UuEndpoint = "https://uu.example/real-endpoint", Enabled = false, AllowActiveDisconnect = false, KeepAwakeOnAc = false
            };
            var snapshot = DiagnosticsPackage.Snapshot(settings)!;
            Require(!snapshot.Enabled && !snapshot.AllowActiveDisconnect && !snapshot.KeepAwakeOnAc, "快照应保留三个开关取值");
            Require(snapshot.InterfaceId == "00000000-0000-0000-0000-000000000001", "快照应保留网卡 InterfaceId");
            Require(snapshot.WifiProfile == "CSUST-Staff", "快照应保留 Wi-Fi 配置名");
            Require(snapshot.PortalOrigin == "https://login.csust.edu.cn", "快照应保留受校验的门户地址");
            var json = JsonSerializer.Serialize(snapshot, DiagnosticsPackage.PackageJson);
            Require(!json.Contains("7897", StringComparison.Ordinal) && !json.Contains("uu.example", StringComparison.Ordinal), "快照混入代理或 UU 检测地址");
            Require(!json.Contains("ProxyUrl", StringComparison.Ordinal) && !json.Contains("UuEndpoint", StringComparison.Ordinal), "快照保留受排除字段名");
            Require(DiagnosticsPackage.Snapshot(null) is null, "空设置应无快照");
            return Task.CompletedTask;
        }),
        ("诊断包：日志按近3天窗口与文件名格式筛选", () =>
        {
            WithTemp(directory =>
            {
                foreach (var name in new[] { "autonet-2026-10-15.jsonl", "autonet-2026-10-14.jsonl", "autonet-2026-10-13.jsonl",
                    "autonet-2026-10-12.jsonl", "autonet-2026-10-09.jsonl", "autonet-2026-13-45.jsonl", "autonet-2026-10-1.jsonl",
                    "autonet-2026-10-14.txt", "notes.jsonl", "autonet-2026-10-14.jsonl.tmp", "autonet-.jsonl" })
                    File.WriteAllText(Path.Combine(directory, name), name);
                var selected = DiagnosticsPackage.SelectLogs(directory, FixtureToday);
                Require(selected.Count == 3, "窗口内应只剩三天日志，实得 " + selected.Count);
                Require(selected.Select(s => s.EntryName).SequenceEqual(new[]
                {
                    "logs/autonet-2026-10-13.jsonl", "logs/autonet-2026-10-14.jsonl", "logs/autonet-2026-10-15.jsonl"
                }), "窗口内应按日期升序且只含合法文件名");
                Require(DiagnosticsPackage.SelectLogs(Path.Combine(directory, "missing"), FixtureToday).Count == 0, "目录不存在应无日志");
            });
            return Task.CompletedTask;
        }),
        ("诊断包：输出路径默认桌面命名并区分目录与显式文件", () =>
        {
            var desktop = Path.Combine("desktop", "fixture");
            Require(DiagnosticsPackage.ResolveOutputPath(null, desktop, FixtureNow) ==
                Path.Combine(desktop, "autonet-diagnostics-20261015-124437.zip"), "缺省路径应落桌面默认名");
            Require(DiagnosticsPackage.ResolveOutputPath("   ", desktop, FixtureNow) ==
                Path.Combine(desktop, "autonet-diagnostics-20261015-124437.zip"), "空白路径应等同缺省");
            var explicitFile = Path.Combine("out", "diag.zip");
            Require(DiagnosticsPackage.ResolveOutputPath(explicitFile, desktop, FixtureNow) == explicitFile, "显式文件路径应原样使用");
            WithTemp(directory =>
            {
                Require(DiagnosticsPackage.ResolveOutputPath(directory, desktop, FixtureNow) ==
                    Path.Combine(directory, "autonet-diagnostics-20261015-124437.zip"), "已存在目录应接收默认文件名");
                Require(DiagnosticsPackage.ResolveOutputPath(directory + Path.DirectorySeparatorChar, desktop, FixtureNow) ==
                    Path.Combine(directory, "autonet-diagnostics-20261015-124437.zip"), "目录后缀路径应接收默认文件名");
            });
            return Task.CompletedTask;
        }),
        ("诊断包：完整导出包含合同条目且README记录时间构建与内容", () =>
        {
            WithTemp(directory =>
            {
                var logs = Path.Combine(directory, "logs");
                Directory.CreateDirectory(logs);
                File.WriteAllText(Path.Combine(logs, "autonet-2026-10-15.jsonl"), "{\"schemaVersion\":2,\"action\":\"today\"}\n");
                File.WriteAllText(Path.Combine(logs, "autonet-2026-10-14.jsonl"), "{\"schemaVersion\":2,\"action\":\"yesterday\"}\n");
                File.WriteAllText(Path.Combine(logs, "autonet-2026-10-01.jsonl"), "{\"schemaVersion\":2,\"action\":\"old\"}\n");
                var statusPath = Path.Combine(directory, "status.json");
                File.WriteAllText(statusPath, "{\"State\":\"校园网在线\",\"UpdatedAt\":\"2026-10-15T04:44:00Z\"}");
                var settings = new Settings
                {
                    InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "CSUST",
                    ProxyUrl = "http://127.0.0.1:7897", UuEndpoint = "https://uu.example/real-endpoint"
                };
                var report = new NetworkDiagnosticReport
                {
                    WifiConnected = true, WifiState = "Ready", InterfaceIndex = 7, WifiIpv4 = "10.20.30.40",
                    DnsServers = ["10.1.1.1"], DirectOnline = true, Portal = "LoggedIn",
                    Route = new RouteSelection("Wifi", 7, "110.242.68.3"),
                    SocketBinding = "生产客户端使用指定 Wi-Fi 接口和源 IPv4；不等于物理出口证明"
                };
                var portal = new PortalAttempt(LoginResult.Accepted, PortalStage.Status, PortalFailure.None, 200)
                {
                    ObservedState = PortalState.LoggedIn,
                    Evidence = new PortalEvidence { StatusAddress = "Valid", ResponseShape = "Object", ResponseContentType = "Javascript", ResponseCharacters = 42 }
                };
                var output = Path.Combine(directory, "out", "autonet-diagnostics-20261015-124437.zip");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "stale package that must be replaced");
                var result = DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                {
                    OutputPath = output, LogDirectory = logs, StatusPath = statusPath,
                    LoadSettings = () => settings, Collect = (_, _) => Task.FromResult(new LiveDiagnostics(report, portal)),
                    ExportedAt = FixtureNow, BuildId = "test-build-1"
                }, default).GetAwaiter().GetResult();

                Require(result.Path == output, "应导出到指定路径");
                Require(result.Entries.SequenceEqual(new[]
                {
                    "README.txt", "settings-snapshot.json", "net_state.json", "portal_diagnostic.json", "status.json",
                    "logs/autonet-2026-10-14.jsonl", "logs/autonet-2026-10-15.jsonl"
                }), "包内条目与 ADR-0003 合同不符");
                Require(result.Notes.Count == 0, "完整输入不应产生缺口说明");

                using var zip = ZipFile.OpenRead(result.Path);
                string Text(string name) => new StreamReader(zip.GetEntry(name)!.Open()).ReadToEnd();
                var readme = Text("README.txt");
                Require(readme.Contains("2026-10-15 12:44:37 +08:00", StringComparison.Ordinal) && readme.Contains("test-build-1", StringComparison.Ordinal), "README 缺导出时间或构建号");
                Require(readme.Contains("net_state.json", StringComparison.Ordinal) && readme.Contains("logs/autonet-2026-10-14.jsonl", StringComparison.Ordinal), "README 内容清单不完整");
                Require(readme.Contains("阅读指引", StringComparison.Ordinal) && readme.Contains("隐私说明", StringComparison.Ordinal), "README 缺阅读指引或隐私说明");
                var net = JsonSerializer.Deserialize<NetworkDiagnosticReport>(Text("net_state.json"), DiagnosticsPackage.PackageJson)!;
                Require(net.WifiIpv4 == "10.20.30.40", "net_state 应保留采集到的本机 IPv4");
                Require(net.Portal == "LoggedIn" && net.Route.Kind == "Wifi", "net_state 应保留门户状态与路由样本");
                var portalJson = JsonSerializer.Deserialize<PortalAttempt>(Text("portal_diagnostic.json"), DiagnosticsPackage.PackageJson)!;
                Require(portalJson.Stage == PortalStage.Status && portalJson.Failure == PortalFailure.None, "门户诊断应保留阶段与失败分类");
                Require(portalJson.Evidence?.StatusAddress == "Valid", "门户诊断应保留参数存在性证据");
                Require(Text("status.json").Contains("校园网在线", StringComparison.Ordinal), "status.json 应为快照原文");
                Require(Text("logs/autonet-2026-10-15.jsonl").Contains("today", StringComparison.Ordinal), "日志应原样包含");
                var snapshot = JsonSerializer.Deserialize<SettingsSnapshot>(Text("settings-snapshot.json"), DiagnosticsPackage.PackageJson)!;
                Require(snapshot.WifiProfile == "CSUST", "设置快照应保留配置名");
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    var content = reader.ReadToEnd();
                    Require(!content.Contains("7897", StringComparison.Ordinal) && !content.Contains("uu.example", StringComparison.Ordinal),
                        "包内出现代理或 UU 检测地址：" + entry.FullName);
                    Require(!MacShape.IsMatch(content) && !CredentialShape.IsMatch(content), "包内出现 MAC 或凭据形态：" + entry.FullName);
                }
            });
            return Task.CompletedTask;
        }),
        ("诊断包：缺少设置、状态与日志时仍导出并逐项说明缺口", () =>
        {
            foreach (var load in new Func<Settings?>[] { () => null, () => throw new UnauthorizedAccessException("synthetic denial") })
            {
                WithTemp(directory =>
                {
                    var emptyLogs = Path.Combine(directory, "empty-logs");
                    Directory.CreateDirectory(emptyLogs);
                    var result = DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                    {
                        OutputPath = Path.Combine(directory, "pkg.zip"), LogDirectory = emptyLogs,
                        StatusPath = Path.Combine(directory, "missing-status.json"),
                        LoadSettings = load, Profiles = () => Array.Empty<WifiProfile>(),
                        ExportedAt = FixtureNow, BuildId = "test-build-1"
                    }, default).GetAwaiter().GetResult();
                    Require(result.Entries.SequenceEqual(new[] { "README.txt" }), "无可选内容时只应保留说明");
                    Require(result.Notes.Any(n => n.Contains("设置", StringComparison.Ordinal)), "未说明设置缺口");
                    Require(result.Notes.Any(n => n.Contains("status.json", StringComparison.Ordinal)), "未说明状态缺口");
                    Require(result.Notes.Any(n => n.Contains("日志", StringComparison.Ordinal)), "未说明日志缺口");
                    using var zip = ZipFile.OpenRead(result.Path);
                    Require(zip.Entries.Count == 1, "缺口场景不应捏造内容");
                    var readme = new StreamReader(zip.GetEntry("README.txt")!.Open()).ReadToEnd();
                    Require(readme.Contains("缺失项", StringComparison.Ordinal) && readme.Contains("未找到可用设置", StringComparison.Ordinal), "README 未记录缺失项");
                });
            }
            return Task.CompletedTask;
        }),
        ("诊断包：已存设置无效时回退当前连接配置并在包内标注", () =>
        {
            WithTemp(directory =>
            {
                var invalid = new Settings { InterfaceId = "not-a-guid", WifiProfile = "" };
                var profile = new WifiProfile("00000000-0000-0000-0000-000000000002", "WLAN", "CSUST", true, true);
                var seen = new List<Settings>();
                var result = DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                {
                    OutputPath = Path.Combine(directory, "pkg.zip"), LogDirectory = Path.Combine(directory, "no-logs"),
                    StatusPath = Path.Combine(directory, "missing-status.json"),
                    LoadSettings = () => invalid, Profiles = () => new[] { profile },
                    Collect = (s, _) => { seen.Add(s); return Task.FromResult(new LiveDiagnostics(null, null)); },
                    ExportedAt = FixtureNow, BuildId = "test-build-1"
                }, default).GetAwaiter().GetResult();
                Require(seen.Count == 1, "回退后应恰好采集一次");
                Require(seen[0].InterfaceId == "00000000-0000-0000-0000-000000000002" && seen[0].WifiProfile == "CSUST", "回退应使用当前连接配置");
                Require(result.Notes.Any(n => n.Contains("未找到可用的已保存设置", StringComparison.Ordinal)), "未标注回退来源");
                Require(result.Notes.Any(n => n.Contains("net_state.json", StringComparison.Ordinal)), "未说明实时状态缺口");
                Require(result.Entries.SequenceEqual(new[] { "README.txt", "settings-snapshot.json" }), "回退视图仍应留设置快照");
                using var zip = ZipFile.OpenRead(result.Path);
                var snapshot = JsonSerializer.Deserialize<SettingsSnapshot>(
                    new StreamReader(zip.GetEntry("settings-snapshot.json")!.Open()).ReadToEnd(), DiagnosticsPackage.PackageJson)!;
                Require(snapshot.InterfaceId == "00000000-0000-0000-0000-000000000002", "快照应对应回退配置");
            });
            return Task.CompletedTask;
        }),
        ("诊断包：设置文件损坏时降级为回退视图且不中断导出", () =>
        {
            WithTemp(directory =>
            {
                var profile = new WifiProfile("00000000-0000-0000-0000-000000000003", "WLAN", "CSUST", true, true);
                var result = DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                {
                    OutputPath = Path.Combine(directory, "pkg.zip"), LogDirectory = Path.Combine(directory, "no-logs"),
                    StatusPath = Path.Combine(directory, "missing-status.json"),
                    LoadSettings = () => throw new JsonException("synthetic corrupt settings"),
                    Profiles = () => new[] { profile },
                    Collect = (_, _) => Task.FromResult(new LiveDiagnostics(null, null)),
                    ExportedAt = FixtureNow, BuildId = "test-build-1"
                }, default).GetAwaiter().GetResult();
                Require(result.Notes.Any(n => n.Contains("JsonException", StringComparison.Ordinal)), "损坏设置应记录异常类型");
                Require(result.Notes.Any(n => n.Contains("未找到可用的已保存设置", StringComparison.Ordinal)), "损坏设置应回退并标注");
                Require(result.Entries.SequenceEqual(new[] { "README.txt", "settings-snapshot.json" }), "损坏设置不应中断既有证据导出");
                using var zip = ZipFile.OpenRead(result.Path);
                var snapshot = JsonSerializer.Deserialize<SettingsSnapshot>(
                    new StreamReader(zip.GetEntry("settings-snapshot.json")!.Open()).ReadToEnd(), DiagnosticsPackage.PackageJson)!;
                Require(snapshot.InterfaceId == "00000000-0000-0000-0000-000000000003", "快照应对应回退配置");
            });
            return Task.CompletedTask;
        }),
        ("诊断包：采集超时只降级实时内容且仍交付既有证据", () =>
        {
            WithTemp(directory =>
            {
                var logs = Path.Combine(directory, "logs");
                Directory.CreateDirectory(logs);
                File.WriteAllText(Path.Combine(logs, "autonet-2026-10-15.jsonl"), "{\"schemaVersion\":2,\"action\":\"today\"}\n");
                var result = DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                {
                    OutputPath = Path.Combine(directory, "pkg.zip"), LogDirectory = logs,
                    StatusPath = Path.Combine(directory, "missing-status.json"),
                    LoadSettings = () => new Settings { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "CSUST" },
                    Collect = async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(10), token); return new LiveDiagnostics(null, null); },
                    CollectionTimeout = TimeSpan.FromMilliseconds(50),
                    ExportedAt = FixtureNow, BuildId = "test-build-1"
                }, default).GetAwaiter().GetResult();
                Require(result.Notes.Any(n => n.Contains("实时采集超时", StringComparison.Ordinal)), "超时应明确记录");
                Require(result.Entries.SequenceEqual(new[] { "README.txt", "settings-snapshot.json", "logs/autonet-2026-10-15.jsonl" }),
                    "超时不应丢掉既有证据");
                using var zip = ZipFile.OpenRead(result.Path);
                Require(zip.Entries.Count == 3, "超时包应只含说明、快照与日志");
            });
            return Task.CompletedTask;
        }),
        ("诊断包：外部取消不产出半包", () =>
        {
            WithTemp(directory =>
            {
                var settings = new Settings { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "CSUST" };
                var output = Path.Combine(directory, "pkg.zip");
                using var canceled = new System.Threading.CancellationTokenSource();
                canceled.Cancel();
                try
                {
                    DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions
                    {
                        OutputPath = output, LoadSettings = () => settings,
                        Collect = (_, token) => throw new OperationCanceledException(token)
                    }, canceled.Token).GetAwaiter().GetResult();
                    throw new Exception("取消被吞且产出了包");
                }
                catch (OperationCanceledException) { }
                Require(!File.Exists(output), "取消后不得留下半包");
            });
            return Task.CompletedTask;
        })
    ];

    private static void WithTemp(Action<string> run)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoNet-diagpkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { run(directory); }
        finally
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理越界");
            Directory.Delete(target, true);
        }
    }

    private static void Require([DoesNotReturnIf(false)] bool value, string message = "断言不成立")
    {
        if (!value) throw new Exception(message);
    }
}
