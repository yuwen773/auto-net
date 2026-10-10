using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoNet.Core;

public enum PortalState { Unknown, LoggedIn, NeedsLogin }
public enum LoginResult { Accepted, RejectedCredentials, NeedsInteraction, Rejected, Unsupported }

public sealed class DrcomPortal(HttpClient http, Uri origin)
{
    public async Task<PortalState> InspectAsync(CancellationToken ct) => (await ReadStateAsync(ct)).State;

    public async Task<PortalAttempt> InspectDetailedAsync(CancellationToken ct)
    {
        PortalAttempt Unknown(PortalFailure failure) => new(LoginResult.Rejected, PortalStage.Status, failure);
        if (!Settings.IsSupportedPortal(origin)) return Unknown(PortalFailure.InvalidTarget);
        try
        {
            var (state, status) = await ReadStateAsync(ct);
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

    private async Task<(PortalState State, int? HttpStatus)> ReadStateAsync(CancellationToken ct)
    {
        if (!Settings.IsSupportedPortal(origin)) return (PortalState.Unknown, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2000);
        using var response = await http.GetAsync(new Uri(origin, "/drcom/chkstatus?callback=dr1001&v=" + Random.Shared.Next(100000, 999999)), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK) return (PortalState.Unknown, (int)response.StatusCode);
        using var json = ParseJsonp(await ReadLimitedAsync(response, timeout.Token));
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("result", out var result))
            return (PortalState.Unknown, (int)response.StatusCode);
        var state = result.ToString() switch { "1" => PortalState.LoggedIn, "0" => PortalState.NeedsLogin, _ => PortalState.Unknown };
        return (state, (int)response.StatusCode);
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

            stage = PortalStage.HomePage;
            using var pageResponse = await http.GetAsync(new Uri(origin, "/"), HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (pageResponse.StatusCode != HttpStatusCode.OK)
                return Outcome(HttpFailureResult(pageResponse.StatusCode), PortalFailure.HttpStatus, (int)pageResponse.StatusCode);
            var page = await ReadLimitedAsync(pageResponse, requestToken);
            stage = PortalStage.ClientIpv4;
            var clientIp = Regex.Match(page, @"\bv4ip\s*=\s*['"" ]([^'"" ;]+)").Groups[1].Value;
            if (!IPAddress.TryParse(clientIp, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return Outcome(LoginResult.Unsupported, PortalFailure.MissingClientIpv4);
            var configQuery = new Dictionary<string, string>
            {
                ["callback"] = "dr1001", ["jsVersion"] = "4.X", ["v"] = Random.Shared.Next(100000, 999999).ToString(), ["program_index"] = "", ["wlan_vlan_id"] = "1",
                ["wlan_user_ip"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientIp)),
                ["wlan_user_ipv6"] = "", ["wlan_user_ssid"] = "", ["wlan_user_areaid"] = "",
                ["wlan_ac_ip"] = "", ["wlan_ap_mac"] = "000000000000", ["gw_id"] = "000000000000"
            };
            var configUri = new UriBuilder(origin) { Port = 802, Path = "/eportal/portal/page/loadConfig",
                Query = string.Join('&', configQuery.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) };
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
            if (credentials is null) { stage = PortalStage.Complete; return Outcome(LoginResult.Accepted, PortalFailure.None); }

            stage = PortalStage.Submission;
            submitted = true;
            // Production transport disables redirects; only this guarded branch constructs a credential-bearing request.
            using var response = await http.GetAsync(new Uri(origin, "/drcom/login?" + BuildLoginQuery(credentials)), HttpCompletionOption.ResponseHeadersRead, requestToken);
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

    public static string BuildLoginQuery(Credentials credentials) => string.Join('&', new Dictionary<string, string>
    {
        ["callback"] = "dr1001", ["DDDDD"] = credentials.Username, ["upass"] = credentials.Password,
        ["0MKKey"] = "123456", ["R1"] = "0", ["R2"] = "", ["R3"] = "0", ["R6"] = "0",
        ["para"] = "00", ["v6ip"] = "", ["terminal_type"] = "1", ["lang"] = "zh",
        ["jsVersion"] = "4.X", ["v"] = Random.Shared.Next(100000, 999999).ToString()
    }.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

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
