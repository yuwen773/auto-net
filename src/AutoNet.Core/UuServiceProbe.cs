namespace AutoNet.Core;

/// <summary>
/// 单飞的 UU 读取（工单 #27 可靠性批次）：任意时刻最多存在一个后台读取任务。
/// SCM/进程枚举/TCP 表调用本身不可取消；如果每次超时都新建任务，慢调用会让线程池任务
/// 持续累积。这里让所有调用方等待同一个任务：等待有界，超时只返回受控文案，任务继续
/// 运行并在完成后被下一次读取复用，不会叠加。
///
/// 读取异常统一降级为受控文案（与 <see cref="HealthChecks.UuService"/> 一致），
/// 因此调用方不需要为该任务挂异常观察。
/// </summary>
public sealed class UuServiceProbe(Func<string> read, TimeSpan timeout)
{
    private readonly object gate = new();
    private Task<string>? running;

    public async Task<string> ReadAsync(CancellationToken ct)
    {
        Task<string> work;
        lock (gate)
        {
            if (running is null || running.IsCompleted)
            {
                work = running = Task.Run(read, CancellationToken.None);
                // 等待方可能已超时离开；异常仍要被观察，不能留给终结器。
                HealthChecks.Observe(work);
            }
            else work = running;
        }
        try { return await work.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (System.TimeoutException) { return "UU 状态未知，检测超时"; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return "UU 状态未知，读取失败"; }
    }
}
