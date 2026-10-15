using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 真实窗口捕获：用 PrintWindow(PW_RENDERFULLCONTENT) 取窗口自身的合成内容并保存为 PNG。
/// 刻意不用 RenderTargetBitmap：实测 Avalonia 12.1.4 的离屏渲染路径会破坏部分中文长文本
/// （真实窗口在 GPU 与软件渲染模式下均正常，见 artifacts 中 render-rtb-defect 诊断图）。
/// </summary>
internal static class WindowCapture
{
    private const uint RenderFullContent = 0x00000002;

    public static void Save(Window window, string path)
    {
        var handle = window.TryGetPlatformHandle()?.Handle
            ?? throw new InvalidOperationException("窗口句柄不可用，无法捕获。");
        if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("GetWindowRect 失败。");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 1 || height < 1) throw new InvalidOperationException("窗口尺寸无效。");

        var windowDc = GetWindowDC(handle);
        var memoryDc = CreateCompatibleDC(windowDc);
        var bitmap = CreateCompatibleBitmap(windowDc, width, height);
        try
        {
            var previous = SelectObject(memoryDc, bitmap);
            try
            {
                if (!PrintWindow(handle, memoryDc, RenderFullContent)) throw new InvalidOperationException("PrintWindow 失败。");
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = width,
                        Height = -height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0
                    }
                };
                var buffer = Marshal.AllocHGlobal(width * height * 4);
                try
                {
                    if (GetDIBits(memoryDc, bitmap, 0, (uint)height, buffer, ref info, 0) == 0)
                        throw new InvalidOperationException("GetDIBits 失败。");

                    var stride = width * 4;
                    var pixels = new byte[stride * height];
                    Marshal.Copy(buffer, pixels, 0, pixels.Length);
                    using var writeable = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    using (var locked = writeable.Lock())
                    {
                        if (locked.RowBytes == stride)
                        {
                            Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
                        }
                        else
                        {
                            for (var row = 0; row < height; row++)
                                Marshal.Copy(pixels, row * stride, IntPtr.Add(locked.Address, row * locked.RowBytes), stride);
                        }
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    writeable.Save(path, PngBitmapEncoderOptions.Default);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { SelectObject(memoryDc, previous); }
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(handle, windowDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);
}
