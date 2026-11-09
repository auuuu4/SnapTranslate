using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SnapTranslate.Desktop.Capture;

/// <summary>
/// 贴图窗口：置顶、无边框，拖动窗口移动位置，滚轮缩放，Esc 或双击关闭。
/// 图片按截图时的逻辑尺寸展示，避免 Retina 截图像素尺寸看起来放大一倍。
/// </summary>
public partial class PinnedImageWindow : Window
{
    private const double MinimumZoom = 0.1;
    private const double MaximumZoom = 4;

    private readonly Bitmap _image;
    private readonly double _sourceScale;
    private double _zoom = 1;
    private Point _dragStart;
    private PixelPoint _dragWindow;
    private bool _dragging;

    /// <summary>
    /// 公开无参构造函数只为满足 Avalonia XAML 运行时加载器；实际使用一律走带图片的构造函数。
    /// </summary>
    public PinnedImageWindow() : this(CreatePlaceholderImage())
    {
    }

    /// <summary>用默认设置（渐变描边、1:1 逻辑尺寸）贴图。</summary>
    public PinnedImageWindow(Bitmap image) : this(image, PinnedImageEdgeEffect.Gradient, 1)
    {
    }

    /// <summary>
    /// 构造贴图窗口。<paramref name="scale"/> 是截图时的屏幕缩放比例，
    /// 用来把图像像素换算成逻辑尺寸；<paramref name="edgeEffect"/> 控制渐变描边。
    /// </summary>
    public PinnedImageWindow(Bitmap image, PinnedImageEdgeEffect edgeEffect, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(image);

        InitializeComponent();
        _image = image;
        _sourceScale = CaptureGeometry.NormalizeScale(scale);
        PinnedImage.Source = image;
        ApplyEdgeEffect(edgeEffect);
        ApplyWindowSize();

        Opened += OnWindowOpened;
    }

    private static WriteableBitmap CreatePlaceholderImage() => new(
        new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

    /// <summary>构造并立即显示贴图窗口，返回窗口实例方便调用方记录或关闭。</summary>
    public static PinnedImageWindow ShowPinned(
        Bitmap image,
        double scale = 1,
        PinnedImageEdgeEffect edgeEffect = PinnedImageEdgeEffect.Gradient)
    {
        var window = new PinnedImageWindow(image, edgeEffect, scale);
        window.Show();
        return window;
    }

    private Size BaseSize() => new(
        Math.Max(1, _image.PixelSize.Width / _sourceScale),
        Math.Max(1, _image.PixelSize.Height / _sourceScale));

    private void ApplyWindowSize()
    {
        var baseSize = BaseSize();
        Width = Math.Max(48, baseSize.Width * _zoom);
        Height = Math.Max(36, baseSize.Height * _zoom);
    }

    private void ApplyEdgeEffect(PinnedImageEdgeEffect edgeEffect)
    {
        if (edgeEffect == PinnedImageEdgeEffect.None)
        {
            PinFrame.BorderThickness = new Thickness(0);
            PinFrame.Padding = new Thickness(0);
            PinFrame.BorderBrush = Brushes.Transparent;
            return;
        }

        PinFrame.BorderThickness = new Thickness(1.5);
        PinFrame.Padding = new Thickness(2);
        PinFrame.BorderBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x59, 0xD6, 0xB5), 0),
                new GradientStop(Color.FromRgb(0x11, 0x85, 0x6B), 0.48),
                new GradientStop(Color.FromRgb(0x4E, 0x7F, 0xD1), 1)
            }
        };
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        Activate();
        PinFrame.Focus();
        CenterOnPrimaryScreen();
    }

    private void CenterOnPrimaryScreen()
    {
        var screen = Screens.Primary;
        if (screen is null) return;

        var scaling = RenderScaling;
        var width = Width * scaling;
        var height = Height * scaling;
        var workingArea = screen.WorkingArea;
        Position = new PixelPoint(
            workingArea.X + (int)Math.Round((workingArea.Width - width) / 2),
            workingArea.Y + (int)Math.Round((workingArea.Height - height) / 2));
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            Close();
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragWindow = Position;
        e.Pointer.Capture(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        e.Handled = true;
        var point = e.GetPosition(this);
        var scaling = RenderScaling;
        Position = new PixelPoint(
            _dragWindow.X + (int)Math.Round((point.X - _dragStart.X) * scaling),
            _dragWindow.Y + (int)Math.Round((point.Y - _dragStart.Y) * scaling));
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled) return;

        var factor = e.Delta.Y >= 0 ? 1.1 : 0.9;
        var zoom = Math.Clamp(_zoom * factor, MinimumZoom, MaximumZoom);
        if (Math.Abs(zoom - _zoom) < 1e-6) return;

        // 以指针位置为锚点缩放，避免图片在鼠标下方跳动。
        var pointer = e.GetPosition(this);
        var ratio = zoom / _zoom;
        var scaling = RenderScaling;
        Position = new PixelPoint(
            Position.X + (int)Math.Round(pointer.X * (1 - ratio) * scaling),
            Position.Y + (int)Math.Round(pointer.Y * (1 - ratio) * scaling));
        _zoom = zoom;
        ApplyWindowSize();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}