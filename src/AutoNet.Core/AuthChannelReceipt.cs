namespace AutoNet.Core;

/// <summary>
/// 内嵌认证通道结果的消费边界（工单 #23 / ADR-0009）：
/// 双核对——结果文件必须位于约定路径，且判定时刻不早于通道发起时刻；伪造、错乱、过期一律拒绝。
/// 结论映射——通道结论映射到既有凭据提交分类（LoginResult），不新增状态种类；
/// Unknown 不伪造任何提交结论（返回 null，由调用方按「提交结果未知」语义处置，
/// 即阻断后续提交直至复检消解或人工解除——SubmissionPending 既有路径）。
/// </summary>
public static class AuthChannelReceipt
{
    public static LoginResult? MapToSubmissionOutcome(AuthChannelConclusion conclusion) => conclusion switch
    {
        AuthChannelConclusion.LoggedIn => LoginResult.Accepted,
        AuthChannelConclusion.NotLoggedIn => LoginResult.RejectedCredentials,
        AuthChannelConclusion.NeedsHuman => LoginResult.NeedsInteraction,
        AuthChannelConclusion.Unknown => null,
        _ => null,
    };

    public static bool Validate(
        AuthChannelResult result,
        string actualPath,
        string expectedPath,
        DateTimeOffset notBefore,
        out string reason)
    {
        reason = string.Empty;
        if (actualPath is null || expectedPath is null)
        {
            reason = "path-missing";
            return false;
        }
        var actual = Path.GetFullPath(actualPath);
        var expected = Path.GetFullPath(expectedPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            reason = "path-mismatch";
            return false;
        }
        // 判定时刻允许 1 秒时钟容差；早于发起时刻即为过期/伪造。
        if (result.DetectedAt < notBefore.AddSeconds(-1))
        {
            reason = "stale-result";
            return false;
        }
        return true;
    }
}
