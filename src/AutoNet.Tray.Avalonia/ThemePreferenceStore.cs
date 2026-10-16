using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoNet.Tray.Avalonia;

/// <summary>界面主题偏好（迁移工单 #9 / 故事 26–29）：跟随系统为默认，可选浅色或深色。</summary>
internal enum ThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>偏好文件载荷：只含主题枚举一个字段，绝不保存账号、密码、Cookie 或 Token（故事 70）。</summary>
internal sealed record ThemePreferenceFile([property: JsonPropertyName("theme")] string Theme);

/// <summary>
/// 主题偏好的用户级存储（#9 / 故事 28、70）：按 Windows 用户隔离（用户配置目录独立于
/// ProgramData 受限业务存储），不降低 private ACL，也不触碰任何受限凭据模型。
/// 读取失败（缺失、损坏、未知值）一律回退为跟随系统；保存失败只返回 false，不抛出。
/// QA 模式（AUTONET_QA_DIR 激活）下重定向到证据目录：QA 既不读写本机真实偏好，
/// 也不因开发者机器上的真实偏好产生不确定的截图证据。
/// </summary>
internal sealed class ThemePreferenceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>偏好文件名（QA 脚本与本机路径共用同一约定）。</summary>
    public const string FileName = "ui-preferences.json";

    private readonly string path;

    public ThemePreferenceStore(string path) => this.path = path;

    /// <summary>
    /// 生产路径：当前 Windows 用户的本地配置目录（按用户隔离；文件继承用户配置目录的
    /// 默认 ACL，且内容只含主题枚举、不含任何秘密，故事 70）。
    /// </summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoNet",
        FileName);

    /// <summary>按运行环境选择路径：QA 激活时一律使用证据目录，普通模式使用真实用户目录。</summary>
    public static ThemePreferenceStore Create(QaEnvironment qa) =>
        new(qa.Active ? Path.Combine(qa.Directory, FileName) : DefaultPath());

    /// <summary>读取偏好；缺失、损坏或含未知值时回退为跟随系统（故事 26 的默认语义）。</summary>
    public ThemePreference Load()
    {
        try
        {
            var file = JsonSerializer.Deserialize<ThemePreferenceFile>(File.ReadAllText(path));
            if (file?.Theme is { } theme && Enum.TryParse(theme, ignoreCase: true, out ThemePreference parsed))
                return parsed;
            return ThemePreference.System;
        }
        catch (Exception)
        {
            return ThemePreference.System;
        }
    }

    /// <summary>原子保存偏好；成功返回 true，任何失败只返回 false（主题偏好不值得打扰用户）。</summary>
    public bool TrySave(ThemePreference preference)
    {
        var payload = JsonSerializer.Serialize(
            new ThemePreferenceFile(ToText(preference)),
            Options);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(temporary, payload);
                    File.Move(temporary, path, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ToText(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "system"
    };
}
