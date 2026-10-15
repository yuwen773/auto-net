using System.Text;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Contains("--diagnose") || args.Contains("--diagnose-portal"))
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var profile = Wifi.Profiles().FirstOrDefault(p => p.Connected && p.AllUsers);
        if (profile is null) { Console.WriteLine("未找到当前连接的所有用户 Wi-Fi 配置；诊断未执行。未修改网络。"); return 2; }
        var settings = new Settings { InterfaceId = profile.InterfaceId, WifiProfile = profile.Name };
        var link = Wifi.Read(settings);
        using var http = link.State == WifiState.Ready ? DirectNetwork.CreateClient(link) : new HttpClient();
        if (args.Contains("--diagnose-portal"))
        {
            if (link.State != WifiState.Ready) { Console.WriteLine("选定 Wi-Fi 的 IPv4 尚未就绪；未发起认证诊断。未修改网络。"); return 2; }
            var attempt = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).DiagnoseAsync(timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new {
                readOnly = true, observedPortalState = attempt.ObservedState.ToString(), stage = attempt.Stage.ToString(),
                failure = attempt.Failure.ToString(), attempt.HttpStatus, attempt.Field, attempt.Flags, attempt.Evidence, attempt.Error,
                credentialRequestStarted = attempt.CredentialRequestStarted, summary = attempt.SafeSummary,
                note = "仅反映本次前置检查；已认证时的页面可能不同，不能回推历史故障。未加载凭据、未清除认证阻断。"
            }, Storage.Json));
            return attempt.Failure == PortalFailure.None ? 0 : 1;
        }
        var report = await NetworkDiagnostics.RunAsync(settings, link, http, await HealthChecks.ReadRouteAsync(link, timeout.Token), timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new {
            report, systemPath = await HealthChecks.SystemPathAsync(settings, link, timeout.Token), uu = HealthChecks.UuService()
        }, Storage.Json));
        return report.DirectOnline == true ? 0 : 1;
    }
    catch (Exception e)
    {
        Console.WriteLine("只读诊断未完成：" + e.GetType().Name + "；未修改网络。");
        return 3;
    }
}

if (args.Contains("--export-diagnostics"))
{
    try
    {
        var index = Array.IndexOf(args, "--export-diagnostics");
        var requested = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await DiagnosticsPackage.ExportAsync(new DiagnosticsPackageOptions { OutputPath = requested }, timeout.Token);
        Console.WriteLine("诊断包已导出：" + result.Path);
        Console.WriteLine("包含 " + result.Entries.Count + " 项：");
        foreach (var entry in result.Entries) Console.WriteLine("  - " + entry);
        foreach (var note in result.Notes) Console.WriteLine("说明：" + note);
        Console.WriteLine("只读导出：未修改网络、未加载凭据、未清除认证阻断；打包内容为采集时所见。");
        return 0;
    }
    catch (Exception e)
    {
        Console.WriteLine("诊断包导出失败：" + e.GetType().Name + "；未修改网络。若无法读取程序数据目录，请以管理员身份运行。");
        return 3;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); // HTTP failures must never log credential-bearing URLs.
builder.Services.AddWindowsService(options => options.ServiceName = "AutoNet");
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
builder.Services.AddSingleton<IMonitorEnvironment, MonitorEnvironment>();
builder.Services.AddSingleton(TimeProvider.System);
// ADR-0001 QA fault injection: with AUTONET_QA_SUBMISSION_LOST=1 the submission request is
// really sent but its response is treated as lost, for offline regression and live drill of
// the recheck resolution; the classification and safety rules are untouched.
builder.Services.AddHostedService(sp => new MonitorWorker(
    sp.GetRequiredService<IMonitorEnvironment>(), sp.GetRequiredService<TimeProvider>(),
    qaSubmissionLost: Environment.GetEnvironmentVariable("AUTONET_QA_SUBMISSION_LOST") == "1"));
await builder.Build().RunAsync();
return 0;
