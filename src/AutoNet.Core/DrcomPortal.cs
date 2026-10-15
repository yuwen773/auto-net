using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoNet.Core;

public enum PortalState { Unknown, LoggedIn, NeedsLogin }
public enum LoginResult { Accepted, RejectedCredentials, NeedsInteraction, Rejected, Unsupported, RejectedBinding }

// Phase budgets for one portal attempt (ADR-0004): the preflight, the submission review
// chain and the credential request each get their own window, so slow safety I/O can
// neither eat the submission window nor disguise itself as a portal timeout.
public sealed record PortalBudgets(TimeSpan Preflight, TimeSpan Review, TimeSpan Submission)
{
    public static PortalBudgets Default { get; } =
        new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
}

public sealed class DrcomPortal(HttpClient http, Uri origin, Action<PortalStep>? trace = null, PortalBudgets? budgets = null, bool qaSubmissionLost = false)
{
    private PortalEvidence evidence = new();
    private PortalStage responseStage;
    private int? responseStatus;
    private long requestStarted;
    private readonly PortalBudgets budgets = budgets ?? PortalBudgets.Default;

    private async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var type = response.Content.Headers.ContentType?.MediaType;
        evidence = evidence with { ResponseContentType = type switch
        {
            "text/html" => "Html", "application/json" => "Json", "application/javascript" or "text/javascript" => "Javascript",
            "text/plain" => "Text", null => "Missing", _ => "Other"
        } };
        try
        {
            var text = await ReadLimitedAsync(response, ct);
            evidence = evidence with { ResponseCharacters = text.Length };
            trace?.Invoke(new(responseStage, "BodyRead", Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds, responseStatus, Evidence: evidence));
            return text;
        }
        catch (Exception e)
        {
            trace?.Invoke(new(responseStage, "BodyReadFailed", Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds, responseStatus, DiagnosticError.From(e), evidence));
            throw;
        }
    }

    private static string AddressEvidence(string? address) => string.IsNullOrEmpty(address) ? "Missing" : IsUsableClientIpv4(address) ? "Valid" : "Invalid";

    private async Task<HttpResponseMessage> GetAsync(Uri uri, PortalStage stage, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        requestStarted = started;
        responseStage = stage;
        responseStatus = null;
        evidence = evidence with { ResponseContentType = null, ResponseCharacters = null, ResponseShape = null };
        trace?.Invoke(new(stage, "Started"));
        try
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            responseStatus = (int)response.StatusCode;
            trace?.Invoke(new(stage, "HeadersReceived", Stopwatch.GetElapsedTime(started).TotalMilliseconds, (int)response.StatusCode));
            return response;
        }
        catch (Exception e)
        {
            trace?.Invoke(new(stage, "Failed", Stopwatch.GetElapsedTime(started).TotalMilliseconds, Error: DiagnosticError.From(e)));
            throw;
        }
    }
    // Plain HTTP so the campus gateway intercepts it and answers with its redirect.
    private static readonly Uri GatewayProbe = new("http://www.msftconnecttest.com/connecttest.txt");

    // The portal prefixes the account with its carrier tuple; index 0 is the default
    // "校园用户" service listed by the 802 configuration.
    private const string AccountPrefix = ",0,";

    public async Task<PortalState> InspectAsync(CancellationToken ct) => (await ReadStateAsync(ct)).State;

    public async Task<PortalAttempt> InspectDetailedAsync(CancellationToken ct)
    {
        evidence = new(); responseStatus = null;
        PortalAttempt Unknown(PortalFailure failure, Exception? error = null) => new(LoginResult.Rejected, PortalStage.Status, failure, responseStatus)
            { Evidence = evidence, Error = error is null ? null : DiagnosticError.From(error) };
        if (!Settings.IsSupportedPortal(origin)) return Unknown(PortalFailure.InvalidTarget);
        try
        {
            var (state, status, _, shape) = await ReadStateAsync(ct);
            // A 200 whose body no longer carries the result field is a changed response
            // shape (ADR-0005); a recognised shape with an unrecognised state value stays
            // StatusUnknown, which is not a layout signal.
            var failure = status is not null && status != 200 ? PortalFailure.HttpStatus :
                !shape ? PortalFailure.InvalidResponse :
                state == PortalState.Unknown ? PortalFailure.StatusUnknown : PortalFailure.None;
            return new PortalAttempt(failure == PortalFailure.None ? LoginResult.Accepted : LoginResult.Rejected,
                PortalStage.Status, failure, status) { ObservedState = state, Evidence = evidence };
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { return Unknown(PortalFailure.Timeout, e); }
        catch (HttpRequestException e) { return Unknown(e.HttpRequestError == HttpRequestError.SecureConnectionError ? PortalFailure.Tls : PortalFailure.Transport, e); }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return Unknown(PortalFailure.InvalidResponse, e); }
        catch (IOException e) { return Unknown(PortalFailure.Transport, e); }
    }

    // ResultField reports whether the status response kept the known shape (a result
    // property); the state itself may still be an unrecognised value.
    private sealed record PortalStatus(PortalState State, int? HttpStatus, string? ClientIpv4, bool ResultField = true);

    // The status endpoint already reports the client IPv4 as a JSON field on every response,
    // authenticated or not. The home page only carries it while a session is established,
    // so scraping it there fails exactly when login is needed.
    private async Task<PortalStatus> ReadStateAsync(CancellationToken ct)
    {
        if (!Settings.IsSupportedPortal(origin)) return new(PortalState.Unknown, null, null, ResultField: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2000);
        using var response = await GetAsync(new Uri(origin, "/drcom/chkstatus?callback=dr1001&v=" + Random.Shared.Next(100000, 999999)), PortalStage.Status, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK) return new(PortalState.Unknown, (int)response.StatusCode, null, ResultField: false);
        using var json = ParseJsonp(await ReadResponseAsync(response, timeout.Token));
        evidence = evidence with { ResponseShape = json.RootElement.ValueKind.ToString() };
        // No result property means the response shape changed (ADR-0005 layout signal);
        // a present-but-unrecognised value stays a plain unknown state.
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("result", out var result))
            return new(PortalState.Unknown, (int)response.StatusCode, null, ResultField: false);
        var state = result.ToString() switch { "1" => PortalState.LoggedIn, "0" => PortalState.NeedsLogin, _ => PortalState.Unknown };
        var clientIp = json.RootElement.TryGetProperty("v4ip", out var ip) && ip.ValueKind == JsonValueKind.String ? ip.GetString() : null;
        evidence = evidence with { StatusAddress = AddressEvidence(clientIp) };
        return new(state, (int)response.StatusCode, clientIp);
    }

    private static bool IsUsableClientIpv4([NotNullWhen(true)] string? candidate) =>
        IPAddress.TryParse(candidate, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        parsed.ToString() == candidate && !IPAddress.IsLoopback(parsed) && !parsed.Equals(IPAddress.Broadcast) &&
        parsed.GetAddressBytes()[0] is > 0 and < 224;

    // Returns "" when the page carries no address; IsUsableClientIpv4 rejects it like any other
    // non-address, so there is no separate "absent" case to represent.
    private static string ExtractClientIpv4(string page) =>
        Regex.Match(page, @"\bv4ip\s*=\s*['"" ]([^'"" ;]+)").Groups[1].Value;

    public sealed record GatewayContext(string? ClientIpv4, string? AcIp, string? AcName, string? UserMac)
    {
        // Required gateway context; whether it resolves the observed VLAN rejection needs live validation.
        public bool CanSubmit => IsUsableClientIpv4(AcIp) && !string.IsNullOrWhiteSpace(UserMac) &&
            !UserMac.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    }

    // Plain HTTP is redirected by the campus gateway while unauthenticated, and the redirect
    // carries the addressing the portal expects us to log in with (client IP, AC address and
    // name, and the gateway's own MAC token). Production disables redirects, so the header
    // survives to us instead of being followed.
    // Unverified: whether every field is always present, and whether the MAC token is stable
    // per client or regenerated per redirect. Both were only ever seen while authenticated.
    private async Task<(GatewayContext Context, PortalFailure Failure, int HttpStatus)> ReadGatewayAsync(CancellationToken ct)
    {
        // Bounded like the status probe: a gateway that black-holes instead of redirecting
        // must not spend the whole deadline and starve the configuration and login requests.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2000);
        using var response = await GetAsync(GatewayProbe, PortalStage.GatewayRedirect, timeout.Token);
        var empty = new GatewayContext(null, null, null, null);
        var status = (int)response.StatusCode;
        var location = response.Headers.Location;
        evidence = evidence with { RedirectPresent = location is not null, GatewayAddress = "Missing", AcAddress = "Missing", MacToken = "Missing" };
        // A plain probe response carries no context; retain the status/home-page IPv4 fallback.
        if (response.StatusCode == HttpStatusCode.OK && location is null) return (empty, PortalFailure.None, status);
        if (response.StatusCode != HttpStatusCode.Found) return (empty, PortalFailure.HttpStatus, status);
        if (location is null) return (empty, PortalFailure.None, status);
        // Relative targets cannot establish a supported portal destination; do not guess a base.
        if (!location.IsAbsoluteUri || location.Scheme != "https" ||
            !location.Host.Equals("login.csust.edu.cn", StringComparison.OrdinalIgnoreCase) ||
            location.Port != 443 || location.UserInfo != "" || location.Fragment != "" || location.AbsolutePath != "/")
        {
            evidence = evidence with { RedirectTrusted = false };
            return (empty, PortalFailure.InvalidTarget, status);
        }
        evidence = evidence with { RedirectTrusted = true };
        var query = location.Query;
        if (string.IsNullOrEmpty(query)) return (empty, PortalFailure.None, status);
        var values = QueryValues(query);
        if (values is null) return (empty, PortalFailure.InvalidResponse, status);
        var mac = values.GetValueOrDefault("wlanusermac");
        evidence = evidence with
        {
            GatewayAddress = AddressEvidence(values.GetValueOrDefault("wlanuserip")), AcAddress = AddressEvidence(values.GetValueOrDefault("wlanacip")),
            MacToken = string.IsNullOrEmpty(mac) ? "Missing" : mac.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? "Invalid" : "Present"
        };
        return (new(values.GetValueOrDefault("wlanuserip"), values.GetValueOrDefault("wlanacip"),
            values.GetValueOrDefault("wlanacname"), values.GetValueOrDefault("wlanusermac")), PortalFailure.None, status);
    }

    // Split and unescape the query once rather than once per field.
    private static Dictionary<string, string>? QueryValues(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            if (key is not ("wlanuserip" or "wlanacip" or "wlanacname" or "wlanusermac")) continue;
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
            if (!values.TryAdd(key, value)) return null;
        }
        return values;
    }

    public async Task<LoginResult> LoginAsync(Credentials credentials, CancellationToken ct) =>
        (await LoginDetailedAsync(credentials, ct)).Result;

    public Task<PortalAttempt> LoginDetailedAsync(Credentials credentials, CancellationToken ct, Func<CancellationToken, bool>? authorizeSubmission = null) => ExecuteAsync(credentials, ct, authorizeSubmission);

    // Reads the same prerequisites as login; this path has no credentials and cannot submit them.
    public Task<PortalAttempt> DiagnoseAsync(CancellationToken ct) => ExecuteAsync(null, ct);

    private async Task<PortalAttempt> ExecuteAsync(Credentials? credentials, CancellationToken ct, Func<CancellationToken, bool>? authorizeSubmission = null)
    {
        evidence = new(); responseStatus = null;
        var stage = PortalStage.Unknown;
        var state = PortalState.Unknown;
        var submitted = false;
        Dictionary<string, string>? flags = null;
        DiagnosticError? error = null;
        PortalAttempt Outcome(LoginResult result, PortalFailure failure, int? status = null, string? field = null) =>
            new(result, stage, failure, status ?? (responseStage == stage ? responseStatus : null), field, submitted) { ObservedState = state, Flags = flags, Evidence = evidence, Error = error };
        if (!Settings.IsSupportedPortal(origin)) return Outcome(LoginResult.Unsupported, PortalFailure.InvalidTarget);
        // Phase budgets (ADR-0004): preflight, review chain and submission each get their own
        // window. A shared deadline let slow safety I/O eat the submission window and made
        // every overrun look like a submission timeout.
        using var preflightBudget = new CancellationTokenSource(budgets.Preflight);
        using var preflight = CancellationTokenSource.CreateLinkedTokenSource(ct, preflightBudget.Token);
        var requestToken = preflight.Token;
        try
        {
            stage = PortalStage.Status;
            var inspection = await ReadStateAsync(requestToken);
            state = inspection.State;
            if (credentials is not null && state != PortalState.NeedsLogin)
            {
                // A 200 whose body lost the result field is a changed response shape
                // (ADR-0005): pause like the other structural failures instead of retrying
                // into it. A recognised shape with an unrecognised value stays StatusUnknown,
                // and a non-200 keeps the pre-existing status-unknown classification.
                var shapeChanged = inspection.HttpStatus == 200 && !inspection.ResultField;
                return Outcome(shapeChanged ? LoginResult.Unsupported : LoginResult.Rejected,
                    state == PortalState.LoggedIn ? PortalFailure.AlreadyAuthenticated :
                    shapeChanged ? PortalFailure.InvalidResponse : PortalFailure.StatusUnknown, inspection.HttpStatus);
            }
            // The read-only path must not report a complete preflight when the status
            // response itself is unrecognisable (ADR-0005); an already-logged-in portal
            // still completes, because there is nothing to submit.
            if (credentials is null && state == PortalState.Unknown)
            {
                var shapeChanged = inspection.HttpStatus == 200 && !inspection.ResultField;
                return Outcome(shapeChanged ? LoginResult.Unsupported : LoginResult.Rejected,
                    shapeChanged ? PortalFailure.InvalidResponse : PortalFailure.StatusUnknown, inspection.HttpStatus);
            }

            // Transport and timeout failures here fall through to the same classification as
            // the other stages rather than being caught locally.
            stage = PortalStage.GatewayRedirect;
            var gatewayRead = await ReadGatewayAsync(requestToken);
            if (gatewayRead.Failure != PortalFailure.None)
                return Outcome(LoginResult.Rejected, gatewayRead.Failure, gatewayRead.HttpStatus);
            var gateway = gatewayRead.Context;

            string? clientIp = gateway.ClientIpv4;
            if (IsUsableClientIpv4(clientIp)) evidence = evidence with { AddressSource = "GatewayRedirect" };
            if (!IsUsableClientIpv4(clientIp))
            {
                clientIp = inspection.ClientIpv4;
                if (IsUsableClientIpv4(clientIp)) evidence = evidence with { AddressSource = "StatusResponse" };
            }
            if (!IsUsableClientIpv4(clientIp))
            {
                // Only a fallback: the home page omits v4ip until a session exists.
                stage = PortalStage.HomePage;
                using var pageResponse = await GetAsync(new Uri(origin, "/"), PortalStage.HomePage, requestToken);
                if (pageResponse.StatusCode != HttpStatusCode.OK)
                    return Outcome(HttpFailureResult(pageResponse.StatusCode), PortalFailure.HttpStatus, (int)pageResponse.StatusCode);
                clientIp = ExtractClientIpv4(await ReadResponseAsync(pageResponse, requestToken));
                evidence = evidence with { HomeAddress = AddressEvidence(clientIp), AddressSource = IsUsableClientIpv4(clientIp) ? "HomePage" : "None" };
            }
            stage = PortalStage.ClientIpv4;
            if (!IsUsableClientIpv4(clientIp))
                return Outcome(LoginResult.Rejected, PortalFailure.MissingClientIpv4);
            var configQuery = new Dictionary<string, string>
            {
                ["callback"] = "dr1001", ["jsVersion"] = "4.X", ["v"] = Random.Shared.Next(100000, 999999).ToString(), ["program_index"] = "", ["wlan_vlan_id"] = "1",
                ["wlan_user_ip"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientIp)),
                ["wlan_user_ipv6"] = "", ["wlan_user_ssid"] = "", ["wlan_user_areaid"] = "",
                ["wlan_ac_ip"] = "", ["wlan_ap_mac"] = "000000000000", ["gw_id"] = "000000000000"
            };
            var configUri = new UriBuilder(origin) { Port = 802, Path = "/eportal/portal/page/loadConfig",
                Query = EncodeQuery(configQuery) };
            stage = PortalStage.Configuration;
            // Bounded like the status and gateway reads: a hung configuration request must
            // not spend the whole preflight phase.
            using var configTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            configTimeout.CancelAfter(2000);
            using var configResponse = await GetAsync(configUri.Uri, PortalStage.Configuration, configTimeout.Token);
            if (configResponse.StatusCode != HttpStatusCode.OK)
                return Outcome(HttpFailureResult(configResponse.StatusCode), PortalFailure.HttpStatus, (int)configResponse.StatusCode);
            using var config = ParseJsonp(await ReadResponseAsync(configResponse, configTimeout.Token));
            evidence = evidence with { ResponseShape = config.RootElement.ValueKind.ToString() };
            if (config.RootElement.ValueKind != JsonValueKind.Object || config.RootElement.EnumerateObject().Count(p => p.Name == "data") != 1 ||
                !config.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Outcome(LoginResult.Unsupported, PortalFailure.MalformedConfiguration);
            stage = PortalStage.AuthenticationMode;
            flags = new Dictionary<string, string>();
            PortalFailure firstFailure = PortalFailure.None;
            string? firstField = null;
            foreach (var name in new[] { "en_md5", "password_cut", "enable_r3" })
            {
                var failure = CheckFlag(data, name);
                flags[name] = failure == PortalFailure.None ? "Zero" : failure.ToString();
                if (firstFailure == PortalFailure.None && failure != PortalFailure.None) { firstFailure = failure; firstField = name; }
            }
            if (firstFailure != PortalFailure.None) return Outcome(LoginResult.Unsupported, firstFailure, field: firstField);

            // The preflight budget must still be alive before the review and submission
            // phases start. Checked here — before the stage is re-signed to Submission — an
            // expiry is classified at the last preflight stage instead of disguising itself
            // as a submission timeout.
            requestToken.ThrowIfCancellationRequested();
            stage = PortalStage.Submission;
            // Without the gateway's MAC token the portal answers "只能绑定指定的VLAN设备", so
            // refuse rather than submit a request that cannot succeed. Reported as Rejected,
            // not Unsupported: Unsupported would set CredentialsBlocked and tell the user to
            // re-enter a working password.
            if (!gateway.CanSubmit)
                return Outcome(LoginResult.Rejected, PortalFailure.MissingGatewayParameters);
            if (credentials is null) { stage = PortalStage.Complete; return Outcome(LoginResult.Accepted, PortalFailure.None); }
            if (authorizeSubmission is not null)
            {
                // The review chain runs between steps only: a file or WLAN read that blocks
                // cannot be interrupted, but every step boundary checks the chain's own
                // budget. Expiry is a safe refusal, never a submission and never a portal
                // timeout, so the log tells "the chain was too slow" from "the portal never
                // answered".
                using var reviewBudget = new CancellationTokenSource(budgets.Review);
                using var review = CancellationTokenSource.CreateLinkedTokenSource(ct, reviewBudget.Token);
                bool authorized;
                try { authorized = authorizeSubmission(review.Token); }
                catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
                {
                    error = DiagnosticError.From(e);
                    return Outcome(LoginResult.Unsupported, PortalFailure.SafetyPersistence);
                }
                if (!authorized) return Outcome(LoginResult.Unsupported, PortalFailure.SafetyPersistence);
            }
            // The credential request itself gets a fresh window, whatever the preflight and
            // the review chain spent; caller cancellation still aborts before anything is sent.
            using var submissionBudget = new CancellationTokenSource(budgets.Submission);
            using var submission = CancellationTokenSource.CreateLinkedTokenSource(ct, submissionBudget.Token);
            submission.Token.ThrowIfCancellationRequested();

            // Production transport disables redirects, so this credential-bearing request is
            // the only one that leaves the machine and it is never followed anywhere else.
            var loginUri = new UriBuilder(origin)
            {
                Port = 802, Path = "/eportal/portal/login",
                Query = BuildLoginQuery(credentials, clientIp, gateway)
            };
            submitted = true;
            using var response = await GetAsync(loginUri.Uri, PortalStage.Submission, submission.Token);
            // QA fault injection (ADR-0001): the request really left the machine; discarding the
            // response recreates "sent but lost" so the read-only recheck path can be exercised
            // offline and induced on a live machine without touching the safety classification.
            if (qaSubmissionLost) throw new IOException("QA 故障注入：提交请求已发出，模拟响应丢失。");
            if (response.StatusCode != HttpStatusCode.OK)
                return Outcome(HttpFailureResult(response.StatusCode), PortalFailure.HttpStatus, (int)response.StatusCode);
            using var json = ParseJsonp(await ReadResponseAsync(response, submission.Token));
            evidence = evidence with { ResponseShape = json.RootElement.ValueKind.ToString() };
            var result = Classify(json.RootElement);
            var outcome = Outcome(result, result switch
            {
                LoginResult.Accepted => PortalFailure.None,
                LoginResult.RejectedCredentials => PortalFailure.CredentialsRejected,
                LoginResult.NeedsInteraction => PortalFailure.InteractionRequired,
                LoginResult.RejectedBinding => PortalFailure.BindingRejected,
                _ => PortalFailure.LoginRejected
            });
            // Only an explicit temporary refusal grants a new submission. A lost/invalid
            // response after sending credentials leaves SubmissionPending for manual review.
            var temporary = result == LoginResult.Rejected && json.RootElement.TryGetProperty("result", out var rejected) && rejected.ToString() == "0" &&
                new[] { "msg", "Msg", "message" }.Any(key => json.RootElement.TryGetProperty(key, out var message) &&
                    message.ValueKind == JsonValueKind.String && new[] { "ldap timeout", "server busy", "temporarily unavailable" }
                        .Any(text => message.GetString()!.Equals(text, StringComparison.OrdinalIgnoreCase)));
            return outcome with { RetryPermitted = temporary };
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { error = DiagnosticError.From(e); return Outcome(LoginResult.Rejected, PortalFailure.Timeout); }
        catch (HttpRequestException e)
        {
            var tls = e.HttpRequestError == HttpRequestError.SecureConnectionError;
            error = DiagnosticError.From(e);
            return Outcome(tls ? LoginResult.Unsupported : LoginResult.Rejected, tls ? PortalFailure.Tls : PortalFailure.Transport);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException) { error = DiagnosticError.From(e); return Outcome(LoginResult.Unsupported, PortalFailure.InvalidResponse); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { error = DiagnosticError.From(e); return Outcome(LoginResult.Unsupported, PortalFailure.InvalidResponse); }
        catch (IOException e) { error = DiagnosticError.From(e); return Outcome(LoginResult.Rejected, PortalFailure.Transport); }
    }

    private static LoginResult HttpFailureResult(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            ? LoginResult.Rejected : LoginResult.Unsupported;

    private static PortalFailure CheckFlag(JsonElement data, string name)
    {
        var count = data.EnumerateObject().Count(p => p.Name == name);
        if (count == 0) return PortalFailure.MissingFlag;
        if (count != 1) return PortalFailure.DuplicateFlag;
        var value = data.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out var number) ? number == 0 ? PortalFailure.None : PortalFailure.UnsupportedFlag : PortalFailure.InvalidFlagType;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() == "0" ? PortalFailure.None : PortalFailure.UnsupportedFlag;
        return PortalFailure.InvalidFlagType;
    }

    // Parameter set observed on the portal's own login request (port 802, /eportal/portal/login).
    // Unverified: the ",0," account prefix, the MAC token semantics, and whether the portal needs
    // the PHPSESSID cookie the browser carried — none could be confirmed without submitting a
    // real password, so none of it is asserted as verified.
    public static string BuildLoginQuery(Credentials credentials, string clientIpv4, GatewayContext gateway) =>
        EncodeQuery(new Dictionary<string, string>
        {
            ["callback"] = "dr1003", ["login_method"] = "1",
            ["user_account"] = AccountPrefix + credentials.Username, ["user_password"] = credentials.Password,
            ["wlan_user_ip"] = clientIpv4, ["wlan_user_ipv6"] = "",
            ["wlan_user_mac"] = gateway.UserMac ?? "", ["wlan_ac_ip"] = gateway.AcIp ?? "",
            ["wlan_ac_name"] = gateway.AcName ?? "", ["jsVersion"] = "4.2.1",
            ["terminal_type"] = "1", ["lang"] = "zh-cn", ["v"] = Random.Shared.Next(1000, 9999).ToString()
        });

    private static string EncodeQuery(Dictionary<string, string> parameters) =>
        string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public static LoginResult Classify(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return LoginResult.Rejected;
        if (json.TryGetProperty("result", out var result) && result.ToString() is "1" or "ok") return LoginResult.Accepted;
        var message = string.Join(' ', new[] { "msg", "Msg", "message" }.Select(key =>
            json.TryGetProperty(key, out var value) ? value.ToString() : "")).ToLowerInvariant();
        var compact = Regex.Replace(message, @"\s+", "");
        if (new[] { "只能绑定指定的vlan设备", "只能绑定指定的mac设备", "只能绑定指定的ac设备" }.Any(compact.Contains))
            return LoginResult.RejectedBinding;
        // Error strings verified against the campus portal's public a78.js. Do not classify generic result=0 as bad credentials.
        if (new[] { "userid error1", "userid error2", "userid error3", "auth error99", "auth error198", "auth error199",
            "密码错误", "账号不存在", "帐号不存在", "用户名或密码错误", "invalid password", "password error" }.Any(message.Contains))
            return LoginResult.RejectedCredentials;
        if (message.Contains("captcha") || message.Contains("验证码")) return LoginResult.NeedsInteraction;
        return LoginResult.Rejected;
    }

    public static JsonDocument ParseJsonp(string content)
    {
        content = content.Trim().TrimStart('\uFEFF');
        if (content.StartsWith('{')) return JsonDocument.Parse(content);
        var match = Regex.Match(content, @"\A[A-Za-z_$][\w$]*\s*\(\s*(\{[\s\S]*\})\s*\)\s*;?\s*\z");
        if (!match.Success) throw new InvalidDataException("Unrecognized portal response.");
        return JsonDocument.Parse(match.Groups[1].Value);
    }

    public static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 512 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response too large.");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + read > limit) throw new InvalidDataException("Response too large.");
            bytes.Write(buffer, 0, read);
        }
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8";
        return Encoding.GetEncoding(charset).GetString(bytes.ToArray());
    }
}
