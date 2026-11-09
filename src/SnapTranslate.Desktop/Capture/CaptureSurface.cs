using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SnapTranslate.Desktop.Capture;

/// <summary>
/// 截图浮层的绘制层：压暗遮罩、选区边框、八个缩放手柄以及标注预览都在这里画。
/// 用自绘代替大量子控件，拖动时刷新更稳定。
/// </summary>
public sealed class CaptureSurface : Control
{
    /// <summary>手柄边长（DIP）。</summary>
    public const double HandleSize = 9;

    private static readonly IBrush ShadeBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x12, 0x10));
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.FromRgb(0x59, 0xD6, 0xB5)), 1.5);
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x85, 0x6B)), 1.5);

    /// <summary>当前选区（DIP 坐标），为空表示还没开始框选。</summary>
    public Rect? Selection { get; set; }

    /// <summary>已确认的标注（图像像素坐标）。</summary>
    public IReadOnlyList<Annotation> Annotations { get; set; } = Array.Empty<Annotation>();

    /// <summary>正在拖拽中的标注预览（图像像素坐标）。</summary>
    public Annotation? Draft { get; set; }

    /// <summary>图像像素 → DIP 的换算比例。</summary>
    public double ImageScale { get; set; } = 1;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        if (Selection is not { } selection)
        {
            context.FillRectangle(ShadeBrush, new Rect(size));
            return;
        }

        DrawShade(context, selection, size);

        var scale = CaptureGeometry.NormalizeScale(ImageScale);
        var transform = Matrix.CreateScale(1 / scale, 1 / scale);
        var clip = new Rect(0, 0, size.Width * scale, size.Height * scale);
        if (Annotations.Count > 0)
            AnnotationRenderer.Draw(context, Annotations, transform, clip, null);
        if (Draft is { } draft)
            AnnotationRenderer.Draw(context, new[] { draft }, transform, clip, null);

        context.DrawRectangle(null, SelectionPen, selection);
        DrawHandles(context, selection);
    }

    private static void DrawShade(DrawingContext context, Rect selection, Size size)
    {
        context.FillRectangle(ShadeBrush, new Rect(0, 0, size.Width, Math.Max(0, selection.Top)));
        context.FillRectangle(ShadeBrush, new Rect(0, selection.Bottom, size.Width, Math.Max(0, size.Height - selection.Bottom)));
        context.FillRectangle(ShadeBrush, new Rect(0, selection.Top, Math.Max(0, selection.Left), Math.Max(0, selection.Height)));
        context.FillRectangle(ShadeBrush,
            new Rect(selection.Right, selection.Top, Math.Max(0, size.Width - selection.Right), Math.Max(0, selection.Height)));
    }

    private static void DrawHandles(DrawingContext context, Rect selection)
    {
        var half = HandleSize / 2;
        foreach (var point in HandlePoints(selection))
        {
            var rect = new Rect(point.X - half, point.Y - half, HandleSize, HandleSize);
            context.FillRectangle(HandleBrush, rect);
            context.DrawRectangle(null, HandlePen, rect);
        }
    }

    /// <summary>八个手柄的中心点：四角 + 四边中点。</summary>
    public static IReadOnlyList<Point> HandlePoints(Rect selection) =>
    [
        selection.TopLeft,
        new Point(selection.Center.X, selection.Top),
        selection.TopRight,
        new Point(selection.Right, selection.Center.Y),
        selection.BottomRight,
        new Point(selection.Center.X, selection.Bottom),
        selection.BottomLeft,
        new Point(selection.Left, selection.Center.Y)
    ];
}
