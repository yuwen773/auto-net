namespace AutoNet.Core;

/// <summary>
/// 配置地址的安全投影规则（#41 审查修复）。status.json 对普通用户可读，诊断包也会原样收录它，
/// 因此代理与 UU 地址不能整串公开：两者的路径、查询或片段可能携带令牌。
/// - 不含隐藏部分：投影完整地址，保存时不会改变字节；
/// - 含隐藏部分：只投影 scheme://host[:port]，并标记 <c>Redacted</c>，由设置页显示“未回显”说明；
///   保存端在该字段未被编辑时按标记保留受限存储中的完整原值，绝不以投影值覆盖。
/// 隐藏部分的判定只看**原始输入**：<see cref="Uri"/> 会把 <c>/秘密/../</c>、<c>/./</c> 这类点段
/// 规范化成 <c>/</c>，按规范化结果判断会把原始秘密当成安全地址并原样公开。
/// 本类无 I/O、无副作用，服务投影与设置页共用同一规则。
/// </summary>
public static class ConfigProjection
{
    /// <summary>地址是否含有不能进入公开状态的路径、查询或片段；无法安全解析的输入一律视为隐藏。</summary>
    public static bool HasHiddenParts(string url) => !TryProject(url, out _, out var redacted) || redacted;

    /// <summary>
    /// 可写入公开状态的展示值。无效地址、含 userinfo 的地址与无法可靠切分的输入返回空串；
    /// 含隐藏部分时返回仅含 scheme://host[:port] 的来源；安全地址原样返回（仅去掉首尾空白）。
    /// </summary>
    public static string Display(string url) => TryProject(url, out var display, out _) ? display : "";

    private static bool TryProject(string url, out string display, out bool redacted)
    {
        display = "";
        redacted = true;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // userinfo 不属于可公开的配置投影（Settings.Validate 本已拒绝）。
        if (uri.UserInfo.Length != 0) return false;
        if (!TrySplitAuthority(url, uri.Scheme, out var authority, out var rest)) return false;
        // 授权部分只允许主机名/端口/IPv6 字面量；其他字符无法证明不会把路径内容带进展示值。
        if (!IsSafeAuthority(authority)) return false;

        if (rest is "" or "/")
        {
            display = url.Trim();
            redacted = false;
            return true;
        }

        display = uri.Scheme + "://" + authority;
        return true;
    }

    /// <summary>
    /// 按原始输入切出授权部分与其余部分。Uri 会规范化点段、编码与反斜杠，
    /// 因此这里只做最小切分：授权部分在第一个 '/', '?', '#' 或 '\' 处结束。
    /// </summary>
    private static bool TrySplitAuthority(string url, string scheme, out string authority, out string rest)
    {
        authority = "";
        rest = "";
        var trimmed = url.Trim();
        var separator = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) return false;
        if (!trimmed.AsSpan(0, separator).Equals(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var start = separator + 3;
        var end = trimmed.Length;
        for (var i = start; i < trimmed.Length; i++)
        {
            if (trimmed[i] is '/' or '?' or '#' or '\\') { end = i; break; }
        }

        authority = trimmed[start..end];
        if (authority.Length == 0) return false;
        rest = trimmed[end..];
        return true;
    }

    private static bool IsSafeAuthority(string authority)
    {
        foreach (var c in authority)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '-' or ':' or '[' or ']' or '_' or '%')) return false;
        }

        return true;
    }
}
