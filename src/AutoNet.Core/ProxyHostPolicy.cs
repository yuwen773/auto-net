using System.Globalization;
using System.Net;

namespace AutoNet.Core;

/// <summary>
/// 认证页/内嵌通道代理的允许列表策略（工单 #38）：唯一允许的主机是固定校园门户主机。
///
/// 纯函数、无 I/O，因此可以离线穷举边界。原型在开发中修掉过两个真实缺陷，这里都固化成规则：
/// 1. 允许列表两侧都必须重新规范化——调用方传入 <c>LOGIN.CSUST.EDU.CN</c> 不能变成绕过口子；
/// 2. 只做完整匹配，不做后缀或通配——<c>login.csust.edu.cn.evil.example</c> 之类必须拒绝。
/// 字面量 IP 一律拒绝：允许列表是域名语义，放开 IP 会绕过域名约束。
/// </summary>
internal static class ProxyHostPolicy
{
    internal const string PortalHost = Settings.PortalHost;

    /// <summary>
    /// 规范化主机名：去空白、去结尾点、IDN 转 ASCII、转小写。
    /// 含空白、控制字符、用户信息、路径或非 ASCII 的输入直接判为不可信。
    /// </summary>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var host = raw.Trim();
        if (host.Length > 253) return null;
        foreach (var c in host)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@' or '?' or '#') return null;
        }
        if (host.EndsWith('.')) host = host[..^1];
        if (host.Length == 0) return null;
        try { host = new IdnMapping().GetAscii(host); }
        catch (ArgumentException) { return null; }
        // DNS 名大小写不敏感；不统一大小写会让允许列表被大小写变体绕过。
        host = host.ToLowerInvariant();
        if (host.Length is 0 or > 253) return null;
        // 字面量 IP 不是允许列表语义：即便它恰好解析到校园地址也不放行。
        if (IPAddress.TryParse(host, out _)) return null;
        // 逐标签校验：非空、不超过 63、只允许字母数字与连字符、不以连字符开头或结尾。
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63) return null;
            if (label[0] == '-' || label[^1] == '-') return null;
            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
            }
        }
        return host;
    }

    /// <summary>
    /// 主机是否在允许集合内。两侧都重新规范化，调用方忘记规范化不能变成绕过口子。
    /// 只做完整匹配，不做后缀或通配匹配。
    /// </summary>
    internal static bool IsAllowed(string? raw)
    {
        var normalized = Normalize(raw);
        return normalized is not null && string.Equals(normalized, PortalHost, StringComparison.Ordinal);
    }
}
