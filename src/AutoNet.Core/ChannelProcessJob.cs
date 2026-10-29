using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoNet.Core;

// The handle stays alive until this process exits: closing it kills all processes in the job,
// including this process. Every descendant therefore inherits the same kill-on-close boundary.
internal sealed class ChannelProcessJob
{
    private static readonly List<ChannelProcessJob> LiveJobs = [];
    private readonly SafeFileHandle handle;
    public string Name { get; }

    private ChannelProcessJob(SafeFileHandle handle, string name) => (this.handle, Name) = (handle, name);

    public static ChannelProcessJob CreateForCurrentProcess()
    {
        var name = "Global\\AutoNetChannel-" + Guid.NewGuid().ToString("N");
        var handle = CreateJobObject(IntPtr.Zero, name);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(handle, GetCurrentProcess()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var job = new ChannelProcessJob(handle, name);
            lock (LiveJobs) LiveJobs.Add(job);
            return job;
        }
        catch { handle.Dispose(); throw; }
    }

    public bool Contains(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return IsProcessInJob(process.Handle, handle, out var result) && result;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    public static bool? IsActive(string name)
    {
        const string prefix = "Global\\AutoNetChannel-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return null;
        using var job = OpenJobObject(0x0004, false, name); // JOB_OBJECT_QUERY
        if (job.IsInvalid) return Marshal.GetLastWin32Error() == 2 ? false : null;
        var info = new BasicAccountingInformation();
        return QueryInformationJobObject(job, 1, ref info, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero)
            ? info.ActiveProcesses > 0 : null;
    }

    public static bool IsMember(string name, int processId)
    {
        if (IsActive(name) != true) return false;
        using var job = OpenJobObject(0x0004, false, name);
        if (job.IsInvalid) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return IsProcessInJob(process.Handle, job, out var result) && result;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenJobObjectW")]
    private static extern SafeFileHandle OpenJobObject(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, ref BasicAccountingInformation info, uint length, IntPtr returnLength);
}
