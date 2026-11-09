using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Tests.Capture;

/// <summary>测试用的合成截图与像素读取工具。</summary>
internal static class CaptureTestImages
{
    /// <summary>生成一张渐变测试图，并解码成和真实截图一样的普通 Bitmap。</summary>
    public static Bitmap CreateGradientBitmap(int width, int height) => Decode(CreateGradientWriteable(width, height));

    /// <summary>生成一张可写的渐变测试图，每个像素颜色由坐标决定，便于断言。</summary>
    public static WriteableBitmap CreateGradientWriteable(int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        if (framebuffer.Format != PixelFormat.Bgra8888)
            throw new InvalidOperationException("测试期望 Bgra8888 帧缓冲。");

        for (var y = 0; y < height; y++)
        {
            var row = new byte[width * 4];
            for (var x = 0; x < width; x++)
            {
                var index = x * 4;
                row[index] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[index + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[index + 2] = (byte)(128 + ((x + y) % 64));
                row[index + 3] = 255;
            }

            Marshal.Copy(row, 0, framebuffer.Address + y * framebuffer.RowBytes, row.Length);
        }

        return bitmap;
    }

    /// <summary>先编码成 PNG 再解码，得到生产环境里 screencapture 之后那种普通 Bitmap。</summary>
    public static Bitmap Decode(WriteableBitmap source)
    {
        using var stream = new MemoryStream();
        source.Save(stream);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    /// <summary>把任意可读位图读成 Bgra8888 / Unpremul 的整图字节。</summary>
    public static byte[] ReadBgra(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        using var copy = new WriteableBitmap(
            size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var framebuffer = copy.Lock())
        {
            if (framebuffer.Format != PixelFormat.Bgra8888)
                throw new InvalidOperationException("测试期望 Bgra8888 帧缓冲。");
            bitmap.CopyPixels(framebuffer, AlphaFormat.Unpremul);
        }

        using (var framebuffer = copy.Lock())
        {
            var rowBytes = Math.Min(stride, framebuffer.RowBytes);
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(framebuffer.Address + y * framebuffer.RowBytes, pixels, y * stride, rowBytes);
        }

        return pixels;
    }

    /// <summary>按 Bgra8888 字节顺序取一个像素。</summary>
    public static Color PixelAt(byte[] pixels, int width, int x, int y)
    {
        var index = (y * width + x) * 4;
        return Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
    }

    /// <summary>构造一张用于截图浮层的整屏数据。</summary>
    public static CapturedScreen CreateScreen(int width, int height) => new(
        CreateGradientBitmap(width, height),
        new PixelRect(0, 0, width, height),
        1);
}
