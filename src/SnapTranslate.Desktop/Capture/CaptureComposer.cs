using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SnapTranslate.Desktop.Capture;

/// <summary>
/// 把截图按选区裁剪并叠加标注，全部在内存里用 Avalonia 的绘制接口完成，
/// 不依赖具体窗口，所以能在无头环境（测试）里运行。
/// </summary>
public static class CaptureComposer
{
    /// <summary>
    /// 按 <paramref name="region"/>（截图位图像素坐标）裁剪 <paramref name="source"/>，
    /// 并把 <paramref name="annotations"/> 画到结果上。返回值由调用方负责释放。
    /// </summary>
    public static Bitmap Render(Bitmap source, IReadOnlyList<Annotation>? annotations, PixelRect region)
    {
        ArgumentNullException.ThrowIfNull(source);

        var imageBounds = new PixelRect(source.PixelSize);
        var crop = ClampRegion(region, imageBounds);
        var target = new RenderTargetBitmap(crop.Size, new Vector(96, 96));
        try
        {
            using (var context = target.CreateDrawingContext())
            {
                context.DrawImage(
                    source,
                    new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                    new Rect(0, 0, crop.Width, crop.Height));

                if (annotations is { Count: > 0 })
                {
                    AnnotationRenderer.Draw(
                        context,
                        annotations,
                        Matrix.CreateTranslation(-crop.X, -crop.Y),
                        new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                        CreateMosaicColorSource(source, annotations));
                }
            }

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>把裁剪区域限制在图像内；空区域回退成整张图，保证至少返回一个像素。</summary>
    public static PixelRect ClampRegion(PixelRect region, PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return new PixelRect();
        if (region.Width <= 0 || region.Height <= 0) return bounds;

        var left = Math.Clamp(region.X, 0, bounds.Width - 1);
        var top = Math.Clamp(region.Y, 0, bounds.Height - 1);
        var right = Math.Clamp(region.Right, left + 1, bounds.Width);
        var bottom = Math.Clamp(region.Bottom, top + 1, bounds.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static Func<PixelRect, Color?>? CreateMosaicColorSource(Bitmap source, IReadOnlyList<Annotation> annotations)
    {
        if (!annotations.Any(item => item.Kind == AnnotationKind.Mosaic)) return null;

        var size = source.PixelSize;
        var pixels = TryReadPixels(source);
        if (pixels is null || size.Width <= 0 || size.Height <= 0) return null;
        return area => AverageColor(pixels, size, area);
    }

    /// <summary>把任意可读位图转成 Bgra8888 / Unpremul 的整图字节，失败时返回 null（马赛克退化为占位块）。</summary>
    private static byte[]? TryReadPixels(Bitmap source)
    {
        var size = source.PixelSize;
        if (size.Width <= 0 || size.Height <= 0) return null;

        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        try
        {
            using var readable = new WriteableBitmap(
                size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var framebuffer = readable.Lock())
            {
                if (framebuffer.Format != PixelFormat.Bgra8888) return null;
                source.CopyPixels(framebuffer, AlphaFormat.Unpremul);
            }

            using (var framebuffer = readable.Lock())
            {
                var rowBytes = Math.Min(stride, framebuffer.RowBytes);
                for (var y = 0; y < size.Height; y++)
                {
                    Marshal.Copy(
                        framebuffer.Address + y * framebuffer.RowBytes,
                        pixels,
                        y * stride,
                        rowBytes);
                    if (rowBytes < stride)
                        Array.Clear(pixels, y * stride + rowBytes, stride - rowBytes);
                }
            }

            return pixels;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Color? AverageColor(byte[] pixels, PixelSize size, PixelRect area)
    {
        var left = Math.Clamp(area.X, 0, Math.Max(0, size.Width - 1));
        var top = Math.Clamp(area.Y, 0, Math.Max(0, size.Height - 1));
        var right = Math.Clamp(area.Right, left + 1, size.Width);
        var bottom = Math.Clamp(area.Bottom, top + 1, size.Height);

        long blue = 0, green = 0, red = 0, alpha = 0;
        long count = 0;
        for (var y = top; y < bottom; y++)
        {
            var rowOffset = y * size.Width * 4;
            for (var x = left; x < right; x++)
            {
                var index = rowOffset + x * 4;
                blue += pixels[index];
                green += pixels[index + 1];
                red += pixels[index + 2];
                alpha += pixels[index + 3];
                count++;
            }
        }

        if (count == 0) return null;
        return Color.FromArgb(
            (byte)(alpha / count),
            (byte)(red / count),
            (byte)(green / count),
            (byte)(blue / count));
    }
}
