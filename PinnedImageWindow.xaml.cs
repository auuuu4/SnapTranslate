using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace SnapTranslate;

public partial class PinnedImageWindow : Window
{
    private readonly BitmapSource _image;
    private double _initialWidth;
    private double _initialHeight;
    private Point _dragStart;
    private double _windowLeft;
    private double _windowTop;
    private bool _dragging;

    public PinnedImageWindow(
        BitmapSource image,
        PinnedImageEdgeEffect edgeEffect = PinnedImageEdgeEffect.Gradient)
    {
        InitializeComponent();
        _image = image;
        PinnedImage.Source = image;
        ApplyEdgeEffect(edgeEffect);
    }

    private void ApplyEdgeEffect(PinnedImageEdgeEffect edgeEffect)
    {
        if (edgeEffect == PinnedImageEdgeEffect.None)
        {
            PinFrame.BorderBrush = System.Windows.Media.Brushes.Transparent;
            PinFrame.BorderThickness = new Thickness(0);
            PinFrame.Padding = new Thickness(0);
            PinFrame.Margin = new Thickness(0);
            PinFrame.Background = System.Windows.Media.Brushes.Transparent;
            PinFrame.Effect = null;
            return;
        }

        var borderBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(System.Windows.Media.Color.FromRgb(0x59, 0xD6, 0xB5), 0),
                new GradientStop(System.Windows.Media.Color.FromRgb(0x11, 0x85, 0x6B), 0.48),
                new GradientStop(System.Windows.Media.Color.FromRgb(0x4E, 0x7F, 0xD1), 1)
            }
        };
        borderBrush.Freeze();

        PinFrame.BorderBrush = borderBrush;
        PinFrame.BorderThickness = new Thickness(1.5);
        PinFrame.Padding = new Thickness(2);
        PinFrame.Margin = new Thickness(8);
        PinFrame.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x21, 0x1F));
        PinFrame.Effect = new DropShadowEffect
        {
            Color = System.Windows.Media.Color.FromRgb(0x11, 0x85, 0x6B),
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.48
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigurePlacement();
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void ConfigurePlacement()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = Math.Max(1, dpi.DpiScaleX);
        var scaleY = Math.Max(1, dpi.DpiScaleY);
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var imageScale = Math.Min(
            1,
            Math.Min(screen.Width * 0.7 / _image.PixelWidth, screen.Height * 0.7 / _image.PixelHeight));
        var pixelWidth = Math.Max(80 * scaleX, _image.PixelWidth * imageScale);
        var pixelHeight = Math.Max(50 * scaleY, _image.PixelHeight * imageScale);
        _initialWidth = pixelWidth / scaleX;
        _initialHeight = pixelHeight / scaleY;
        PinnedImage.Width = _initialWidth;
        PinnedImage.Height = _initialHeight;
        Left = (screen.Left + (screen.Width - pixelWidth) / 2) / scaleX;
        Top = (screen.Top + (screen.Height - pixelHeight) / 2) / scaleY;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cursor = Forms.Cursor.Position;
        _dragStart = new Point(cursor.X, cursor.Y);
        _windowLeft = Left;
        _windowTop = Top;
        _dragging = true;
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cursor = Forms.Cursor.Position;
        var point = new Point(cursor.X, cursor.Y);
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _windowLeft + (point.X - _dragStart.X) / Math.Max(1, dpi.DpiScaleX);
        Top = _windowTop + (point.Y - _dragStart.Y) / Math.Max(1, dpi.DpiScaleY);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.08 : -0.08), 0.2, 1);
            return;
        }

        var factor = e.Delta > 0 ? 1.1 : 0.9;
        var oldWidth = PinnedImage.Width;
        var oldHeight = PinnedImage.Height;
        var dpi = VisualTreeHelper.GetDpi(this);
        var newWidth = Math.Clamp(
            oldWidth * factor,
            60,
            _image.PixelWidth * 4.0 / Math.Max(1, dpi.DpiScaleX));
        var newHeight = newWidth * _image.PixelHeight / _image.PixelWidth;
        var pointer = e.GetPosition(this);
        Left += pointer.X * (1 - newWidth / oldWidth);
        Top += pointer.Y * (1 - newHeight / oldHeight);
        PinnedImage.Width = newWidth;
        PinnedImage.Height = newHeight;
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        PinnedImage.Width = _initialWidth;
        PinnedImage.Height = _initialHeight;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardService.SetImageAsync(_image);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"复制失败：{ex.Message}", "SnapTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
            FileName = $"贴图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;
        BitmapEncoder encoder = string.Equals(Path.GetExtension(dialog.FileName), ".jpg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_image));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    private void Topmost_Click(object sender, RoutedEventArgs e) => Topmost = TopmostMenuItem.IsChecked;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
