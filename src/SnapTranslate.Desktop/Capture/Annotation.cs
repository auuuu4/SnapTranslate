using Avalonia;
using Avalonia.Media;

namespace SnapTranslate.Desktop.Capture;

/// <summary>截图浮层里可选的工具。</summary>
public enum CaptureTool
{
    /// <summary>选择 / 移动选区，不产生标注。</summary>
    Select,

    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Text,
    Mosaic
}

/// <summary>一条标注的种类。</summary>
public enum AnnotationKind
{
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Text,
    Mosaic
}

/// <summary>
/// 一条标注。所有坐标、线宽、字号都使用「截图位图像素坐标」：
/// 原点在整张截图位图的左上角，与屏幕缩放比例无关，
/// 这样 <see cref="CaptureComposer"/> 落图时不依赖 Retina 比例。
/// </summary>
public sealed class Annotation
{
    /// <summary>标注种类。</summary>
    public AnnotationKind Kind { get; init; }

    /// <summary>矩形 / 椭圆 / 文字 / 马赛克的外框；文字只使用左上角。</summary>
    public Rect Bounds { get; init; }

    /// <summary>箭头起点。</summary>
    public Point Start { get; init; }

    /// <summary>箭头终点。</summary>
    public Point End { get; init; }

    /// <summary>画笔轨迹点。</summary>
    public IReadOnlyList<Point> Points { get; init; } = Array.Empty<Point>();

    /// <summary>文字内容。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>文字字号（图像像素）。</summary>
    public double FontSize { get; init; } = 24;

    /// <summary>标注颜色。</summary>
    public Color Color { get; init; } = Color.FromRgb(0xE5, 0x39, 0x35);

    /// <summary>线宽（图像像素）。</summary>
    public double Thickness { get; init; } = 4;

    /// <summary>马赛克块大小（图像像素）。</summary>
    public int MosaicBlockSize { get; init; } = 14;
}

/// <summary>标注的工厂方法与有效性判断，保证所有标注都用同一套坐标约定。</summary>
public static class Annotations
{
    /// <summary>矩形标注。</summary>
    public static Annotation Rectangle(Rect bounds, Color color, double thickness) => new()
    {
        Kind = AnnotationKind.Rectangle,
        Bounds = bounds,
        Color = color,
        Thickness = thickness
    };

    /// <summary>椭圆标注。</summary>
    public static Annotation Ellipse(Rect bounds, Color color, double thickness) => new()
    {
        Kind = AnnotationKind.Ellipse,
        Bounds = bounds,
        Color = color,
        Thickness = thickness
    };

    /// <summary>箭头标注。</summary>
    public static Annotation Arrow(Point start, Point end, Color color, double thickness) => new()
    {
        Kind = AnnotationKind.Arrow,
        Start = start,
        End = end,
        Color = color,
        Thickness = thickness
    };

    /// <summary>画笔标注。</summary>
    public static Annotation Pen(IReadOnlyList<Point> points, Color color, double thickness) => new()
    {
        Kind = AnnotationKind.Pen,
        Points = points,
        Color = color,
        Thickness = thickness
    };

    /// <summary>文字标注，<paramref name="origin"/> 是文字左上角。</summary>
    public static Annotation Text(Point origin, string text, Color color, double fontSize) => new()
    {
        Kind = AnnotationKind.Text,
        Bounds = new Rect(origin, new Size(0, 0)),
        Text = text,
        Color = color,
        FontSize = fontSize
    };

    /// <summary>马赛克标注。</summary>
    public static Annotation Mosaic(Rect bounds, int blockSize) => new()
    {
        Kind = AnnotationKind.Mosaic,
        Bounds = bounds,
        MosaicBlockSize = Math.Max(2, blockSize)
    };

    /// <summary>太小的标注不值得记录，避免误点时留下看不见的痕迹。</summary>
    public static bool IsMeaningful(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return annotation.Kind switch
        {
            AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Mosaic =>
                annotation.Bounds.Width >= 3 && annotation.Bounds.Height >= 3,
            AnnotationKind.Arrow => CaptureGeometry.Distance(annotation.Start, annotation.End) >= 4,
            AnnotationKind.Pen => annotation.Points.Count >= 2,
            AnnotationKind.Text => !string.IsNullOrWhiteSpace(annotation.Text),
            _ => false
        };
    }
}