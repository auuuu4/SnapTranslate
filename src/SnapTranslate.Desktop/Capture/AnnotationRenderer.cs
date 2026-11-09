using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace SnapTranslate.Desktop.Capture;

/// <summary>
/// 把标注画到 <see cref="DrawingContext"/> 上。浮层实时预览和最终落图共用这一份绘制逻辑，
/// 区别只在传入的变换矩阵：预览把图像像素缩放到 DIP，落图只做平移。
/// </summary>
internal static class AnnotationRenderer
{
    private static readonly Typeface TextTypeface = new(
        new FontFamily("Inter, PingFang SC, Helvetica Neue, Arial"),
        FontStyle.Normal,
        FontWeight.SemiBold);

    private static readonly IBrush MosaicPlaceholderBrush = new SolidColorBrush(Color.FromArgb(0x9A, 0x8A, 0x96, 0x9D));

    private static readonly IPen MosaicPlaceholderPen = new Pen(
        new SolidColorBrush(Color.FromArgb(0xD0, 0xEC, 0xF1, 0xEF)), 1.5,
        new DashStyle(new double[] { 4, 3 }, 0));

    /// <summary>
    /// 绘制一组标注。<paramref name="transform"/> 把标注坐标换算到目标坐标系，
    /// <paramref name="clipBounds"/> 是变换后坐标系里的裁剪范围。
    /// <paramref name="mosaicColorSource"/> 为空时马赛克画半透明占位块（浮层预览用），
    /// 非空时按块取平均色（落图用）。
    /// </summary>
    public static void Draw(
        DrawingContext context,
        IReadOnlyList<Annotation> annotations,
        Matrix transform,
        Rect clipBounds,
        Func<PixelRect, Color?>? mosaicColorSource)
    {
        if (annotations.Count == 0) return;

        using var transformState = context.PushTransform(transform);
        using var clipState = context.PushClip(clipBounds);
        foreach (var annotation in annotations)
        {
            DrawOne(context, annotation, mosaicColorSource);
        }
    }

    private static void DrawOne(DrawingContext context, Annotation annotation, Func<PixelRect, Color?>? mosaicColorSource)
    {
        switch (annotation.Kind)
        {
            case AnnotationKind.Rectangle:
                context.DrawRectangle(null, CreatePen(annotation), annotation.Bounds);
                break;
            case AnnotationKind.Ellipse:
                context.DrawEllipse(
                    null,
                    CreatePen(annotation),
                    annotation.Bounds.Center,
                    annotation.Bounds.Width / 2,
                    annotation.Bounds.Height / 2);
                break;
            case AnnotationKind.Arrow:
                DrawArrow(context, annotation);
                break;
            case AnnotationKind.Pen:
                DrawPen(context, annotation);
                break;
            case AnnotationKind.Text:
                DrawText(context, annotation);
                break;
            case AnnotationKind.Mosaic:
                DrawMosaic(context, annotation, mosaicColorSource);
                break;
        }
    }

    private static IPen CreatePen(Annotation annotation) => new Pen(
        new SolidColorBrush(annotation.Color),
        Math.Max(1, annotation.Thickness),
        null,
        PenLineCap.Round,
        PenLineJoin.Round);

    private static void DrawArrow(DrawingContext context, Annotation annotation)
    {
        var dx = annotation.End.X - annotation.Start.X;
        var dy = annotation.End.Y - annotation.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.5) return;

        var pen = CreatePen(annotation);
        context.DrawLine(pen, annotation.Start, annotation.End);

        var unit = new Vector(dx / length, dy / length);
        var normal = new Vector(-unit.Y, unit.X);
        var headLength = Math.Max(annotation.Thickness * 3, 9);
        var headWidth = headLength * 0.5;
        var basePoint = new Point(
            annotation.End.X - unit.X * headLength,
            annotation.End.Y - unit.Y * headLength);
        context.DrawLine(pen, annotation.End,
            new Point(basePoint.X + normal.X * headWidth, basePoint.Y + normal.Y * headWidth));
        context.DrawLine(pen, annotation.End,
            new Point(basePoint.X - normal.X * headWidth, basePoint.Y - normal.Y * headWidth));
    }

    private static void DrawPen(DrawingContext context, Annotation annotation)
    {
        var points = annotation.Points;
        if (points.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(points[0], false);
            for (var index = 1; index < points.Count; index++)
                geometryContext.LineTo(points[index]);
            geometryContext.EndFigure(false);
        }

        context.DrawGeometry(null, CreatePen(annotation), geometry);
    }

    private static void DrawText(DrawingContext context, Annotation annotation)
    {
        if (string.IsNullOrWhiteSpace(annotation.Text)) return;
        var formatted = new FormattedText(
            annotation.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            Math.Max(1, annotation.FontSize),
            new SolidColorBrush(annotation.Color));
        context.DrawText(formatted, annotation.Bounds.TopLeft);
    }

    private static void DrawMosaic(DrawingContext context, Annotation annotation, Func<PixelRect, Color?>? mosaicColorSource)
    {
        var bounds = annotation.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;

        if (mosaicColorSource is null)
        {
            context.FillRectangle(MosaicPlaceholderBrush, bounds);
            context.DrawRectangle(null, MosaicPlaceholderPen, bounds);
            return;
        }

        var blockSize = Math.Max(2, annotation.MosaicBlockSize);
        for (var y = bounds.Y; y < bounds.Bottom; y += blockSize)
        {
            for (var x = bounds.X; x < bounds.Right; x += blockSize)
            {
                var width = Math.Min(blockSize, bounds.Right - x);
                var height = Math.Min(blockSize, bounds.Bottom - y);
                if (width < 1 || height < 1) continue;

                var area = new PixelRect(
                    (int)Math.Floor(x),
                    (int)Math.Floor(y),
                    (int)Math.Ceiling(width),
                    (int)Math.Ceiling(height));
                var color = mosaicColorSource(area);
                if (color is null) continue;
                context.FillRectangle(new SolidColorBrush(color.Value), new Rect(x, y, width, height));
            }
        }
    }
}