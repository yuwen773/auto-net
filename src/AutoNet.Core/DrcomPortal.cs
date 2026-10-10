using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoNet.Core;

public enum PortalState { Unknown, LoggedIn, NeedsLogin }
public enum LoginResult { Accepted, RejectedCredentials, NeedsInteraction, Rejected, Unsupported }

public sealed class DrcomPortal(HttpClient http, Uri origin)
{
    public async Task<PortalState> InspectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2000);
        ct = timeout.Token;
        using var response = await http.GetAsync(new Uri(origin, "/drcom/chkstatus?callback=dr1001&v=" + Random.Shared.Next(100000, 999999)), HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) return PortalState.Unknown;
        using var json = ParseJsonp(await ReadLimitedAsync(response, ct));
        if (!json.RootElement.TryGetProperty("result", out var result)) return PortalState.Unknown;
        return result.ToString() switch { "1" => PortalState.LoggedIn, "0" => PortalState.NeedsLogin, _ => PortalState.Unknown };
    }

    public async Task<LoginResult> LoginAsync(Credentials credentials, CancellationToken ct)
    {
        // Never transmit credentials unless the configured, TLS-validated portal says logged out.
        if (await InspectAsync(ct) != PortalState.NeedsLogin) return LoginResult.Rejected;
        using var pageResponse = await http.GetAsync(new Uri(origin, "/"), HttpCompletionOption.ResponseHeadersRead, ct);
        if (pageResponse.StatusCode != HttpStatusCode.OK) return LoginResult.Unsupported;
        var page = await ReadLimitedAsync(pageResponse, ct);
        var clientIp = Regex.Match(page, @"\bv4ip\s*=\s*['"" ]([^'"" ;]+)").Groups[1].Value;
        if (!IPAddress.TryParse(clientIp, out _)) return LoginResult.Unsupported;
        var configQuery = new Dictionary<string, string>
        {
            ["callback"] = "dr1001", ["jsVersion"] = "4.X", ["v"] = Random.Shared.Next(100000, 999999).ToString(), ["program_index"] = "", ["wlan_vlan_id"] = "1",
            ["wlan_user_ip"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientIp)),
            ["wlan_user_ipv6"] = "", ["wlan_user_ssid"] = "", ["wlan_user_areaid"] = "",
            ["wlan_ac_ip"] = "", ["wlan_ap_mac"] = "000000000000", ["gw_id"] = "000000000000"
        };
        var configUri = new UriBuilder(origin) { Port = 802, Path = "/eportal/portal/page/loadConfig",
            Query = string.Join('&', configQuery.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) };
        using var configResponse = await http.GetAsync(configUri.Uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (configResponse.StatusCode != HttpStatusCode.OK) return LoginResult.Unsupported;
        using var config = ParseJsonp(await ReadLimitedAsync(configResponse, ct));
        if (!config.RootElement.TryGetProperty("data", out var data)) return LoginResult.Unsupported;
        // Refuse changed authentication modes rather than guessing challenge/MD5/suffix semantics.
        if (NonZero(data, "en_md5") || NonZero(data, "password_cut") || NonZero(data, "enable_r3"))
            return LoginResult.Unsupported;
        var query = BuildLoginQuery(credentials);
        using var response = await http.GetAsync(new Uri(origin, "/drcom/login?" + query), HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) return LoginResult.Rejected;
        using var json = ParseJsonp(await ReadLimitedAsync(response, ct));
        return Classify(json.RootElement);
    }

    private static bool NonZero(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ToString() is not ("" or "0");

    public static string BuildLoginQuery(Credentials credentials) => string.Join('&', new Dictionary<string, string>
    {
        ["callback"] = "dr1001", ["DDDDD"] = credentials.Username, ["upass"] = credentials.Password,
        ["0MKKey"] = "123456", ["R1"] = "0", ["R2"] = "", ["R3"] = "0", ["R6"] = "0",
        ["para"] = "00", ["v6ip"] = "", ["terminal_type"] = "1", ["lang"] = "zh",
        ["jsVersion"] = "4.X", ["v"] = Random.Shared.Next(100000, 999999).ToString()
    }.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public static LoginResult Classify(JsonElement json)
    {
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
