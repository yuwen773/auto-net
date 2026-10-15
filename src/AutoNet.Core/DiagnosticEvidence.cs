using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;

namespace AutoNet.Core;

// Never retain exception messages, response bodies, URLs, addresses or caller supplied text.
public sealed record DiagnosticError(string Type, int HResult, string? HttpError, string? SocketError, int? NativeError, string[] CallSites)
{
    public static DiagnosticError From(Exception exception)
    {
        var chain = new List<Exception>();
        for (var current = exception; current is not null && chain.Count < 5; current = current.InnerException) chain.Add(current);
        var http = chain.OfType<HttpRequestException>().FirstOrDefault();
        var socket = chain.OfType<SocketException>().FirstOrDefault();
        var native = chain.OfType<Win32Exception>().FirstOrDefault();
        var sites = chain.SelectMany(e => new StackTrace(e, false).GetFrames())
            .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.FullName?.StartsWith("AutoNet.", StringComparison.Ordinal) == true)
            .Select(m => m!.DeclaringType!.FullName + "." + m.Name).Distinct().Take(8).ToArray();
        return new(exception.GetType().Name, exception.HResult, http?.HttpRequestError.ToString(),
            socket?.SocketErrorCode.ToString(), native?.NativeErrorCode, sites);
    }
}

/// <summary>
/// ADR-0006 confidence annotation. Controlled values: "Confirmed" (content-verified
/// against a known endpoint) or "Tentative" (reserved for a future loose single-source
/// probe). A string like <see cref="ProbeEvidence.Failure"/> so the structured diagnostic
/// log stays readable without an enum converter; every current probe is Confirmed.
/// </summary>
public sealed record ProbeEvidence(string Target, bool Passed, string Failure, double ElapsedMilliseconds, int? HttpStatus = null, DiagnosticError? Error = null, string Confidence = "Confirmed");
public sealed record InternetEvidence(ProbeEvidence[] Probes)
{
    public bool Online => Probes.Any(p => p.Passed);
}

public sealed record PortalEvidence
{
    public string StatusAddress { get; init; } = "NotChecked";
    public string GatewayAddress { get; init; } = "NotChecked";
    public string HomeAddress { get; init; } = "NotChecked";
    public string AddressSource { get; init; } = "None";
    public string AcAddress { get; init; } = "NotChecked";
    public string MacToken { get; init; } = "NotChecked";
    public bool? RedirectPresent { get; init; }
    public bool? RedirectTrusted { get; init; }
    public string? ResponseShape { get; init; }
    public string? ResponseContentType { get; init; }
    public int? ResponseCharacters { get; init; }
}

public sealed record PortalStep(PortalStage Stage, string Phase, double ElapsedMilliseconds = 0, int? HttpStatus = null, DiagnosticError? Error = null, PortalEvidence? Evidence = null);

public sealed record DiagnosticData
{
    public string? Stage { get; init; }
    public string? Outcome { get; init; }
    public string? Reason { get; init; }
    public string? ActionId { get; init; }
    public int? NativeError { get; init; }
    public int? HttpStatus { get; init; }
    public double? ElapsedMilliseconds { get; init; }
    public DiagnosticError? Error { get; init; }
    public ProbeEvidence[]? Probes { get; init; }
    public PortalAttempt? Authentication { get; init; }
    public PortalEvidence? Parameters { get; init; }
    public string? WifiState { get; init; }
    public bool? HasIpv4 { get; init; }
    public int? DnsServerCount { get; init; }
    public string? Condition { get; init; }
    public bool? Enabled { get; init; }
    public bool? HasCredentials { get; init; }
    public bool? AllowActiveDisconnect { get; init; }
    public string? AuthenticationState { get; init; }
    public string? AuthenticationRecordSource { get; init; }
    public string? AuthenticationFailure { get; init; }
    public int? AuthenticationAttempts { get; init; }
    public int? AuthenticationAttemptMax { get; init; }
    public int? DisconnectAttempts { get; init; }
    public int? DisconnectAttemptMax { get; init; }
    public int? RequiredFailureRounds { get; init; }
    public int? RequiredObservationSeconds { get; init; }
    public string? DisconnectState { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    public int? FailedRounds { get; init; }
    public double? ObservationSeconds { get; init; }
    public string? PreviousRunId { get; init; }
    public string? PreviousIncidentId { get; init; }
    public long? PreviousSequence { get; init; }
    public int? LostEvents { get; init; }
}
