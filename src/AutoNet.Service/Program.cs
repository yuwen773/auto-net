using System.Text;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Contains("--diagnose"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var profile = Wifi.Profiles().FirstOrDefault(p => p.Connected && p.AllUsers);
    if (profile is null) { Console.WriteLine("未找到当前连接的所有用户 Wi-Fi 配置。"); return 1; }
    var settings = new Settings { InterfaceId = profile.InterfaceId, WifiProfile = profile.Name };
    var link = Wifi.Read(settings);
    bool direct = false;
    var portal = PortalState.Unknown;
    if (link.Address is not null)
    {
        using var http = DirectNetwork.CreateClient(link);
        direct = await HealthChecks.InternetAsync(http, timeout.Token);
        try { portal = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).InspectAsync(timeout.Token); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException) { }
    }
    Console.WriteLine(JsonSerializer.Serialize(new {
        wifiConnected = link.Connected, hasIpv4 = link.Address is not null, directOnline = direct,
        portal = portal.ToString(), systemPath = await HealthChecks.SystemPathAsync(settings, link, timeout.Token),
        uu = HealthChecks.UuService(), mutations = false
    }, Storage.Json));
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); // HTTP failures must never log credential-bearing URLs.
builder.Services.AddWindowsService(options => options.ServiceName = "AutoNet");
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
builder.Services.AddHostedService<MonitorWorker>();
await builder.Build().RunAsync();
return 0;
