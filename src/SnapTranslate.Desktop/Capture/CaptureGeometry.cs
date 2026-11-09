using Avalonia;

namespace SnapTranslate.Desktop.Capture;

/// <summary>选区上的手柄位置。</summary>
public enum SelectionHandle
{
    None,

    /// <summary>拖动手柄以外的选区内部 = 移动整个选区。</summary>
    Move,

    NorthWest,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West
}

/// <summary>
/// 选区几何与坐标系换算。浮层画布使用 DIP，截图位图使用像素，
/// 两者通过屏幕缩放比例（macOS Retina 通常是 2）换算。
/// </summary>
public static class CaptureGeometry
{
    /// <summary>选区的最小边长（DIP），小于它视为误触。</summary>
    public const double MinimumSelectionSize = 8;

    /// <summary>把任意两个点组成的矩形归一化成正宽高（支持右下往左上拖拽）。</summary>
    public static Rect Normalize(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X),
        Math.Abs(second.Y - first.Y));

    /// <summary>把矩形限制在画布内，并保证宽高不小于 <see cref="MinimumSelectionSize"/>。</summary>
    public static Rect Clamp(Rect rect, Size bounds)
    {
        var width = Math.Min(Math.Max(MinimumSelectionSize, rect.Width), Math.Max(MinimumSelectionSize, bounds.Width));
        var height = Math.Min(Math.Max(MinimumSelectionSize, rect.Height), Math.Max(MinimumSelectionSize, bounds.Height));
        var x = Math.Clamp(rect.X, 0, Math.Max(0, bounds.Width - width));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, bounds.Height - height));
        return new Rect(x, y, width, height);
    }

    /// <summary>平移选区并限制在画布内。</summary>
    public static Rect Move(Rect original, Vector delta, Size bounds) =>
        Clamp(new Rect(original.X + delta.X, original.Y + delta.Y, original.Width, original.Height), bounds);

    /// <summary>按手柄方向缩放选区，并保证不越界、不塌陷。</summary>
    public static Rect Resize(Rect original, SelectionHandle handle, Vector delta, Size bounds)
    {
        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;

        if (handle is SelectionHandle.NorthWest or SelectionHandle.West or SelectionHandle.SouthWest) left += delta.X;
        if (handle is SelectionHandle.NorthEast or SelectionHandle.East or SelectionHandle.SouthEast) right += delta.X;
        if (handle is SelectionHandle.NorthWest or SelectionHandle.North or SelectionHandle.NorthEast) top += delta.Y;
        if (handle is SelectionHandle.SouthWest or SelectionHandle.South or SelectionHandle.SouthEast) bottom += delta.Y;

        left = Math.Clamp(left, 0, bounds.Width);
        right = Math.Clamp(right, 0, bounds.Width);
        top = Math.Clamp(top, 0, bounds.Height);
        bottom = Math.Clamp(bottom, 0, bounds.Height);

        if (right - left < MinimumSelectionSize)
        {
            if (handle is SelectionHandle.NorthWest or SelectionHandle.West or SelectionHandle.SouthWest)
                left = right - MinimumSelectionSize;
            else
                right = left + MinimumSelectionSize;
        }

        if (bottom - top < MinimumSelectionSize)
        {
            if (handle is SelectionHandle.NorthWest or SelectionHandle.North or SelectionHandle.NorthEast)
                top = bottom - MinimumSelectionSize;
            else
                bottom = top + MinimumSelectionSize;
        }

        return Clamp(new Rect(left, top, right - left, bottom - top), bounds);
    }

    /// <summary>选区是否达到可用尺寸。</summary>
    public static bool IsUsableSelection(Rect rect) =>
        rect.Width >= MinimumSelectionSize && rect.Height >= MinimumSelectionSize;

    /// <summary>两个点之间的欧氏距离。Avalonia 的 Point - Point 仍返回 Point，这里统一封装。</summary>
    public static double Distance(Point first, Point second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>缩放比例兜底，避免 0 或负数导致除零。</summary>
    public static double NormalizeScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1d;

    /// <summary>图像像素坐标 → 屏幕 DIP 坐标。</summary>
    public static Point ToDipPoint(Point imagePoint, double scale) =>
        new(imagePoint.X / NormalizeScale(scale), imagePoint.Y / NormalizeScale(scale));

    /// <summary>屏幕 DIP 坐标 → 图像像素坐标。</summary>
    public static Point ToImagePoint(Point dip, double scale)
    {
        scale = NormalizeScale(scale);
        return new Point(dip.X * scale, dip.Y * scale);
    }

    /// <summary>屏幕 DIP 矩形 → 图像像素矩形（不取整，仅换算）。</summary>
    public static Rect ToImageRect(Rect dip, double scale)
    {
        scale = NormalizeScale(scale);
        return new Rect(dip.X * scale, dip.Y * scale, dip.Width * scale, dip.Height * scale);
    }

    /// <summary>屏幕 DIP 矩形 → 裁剪用的像素矩形，四舍五入并裁剪到图像范围内。</summary>
    public static PixelRect ToPixelRect(Rect dip, double scale, PixelSize imageSize)
    {
        scale = NormalizeScale(scale);
        if (imageSize.Width <= 0 || imageSize.Height <= 0) return new PixelRect();

        var left = Math.Clamp((int)Math.Round(dip.X * scale), 0, imageSize.Width - 1);
        var top = Math.Clamp((int)Math.Round(dip.Y * scale), 0, imageSize.Height - 1);
        var right = Math.Clamp((int)Math.Round(dip.Right * scale), left + 1, imageSize.Width);
        var bottom = Math.Clamp((int)Math.Round(dip.Bottom * scale), top + 1, imageSize.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>图像像素矩形 → 屏幕 DIP 矩形。</summary>
    public static Rect ToDipRect(PixelRect pixelRect, double scale) =>
        new(pixelRect.X / NormalizeScale(scale), pixelRect.Y / NormalizeScale(scale),
            pixelRect.Width / NormalizeScale(scale), pixelRect.Height / NormalizeScale(scale));
}