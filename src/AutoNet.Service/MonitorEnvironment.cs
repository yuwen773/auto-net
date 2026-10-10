using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Service;

// Windows, network and persistence boundaries used by offline monitoring fixtures.
public interface IMonitorEnvironment
{
    Settings? LoadSettings();
    Credentials? LoadCredentials();
    string CredentialFingerprint();
    bool AuthenticationBlocked(string fingerprint);
    void BlockAuthentication(string fingerprint);
    WifiLink ReadWifi(Settings settings);
    void ConnectWifi(Settings settings);
    void DisconnectWifi(Settings settings);
    HttpClient CreateDirectClient(WifiLink link);
    Task<(string SystemPath, string Uu)> CheckAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct);
    void UpdatePower(bool enabled);
    void WriteStatus(StatusSnapshot status);
    void WriteLog(string action, string detail, double? seconds, StatusSnapshot? snapshot);
}

internal sealed class MonitorEnvironment : IMonitorEnvironment, IDisposable
{
    private readonly PowerGuard power = new();
    public Settings? LoadSettings() => Storage.LoadSettings();
    public Credentials? LoadCredentials() => Storage.LoadCredentials();
    public string CredentialFingerprint() => Storage.CredentialFingerprint();
    public bool AuthenticationBlocked(string fingerprint) => File.Exists(Storage.AuthBlockPath) && Storage.ReadShared(Storage.AuthBlockPath).Trim() == fingerprint;
    public void BlockAuthentication(string fingerprint) => Storage.AtomicWrite(Storage.AuthBlockPath, fingerprint);
    public WifiLink ReadWifi(Settings settings) => Wifi.Read(settings);
    public void ConnectWifi(Settings settings) => Wifi.Connect(settings);
    public void DisconnectWifi(Settings settings) => Wifi.Disconnect(settings);
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
