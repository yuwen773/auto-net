using System.Globalization;
using System.Text.Json;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 浏览器兜底开页消费记录（#11 / 故事 64–67、70）：按 Windows 用户持久化「哪些授予已开过页」
/// 与「最近一次开页时刻」。存储在当前用户本地配置目录（独立于 ProgramData 受限业务存储），
/// 载荷只含授予键（快照里的稳定授予时间戳）与时刻——不含账号、密码、Cookie、Token 或任何
/// 状态原文。与通知消费记录刻意分开成两个文件（故事 70）：通知提醒与开页动作是两条独立的
/// 消费线，互不覆写、互不补造。读取失败（缺失、损坏）一律回退为空记录——空记录的最坏结果是
/// 「同一授予在托盘重启后再自动开一次页」，绝不误开不存在的授予。保存失败只返回 false，
/// 由开页控制器据此拒绝自动开页（决策 11：必要消费记录无法安全保存时不自动开页）。
/// QA 激活（AUTONET_QA_DIR 存在）即重定向到证据目录（与 <see cref="NotificationConsumptionStore"/>
/// 同一约定）。与 <see cref="NotificationConsumptionStore"/> 不同，本记录的授予键来自状态快照的
/// BrowserFallbackNotBefore，不经过通知码词表校验。
/// </summary>
internal sealed class PortalOpenStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>记录文件名（QA 脚本与本机路径共用同一约定）。</summary>
    public const string FileName = "portal-open-consumption.json";

    private readonly string path;

    public PortalOpenStore(string path) => this.path = path;

    /// <summary>生产路径：当前 Windows 用户的本地配置目录（按用户隔离，故事 70）。</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoNet",
        FileName);

    /// <summary>按运行环境选择路径：QA 激活时一律使用证据目录，普通模式使用真实用户目录。</summary>
    public static PortalOpenStore Create(QaEnvironment qa) =>
        new(qa.Active ? Path.Combine(qa.Directory, FileName) : DefaultPath());

    /// <summary>一次读取结果：授予键 → 开页时刻，以及跨授予的最近开页时刻（本地冷却）。</summary>
    public sealed record Consumptions(IReadOnlyDictionary<string, DateTimeOffset> Grants, DateTimeOffset? LastOpenAt);

    /// <summary>读取消费记录；缺失、损坏或形状不符时回退为空（绝不抛出）。</summary>
    public Consumptions Load()
    {
        try
        {
            var file = JsonSerializer.Deserialize<PortalOpenFile>(File.ReadAllText(path), Options);
            if (file is null) return Empty;
            var grants = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            if (file.Consumed is { } entries)
            {
                foreach (var (key, at) in entries)
                    if (GrantKey.IsValid(key)
                        && at != default
                        && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment))
                        grants[key] = moment;
            }

            DateTimeOffset? lastOpenAt = null;
            if (file.LastOpenAt is { } stamp
                && stamp != default
                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var opened))
                lastOpenAt = opened;
            return new Consumptions(grants, lastOpenAt);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    /// <summary>原子保存消费记录；成功返回 true，任何失败只返回 false（由调用方据此拒绝开页）。</summary>
    public bool TrySave(IReadOnlyDictionary<string, DateTimeOffset> grants, DateTimeOffset? lastOpenAt)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, at) in grants)
            if (GrantKey.IsValid(key))
                entries[key] = at.ToString("O");
        var payload = JsonSerializer.Serialize(
            new PortalOpenFile(entries, lastOpenAt is { } open ? open.ToString("O") : null), Options);
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

    private static readonly Consumptions Empty = new(
        new Dictionary<string, DateTimeOffset>(), null);

    /// <summary>授予键的受控形状校验：必须是可解析且圆整回写一致的时刻字符串（防损坏文件注入任意键）。</summary>
    internal static class GrantKey
    {
        public static bool IsValid(string key) =>
            !string.IsNullOrEmpty(key)
            && DateTimeOffset.TryParse(key, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            && parsed.ToString("O", CultureInfo.InvariantCulture) == key;
    }

    /// <summary>记录文件载荷：已开页授予映射与最近开页时刻，不含任何其他字段。</summary>
    internal sealed record PortalOpenFile(Dictionary<string, string>? Consumed, string? LastOpenAt);
}
