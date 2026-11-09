using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Capture;

/// <summary>
/// 全屏截图浮层：铺满整屏截图并压暗，拖动框选区域，之后可以标注、复制、保存、贴图或交给 OCR / 翻译。
/// 只处理交互和状态，真正的裁剪与绘制在 <see cref="CaptureComposer"/> 里完成。
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private const double EdgeMargin = 12;
    private const double HandleHitTolerance = 7;
    private const double DefaultLineWidth = 4;
    private const double DefaultFontSize = 18;

    private readonly CapturedScreen _screen;
    private readonly double _scale;
    private readonly CaptureHistory<Annotation> _history = new();
    private readonly TaskCompletionSource<CaptureOutcome?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Point> _penPoints = [];
    private readonly Cursor _crossCursor = new(StandardCursorType.Cross);
    private readonly Cursor _textCursor = new(StandardCursorType.Ibeam);
    private readonly Cursor _moveCursor = new(StandardCursorType.SizeAll);
    private readonly Cursor _northSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private readonly Cursor _westEastCursor = new(StandardCursorType.SizeWestEast);
    private readonly Cursor _topLeftCursor = new(StandardCursorType.TopLeftCorner);
    private readonly Cursor _topRightCursor = new(StandardCursorType.TopRightCorner);

    private Rect? _selection;
    private Annotation? _draft;
    private Point _pressPoint;
    private Point _drawStart;
    private Point _textOrigin;
    private Point _lastPenPoint;
    private Rect _originalSelection;
    private InteractionMode _interaction;
    private SelectionHandle _resizeHandle;
    private CaptureTool _tool = CaptureTool.Select;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private bool _committingText;

    /// <summary>
    /// 公开无参构造函数只为满足 Avalonia XAML 运行时加载器；实际使用一律走带截图的构造函数。
    /// </summary>
    public CaptureOverlayWindow() : this(CreatePlaceholderScreen())
    {
    }

    /// <summary>用一整屏截图构造浮层；窗口尺寸和位置由截图位图尺寸与缩放比例决定。</summary>
    public CaptureOverlayWindow(CapturedScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        InitializeComponent();
        _screen = screen;
        _scale = CaptureGeometry.NormalizeScale(screen.Scale);
        ScreenImage.Source = screen.Image;
        ApplyWindowGeometry();

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private static CapturedScreen CreatePlaceholderScreen() => new(
        new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul),
        new PixelRect(0, 0, 1, 1),
        1);

    /// <summary>等待用户确认或取消；窗口关闭而未确认时返回 null。</summary>
    public Task<CaptureOutcome?> OutcomeTask => _completion.Task;

    /// <summary>当前选区对应的截图像素区域，未框选时为 null。</summary>
    public PixelRect? SelectedRegion =>
        _selection is { } selection
            ? CaptureGeometry.ToPixelRect(selection, _scale, _screen.Image.PixelSize)
            : null;

    /// <summary>
    /// 弹出截图浮层并等待结果。<paramref name="owner"/> 可见时作为浮层的所有者窗口，
    /// 这样主窗口关闭时浮层也会一起结束。
    /// </summary>
    public static async Task<CaptureOutcome?> ShowAsync(Window owner, CapturedScreen screen)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(screen);

        var window = new CaptureOverlayWindow(screen);
        var completion = window.OutcomeTask;
        if (owner.IsVisible) window.Show(owner);
        else window.Show();
        window.Activate();
        try
        {
            return await completion;
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    /// <summary>像素坐标的标注线宽：DIP 线宽乘屏幕缩放比例。</summary>
    private double ImageThickness => Math.Max(1, DefaultLineWidth * _scale);

    /// <summary>像素坐标的文字字号：DIP 字号乘屏幕缩放比例。</summary>
    private double ImageFontSize => Math.Max(12, DefaultFontSize * _scale);

    /// <summary>像素坐标的马赛克块大小。</summary>
    private int ImageMosaicBlockSize => Math.Max(6, (int)Math.Round(10 * _scale));

    private void ApplyWindowGeometry()
    {
        var pixelSize = _screen.Image.PixelSize;
        Width = Math.Max(1, pixelSize.Width / _scale);
        Height = Math.Max(1, pixelSize.Height / _scale);

        // 截图服务目前只截主屏，主屏左上角就是 (0, 0)；Position 用物理像素。
        Position = new PixelPoint(_screen.Bounds.X, _screen.Bounds.Y);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        Activate();
        Surface.Focus();
        RefreshVisuals();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _completion.TrySetResult(null);
        _crossCursor.Dispose();
        _textCursor.Dispose();
        _moveCursor.Dispose();
        _northSouthCursor.Dispose();
        _westEastCursor.Dispose();
        _topLeftCursor.Dispose();
        _topRightCursor.Dispose();
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || IsChromeSource(e.Source)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        var point = e.GetPosition(this);
        if (TextEditor.IsVisible)
        {
            CommitTextEditor();
            return;
        }

        var canvas = CanvasSize();
        if (canvas.Width <= 0 || canvas.Height <= 0) return;
        if (point.X < 0 || point.Y < 0 || point.X > canvas.Width || point.Y > canvas.Height) return;

        e.Pointer.Capture(this);
        BeginInteraction(ClampToCanvas(point, canvas));
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_interaction == InteractionMode.None)
        {
            Cursor = ResolveCursor(point);
            return;
        }

        e.Handled = true;
        var canvas = CanvasSize();
        switch (_interaction)
        {
            case InteractionMode.CreatingSelection:
                _selection = CaptureGeometry.Normalize(_pressPoint, ClampToCanvas(point, canvas));
                break;
            case InteractionMode.MovingSelection:
                _selection = CaptureGeometry.Move(_originalSelection, point - _pressPoint, canvas);
                break;
            case InteractionMode.ResizingSelection:
                _selection = CaptureGeometry.Resize(_originalSelection, _resizeHandle, point - _pressPoint, canvas);
                break;
            case InteractionMode.Drawing:
                UpdateDraft(point);
                break;
        }

        RefreshVisuals();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_interaction == InteractionMode.None) return;

        e.Handled = true;
        var point = e.GetPosition(this);
        var interaction = _interaction;
        _interaction = InteractionMode.None;
        e.Pointer.Capture(null);

        if (interaction == InteractionMode.CreatingSelection)
        {
            var canvas = CanvasSize();
            var selected = CaptureGeometry.Normalize(_pressPoint, ClampToCanvas(point, canvas));
            if (CaptureGeometry.IsUsableSelection(selected))
            {
                _selection = CaptureGeometry.Clamp(selected, canvas);
            }
            else
            {
                _selection = null;
                _history.Clear();
            }
        }
        else if (interaction == InteractionMode.Drawing)
        {
            UpdateDraft(point);
            if (_draft is { } draft && Annotations.IsMeaningful(draft)) _history.Add(draft);
            _draft = null;
            _penPoints.Clear();
        }

        RefreshVisuals();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || TextEditor.IsVisible) return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;
            case Key.Enter:
                e.Handled = true;
                Finish(CaptureAction.Copy);
                break;
            case Key.Z when IsCommandModifier(e.KeyModifiers):
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo();
                else Undo();
                break;
            case Key.Y when IsCommandModifier(e.KeyModifiers):
                e.Handled = true;
                Redo();
                break;
            case Key.S when IsCommandModifier(e.KeyModifiers):
                e.Handled = true;
                _ = SaveAsync();
                break;
        }
    }

    private static bool IsCommandModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

    private void BeginInteraction(Point point)
    {
        _pressPoint = point;

        if (_selection is not { } selection)
        {
            StartNewSelection(point);
            return;
        }

        if (_tool == CaptureTool.Select)
        {
            var handle = HitTestHandle(point);
            if (handle == SelectionHandle.Move)
            {
                _interaction = InteractionMode.MovingSelection;
                _originalSelection = selection;
            }
            else if (handle != SelectionHandle.None)
            {
                _interaction = InteractionMode.ResizingSelection;
                _resizeHandle = handle;
                _originalSelection = selection;
            }
            else
            {
                StartNewSelection(point);
            }
            return;
        }

        if (!selection.Contains(point)) return;

        if (_tool == CaptureTool.Text)
        {
            OpenTextEditor(point);
            return;
        }

        _interaction = InteractionMode.Drawing;
        _drawStart = CaptureGeometry.ToImagePoint(ClampToSelection(point, selection), _scale);
        _lastPenPoint = _drawStart;
        _penPoints.Clear();
        _penPoints.Add(_drawStart);
        UpdateDraft(point);
        RefreshVisuals();
    }

    private void StartNewSelection(Point point)
    {
        _interaction = InteractionMode.CreatingSelection;
        _selection = new Rect(point, new Size(0, 0));
        _history.Clear();
        _draft = null;
        _penPoints.Clear();
        SetStatus(string.Empty, false);
        RefreshVisuals();
    }

    private void UpdateDraft(Point point)
    {
        if (_selection is not { } selection) return;

        var imagePoint = CaptureGeometry.ToImagePoint(ClampToSelection(point, selection), _scale);
        _draft = _tool switch
        {
            CaptureTool.Rectangle => Annotations.Rectangle(
                CaptureGeometry.Normalize(_drawStart, imagePoint), _color, ImageThickness),
            CaptureTool.Ellipse => Annotations.Ellipse(
                CaptureGeometry.Normalize(_drawStart, imagePoint), _color, ImageThickness),
            CaptureTool.Arrow => Annotations.Arrow(_drawStart, imagePoint, _color, ImageThickness),
            CaptureTool.Pen => UpdatePenDraft(imagePoint),
            CaptureTool.Mosaic => Annotations.Mosaic(
                CaptureGeometry.Normalize(_drawStart, imagePoint), ImageMosaicBlockSize),
            _ => null
        };
    }

    private Annotation UpdatePenDraft(Point imagePoint)
    {
        if (CaptureGeometry.Distance(_lastPenPoint, imagePoint) >= 1.5)
        {
            _penPoints.Add(imagePoint);
            _lastPenPoint = imagePoint;
        }

        return Annotations.Pen(_penPoints.ToArray(), _color, ImageThickness);
    }

    private SelectionHandle HitTestHandle(Point point)
    {
        if (_selection is not { } selection) return SelectionHandle.None;

        var nearLeft = Math.Abs(point.X - selection.Left) <= HandleHitTolerance;
        var nearRight = Math.Abs(point.X - selection.Right) <= HandleHitTolerance;
        var nearTop = Math.Abs(point.Y - selection.Top) <= HandleHitTolerance;
        var nearBottom = Math.Abs(point.Y - selection.Bottom) <= HandleHitTolerance;
        var insideX = point.X >= selection.Left - HandleHitTolerance && point.X <= selection.Right + HandleHitTolerance;
        var insideY = point.Y >= selection.Top - HandleHitTolerance && point.Y <= selection.Bottom + HandleHitTolerance;

        if (nearLeft && nearTop) return SelectionHandle.NorthWest;
        if (nearRight && nearTop) return SelectionHandle.NorthEast;
        if (nearLeft && nearBottom) return SelectionHandle.SouthWest;
        if (nearRight && nearBottom) return SelectionHandle.SouthEast;
        if (nearTop && insideX) return SelectionHandle.North;
        if (nearBottom && insideX) return SelectionHandle.South;
        if (nearLeft && insideY) return SelectionHandle.West;
        if (nearRight && insideY) return SelectionHandle.East;
        return selection.Contains(point) ? SelectionHandle.Move : SelectionHandle.None;
    }

    private Cursor ResolveCursor(Point point)
    {
        if (_tool == CaptureTool.Text) return _textCursor;
        if (_tool != CaptureTool.Select) return _crossCursor;
        if (_selection is null) return _crossCursor;

        return HitTestHandle(point) switch
        {
            SelectionHandle.Move => _moveCursor,
            SelectionHandle.North or SelectionHandle.South => _northSouthCursor,
            SelectionHandle.East or SelectionHandle.West => _westEastCursor,
            SelectionHandle.NorthWest or SelectionHandle.SouthEast => _topLeftCursor,
            SelectionHandle.NorthEast or SelectionHandle.SouthWest => _topRightCursor,
            _ => _crossCursor
        };
    }

    private void RefreshVisuals()
    {
        Surface.Selection = _selection;
        Surface.Annotations = _history.Items;
        Surface.Draft = _draft;
        Surface.ImageScale = _scale;
        Surface.InvalidateVisual();

        var hasSelection = _selection is not null;
        var idle = _interaction == InteractionMode.None;
        Toolbar.IsVisible = idle;
        ReadyGroup.IsVisible = !hasSelection;
        ToolGroup.IsVisible = hasSelection;
        ColorGroup.IsVisible = hasSelection;
        HistoryGroup.IsVisible = hasSelection;
        ActionGroup.IsVisible = hasSelection;
        ConfirmGroup.IsVisible = hasSelection;

        UpdateToolbarButtons();
        UpdateSizeBadge();
        if (idle) PositionToolbar();
    }

    private void UpdateToolbarButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;

        var toolTag = _tool.ToString();
        foreach (var button in ToolButtons)
            SetActiveClass(button, string.Equals(button.Tag as string, toolTag, StringComparison.Ordinal));

        var colorTag = $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";
        foreach (var button in ColorButtons)
            SetActiveClass(button, string.Equals(button.Tag as string, colorTag, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetActiveClass(Control control, bool active)
    {
        if (active)
        {
            if (!control.Classes.Contains("active")) control.Classes.Add("active");
        }
        else
        {
            control.Classes.Remove("active");
        }
    }

    private void UpdateSizeBadge()
    {
        if (_selection is not { } selection)
        {
            SizeBadge.IsVisible = false;
            return;
        }

        var region = CaptureGeometry.ToPixelRect(selection, _scale, _screen.Image.PixelSize);
        SizeText.Text = $"{region.Width} × {region.Height}";
        SizeBadge.IsVisible = true;

        var bounds = GetCanvasBounds();
        SizeBadge.Measure(Size.Infinity);
        var top = selection.Top - SizeBadge.DesiredSize.Height - 6;
        if (top < 4) top = selection.Top + 6;
        var left = Math.Clamp(selection.Left, 4, Math.Max(4, bounds.Width - SizeBadge.DesiredSize.Width - 4));
        Canvas.SetLeft(SizeBadge, left);
        Canvas.SetTop(SizeBadge, top);
    }

    private void PositionToolbar()
    {
        // 先放开宽度限制量出「一行到底有多宽」，再决定是否切换紧凑模式；
        // 否则上一次紧凑模式的换行结果会影响这次判断。
        Toolbar.MaxWidth = double.PositiveInfinity;
        Toolbar.InvalidateMeasure();
        Toolbar.Measure(Size.Infinity);
        if (Toolbar.DesiredSize.Width <= 0) return;

        var bounds = GetCanvasBounds();
        // 一行放不下就切到紧凑模式：右下角对齐并允许换行，避免工具条被窗口裁掉。
        var compact = _selection is null || Toolbar.DesiredSize.Width + EdgeMargin * 2 > bounds.Width;
        if (compact) Toolbar.MaxWidth = Math.Max(180, bounds.Width - EdgeMargin * 2);
        Toolbar.InvalidateMeasure();
        Toolbar.Measure(Size.Infinity);

        var size = Toolbar.DesiredSize;
        double left;
        double top;
        if (!compact && _selection is { } selection)
        {
            left = selection.Right - size.Width;
            top = selection.Bottom + 10;
            if (top + size.Height > bounds.Height - EdgeMargin) top = selection.Top - size.Height - 10;
            left = Math.Clamp(left, EdgeMargin, Math.Max(EdgeMargin, bounds.Width - size.Width - EdgeMargin));
            top = Math.Clamp(top, EdgeMargin, Math.Max(EdgeMargin, bounds.Height - size.Height - EdgeMargin));
        }
        else
        {
            left = Math.Max(EdgeMargin, bounds.Width - size.Width - EdgeMargin);
            top = Math.Max(EdgeMargin, bounds.Height - size.Height - EdgeMargin);
        }

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
    }

    private void PositionStatusBar()
    {
        Canvas.SetLeft(StatusBar, EdgeMargin);
        Canvas.SetTop(StatusBar, EdgeMargin);
    }

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(
            error ? Color.FromRgb(0xFF, 0xD9, 0xD4) : Color.FromRgb(0xDF, 0xF3, 0xEC));
        StatusBar.IsVisible = message.Length > 0;
        PositionStatusBar();
    }

    private Size CanvasSize()
    {
        var pixelSize = _screen.Image.PixelSize;
        return new Size(pixelSize.Width / _scale, pixelSize.Height / _scale);
    }

    private Size GetCanvasBounds()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    private static Point ClampToCanvas(Point point, Size canvas) => new(
        Math.Clamp(point.X, 0, Math.Max(0, canvas.Width)),
        Math.Clamp(point.Y, 0, Math.Max(0, canvas.Height)));

    private static Point ClampToSelection(Point point, Rect selection) => new(
        Math.Clamp(point.X, selection.Left, selection.Right),
        Math.Clamp(point.Y, selection.Top, selection.Bottom));

    private bool IsChromeSource(object? source)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (ReferenceEquals(visual, Toolbar)
                || ReferenceEquals(visual, TextEditor)
                || ReferenceEquals(visual, StatusBar)
                || ReferenceEquals(visual, SizeBadge))
                return true;
            visual = visual.GetVisualParent();
        }

        return false;
    }

    private Button[] ToolButtons =>
    [
        SelectToolButton,
        RectangleToolButton,
        EllipseToolButton,
        ArrowToolButton,
        PenToolButton,
        TextToolButton,
        MosaicToolButton
    ];

    private Button[] ColorButtons =>
    [
        RedColorButton,
        YellowColorButton,
        GreenColorButton,
        BlueColorButton,
        WhiteColorButton
    ];

    private void ToolButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<CaptureTool>(tag, out var tool)) return;
        _tool = tool;
        UpdateToolbarButtons();
        Cursor = ResolveCursor(new Point(-1, -1));
    }

    private void ColorButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Color.TryParse(tag, out var color)) return;
        _color = color;
        UpdateToolbarButtons();
    }

    private void Undo()
    {
        if (_history.Undo() is null) return;
        RefreshVisuals();
    }

    private void Redo()
    {
        if (_history.Redo() is null) return;
        RefreshVisuals();
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object? sender, RoutedEventArgs e) => Redo();

    private void Copy_Click(object? sender, RoutedEventArgs e) => Finish(CaptureAction.Copy);

    private void Ocr_Click(object? sender, RoutedEventArgs e) => Finish(CaptureAction.Ocr);

    private void Translate_Click(object? sender, RoutedEventArgs e) => Finish(CaptureAction.Translate);

    private void Pin_Click(object? sender, RoutedEventArgs e) => Finish(CaptureAction.Pin);

    private void Complete_Click(object? sender, RoutedEventArgs e) => Finish(CaptureAction.Copy);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Cancel();

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_selection is null) return;

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存截图",
                SuggestedFileName = $"SnapTranslate_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }
                ]
            });
            if (file is null) return;

            if (ComposeResult() is not { } result) return;
            try
            {
                var path = file.TryGetLocalPath();
                if (path is null)
                {
                    await using var stream = await file.OpenWriteAsync();
                    result.Image.Save(stream);
                }
                else
                {
                    result.Image.Save(path);
                }

                FinishWith(result, CaptureAction.Save);
            }
            catch (Exception exception)
            {
                result.Image.Dispose();
                SetStatus($"保存失败：{exception.GetBaseException().Message}", true);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"保存失败：{exception.GetBaseException().Message}", true);
        }
    }

    private void Finish(CaptureAction action)
    {
        if (ComposeResult() is not { } result) return;
        FinishWith(result, action);
    }

    private void FinishWith((Bitmap Image, PixelRect Region) result, CaptureAction action)
    {
        var outcome = new CaptureOutcome(result.Image, result.Region, action);
        if (!_completion.TrySetResult(outcome)) result.Image.Dispose();
        Close();
    }

    private void Cancel()
    {
        _completion.TrySetResult(null);
        Close();
    }

    private (Bitmap Image, PixelRect Region)? ComposeResult()
    {
        if (_selection is not { } selection) return null;

        CommitTextEditor();
        var region = CaptureGeometry.ToPixelRect(selection, _scale, _screen.Image.PixelSize);
        if (region.Width <= 0 || region.Height <= 0) return null;
        return (CaptureComposer.Render(_screen.Image, _history.Items, region), region);
    }

    private void OpenTextEditor(Point point)
    {
        if (_selection is not { } selection) return;

        point = ClampToSelection(point, selection);
        _textOrigin = CaptureGeometry.ToImagePoint(point, _scale);
        TextEditor.Text = string.Empty;
        TextEditor.Width = Math.Max(160, Math.Min(320, selection.Right - point.X));
        TextEditor.MaxHeight = Math.Max(40, selection.Bottom - point.Y);
        TextEditor.IsVisible = true;
        Canvas.SetLeft(TextEditor, point.X);
        Canvas.SetTop(TextEditor, point.Y);
        Toolbar.IsVisible = false;
        TextEditor.Focus();
    }

    private void CommitTextEditor()
    {
        if (_committingText || !TextEditor.IsVisible) return;

        _committingText = true;
        try
        {
            var text = TextEditor.Text?.Trim() ?? string.Empty;
            if (text.Length > 0)
                _history.Add(Annotations.Text(_textOrigin, text, _color, ImageFontSize));
            TextEditor.IsVisible = false;
            TextEditor.Text = string.Empty;
        }
        finally
        {
            _committingText = false;
        }

        Surface.Focus();
        RefreshVisuals();
    }

    private void CancelTextEditor()
    {
        if (!TextEditor.IsVisible) return;
        TextEditor.IsVisible = false;
        TextEditor.Text = string.Empty;
        Surface.Focus();
        RefreshVisuals();
    }

    private void TextEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTextEditor();
        }
        else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            CommitTextEditor();
        }
    }

    private enum InteractionMode
    {
        None,
        CreatingSelection,
        MovingSelection,
        ResizingSelection,
        Drawing
    }
}