using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AutoNet.Tray.Avalonia;

internal static class HandoffPipe
{
    public static NamedPipeServerStream Create(string name, string senderSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        // 不授予 FILE_CREATE_PIPE_INSTANCE；客户端也按这些精确权限打开，避免 GENERIC_WRITE 隐含该权限。
        const PipeAccessRights rights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        foreach (var sid in new[] { owner.Value, senderSid }.Distinct(StringComparer.Ordinal))
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), rights, AccessControlType.Allow));

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var address = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, address, descriptor.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = address };
            // Duplex + OVERLAPPED + FIRST_PIPE_INSTANCE；字节流 + REJECT_REMOTE_CLIENTS，单实例，不继承句柄。
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080003, 0x00000008, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { Marshal.FreeHGlobal(address); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint timeout, ref SecurityAttributes attributes);
}
