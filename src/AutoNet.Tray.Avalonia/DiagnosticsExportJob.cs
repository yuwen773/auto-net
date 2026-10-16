using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 提权侧诊断包导出任务（迁移工单 #12 / 故事 73、75–77）：复用 Core 的
/// <see cref="DiagnosticsPackage"/>（六类内容、近三天日志、既有脱敏模型），按普通界面
/// 逐字传来的用户所选路径执行只读采集——提权身份不替换目标位置，采集不加载凭据、
/// 不修改网络、不清除认证阻断。结束后把受控结果（成功/失败 + 缺口说明）写入共享结果
/// 文件并按退出码收场：0 = 成功，3 = 失败；界面凭退出码呈现最终结果（故事 81），
/// 结果文件只补充缺口明细（故事 80）。写不了结果文件不影响退出码裁决。
/// </summary>
internal static class DiagnosticsExportJob
{
    /// <summary>生产结果文件路径（ProgramData\AutoNet；提权可写、普通界面可读的既有共享目录）。</summary>
    public static string ProductionResultPath => Path.Combine(Storage.Root, DiagnosticsExportOutcome.ResultFileName);

    /// <summary>
    /// 执行一次导出并落结果文件。本方法绝不抛出：一切失败都折算为受控结果与退出码。
    /// 返回退出码（0 成功 / 3 失败 / 2 参数缺失）。
    /// <paramref name="configure"/> 供测试离线化注入（日志目录、状态路径、采集桩）；生产路径不传。
    /// </summary>
    public static int Run(string? targetPath, string resultPath, Func<DiagnosticsPackageOptions, DiagnosticsPackageOptions>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            new DiagnosticsExportOutcome(false, targetPath ?? "", "ArgumentException", [],
                DateTimeOffset.UtcNow.ToString("O")).TryWrite(resultPath);
            return 2;
        }

        DiagnosticsExportOutcome outcome;
        try
        {
            var options = new DiagnosticsPackageOptions
            {
                OutputPath = targetPath,
                // README 的导出方式行如实说明采集发起方（与 Service CLI 区分）。
                SourceLabel = "AutoNet 界面导出（独立提权采集；目标位置由普通界面用户选定）",
            };
            if (configure is { } customize) options = customize(options);
            var result = DiagnosticsPackage.ExportAsync(options).GetAwaiter().GetResult();
            outcome = new DiagnosticsExportOutcome(true, result.Path, null, result.Notes, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception e)
        {
            // 安全发布保证失败不留半包；这里只披露异常类型（与既有界面的受控错误口径一致）。
            outcome = new DiagnosticsExportOutcome(false, targetPath, e.GetType().Name, [], DateTimeOffset.UtcNow.ToString("O"));
        }

        outcome.TryWrite(resultPath);
        return outcome.Ok ? 0 : 3;
    }
}
