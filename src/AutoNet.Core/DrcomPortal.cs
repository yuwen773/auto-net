using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoNet.Core;

public enum PortalState { Unknown, LoggedIn, NeedsLogin }
public enum LoginResult { Accepted, RejectedCredentials, NeedsInteraction, Rejected, Unsupported }

public sealed class DrcomPortal(HttpClient http, Uri origin)
{
    // Plain HTTP so the campus gateway intercepts it and answers with its redirect.
    private static readonly Uri GatewayProbe = new("http://www.msftconnecttest.com/connecttest.txt");

    // The portal prefixes the account with its carrier tuple; index 0 is the default
    // "校园用户" service listed by the 802 configuration.
    private const string AccountPrefix = ",0,";

    public async Task<PortalState> InspectAsync(CancellationToken ct) => (await ReadStateAsync(ct)).State;

    public async Task<PortalAttempt> InspectDetailedAsync(CancellationToken ct)
    {
        PortalAttempt Unknown(PortalFailure failure) => new(LoginResult.Rejected, PortalStage.Status, failure);
        if (!Settings.IsSupportedPortal(origin)) return Unknown(PortalFailure.InvalidTarget);
        try
        {
            var (state, status, _) = await ReadStateAsync(ct);
            var failure = status is not null && status != 200 ? PortalFailure.HttpStatus :
                state == PortalState.Unknown ? PortalFailure.StatusUnknown : PortalFailure.None;
            return new PortalAttempt(failure == PortalFailure.None ? LoginResult.Accepted : LoginResult.Rejected,
                PortalStage.Status, failure, status) { ObservedState = state };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unknown(PortalFailure.Timeout); }
        catch (HttpRequestException e) { return Unknown(e.HttpRequestError == HttpRequestError.SecureConnectionError ? PortalFailure.Tls : PortalFailure.Transport); }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return Unknown(PortalFailure.InvalidResponse); }
        catch (IOException) { return Unknown(PortalFailure.Transport); }
    }

    private sealed record PortalStatus(PortalState State, int? HttpStatus, string? ClientIpv4);

    // The status endpoint already reports the client IPv4 as a JSON field on every response,
    // authenticated or not. The home page only carries it while a session is established,
    // so scraping it there fails exactly when login is needed.
    private async Task<PortalStatus> ReadStateAsync(CancellationToken ct)
    {
        if (!Settings.IsSupportedPortal(origin)) return new(PortalState.Unknown, null, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2000);
        using var response = await http.GetAsync(new Uri(origin, "/drcom/chkstatus?callback=dr1001&v=" + Random.Shared.Next(100000, 999999)), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK) return new(PortalState.Unknown, (int)response.StatusCode, null);
        using var json = ParseJsonp(await ReadLimitedAsync(response, timeout.Token));
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("result", out var result))
            return new(PortalState.Unknown, (int)response.StatusCode, null);
        var state = result.ToString() switch { "1" => PortalState.LoggedIn, "0" => PortalState.NeedsLogin, _ => PortalState.Unknown };
        var clientIp = json.RootElement.TryGetProperty("v4ip", out var ip) && ip.ValueKind == JsonValueKind.String ? ip.GetString() : null;
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
        // The MAC token and AC address are what make a submission succeed; without them the portal
        // answers "只能绑定指定的VLAN设备". The invariant belongs to the type so no caller can skip it.
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
        using var response = await http.GetAsync(GatewayProbe, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var empty = new GatewayContext(null, null, null, null);
        var status = (int)response.StatusCode;
        var location = response.Headers.Location;
        // A plain probe response carries no context; retain the status/home-page IPv4 fallback.
        if (response.StatusCode == HttpStatusCode.OK && location is null) return (empty, PortalFailure.None, status);
        if (response.StatusCode != HttpStatusCode.Found) return (empty, PortalFailure.HttpStatus, status);
        if (location is null) return (empty, PortalFailure.None, status);
        // Relative targets cannot establish a supported portal destination; do not guess a base.
        if (!location.IsAbsoluteUri || location.Scheme != "https" ||
            !location.Host.Equals("login.csust.edu.cn", StringComparison.OrdinalIgnoreCase) ||
            location.Port != 443 || location.UserInfo != "" || location.Fragment != "" || location.AbsolutePath != "/")
            return (empty, PortalFailure.InvalidTarget, status);
        var query = location.Query;
        if (string.IsNullOrEmpty(query)) return (empty, PortalFailure.None, status);
        var values = QueryValues(query);
        if (values is null) return (empty, PortalFailure.InvalidResponse, status);
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

    public Task<PortalAttempt> LoginDetailedAsync(Credentials credentials, CancellationToken ct) => ExecuteAsync(credentials, ct);

    // Reads the same prerequisites as login; this path has no credentials and cannot submit them.
    public Task<PortalAttempt> DiagnoseAsync(CancellationToken ct) => ExecuteAsync(null, ct);

    private async Task<PortalAttempt> ExecuteAsync(Credentials? credentials, CancellationToken ct)
    {
        var stage = PortalStage.Unknown;
        var state = PortalState.Unknown;
        var submitted = false;
        Dictionary<string, string>? flags = null;
        PortalAttempt Outcome(LoginResult result, PortalFailure failure, int? status = null, string? field = null) =>
            new(result, stage, failure, status, field, submitted) { ObservedState = state, Flags = flags };
        if (!Settings.IsSupportedPortal(origin)) return Outcome(LoginResult.Unsupported, PortalFailure.InvalidTarget);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var requestToken = linked.Token;
        try
        {
            stage = PortalStage.Status;
            var inspection = await ReadStateAsync(requestToken);
            state = inspection.State;
            if (credentials is not null && state != PortalState.NeedsLogin)
                return Outcome(LoginResult.Rejected, state == PortalState.LoggedIn ? PortalFailure.AlreadyAuthenticated : PortalFailure.StatusUnknown, inspection.HttpStatus);

            // Transport and timeout failures here fall through to the same classification as
            // the other stages rather than being caught locally.
            stage = PortalStage.GatewayRedirect;
            var gatewayRead = await ReadGatewayAsync(requestToken);
            if (gatewayRead.Failure != PortalFailure.None)
                return Outcome(LoginResult.Rejected, gatewayRead.Failure, gatewayRead.HttpStatus);
            var gateway = gatewayRead.Context;

            string? clientIp = gateway.ClientIpv4;
            if (!IsUsableClientIpv4(clientIp)) clientIp = inspection.ClientIpv4;
            if (!IsUsableClientIpv4(clientIp))
            {
                // Only a fallback: the home page omits v4ip until a session exists.
                stage = PortalStage.HomePage;
                using var pageResponse = await http.GetAsync(new Uri(origin, "/"), HttpCompletionOption.ResponseHeadersRead, requestToken);
                if (pageResponse.StatusCode != HttpStatusCode.OK)
                    return Outcome(HttpFailureResult(pageResponse.StatusCode), PortalFailure.HttpStatus, (int)pageResponse.StatusCode);
                clientIp = ExtractClientIpv4(await ReadLimitedAsync(pageResponse, requestToken));
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
            using var configResponse = await http.GetAsync(configUri.Uri, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (configResponse.StatusCode != HttpStatusCode.OK)
                return Outcome(HttpFailureResult(configResponse.StatusCode), PortalFailure.HttpStatus, (int)configResponse.StatusCode);
            using var config = ParseJsonp(await ReadLimitedAsync(configResponse, requestToken));
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

            stage = PortalStage.Submission;
            // Without the gateway's MAC token the portal answers "只能绑定指定的VLAN设备", so
            // refuse rather than submit a request that cannot succeed. Reported as Rejected,
            // not Unsupported: Unsupported would set CredentialsBlocked and tell the user to
            // re-enter a working password.
            if (!gateway.CanSubmit)
                return Outcome(LoginResult.Rejected, PortalFailure.MissingGatewayParameters);
            if (credentials is null) { stage = PortalStage.Complete; return Outcome(LoginResult.Accepted, PortalFailure.None); }

            // Production transport disables redirects, so this credential-bearing request is
            // the only one that leaves the machine and it is never followed anywhere else.
            var loginUri = new UriBuilder(origin)
            {
                Port = 802, Path = "/eportal/portal/login",
                Query = BuildLoginQuery(credentials, clientIp, gateway)
            };
            submitted = true;
            using var response = await http.GetAsync(loginUri.Uri, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return Outcome(HttpFailureResult(response.StatusCode), PortalFailure.HttpStatus, (int)response.StatusCode);
            using var json = ParseJsonp(await ReadLimitedAsync(response, requestToken));
            var result = Classify(json.RootElement);
            return Outcome(result, result switch
            {
                LoginResult.Accepted => PortalFailure.None,
                LoginResult.RejectedCredentials => PortalFailure.CredentialsRejected,
                LoginResult.NeedsInteraction => PortalFailure.InteractionRequired,
                _ => PortalFailure.LoginRejected
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Outcome(LoginResult.Rejected, PortalFailure.Timeout); }
        catch (HttpRequestException e)
        {
            var tls = e.HttpRequestError == HttpRequestError.SecureConnectionError;
            return Outcome(tls ? LoginResult.Unsupported : LoginResult.Rejected, tls ? PortalFailure.Tls : PortalFailure.Transport);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return Outcome(LoginResult.Unsupported, PortalFailure.InvalidResponse); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return Outcome(LoginResult.Unsupported, PortalFailure.InvalidResponse); }
        catch (IOException) { return Outcome(LoginResult.Rejected, PortalFailure.Transport); }
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
