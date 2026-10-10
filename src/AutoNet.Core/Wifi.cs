using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AutoNet.Core;

public sealed record WifiProfile(string InterfaceId, string InterfaceName, string Name, bool AllUsers, bool Connected)
{
    public override string ToString() => $"{InterfaceName} · {Name}" + (AllUsers ? "" : "（仅当前用户）");
}

public sealed record WifiLink(bool Connected, NetworkInterface? Adapter, IPAddress? Address, int Index, IPAddress[] DnsServers, bool OtherProfile = false, bool Connecting = false, bool Unavailable = false)
{
    public WifiState State => OtherProfile ? WifiState.OtherProfile : Connecting ? WifiState.Associating : Index == 0 || Unavailable ? WifiState.Unavailable :
        !Connected ? WifiState.Disconnected : Address is null ? WifiState.WaitingForAddress : WifiState.Ready;
}

public static class Wifi
{
    public static List<WifiProfile> Profiles()
    {
        using var client = new WlanClient();
        Check(WlanEnumInterfaces(client.Handle, IntPtr.Zero, out var list));
        var profiles = new List<WifiProfile>();
        try
        {
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<InterfaceInfo>(list + 8 + i * Marshal.SizeOf<InterfaceInfo>());
                var current = Current(client.Handle, info.Id);
                Check(WlanGetProfileList(client.Handle, ref info.Id, IntPtr.Zero, out var saved));
                try
                {
                    for (var j = 0; j < Marshal.ReadInt32(saved); j++)
                    {
                        var profile = Marshal.PtrToStructure<ProfileInfo>(saved + 8 + j * Marshal.SizeOf<ProfileInfo>());
                        profiles.Add(new(info.Id.ToString(), info.Description, profile.Name,
                            (profile.Flags & 2) == 0, info.State == 1 && profile.Name == current));
                    }
                }
                finally { WlanFreeMemory(saved); }
            }
        }
        finally { WlanFreeMemory(list); }
        return profiles;
    }

    public static WifiLink Read(Settings settings)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
            Guid.TryParse(n.Id, out var id) && id == Guid.Parse(settings.InterfaceId));
        if (adapter is null) return new(false, null, null, 0, []);
        using var client = new WlanClient();
        var id = Guid.Parse(settings.InterfaceId);
        Check(WlanEnumInterfaces(client.Handle, IntPtr.Zero, out var list));
        var state = 0;
        try
        {
            for (var i = 0; i < Marshal.ReadInt32(list); i++)
            {
                var info = Marshal.PtrToStructure<InterfaceInfo>(list + 8 + i * Marshal.SizeOf<InterfaceInfo>());
                if (info.Id == id) { state = info.State; break; }
            }
        }
        finally { WlanFreeMemory(list); }
        var properties = adapter.GetIPProperties();
        var address = properties.UnicastAddresses.Select(a => a.Address).FirstOrDefault(a =>
            a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        var link = new WifiLink(false,
            adapter, address, properties.GetIPv4Properties()?.Index ?? 0,
            properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray());
        return ReadConnection(link, state, settings.WifiProfile, () => Current(client.Handle, id));
    }

    // Native WLAN boundary: interface state is independent of current_connection availability.
    internal static WifiLink ReadConnection(WifiLink link, int state, string targetProfile, Func<string> currentProfile)
    {
        if (state is 3 or 5 or 6 or 7) return link with { Connecting = true };
        if (state == 4) return link;
        if (state != 1) return link with { Unavailable = true };
        var profile = currentProfile();
        return link with { Connected = profile == targetProfile, OtherProfile = profile != "" && profile != targetProfile, Unavailable = profile == "" };
    }

    public static void Connect(Settings settings)
    {
        using var client = new WlanClient();
        var id = Guid.Parse(settings.InterfaceId);
        var parameters = new ConnectionParameters { Mode = 0, Profile = settings.WifiProfile, BssType = 3 };
        // Connect only the explicitly configured saved profile; never reset the adapter.
        Check(WlanConnect(client.Handle, ref id, ref parameters, IntPtr.Zero));
    }

    public static void Disconnect(Settings settings)
    {
        // Recheck the selected profile immediately before the disruptive request.
        if (Read(settings).State != WifiState.Ready) return;
        using var client = new WlanClient();
        var id = Guid.Parse(settings.InterfaceId);
        Check(WlanDisconnect(client.Handle, ref id, IntPtr.Zero));
        // The monitoring loop waits for disconnection before requesting the saved profile.
    }

    private static string Current(IntPtr handle, Guid id)
    {
        var result = WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out _, out var data, out _);
        if (result == 5023) return ""; // ERROR_INVALID_STATE does not establish disconnected state.
        Check(result); // A privacy/access failure must not be mistaken for a disconnected Wi-Fi.
        try
        {
            var state = Marshal.ReadInt32(data);
            return state == 1 ? Marshal.PtrToStringUni(data + 8, 256)?.TrimEnd('\0') ?? "" : "";
        }
        finally { WlanFreeMemory(data); }
    }

    private static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
    private sealed class WlanClient : IDisposable
    {
        public IntPtr Handle { get; }
        public WlanClient() { Check(WlanOpenHandle(2, IntPtr.Zero, out _, out var handle)); Handle = handle; }
        public void Dispose() => WlanCloseHandle(Handle, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceInfo
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConnectionParameters
    {
        public int Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid;
        public IntPtr BssidList;
        public int BssType;
        public uint Flags;
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(IntPtr handle, ref Guid id, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out uint type);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanConnect(IntPtr handle, ref Guid id, ref ConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr handle, ref Guid id, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}
