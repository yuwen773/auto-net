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

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); // HTTP failures must never log credential-bearing URLs.
builder.Services.AddWindowsService(options => options.ServiceName = "AutoNet");
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
builder.Services.AddSingleton<IMonitorEnvironment, MonitorEnvironment>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<MonitorWorker>();
await builder.Build().RunAsync();
return 0;
