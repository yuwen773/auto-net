using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoNet.Service;

internal sealed class PowerGuard : IDisposable
{
    private SafeFileHandle? handle;
    private bool active;

    public void Update(bool enabled)
    {
        if (!GetSystemPowerStatus(out var power)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var needed = enabled && power.AcLineStatus == 1;
        if (needed == active) return;
        if (needed)
        {
            var reason = new ReasonContext { Version = 0, Flags = 1, Reason = "AutoNet 校园网无人值守连接" };
            if (handle is null || handle.IsInvalid) { handle?.Dispose(); handle = PowerCreateRequest(ref reason); }
            if (handle.IsInvalid || !PowerSetRequest(handle, 1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            active = true;
        }
        else
        {
            if (handle is not null && !handle.IsInvalid && !PowerClearRequest(handle, 1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            active = false;
        }
    }

    public void Dispose() { if (active && handle is not null) PowerClearRequest(handle, 1); handle?.Dispose(); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version, Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(SafeFileHandle handle, int requestType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(SafeFileHandle handle, int requestType);
}
