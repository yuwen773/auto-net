using Avalonia.Controls;

namespace AutoNet.Tray.Avalonia;

/// <summary>共享的应用图标（assets/AutoNet.ico，以托管资源嵌入，单文件发布后仍可读取）。</summary>
internal static class AppIcons
{
    private const string EmbeddedResourceName = "AutoNet.Assets.Icon.ico";

    public static WindowIcon Load()
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("缺少嵌入式图标资源：" + EmbeddedResourceName);
        return new WindowIcon(stream);
    }
}
