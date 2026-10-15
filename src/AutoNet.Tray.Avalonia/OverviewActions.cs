namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 概览页动作（迁移工单 #4 / T08）：恢复开关与解除入口的点击语义，UI 按钮与托盘菜单共用。
/// - 恢复开关发送明确的开启或关闭意图（故事 40）：调用方传入点击时显示的动作意图，绝不自行翻转；
/// - 点击动作前重新读取状态并核对可信边界（故事 41）：过期、缺失或读取异常一律不发起（失败关闭）；
/// - 动作本身通过独立提权操作完成（拒绝 UAC 时什么都不发生），此处不加载、不提交凭据。
/// 读取与启动都以委托注入，便于受控测试；本类不依赖任何 UI 类型。
/// </summary>
internal sealed class OverviewActions(Func<CancellationToken, Task<StatusReading>> reread, Action<ElevatedOperationKind> launch)
{
    /// <summary>按明确意图请求开启或关闭自动恢复；返回是否已发起提权操作。</summary>
    public async Task<bool> RequestRecoveryChangeAsync(RecoveryIntent intent, CancellationToken cancellationToken = default)
    {
        if (await ReadTrustedAsync(cancellationToken).ConfigureAwait(false) is null) return false;
        launch(OverviewModel.OperationFor(intent));
        return true;
    }

    /// <summary>请求解除认证暂停；仅在状态可信且确实存在暂停时发起。</summary>
    public async Task<bool> RequestUnblockAsync(CancellationToken cancellationToken = default)
    {
        if (await ReadTrustedAsync(cancellationToken).ConfigureAwait(false) is not { } reading) return false;
        if (!reading.Snapshot!.CredentialsBlocked) return false;
        launch(ElevatedOperationKind.UnblockAuthentication);
        return true;
    }

    private async Task<StatusReading?> ReadTrustedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reading = await reread(cancellationToken).ConfigureAwait(false);
            return reading.Trusted ? reading : null;
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            // 读取失败按“不可确认”处理：不发起任何操作（失败关闭）。
            return null;
        }
    }
}
