using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutoNet.Tray.Avalonia;

/// <summary>从内核进程句柄与令牌读取身份；不接受管道消息自报的 SID、路径或 PID。</summary>
internal sealed record HandoffPeer(int ProcessId, long StartedAt, string UserSid, int SessionId, string ImagePath, bool Elevated)
{
    public static HandoffPeer Capture(Process process) => Capture(process.Id, process.SafeHandle);

    public static HandoffPeer Capture(int processId)
    {
        using var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Capture(processId, handle);
    }

    private static HandoffPeer Capture(int processId, SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out var created, out var exited, out _, out _) || exited != 0)
            throw new Win32Exception();
        if (!OpenProcessToken(handle, 0x0008, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var path = new StringBuilder(32768);
            var size = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size) ||
                !ProcessIdToSessionId(processId, out var session) ||
                !GetTokenInformation(token, 20, out var elevated, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(processId, DateTime.FromFileTimeUtc(created).Ticks,
                identity.User?.Value ?? throw new UnauthorizedAccessException(), session, path.ToString(), elevated != 0);
        }
    }

    public bool Matches(PipeStream pipe, bool server)
    {
        var success = server
            ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id)
            : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out id);
        return success && id == ProcessId && this == Capture(id);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int information, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
}
