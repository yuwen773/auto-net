using System.Diagnostics;
using AutoNet.Core;

internal static class ChannelJobRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("通道进程组：辅助进程退出时子进程随之结束", async () =>
        {
            var info = PeerStart("--channel-job-test-peer");
            using var owner = Process.Start(info)!;
            try
            {
                var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var parts = line?.Split(' ');
                if (parts is not { Length: 3 } || parts[0] != "CHILD" || !int.TryParse(parts[1], out var id))
                    throw new Exception("进程组夹具未就绪：" + line);
                using var child = Process.GetProcessById(id);
                if (child.HasExited) throw new Exception("子进程提前结束");
                if (ChannelProcessJob.IsActive(parts[2]) != true) throw new Exception("无法确认进程组仍活动");
                owner.Kill();
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (!child.HasExited) throw new Exception("辅助进程结束后子进程仍活动");
                if (ChannelProcessJob.IsActive(parts[2]) != false) throw new Exception("进程组结束后仍无法确认退出");
            }
            finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
        })
    ];

    internal static ProcessStartInfo PeerStart(string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardInput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(ChannelJobRegression).Assembly.Location);
        info.ArgumentList.Add(mode);
        return info;
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly Process owner;
        private bool disposed;
        public int OwnerId => owner.Id;
        public int BrowserId { get; }
        public string Name { get; }

        public Fixture()
        {
            owner = Process.Start(PeerStart("--channel-job-test-peer"))!;
            var line = owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var parts = line?.Split(' ');
            if (parts is not { Length: 3 } || parts[0] != "CHILD" || !int.TryParse(parts[1], out var id))
            {
                if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(); }
                owner.Dispose();
                throw new InvalidOperationException("进程组夹具未就绪：" + line);
            }
            BrowserId = id;
            Name = parts[2];
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(); }
            owner.Dispose();
        }
    }

    public static int RunPeer()
    {
        var job = ChannelProcessJob.CreateForCurrentProcess();
        using var child = Process.Start(PeerStart("--channel-session-test-peer"))!;
        if (child.StandardOutput.ReadLine() != "READY" || !job.Contains(child.Id)) return 2;
        Console.WriteLine("CHILD " + child.Id + " " + job.Name); Console.Out.Flush();
        Console.ReadLine();
        return 0;
    }
}
