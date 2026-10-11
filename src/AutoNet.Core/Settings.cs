using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoNet.Core;

public sealed record Settings
{
    public bool Enabled { get; init; } = true;
    public bool AllowActiveDisconnect { get; init; } = true;
    public string InterfaceId { get; init; } = "";
    public string WifiProfile { get; init; } = "";
    public string PortalOrigin { get; init; } = "https://login.csust.edu.cn";
    public bool KeepAwakeOnAc { get; init; } = true;
    public string ProxyUrl { get; init; } = "";
    // Optional real UU endpoint, not its marketing website. Empty = explicitly unverified.
    public string UuEndpoint { get; init; } = "";

    public void Validate()
    {
        if (!Guid.TryParse(InterfaceId, out _) || string.IsNullOrWhiteSpace(WifiProfile))
            throw new ArgumentException("请选择校园 Wi-Fi 网卡和已保存的配置。");
        if (!Uri.TryCreate(PortalOrigin, UriKind.Absolute, out var portal) || !IsSupportedPortal(portal))
            throw new ArgumentException("认证地址仅支持 https://login.csust.edu.cn:443 根地址；802 端口由程序自行使用。");
        if (ProxyUrl != "" && (!Uri.TryCreate(ProxyUrl, UriKind.Absolute, out var proxy) ||
            proxy.Scheme != "http" || !proxy.IsLoopback || proxy.UserInfo != ""))
            throw new ArgumentException("代理检测地址应为本机 HTTP 代理，例如 http://127.0.0.1:7897。");
        if (UuEndpoint != "" && (!Uri.TryCreate(UuEndpoint, UriKind.Absolute, out var uu) ||
            uu.Scheme != "https" || uu.UserInfo != "" || uu.Query != "" || uu.Fragment != ""))
            throw new ArgumentException("UU 检测地址必须是不含令牌的真实 HTTPS 服务地址。");
    }

    public static bool IsSupportedPortal(Uri portal) => portal.IsAbsoluteUri &&
        portal.Scheme == "https" && portal.Host.Equals("login.csust.edu.cn", StringComparison.OrdinalIgnoreCase) &&
        portal.Port == 443 && portal.UserInfo == "" && portal.Query == "" &&
        portal.Fragment == "" && portal.AbsolutePath == "/";
}

public sealed record Credentials(string Username, string Password);

public static class Storage
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoNet");
    public static string Private => Path.Combine(Root, "private");
    public static string SettingsPath => Path.Combine(Private, "settings.json");
    public static string CredentialPath => Path.Combine(Private, "credentials.dpapi");
    public static string StatusPath => Path.Combine(Root, "status.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Settings? LoadSettings() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<Settings>(ReadShared(SettingsPath), Json) : null;

    public static void SaveSettings(Settings settings)
    {
        settings.Validate();
        AtomicWrite(SettingsPath, JsonSerializer.Serialize(settings, Json));
    }

    public static void SaveCredentials(Credentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrEmpty(credentials.Password))
            throw new ArgumentException("请输入校园网账号和密码。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            AtomicWrite(CredentialPath, encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static Credentials? LoadCredentials()
    {
        if (!File.Exists(CredentialPath)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(CredentialPath), null, DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<Credentials>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static void AtomicWrite(string path, string text) => AtomicWrite(path, Encoding.UTF8.GetBytes(text));
    public static void AtomicWrite(string path, byte[] bytes)
    {
        // Directory ACLs must be provisioned by the elevated installer first.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes); file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
