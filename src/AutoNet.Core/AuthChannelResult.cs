namespace AutoNet.Core;

/// <summary>内嵌认证通道结论（工单 #21 / ADR-0009）。只作结果文件的受控词表。</summary>
public enum AuthChannelConclusion
{
    LoggedIn,
    NotLoggedIn,
    NeedsHuman,
    Unknown,
}

/// <summary>内嵌认证通道写入的结果：结论 + 判定时刻 + 脱敏说明。凭据绝不入内。</summary>
public sealed record AuthChannelResult(AuthChannelConclusion Conclusion, DateTimeOffset DetectedAt, string Detail);

/// <summary>
/// 结果文件的严格读取边界（工单 #21 验收：伪造、错乱、过期结果一律拒绝）。
/// 大小受限、字段受控、未知结论拒绝；双核对语义由调用方以 TryLoad 的时间与路径检查承担。
/// </summary>
public static class AuthChannelResultFile
{
    private const long MaxBytes = 64 * 1024;

    public static AuthChannelResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) { throw new FormatException("empty"); }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) { throw new FormatException("root"); }
        var conclusionText = RequireString(root, "conclusion");
        if (!Enum.TryParse<AuthChannelConclusion>(conclusionText, ignoreCase: false, out var conclusion)
            || !Enum.IsDefined(conclusion))
        {
            throw new FormatException("conclusion");
        }
        var atText = RequireString(root, "detectedAt");
        if (!DateTimeOffset.TryParse(atText, out var at) || at > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            throw new FormatException("detectedAt");
        }
        var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
            ? d.GetString() ?? string.Empty
            : string.Empty;
        if (detail.Length > 500) { detail = detail[..500]; }
        return new AuthChannelResult(conclusion, at, detail);
    }

    /// <summary>读取结果文件；不存在、超大小或不可解析时拒绝。调用方自行核对路径与时刻。</summary>
    public static bool TryLoad(string path, out AuthChannelResult? result, out string error)
    {
        result = null;
        error = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "missing"; return false; }
            if (info.Length > MaxBytes) { error = "oversized"; return false; }
            result = Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException
                                   or System.Text.Json.JsonException)
        {
            error = ex is FormatException or System.Text.Json.JsonException ? "invalid" : ex.GetType().Name;
            return false;
        }
    }

    private static string RequireString(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            throw new FormatException(name);
        }
        return v.GetString() ?? throw new FormatException(name);
    }
}
