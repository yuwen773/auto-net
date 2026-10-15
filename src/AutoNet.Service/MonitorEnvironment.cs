using System.Text.Json;
using System.Diagnostics;
using AutoNet.Core;

namespace AutoNet.Service;

// Windows, network and persistence boundaries used by offline monitoring fixtures.
public interface IMonitorEnvironment
{
    Settings? LoadSettings();
    Credentials? LoadCredentials();
    AuthenticationBlock? LoadAuthenticationBlock();
    AuthenticationRecordKind AuthenticationRecordSource => AuthenticationRecordKind.NotRead;
    void BlockAuthentication(AuthenticationBlock block);
    bool TryTransitionAuthentication(AuthenticationBlock? expected, AuthenticationBlock next);
    // ADR-0001 recheck resolution: the release audit is appended before the block clears; an
    // audit write failure surfaces as an exception and the caller must not clear the block.
    bool TryResolveSubmissionPending(AuthenticationBlock? expected, AuthenticationBlock next);
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
    void WriteDiagnostic(ActivityLog entry);
    ActivityLog? ReadLastDiagnostic() => null;
    void WriteDiagnosticFallback(string summary) { }
}

internal sealed class MonitorEnvironment : IMonitorEnvironment, IDisposable
{
    private readonly PowerGuard power = new();
    private readonly SafetyStateStore safety = new(Storage.Private);
    public Settings? LoadSettings() => Storage.LoadSettings();
    public Credentials? LoadCredentials() => Storage.LoadCredentials();
    public AuthenticationBlock? LoadAuthenticationBlock() => safety.LoadAuthenticationBlock();
    public AuthenticationRecordKind AuthenticationRecordSource => safety.LastAuthenticationRecordKind;
    public void BlockAuthentication(AuthenticationBlock block) => safety.BlockAuthentication(block);
    public bool TryTransitionAuthentication(AuthenticationBlock? expected, AuthenticationBlock next) => safety.TryTransitionAuthentication(expected, next);
    public bool TryResolveSubmissionPending(AuthenticationBlock? expected, AuthenticationBlock next) => safety.ResolveSubmissionPending(expected, next);
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
        => WriteDiagnostic(ActivityLog.Create(DateTimeOffset.Now, action, detail, seconds, snapshot));

    public void WriteDiagnostic(ActivityLog entry)
    {
        Directory.CreateDirectory(Storage.Logs);
        var path = Path.Combine(Storage.Logs, $"autonet-{entry.Time.ToLocalTime():yyyy-MM-dd}.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(entry, ActivityLog.Json) + Environment.NewLine);
        foreach (var old in Directory.EnumerateFiles(Storage.Logs, "autonet-????-??-??.jsonl"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14)) File.Delete(old);
    }

    public ActivityLog? ReadLastDiagnostic()
    {
        if (!Directory.Exists(Storage.Logs)) return null;
        foreach (var path in Directory.EnumerateFiles(Storage.Logs, "autonet-????-??-??.jsonl").OrderDescending().Take(2))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - 65536), SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            foreach (var line in reader.ReadToEnd().Split('\n').Reverse())
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ActivityLog>(line, ActivityLog.Json);
                    if (entry?.SchemaVersion == 2 && entry.RunId is not null) return entry;
                }
                catch (JsonException) { }
            }
        }
        return null;
    }

    public void WriteDiagnosticFallback(string summary) => EventLog.WriteEntry("Application", summary, EventLogEntryType.Error, 1101);

    public void Dispose() => power.Dispose();
}
