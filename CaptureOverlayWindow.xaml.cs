using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace SnapTranslate;

public partial class CaptureOverlayWindow : Window
{
    private readonly BitmapSource _source;
    private readonly System.Drawing.Rectangle _screenBounds;
    private readonly IReadOnlyList<System.Drawing.Rectangle> _windowBounds;
    private readonly CaptureResultAction _defaultAction;
    private readonly bool _finishOnSelection;
    private readonly CaptureHistory<CaptureAnnotation> _history = new();
    private readonly List<Thumb> _handles;
    private Rect? _selection;
    private Rect? _pressedSmartRect;
    private CaptureAnnotation? _draft;
    private CaptureTool _tool = CaptureTool.Select;
    private InteractionMode _interaction;
    private SelectionHandle _resizeHandle;
    private Point _start;
    private Point _last;
    private Point _textOrigin;
    private Rect _originalSelection;
    private Color _color = Color.FromRgb(229, 57, 53);
    private bool _committingText;
    private bool _toolbarCompact;

    public BitmapSource? SelectedImage => ResultImage;
    public BitmapSource? ResultImage { get; private set; }
    public CaptureResultAction ResultAction { get; private set; }
    public System.Drawing.Rectangle ResultScreenBounds { get; private set; }

    public CaptureOverlayWindow(ScreenSnapshot snapshot, CaptureResultAction defaultAction)
        : this(snapshot, defaultAction, null, defaultAction == CaptureResultAction.Translate)
    {
    }

    public CaptureOverlayWindow(
        ScreenSnapshot snapshot,
        CaptureResultAction defaultAction,
        bool finishOnSelection)
        : this(snapshot, defaultAction, null, finishOnSelection)
    {
    }

    internal CaptureOverlayWindow(ScreenSnapshot snapshot, CaptureResultAction defaultAction, Rect? previewSelection)
        : this(snapshot, defaultAction, previewSelection, false)
    {
    }

