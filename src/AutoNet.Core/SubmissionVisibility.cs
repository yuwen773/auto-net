namespace AutoNet.Core;

/// <summary>
/// 提交额度级别的受控码（#35）：Core 只广播码与数值，托盘负责措辞。
/// 旧版状态快照缺少这些字段时码为空，托盘必须按“不可确认”降级，不得猜测次数或放行提交。
/// </summary>
public static class SubmissionBudgetCodes
{
    /// <summary>尚无额度记录，也从未发生提交。</summary>
    public const string Unused = "Unused";
    /// <summary>已用一次或两次，额度仍可继续。</summary>
    public const string Available = "Available";
    /// <summary>已用两次，只剩最后一次自动提交机会。</summary>
    public const string NearLimit = "NearLimit";
    /// <summary>三次上限已用尽，自动提交已停止。</summary>
    public const string Exhausted = "Exhausted";
    /// <summary>额度已由门户确认成功或人工重新开始认证重置。</summary>
    public const string Reset = "Reset";
    /// <summary>额度记录缺失或无法确认（例如旧版提交结果未知记录）。</summary>
    public const string Unknown = "Unknown";

    public static bool IsKnown(string? code) => code is Unused or Available or NearLimit or Exhausted or Reset or Unknown;
}
