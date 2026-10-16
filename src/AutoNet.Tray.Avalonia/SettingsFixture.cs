using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置场景共享夹具（#8，与 <see cref="ReadOnlyPortalFixture"/> 同一先例：测试与 QA 共用）：
/// 既有 DPAPI 凭据读写逻辑（与 Core Storage 同形，但路径由调用方给出）与密文明文扫描。
/// 只服务于隔离夹具目录，绝不触碰本机真实受限存储；解密结果只用于场景内核对，不进入任何证据。
/// </summary>
internal static class SettingsFixture
{
    /// <summary>设置场景的虚构配置：虚构网卡上的所有用户 Wi-Fi 配置。</summary>
    public static readonly WifiProfile Profile = new(
        "11111111-2222-3333-4444-555555555555", "TEST-ADAPTER", "TEST-AP-1", AllUsers: true, Connected: true);

    /// <summary>按既有 DPAPI(LocalMachine) 逻辑把凭据写入指定路径（明文只在内存，密文落盘）。</summary>
    public static void WriteCredentials(string path, Credentials credentials)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try { Storage.AtomicWrite(path, ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>按既有 DPAPI 解密逻辑读取指定路径的凭据；文件缺失返回 null。</summary>
    public static Credentials? ReadCredentials(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<Credentials>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>在密文字节中搜索 UTF-8 明文：命中即说明敏感内容以明文落盘（绝不允许，故事 36）。</summary>
    public static bool ContainsUtf8(byte[] haystack, string needle)
    {
        if (needle.Length == 0) return false;
        var pattern = Encoding.UTF8.GetBytes(needle);
        for (var start = 0; start + pattern.Length <= haystack.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < pattern.Length; offset++)
            {
                if (haystack[start + offset] == pattern[offset]) continue;
                matched = false;
                break;
            }

            if (matched) return true;
        }

        return false;
    }
}