    private CaptureOverlayWindow(
        ScreenSnapshot snapshot,
        CaptureResultAction defaultAction,
        Rect? previewSelection,
        bool finishOnSelection)
    {
        InitializeComponent();
        _source = snapshot.Image;
        _screenBounds = snapshot.Bounds;
        _windowBounds = snapshot.WindowBounds;
        _defaultAction = defaultAction;
        _finishOnSelection = finishOnSelection;
        _selection = previewSelection;
        _handles = new[] { HandleNW, HandleN, HandleNE, HandleE, HandleSE, HandleS, HandleSW, HandleW }.ToList();
        ScreenImage.Source = _source;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(-1), _screenBounds.Left, _screenBounds.Top,
                _screenBounds.Width, _screenBounds.Height, 0x0040);
        };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            UpdateSelectionVisuals();
            SetTool(CaptureTool.Select);
        };
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsToolbarSource(e.OriginalSource)) return;
        var point = ClampPoint(e.GetPosition(SelectionCanvas));

        if (TextEditor.Visibility == Visibility.Visible)
        {
            CommitTextEditor();
            if (TextEditor.Visibility == Visibility.Visible) return;
        }

        if (_selection is { } selected && e.ClickCount == 2 && selected.Contains(point) && _tool == CaptureTool.Select)
        {
            Finish(_defaultAction);
            e.Handled = true;
            return;
        }

        _start = point;
        _last = point;
        _pressedSmartRect = FindSmartRect(point);

        if (_selection is null)
        {
            BeginNewSelection(point);
        }
        else if (_tool == CaptureTool.Select)
        {
            var handle = GetHandle(e.OriginalSource);
            if (handle != SelectionHandle.None)
            {
                _interaction = InteractionMode.ResizingSelection;
                _resizeHandle = handle;
                _originalSelection = _selection.Value;
            }
            else if (_selection.Value.Contains(point))
            {
                _interaction = InteractionMode.MovingSelection;
                _originalSelection = _selection.Value;
            }
            else
            {
                _history.Clear();
                BeginNewSelection(point);
            }
        }
        else if (_selection.Value.Contains(point))
        {
            if (_tool == CaptureTool.Text)
            {
                OpenTextEditor(point);
                e.Handled = true;
                return;
            }
            _interaction = InteractionMode.Drawing;
            _draft = CreateAnnotation(_tool, point, point);
        }

        if (_interaction != InteractionMode.None)
        {
            Mouse.Capture(RootGrid);
            HideToolbars();
            SmartBorder.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void BeginNewSelection(Point point)
    {
        _interaction = InteractionMode.CreatingSelection;
        _selection = new Rect(point.X, point.Y, 1, 1);
        _draft = null;
        UpdateSelectionVisuals();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (IsToolbarSource(e.OriginalSource))
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        var point = ClampPoint(e.GetPosition(SelectionCanvas));
        UpdateMagnifier(point);
        if (_interaction == InteractionMode.None)
        {
            if (_selection is null) UpdateSmartSelection(point);
            UpdateCursor(point, e.OriginalSource);
            return;
        }

        switch (_interaction)
        {
            case InteractionMode.CreatingSelection:
                _selection = CaptureGeometry.Normalize(_start, point);
                break;
            case InteractionMode.MovingSelection:
            {
                var moved = CaptureGeometry.Move(_originalSelection, point - _start, CanvasSize());
                var delta = moved.TopLeft - _selection!.Value.TopLeft;
                _selection = moved;
                foreach (var annotation in _history.Items) annotation.Translate(delta);
                _draft?.Translate(delta);
                break;
            }
            case InteractionMode.ResizingSelection:
                _selection = CaptureGeometry.Resize(_originalSelection, _resizeHandle, point - _start, CanvasSize());
                break;
            case InteractionMode.Drawing:
                UpdateDraft(point);
                break;
        }

        _last = point;
        RebuildAnnotations();
        UpdateSelectionVisuals();
        e.Handled = true;
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_interaction == InteractionMode.None) return;
        var point = ClampPoint(e.GetPosition(SelectionCanvas));
        var interaction = _interaction;
        _interaction = InteractionMode.None;
        Mouse.Capture(null);

        if (interaction == InteractionMode.CreatingSelection)
        {
            var selected = CaptureGeometry.Normalize(_start, point);
            if (selected.Width < CaptureGeometry.MinimumSize || selected.Height < CaptureGeometry.MinimumSize)
            {
                selected = _pressedSmartRect ?? Rect.Empty;
            }
            _selection = selected.IsEmpty ? null : CaptureGeometry.Clamp(selected, CanvasSize());
            if (_selection is not null) SetTool(CaptureTool.Select);

            if (_selection is not null && _finishOnSelection)
            {
                Finish(_defaultAction);
                e.Handled = true;
                return;
            }
        }
        else if (interaction == InteractionMode.Drawing && _draft is not null)
        {
            UpdateDraft(point);
            if (_draft.IsMeaningful)
            {
                _history.Add(_draft);
            }
            _draft = null;
        }

        RebuildAnnotations();
        UpdateSelectionVisuals();
        UpdateHistoryButtons();
        e.Handled = true;
    }

    private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsToolbarSource(e.OriginalSource)) return;
        if (TextEditor.Visibility == Visibility.Visible)
        {
            CloseTextEditor(false);
        }
        else if (_draft is not null || _interaction == InteractionMode.Drawing)
        {
            _draft = null;
            _interaction = InteractionMode.None;
            Mouse.Capture(null);
            RebuildAnnotations();
        }
        else if (_selection is not null)
        {
            _selection = null;
            _history.Clear();
            RebuildAnnotations();
            UpdateSelectionVisuals();
        }
        else
        {
            CancelCapture();
        }
        e.Handled = true;
    }

    private void UpdateSmartSelection(Point point)
    {
        var smart = FindSmartRect(point);
        if (smart is null)
        {
            SmartBorder.Visibility = Visibility.Collapsed;
            return;
        }

        SetCanvasRect(SmartBorder, smart.Value);
        SmartBorder.Visibility = Visibility.Visible;
        var pixelRect = ToPixelRect(smart.Value);
        SizeText.Text = $"{pixelRect.Width} × {pixelRect.Height}";
        SizeBadge.Visibility = Visibility.Visible;
        PositionBadge(smart.Value);
    }

    private Rect? FindSmartRect(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        var physicalX = _screenBounds.Left + point.X * _source.PixelWidth / ActualWidth;
        var physicalY = _screenBounds.Top + point.Y * _source.PixelHeight / ActualHeight;
        var candidate = _windowBounds.FirstOrDefault(rect => rect.Contains((int)physicalX, (int)physicalY));
        if (candidate.Width <= 0 || candidate.Height <= 0) return null;
        return new Rect(
            (candidate.Left - _screenBounds.Left) * ActualWidth / _source.PixelWidth,
            (candidate.Top - _screenBounds.Top) * ActualHeight / _source.PixelHeight,
            candidate.Width * ActualWidth / _source.PixelWidth,
            candidate.Height * ActualHeight / _source.PixelHeight);
    }

    private void UpdateSelectionVisuals()
    {
        var width = Math.Max(0, SelectionCanvas.ActualWidth);
        var height = Math.Max(0, SelectionCanvas.ActualHeight);
        if (_selection is not { } selected || width <= 0 || height <= 0)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            AnnotationCanvas.Clip = null;
            foreach (var handle in _handles) handle.Visibility = Visibility.Collapsed;
            HideToolbars();
            if (_interaction == InteractionMode.None) SizeBadge.Visibility = Visibility.Collapsed;
            SetCanvasRect(ShadeTop, new Rect(0, 0, width, height));
            SetCanvasRect(ShadeLeft, Rect.Empty);
            SetCanvasRect(ShadeRight, Rect.Empty);
            SetCanvasRect(ShadeBottom, Rect.Empty);
            return;
        }

        selected = CaptureGeometry.Clamp(selected, CanvasSize());
        _selection = selected;
        SelectionBorder.Visibility = Visibility.Visible;
        SetCanvasRect(SelectionBorder, selected);
        AnnotationCanvas.Width = width;
        AnnotationCanvas.Height = height;
        AnnotationCanvas.Clip = new RectangleGeometry(selected);

        SetCanvasRect(ShadeTop, new Rect(0, 0, width, selected.Top));
        SetCanvasRect(ShadeLeft, new Rect(0, selected.Top, selected.Left, selected.Height));
        SetCanvasRect(ShadeRight, new Rect(selected.Right, selected.Top, Math.Max(0, width - selected.Right), selected.Height));
        SetCanvasRect(ShadeBottom, new Rect(0, selected.Bottom, width, Math.Max(0, height - selected.Bottom)));

        PositionHandles(selected);
        var pixelRect = ToPixelRect(selected);
        SizeText.Text = $"{pixelRect.Width} × {pixelRect.Height}";
        SizeBadge.Visibility = Visibility.Visible;
        PositionBadge(selected);

        if (_interaction == InteractionMode.None && TextEditor.Visibility != Visibility.Visible)
        {
            PositionToolbar(selected);
        }
        SelectionCanvas.InvalidateMeasure();
        SelectionCanvas.InvalidateArrange();
    }

    private void PositionHandles(Rect rect)
    {
        var points = new[]
        {
            rect.TopLeft, new Point(rect.Left + rect.Width / 2, rect.Top), rect.TopRight,
            new Point(rect.Right, rect.Top + rect.Height / 2), rect.BottomRight,
            new Point(rect.Left + rect.Width / 2, rect.Bottom), rect.BottomLeft,
            new Point(rect.Left, rect.Top + rect.Height / 2)
        };
        for (var index = 0; index < _handles.Count; index++)
        {
            var handle = _handles[index];
            handle.Visibility = Visibility.Visible;
            Canvas.SetLeft(handle, points[index].X - handle.Width / 2);
            Canvas.SetTop(handle, points[index].Y - handle.Height / 2);
        }
    }

    private void PositionToolbar(Rect selection)
    {
        var compact = selection.Width < 760;
        SetToolbarCompact(compact);
        var activeToolbar = compact ? CompactToolbar : Toolbar;
        activeToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = activeToolbar.DesiredSize.Width;
        var toolbarHeight = activeToolbar.DesiredSize.Height;
        var canvasWidth = SelectionCanvas.ActualWidth;
        var canvasHeight = SelectionCanvas.ActualHeight;
        double left;
        double top;

        if (selection.Width < toolbarWidth + 24 && selection.Right + toolbarWidth + 8 <= canvasWidth)
        {
            left = selection.Right + 8;
            top = Math.Clamp(selection.Top, 8, Math.Max(8, canvasHeight - toolbarHeight - 8));
        }
        else if (selection.Width < toolbarWidth + 24 && selection.Left - toolbarWidth - 8 >= 0)
        {
            left = selection.Left - toolbarWidth - 8;
            top = Math.Clamp(selection.Top, 8, Math.Max(8, canvasHeight - toolbarHeight - 8));
        }
        else
        {
            left = Math.Clamp(
                selection.Left + (selection.Width - toolbarWidth) / 2,
                8,
                Math.Max(8, canvasWidth - toolbarWidth - 8));
            top = selection.Bottom + 8;
            if (top + toolbarHeight > canvasHeight - 8) top = selection.Top - toolbarHeight - 8;
            if (top < 8) top = Math.Clamp(selection.Bottom - toolbarHeight - 8, 8, Math.Max(8, canvasHeight - toolbarHeight - 8));
        }
        Canvas.SetLeft(activeToolbar, left);
        Canvas.SetTop(activeToolbar, top);
    }

    private void SetToolbarCompact(bool compact)
    {
        if (_toolbarCompact != compact)
        {
            CompactToolsPanel.Visibility = Visibility.Collapsed;
            if (compact)
            {
                ToolOptionsInlineSlot.Content = null;
                ToolOptionsCompactSlot.Content = EditingToolsGroup;
            }
            else
            {
                ToolOptionsCompactSlot.Content = null;
                ToolOptionsInlineSlot.Content = EditingToolsGroup;
            }
            _toolbarCompact = compact;
        }
        Toolbar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactToolbar.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideToolbars()
    {
        Toolbar.Visibility = Visibility.Collapsed;
        CompactToolbar.Visibility = Visibility.Collapsed;
        CompactToolsPanel.Visibility = Visibility.Collapsed;
    }

    private void PositionBadge(Rect selection)
    {
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var top = selection.Top - SizeBadge.DesiredSize.Height - 5;
        if (top < 4) top = selection.Top + 5;
        Canvas.SetLeft(SizeBadge, Math.Clamp(selection.Left, 4, Math.Max(4, SelectionCanvas.ActualWidth - SizeBadge.DesiredSize.Width - 4)));
        Canvas.SetTop(SizeBadge, top);
    }

    private void UpdateMagnifier(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || TextEditor.Visibility == Visibility.Visible) return;
        var pixelX = Math.Clamp((int)Math.Round(point.X * _source.PixelWidth / ActualWidth), 0, _source.PixelWidth - 1);
        var pixelY = Math.Clamp((int)Math.Round(point.Y * _source.PixelHeight / ActualHeight), 0, _source.PixelHeight - 1);
        const int cropWidth = 20;
        const int cropHeight = 15;
        var x = Math.Clamp(pixelX - cropWidth / 2, 0, Math.Max(0, _source.PixelWidth - cropWidth));
        var y = Math.Clamp(pixelY - cropHeight / 2, 0, Math.Max(0, _source.PixelHeight - cropHeight));
        var width = Math.Min(cropWidth, _source.PixelWidth - x);
        var height = Math.Min(cropHeight, _source.PixelHeight - y);
        MagnifierImage.Source = new CroppedBitmap(_source, new Int32Rect(x, y, width, height));

        var color = ReadPixel(pixelX, pixelY);
        PixelInfo.Text = $"{_screenBounds.Left + pixelX}, {_screenBounds.Top + pixelY}   #{color.R:X2}{color.G:X2}{color.B:X2}";
        Magnifier.Visibility = _selection is null || _interaction != InteractionMode.None
            ? Visibility.Visible
            : Visibility.Collapsed;
        Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = point.X + 22;
        var top = point.Y + 22;
        if (left + Magnifier.DesiredSize.Width > SelectionCanvas.ActualWidth) left = point.X - Magnifier.DesiredSize.Width - 22;
        if (top + Magnifier.DesiredSize.Height > SelectionCanvas.ActualHeight) top = point.Y - Magnifier.DesiredSize.Height - 22;
        Canvas.SetLeft(Magnifier, Math.Max(4, left));
        Canvas.SetTop(Magnifier, Math.Max(4, top));
    }

    private Color ReadPixel(int x, int y)
    {
        try
        {
            var bytesPerPixel = Math.Max(1, (_source.Format.BitsPerPixel + 7) / 8);
            var bytes = new byte[bytesPerPixel];
            _source.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, bytesPerPixel, 0);
            return bytesPerPixel >= 3 ? Color.FromRgb(bytes[2], bytes[1], bytes[0]) : Colors.White;
        }
        catch { return Colors.White; }
    }

    private void UpdateCursor(Point point, object source)
    {
        var handle = GetHandle(source);
        if (handle != SelectionHandle.None) return;
        if (_selection is { } selected && selected.Contains(point) && _tool == CaptureTool.Select)
            Cursor = Cursors.SizeAll;
        else
            Cursor = _tool == CaptureTool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    private CaptureAnnotation CreateAnnotation(CaptureTool tool, Point start, Point end)
    {
        var thickness = LineWidthSlider.Value;
        return tool switch
        {
            CaptureTool.Rectangle => new BoxAnnotation(tool, CaptureGeometry.Normalize(start, end), _color, thickness),
            CaptureTool.Ellipse => new BoxAnnotation(tool, CaptureGeometry.Normalize(start, end), _color, thickness),
            CaptureTool.Arrow => new ArrowAnnotation(start, end, _color, thickness),
            CaptureTool.Pen => new PenAnnotation(new List<Point> { start }, _color, thickness),
            CaptureTool.Mosaic => new MosaicAnnotation(CaptureGeometry.Normalize(start, end), Math.Max(8, thickness * 3)),
            _ => throw new InvalidOperationException($"Unsupported capture tool: {tool}")
        };
    }

    private void UpdateDraft(Point point)
    {
        if (_draft is null || _selection is null) return;
        point = ClampToRect(point, _selection.Value);
        switch (_draft)
        {
            case BoxAnnotation box:
                box.Bounds = CaptureGeometry.Normalize(_start, point);
                break;
            case ArrowAnnotation arrow:
                arrow.End = point;
                break;
            case PenAnnotation pen:
                if ((point - pen.Points[^1]).Length >= 1.5) pen.Points.Add(point);
                break;
            case MosaicAnnotation mosaic:
                mosaic.Bounds = CaptureGeometry.Normalize(_start, point);
                break;
        }
    }

    private void RebuildAnnotations()
    {
        AnnotationCanvas.Children.Clear();
        foreach (var annotation in _history.Items)
        {
            AnnotationCanvas.Children.Add(annotation is MosaicAnnotation mosaic
                ? CreateMosaicPreview(mosaic)
                : annotation.CreateElement());
        }
        if (_draft is not null) AnnotationCanvas.Children.Add(_draft.CreateElement());
    }

    private UIElement CreateMosaicPreview(MosaicAnnotation mosaic)
    {
        var pixelRect = ToPixelRect(mosaic.Bounds);
        var preview = new WriteableBitmap(new CroppedBitmap(_source, pixelRect));
        var blockSize = Math.Max(4, (int)Math.Round(mosaic.BlockSize * pixelRect.Width / Math.Max(1, mosaic.Bounds.Width)));
        Pixelate(preview, new Int32Rect(0, 0, preview.PixelWidth, preview.PixelHeight), blockSize);
        var image = new Image
        {
            Source = preview,
            Width = mosaic.Bounds.Width,
            Height = mosaic.Bounds.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, mosaic.Bounds.Left);
        Canvas.SetTop(image, mosaic.Bounds.Top);
        return image;
    }

    private void OpenTextEditor(Point point)
    {
        if (_selection is null) return;
        _textOrigin = point;
        TextEditor.Text = string.Empty;
        TextEditor.Width = Math.Max(140, Math.Min(320, _selection.Value.Right - point.X));
        TextEditor.MaxHeight = Math.Max(40, _selection.Value.Bottom - point.Y);
        TextEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(TextEditor, point.X);
        Canvas.SetTop(TextEditor, point.Y);
        HideToolbars();
        Magnifier.Visibility = Visibility.Collapsed;
        TextEditor.Focus();
    }

    private void CommitTextEditor()
    {
        if (_committingText || TextEditor.Visibility != Visibility.Visible) return;
        _committingText = true;
        var text = TextEditor.Text.Trim();
        if (text.Length > 0)
        {
            _history.Add(new TextAnnotation(_textOrigin, text, _color, Math.Max(14, LineWidthSlider.Value * 4.5)));
        }
        CloseTextEditor(false);
        RebuildAnnotations();
        UpdateHistoryButtons();
        _committingText = false;
    }

    private void CloseTextEditor(bool keepText)
    {
        if (!keepText) TextEditor.Text = string.Empty;
        TextEditor.Visibility = Visibility.Collapsed;
        Focus();
        UpdateSelectionVisuals();
    }

    private void TextEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseTextEditor(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            CommitTextEditor();
            e.Handled = true;
        }
    }

    private void TextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_committingText && TextEditor.Visibility == Visibility.Visible && !IsToolbarSource(e.NewFocus)) CommitTextEditor();
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<CaptureTool>(tag, out var tool)) return;
        SetTool(tool);
        // 选完工具就把工具条收起来，免得挡住很小的选区
        if (_toolbarCompact && CompactToolsPanel.Visibility == Visibility.Visible)
        {
            CompactToolsPanel.Visibility = Visibility.Collapsed;
            if (_selection is { } selection) PositionToolbar(selection);
            UpdateSelectionVisuals();
        }
    }

    private void SetTool(CaptureTool tool)
    {
        _tool = tool;
        var active = new SolidColorBrush(Color.FromRgb(17, 133, 107));
        foreach (var button in new[] { SelectToolButton, RectangleToolButton, EllipseToolButton, ArrowToolButton, PenToolButton, TextToolButton, MosaicToolButton })
            button.Background = Equals(button.Tag?.ToString(), tool.ToString()) ? active : Brushes.Transparent;

        // 紧凑工具条收起后，省略号按钮直接显示当前工具，避免看不出在用哪支笔
        var glyph = tool switch
        {
            CaptureTool.Rectangle => "□",
            CaptureTool.Ellipse => "○",
            CaptureTool.Arrow => "→",
            CaptureTool.Pen => "✎",
            CaptureTool.Text => "T",
            CaptureTool.Mosaic => "▦",
            _ => "•••"
        };
        MoreToolsButton.Content = glyph;
        MoreToolsButton.ToolTip = "更多标注工具（当前：" + tool switch
        {
            CaptureTool.Rectangle => "矩形",
            CaptureTool.Ellipse => "椭圆",
            CaptureTool.Arrow => "箭头",
            CaptureTool.Pen => "画笔",
            CaptureTool.Text => "文字",
            CaptureTool.Mosaic => "马赛克",
            _ => "选择"
        } + "）";

        Cursor = tool == CaptureTool.Select ? Cursors.Arrow : tool == CaptureTool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;
        _color = (Color)ColorConverter.ConvertFromString(value);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _history.Undo();
        RebuildAnnotations();
        UpdateHistoryButtons();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        _history.Redo();
        RebuildAnnotations();
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Finish(CaptureResultAction.Copy);
    private void Save_Click(object sender, RoutedEventArgs e) => Finish(CaptureResultAction.Save);
    private void Pin_Click(object sender, RoutedEventArgs e) => Finish(CaptureResultAction.Pin);
    private void Ocr_Click(object sender, RoutedEventArgs e) => Finish(CaptureResultAction.Ocr);
    private void Translate_Click(object sender, RoutedEventArgs e) => Finish(CaptureResultAction.Translate);
    private void Complete_Click(object sender, RoutedEventArgs e) => Finish(_defaultAction);
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelCapture();
    private void MoreTools_Click(object sender, RoutedEventArgs e)
    {
        var expand = CompactToolsPanel.Visibility != Visibility.Visible;
        CompactToolsPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        if (_selection is { } selection) PositionToolbar(selection);
        UpdateSelectionVisuals();
    }

    private void Finish(CaptureResultAction action)
    {
        CommitTextEditor();
        if (_selection is null) return;
        var pixelRect = ToPixelRect(_selection.Value);
        ResultScreenBounds = new System.Drawing.Rectangle(
            _screenBounds.Left + pixelRect.X,
            _screenBounds.Top + pixelRect.Y,
            pixelRect.Width,
            pixelRect.Height);
        ResultImage = ComposeResult();
        ResultAction = action;
        DialogResult = true;
    }

    private BitmapSource ComposeResult()
    {
        var selection = _selection ?? throw new InvalidOperationException("No capture selection.");
        var pixelRect = ToPixelRect(selection);
        var cropped = new CroppedBitmap(_source, pixelRect);
        var baseImage = new WriteableBitmap(cropped);

        foreach (var mosaic in _history.Items.OfType<MosaicAnnotation>())
        {
            var relative = new Rect(mosaic.Bounds.X - selection.X, mosaic.Bounds.Y - selection.Y,
                mosaic.Bounds.Width, mosaic.Bounds.Height);
            Pixelate(baseImage, ToPixelRect(relative, selection.Size, pixelRect.Width, pixelRect.Height),
                Math.Max(5, (int)Math.Round(mosaic.BlockSize * pixelRect.Width / selection.Width)));
        }

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(baseImage, new Rect(0, 0, pixelRect.Width, pixelRect.Height));
            var scaleX = pixelRect.Width / selection.Width;
            var scaleY = pixelRect.Height / selection.Height;
            foreach (var annotation in _history.Items.Where(item => item is not MosaicAnnotation))
                annotation.Draw(drawing, selection.TopLeft, scaleX, scaleY);
        }

        var result = new RenderTargetBitmap(pixelRect.Width, pixelRect.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static void Pixelate(WriteableBitmap bitmap, Int32Rect requested, int blockSize)
    {
        var rect = new Int32Rect(
            Math.Clamp(requested.X, 0, bitmap.PixelWidth),
            Math.Clamp(requested.Y, 0, bitmap.PixelHeight),
            Math.Clamp(requested.Width, 0, bitmap.PixelWidth - Math.Clamp(requested.X, 0, bitmap.PixelWidth)),
            Math.Clamp(requested.Height, 0, bitmap.PixelHeight - Math.Clamp(requested.Y, 0, bitmap.PixelHeight)));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var stride = rect.Width * 4;
        var pixels = new byte[stride * rect.Height];
        bitmap.CopyPixels(rect, pixels, stride, 0);
        blockSize = Math.Max(3, blockSize);
        for (var by = 0; by < rect.Height; by += blockSize)
        for (var bx = 0; bx < rect.Width; bx += blockSize)
        {
            var index = by * stride + bx * 4;
            var b = pixels[index];
            var g = pixels[index + 1];
            var r = pixels[index + 2];
            var a = pixels[index + 3];
            for (var y = by; y < Math.Min(rect.Height, by + blockSize); y++)
            for (var x = bx; x < Math.Min(rect.Width, bx + blockSize); x++)
            {
                var target = y * stride + x * 4;
                pixels[target] = b;
                pixels[target + 1] = g;
                pixels[target + 2] = r;
                pixels[target + 3] = a;
            }
        }
        bitmap.WritePixels(rect, pixels, stride, 0);
    }

    private Int32Rect ToPixelRect(Rect rect)
        => ToPixelRect(rect, CanvasSize(), _source.PixelWidth, _source.PixelHeight);

    private static Int32Rect ToPixelRect(Rect rect, Size canvas, int pixelWidth, int pixelHeight)
    {
        var x = Math.Clamp((int)Math.Round(rect.X * pixelWidth / Math.Max(1, canvas.Width)), 0, Math.Max(0, pixelWidth - 1));
        var y = Math.Clamp((int)Math.Round(rect.Y * pixelHeight / Math.Max(1, canvas.Height)), 0, Math.Max(0, pixelHeight - 1));
        var right = Math.Clamp((int)Math.Round(rect.Right * pixelWidth / Math.Max(1, canvas.Width)), x + 1, pixelWidth);
        var bottom = Math.Clamp((int)Math.Round(rect.Bottom * pixelHeight / Math.Max(1, canvas.Height)), y + 1, pixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (TextEditor.Visibility == Visibility.Visible) return;
        if (e.Key == Key.Escape)
        {
            CancelCapture();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Finish(_defaultAction);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Undo_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ||
                 (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            Redo_Click(sender, e);
            e.Handled = true;
        }
    }

    private void CancelCapture()
    {
        DialogResult = false;
        Close();
    }

    private Size CanvasSize() => new(Math.Max(1, SelectionCanvas.ActualWidth), Math.Max(1, SelectionCanvas.ActualHeight));

    private Point ClampPoint(Point point)
        => new(Math.Clamp(point.X, 0, Math.Max(0, SelectionCanvas.ActualWidth)),
            Math.Clamp(point.Y, 0, Math.Max(0, SelectionCanvas.ActualHeight)));

    private static Point ClampToRect(Point point, Rect rect)
        => new(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom));

    private static void SetCanvasRect(FrameworkElement element, Rect rect)
    {
        if (rect.IsEmpty || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)) rect = new Rect(0, 0, 0, 0);
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
        element.Width = Math.Max(0, rect.Width);
        element.Height = Math.Max(0, rect.Height);
    }

    private SelectionHandle GetHandle(object source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Thumb { Tag: string tag } && Enum.TryParse<SelectionHandle>(tag, out var handle)) return handle;
            current = VisualTreeHelper.GetParent(current);
        }
        return SelectionHandle.None;
    }

    private bool IsToolbarSource(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, Toolbar)
                || ReferenceEquals(current, CompactToolbar)
                || ReferenceEquals(current, TextEditor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private enum InteractionMode { None, CreatingSelection, MovingSelection, ResizingSelection, Drawing }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

internal abstract class CaptureAnnotation
{
    protected CaptureAnnotation(Color color, double thickness)
    {
        Color = color;
        Thickness = thickness;
    }

    public Color Color { get; }
    public double Thickness { get; }
    public abstract bool IsMeaningful { get; }
    public abstract UIElement CreateElement();
    public abstract void Draw(DrawingContext drawing, Point offset, double scaleX, double scaleY);
    public abstract void Translate(Vector delta);

    protected Pen CreatePen(double scale = 1)
        => new(new SolidColorBrush(Color), Math.Max(1, Thickness * scale))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
}

internal sealed class BoxAnnotation : CaptureAnnotation
{
    public BoxAnnotation(CaptureTool tool, Rect bounds, Color color, double thickness) : base(color, thickness)
    {
        Tool = tool;
        Bounds = bounds;
    }

    public CaptureTool Tool { get; }
    public Rect Bounds { get; set; }
    public override bool IsMeaningful => Bounds.Width >= 3 && Bounds.Height >= 3;

    public override UIElement CreateElement()
    {
        Shape shape = Tool == CaptureTool.Ellipse ? new Ellipse() : new Rectangle();
        shape.Width = Bounds.Width;
        shape.Height = Bounds.Height;
        shape.Stroke = new SolidColorBrush(Color);
        shape.StrokeThickness = Thickness;
        shape.Fill = Brushes.Transparent;
        shape.IsHitTestVisible = false;
        Canvas.SetLeft(shape, Bounds.Left);
        Canvas.SetTop(shape, Bounds.Top);
        return shape;
    }

    public override void Draw(DrawingContext drawing, Point offset, double scaleX, double scaleY)
    {
        var rect = new Rect((Bounds.X - offset.X) * scaleX, (Bounds.Y - offset.Y) * scaleY,
            Bounds.Width * scaleX, Bounds.Height * scaleY);
        if (Tool == CaptureTool.Ellipse) drawing.DrawEllipse(null, CreatePen((scaleX + scaleY) / 2),
            new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
        else drawing.DrawRectangle(null, CreatePen((scaleX + scaleY) / 2), rect);
    }

    public override void Translate(Vector delta) => Bounds = new Rect(Bounds.TopLeft + delta, Bounds.Size);
}

internal sealed class ArrowAnnotation : CaptureAnnotation
{
    public ArrowAnnotation(Point start, Point end, Color color, double thickness) : base(color, thickness)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; private set; }
    public Point End { get; set; }
    public override bool IsMeaningful => (End - Start).Length >= 5;

    public override UIElement CreateElement()
    {
        var path = new Path
        {
            Data = CreateArrowGeometry(Start, End, Math.Max(10, Thickness * 3)),
            Stroke = new SolidColorBrush(Color),
            StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = new SolidColorBrush(Color),
            IsHitTestVisible = false
        };
        return path;
    }

    public override void Draw(DrawingContext drawing, Point offset, double scaleX, double scaleY)
    {
        var start = new Point((Start.X - offset.X) * scaleX, (Start.Y - offset.Y) * scaleY);
        var end = new Point((End.X - offset.X) * scaleX, (End.Y - offset.Y) * scaleY);
        var scale = (scaleX + scaleY) / 2;
        drawing.DrawGeometry(new SolidColorBrush(Color), CreatePen(scale), CreateArrowGeometry(start, end, Math.Max(10, Thickness * 3) * scale));
    }

    public override void Translate(Vector delta)
    {
        Start += delta;
        End += delta;
    }

    private static Geometry CreateArrowGeometry(Point start, Point end, double head)
    {
        var vector = end - start;
        if (vector.Length < 0.1) return Geometry.Empty;
        vector.Normalize();
        var normal = new Vector(-vector.Y, vector.X);
        var basePoint = end - vector * head;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(start, false, false);
        context.LineTo(end, true, false);
        context.BeginFigure(end, true, true);
        context.LineTo(basePoint + normal * head * 0.45, true, false);
        context.LineTo(basePoint - normal * head * 0.45, true, false);
        geometry.Freeze();
        return geometry;
    }
}

internal sealed class PenAnnotation : CaptureAnnotation
{
    public PenAnnotation(List<Point> points, Color color, double thickness) : base(color, thickness) => Points = points;
    public List<Point> Points { get; }
    public override bool IsMeaningful => Points.Count >= 2;

    public override UIElement CreateElement()
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(Color),
            StrokeThickness = Thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        foreach (var point in Points) line.Points.Add(point);
        return line;
    }

    public override void Draw(DrawingContext drawing, Point offset, double scaleX, double scaleY)
    {
        if (Points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = Points[0];
            context.BeginFigure(new Point((first.X - offset.X) * scaleX, (first.Y - offset.Y) * scaleY), false, false);
            foreach (var point in Points.Skip(1))
                context.LineTo(new Point((point.X - offset.X) * scaleX, (point.Y - offset.Y) * scaleY), true, false);
        }
        drawing.DrawGeometry(null, CreatePen((scaleX + scaleY) / 2), geometry);
    }

    public override void Translate(Vector delta)
    {
        for (var index = 0; index < Points.Count; index++) Points[index] += delta;
    }
}

