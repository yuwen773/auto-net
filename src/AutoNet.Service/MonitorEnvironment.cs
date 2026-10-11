using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Service;

// Windows, network and persistence boundaries used by offline monitoring fixtures.
public interface IMonitorEnvironment
{
    Settings? LoadSettings();
    Credentials? LoadCredentials();
    AuthenticationBlock? LoadAuthenticationBlock();
    void BlockAuthentication(AuthenticationBlock block);
    bool TryTransitionAuthentication(AuthenticationBlock? expected, AuthenticationBlock next);
    DisconnectBudget LoadDisconnectBudget(Settings settings);
    void SaveDisconnectBudget(Settings settings, DisconnectBudget budget);
    bool TryTransitionDisconnectBudget(Settings settings, DisconnectBudget expected, DisconnectBudget next);
    WifiLink ReadWifi(Settings settings);
    WifiRequestResult ConnectWifi(Settings settings);
    WifiRequestResult DisconnectWifi(Settings settings);
    HttpClient CreateDirectClient(WifiLink link);
    Task<(string SystemPath, string Uu)> CheckAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct);
    void UpdatePower(bool enabled);
    void WriteStatus(StatusSnapshot status);
    void WriteLog(string action, string detail, double? seconds, StatusSnapshot? snapshot);
}

internal sealed class MonitorEnvironment : IMonitorEnvironment, IDisposable
{
    private readonly PowerGuard power = new();
    private readonly SafetyStateStore safety = new(Storage.Private);
    public Settings? LoadSettings() => Storage.LoadSettings();
    public Credentials? LoadCredentials() => Storage.LoadCredentials();
    public AuthenticationBlock? LoadAuthenticationBlock() => safety.LoadAuthenticationBlock();
    public void BlockAuthentication(AuthenticationBlock block) => safety.BlockAuthentication(block);
    public bool TryTransitionAuthentication(AuthenticationBlock? expected, AuthenticationBlock next) => safety.TryTransitionAuthentication(expected, next);
    public DisconnectBudget LoadDisconnectBudget(Settings settings) => safety.LoadDisconnectBudget(settings);
    public void SaveDisconnectBudget(Settings settings, DisconnectBudget budget) => safety.SaveDisconnectBudget(settings, budget);
    public bool TryTransitionDisconnectBudget(Settings settings, DisconnectBudget expected, DisconnectBudget next) => safety.TryTransitionDisconnectBudget(settings, expected, next);
    public WifiLink ReadWifi(Settings settings) => Wifi.Read(settings);
    public WifiRequestResult ConnectWifi(Settings settings) => Wifi.Connect(settings);
    public WifiRequestResult DisconnectWifi(Settings settings) => Wifi.Disconnect(settings);
    public HttpClient CreateDirectClient(WifiLink link) => DirectNetwork.CreateClient(link);
    public void UpdatePower(bool enabled) => power.Update(enabled);
    public void WriteStatus(StatusSnapshot status) => Storage.AtomicWrite(Storage.StatusPath, JsonSerializer.Serialize(status, Storage.Json));

    public async Task<(string SystemPath, string Uu)> CheckAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct)
    {
        var uu = HealthChecks.UuService();
        var path = await HealthChecks.SystemPathAsync(settings, link, ct);
        if (settings.UuEndpoint != "")
        {
            using var system = HealthChecks.SystemClient(settings.ProxyUrl);
            uu += await HealthChecks.ProbeAsync(system, new Uri(settings.UuEndpoint), false, ct)
                ? "；指定服务可达，会话待实测" : "；指定服务不可达，会话未知";
        }
        return (path, uu);
    }

    public void WriteLog(string action, string detail, double? seconds, StatusSnapshot? snapshot)
    {
        Directory.CreateDirectory(Storage.Logs);
        var now = DateTimeOffset.Now;
        var path = Path.Combine(Storage.Logs, $"autonet-{now:yyyy-MM-dd}.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(ActivityLog.Create(now, action, detail, seconds, snapshot), ActivityLog.Json) + Environment.NewLine);
        foreach (var old in Directory.EnumerateFiles(Storage.Logs, "autonet-????-??-??.jsonl"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14)) File.Delete(old);
    }

    public void Dispose() => power.Dispose();
}
