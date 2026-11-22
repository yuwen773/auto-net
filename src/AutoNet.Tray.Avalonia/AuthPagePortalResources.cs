using System.Text.RegularExpressions;
using AutoNet.Core;
using Microsoft.Web.WebView2.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>门户 802 端口上用于呈现页面的只读 HTTPS 资源。</summary>
internal static class AuthPagePortalResources
{
    internal const string Origin = "https://" + Settings.PortalHost + ":802";
    private const string AssetRoot = "/eportal/public/pageAsset/js/";
    private const string ConfigPath = "/eportal/portal/page/loadConfig";
    private const string ThemeRoot = "/eportal/extern/[A-Za-z0-9_-]{1,64}/[A-Za-z0-9_-]{1,64}/";

    private static readonly HashSet<string> ConfigFields = new(StringComparer.Ordinal)
    {
        "callback", "program_index", "wlan_vlan_id", "wlan_user_ip", "wlan_user_ipv6",
        "wlan_user_ssid", "wlan_user_areaid", "wlan_ac_ip", "wlan_ap_mac", "gw_id", "jsVersion", "v", "lang"
    };

    internal static Uri? GetAddress(string url, string method, CoreWebView2WebResourceContext context)
    {
        if (method != "GET" || url.Length > 2048 || !url.StartsWith(Origin + "/", StringComparison.Ordinal) ||
            url.Contains('#') || !Uri.TryCreate(url, UriKind.Absolute, out var address)) return null;

        // 对原始路径做匹配，避免编码路径或路径归一化引入额外的可达端点。
        var queryStart = url.IndexOf('?');
        var path = queryStart < 0 ? url[Origin.Length..] : url[Origin.Length..queryStart];
        var query = queryStart < 0 ? "" : url[(queryStart + 1)..];
        var versioned = queryStart < 0 || Regex.IsMatch(query, @"\Av=[A-Za-z0-9_.-]{1,64}\z");
        var allowed = context switch
        {
            CoreWebView2WebResourceContext.Script when path == ConfigPath => IsConfigQuery(query),
            CoreWebView2WebResourceContext.Script => versioned && (path is AssetRoot + "store.js" or
                AssetRoot + "all.js" or AssetRoot + "layer/layer.js" or AssetRoot + "jquery.i18n.js"
                || Regex.IsMatch(path, @"\A" + ThemeRoot + @"pc_[0-9]{1,4}\.js\z")),
            CoreWebView2WebResourceContext.Stylesheet => versioned && path is
                AssetRoot + "layer/theme/default/layer.css" or AssetRoot + "layer/theme/default/stylesheet.css",
            CoreWebView2WebResourceContext.Image => queryStart < 0 &&
                Regex.IsMatch(path, @"\A" + ThemeRoot + @"[A-Fa-f0-9]{32}\.(?:jpe?g|png|gif|webp)\z"),
            _ => false
        };
        return allowed ? address : null;
    }

    private static bool IsConfigQuery(string query)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in query.Split('&'))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0) return false;
            var name = field[..separator];
            if (!ConfigFields.Contains(name) || !seen.Add(name)) return false;
            var value = Uri.UnescapeDataString(field[(separator + 1)..]);
            if (value.Any(c => char.IsControl(c) || c is '&' or ';' or '#' or '%')) return false;
            if (name == "callback" && !Regex.IsMatch(value, @"\A[A-Za-z_$][A-Za-z0-9_$]{0,127}\z")) return false;
        }
        return seen.Contains("callback");
    }

    internal static bool IsMediaTypeAllowed(CoreWebView2WebResourceContext context, string mediaType) => context switch
    {
        CoreWebView2WebResourceContext.Script => mediaType is "application/javascript" or "text/javascript",
        CoreWebView2WebResourceContext.Stylesheet => mediaType == "text/css",
        CoreWebView2WebResourceContext.Image => mediaType is "image/jpeg" or "image/png" or "image/gif" or "image/webp",
        _ => false
    };
}