internal sealed class TextAnnotation : CaptureAnnotation
{
    public TextAnnotation(Point origin, string text, Color color, double fontSize) : base(color, 1)
    {
        Origin = origin;
        Text = text;
        FontSize = fontSize;
    }

    public Point Origin { get; private set; }
    public string Text { get; }
    public double FontSize { get; }
    public override bool IsMeaningful => Text.Length > 0;

    public override UIElement CreateElement()
    {
        var block = new TextBlock
        {
            Text = Text,
            Foreground = new SolidColorBrush(Color),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = FontSize,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(block, Origin.X);
        Canvas.SetTop(block, Origin.Y);
        return block;
    }

    public override void Draw(DrawingContext drawing, Point offset, double scaleX, double scaleY)
    {
        var scale = (scaleX + scaleY) / 2;
        var formatted = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            FontSize * scale, new SolidColorBrush(Color), 1);
        drawing.DrawText(formatted, new Point((Origin.X - offset.X) * scaleX, (Origin.Y - offset.Y) * scaleY));
    }

    public override void Translate(Vector delta) => Origin += delta;
}

internal sealed class MosaicAnnotation : CaptureAnnotation
{
    public MosaicAnnotation(Rect bounds, double blockSize) : base(Colors.Transparent, 0)
    {
        Bounds = bounds;
        BlockSize = blockSize;
    }

    public Rect Bounds { get; set; }
    public double BlockSize { get; }
    public override bool IsMeaningful => Bounds.Width >= 4 && Bounds.Height >= 4;

    public override UIElement CreateElement()
    {
        var rectangle = new Rectangle
        {
            Width = Bounds.Width,
            Height = Bounds.Height,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 128, 138, 148)),
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 225, 230, 235)),
            StrokeDashArray = new DoubleCollection { 2, 2 },
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rectangle, Bounds.X);
        Canvas.SetTop(rectangle, Bounds.Y);
        return rectangle;
    }

    public override void Draw(DrawingContext drawing, Point offset, double scaleX, double scaleY) { }
    public override void Translate(Vector delta) => Bounds = new Rect(Bounds.TopLeft + delta, Bounds.Size);
}
