using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.AuthChannel;

// 内嵌认证通道辅助进程（工单 #21 / ADR-0009）。
// 凭据经 stdin JSON {"Username":...,"Password":...} 传入，绝不进命令行、不落盘；
// 结果写入 --result 指定路径（AuthChannelResultFile 契约）；会话目录退出即弃。
// 退出码：0=LoggedIn，3=NotLoggedIn，4=NeedsHuman/Unknown，1=运行失败，2=用法错误。
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = ChannelOptions.Parse(args);
            if (options is null)
            {
                Console.Error.WriteLine("usage: AutoNet.AuthChannel --start-url <url> --result <path> [--timeout <seconds>] [--fixture-dir <dir> --fixture-host <host>]");
                return 2;
            }

            var credential = options.Injected ?? CredentialInput.ReadStdin();
            if (credential is null || string.IsNullOrEmpty(credential.Username) || string.IsNullOrEmpty(credential.Password))
            {
                Console.Error.WriteLine("missing credential");
                return 2;
            }

            var window = new AuthChannelWindow(options, credential);
            Application.Run(window);
            window.CleanupSession();
            return window.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name);
            return 1;
        }
    }
}

internal sealed record ChannelOptions(
    string StartUrl,
    string ResultPath,
    int TimeoutSeconds,
    string? FixtureDir,
    string FixtureHost,
    CredentialSource? Injected = null)
{
    public static ChannelOptions? Parse(string[] args)
    {
        string? startUrl = null, resultPath = null, fixtureDir = null, fixtureHost = "portal.fixture";
        var timeout = 90;
        var fromSettings = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--start-url" when i + 1 < args.Length: startUrl = args[++i]; break;
                case "--result" when i + 1 < args.Length: resultPath = args[++i]; break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[++i], out var t) && t is > 0 and <= 600: timeout = t; break;
                case "--fixture-dir" when i + 1 < args.Length: fixtureDir = Path.GetFullPath(args[++i]); break;
                case "--fixture-host" when i + 1 < args.Length: fixtureHost = args[++i]; break;
                case "--from-settings": fromSettings = true; break;
                default: return null;
            }
        }
        CredentialSource? injected = null;
        if (fromSettings)
        {
            // 提权令牌读取机器级凭据（LocalMachine DPAPI）：凭据只在进程内存，不经命令行。
            var settings = Storage.LoadSettings();
            var credentials = Storage.LoadCredentials();
            if (settings is null || credentials is null)
            {
                Console.Error.WriteLine("settings-or-credentials-missing");
                return null;
            }
            if (settings.AuthChannelMode == AuthChannelModes.Off)
            {
                Console.Error.WriteLine("mode-off");
                return null;
            }
            startUrl = settings.PortalOrigin;
            injected = new CredentialSource(credentials.Username, credentials.Password);
        }
        if (string.IsNullOrWhiteSpace(startUrl) || string.IsNullOrWhiteSpace(resultPath)) { return null; }
        if (fixtureDir is not null && !Directory.Exists(fixtureDir)) { return null; }
        return new ChannelOptions(startUrl, resultPath, timeout, fixtureDir, fixtureHost, injected);
    }
}

internal sealed record CredentialSource(string Username, string Password);

internal static class CredentialInput
{
    public static CredentialSource? ReadStdin()
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            var json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json)) { return null; }
            using var doc = JsonDocument.Parse(json);
            var username = doc.RootElement.TryGetProperty("Username", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() : null;
            var password = doc.RootElement.TryGetProperty("Password", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
            return (username, password) is (_, _) ? new CredentialSource(username ?? "", password ?? "") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
