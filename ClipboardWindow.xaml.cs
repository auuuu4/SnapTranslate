using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTranslate;

public partial class ClipboardWindow : Window
{
    private readonly ClipboardRepository _repository;
    private readonly Func<ClipboardItem, bool, bool, IntPtr, Task> _useItem;
    private readonly Func<ClipboardItem, Task> _ocrItem;
    private readonly Action _changed;
    private readonly Func<ClipboardItem, bool, Task> _setFavorite;
    private readonly Func<ClipboardItem, string, string, Task> _setNote;
    private readonly DispatcherTimer _searchRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(140)
    };
    private IReadOnlyList<ClipboardItem> _items = Array.Empty<ClipboardItem>();
    private CancellationTokenSource? _refreshCancellation;
    private IntPtr _targetWindow;
    private string _lastKeyword = string.Empty;
    private ClipboardFilter _lastFilter = ClipboardFilter.All;
    private string? _pendingSelectedId;
    private bool _autoPaste;
    private bool _modalOpen;
    private bool _useInProgress;
    private bool _itemsDirty = true;
    private bool _closed;

    internal bool IsUseInProgress => _useInProgress;

    public ClipboardWindow(
        ClipboardRepository repository,
        Func<ClipboardItem, bool, bool, IntPtr, Task> useItem,
        Func<ClipboardItem, Task> ocrItem,
        Action changed,
        Func<ClipboardItem, bool, Task> setFavorite,
        Func<ClipboardItem, string, string, Task> setNote)
    {
        InitializeComponent();
        _repository = repository;
        _useItem = useItem;
        _ocrItem = ocrItem;
        _changed = changed;
        _setFavorite = setFavorite;
        _setNote = setNote;
        ItemsList.ItemsSource = _items;
        _searchRefreshTimer.Tick += SearchRefreshTimer_Tick;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            _searchRefreshTimer.Stop();
            _refreshCancellation?.Cancel();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _searchRefreshTimer.Stop();
            _refreshCancellation?.Cancel();
        };
    }

    public void ShowForTarget(IntPtr targetWindow, bool autoPaste)
    {
        if (_useInProgress) return;
        _searchRefreshTimer.Stop();
        _targetWindow = targetWindow;
        _autoPaste = autoPaste;
        if (!IsVisible)
        {
            Show();
            PositionNearCursor();
        }
        WindowState = WindowState.Normal;
        Activate();
        SearchText.Focus();
        SearchText.SelectAll();
        if (_items.Count == 0) StatusText.Text = "正在加载...";
        _ = RefreshItemsAsync();
    }

    public void RefreshItems(string? selectedId = null)
    {
        MarkDirty(selectedId);
    }

    public void MarkDirty(string? selectedId = null)
    {
        _itemsDirty = true;
        _pendingSelectedId = selectedId ?? _pendingSelectedId;
        if (IsVisible) _ = RefreshItemsAsync();
    }

    private async Task RefreshItemsAsync(bool force = false)
    {
        if (!IsInitialized || _closed) return;

        var keyword = SearchText.Text.Trim();
        var filter = Enum.TryParse<ClipboardFilter>(SelectedTag(FilterCombo), out var parsed)
            ? parsed
            : ClipboardFilter.All;
        if (!force
            && !_itemsDirty
            && string.Equals(keyword, _lastKeyword, StringComparison.Ordinal)
            && filter == _lastFilter)
        {
            ApplyPendingSelection();
            return;
        }

        var previous = _refreshCancellation;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        previous?.Cancel();
        try
        {
            var result = await _repository.SearchWithCountAsync(keyword, filter, 200, cancellation.Token);
            if (cancellation.IsCancellationRequested
                || !ReferenceEquals(_refreshCancellation, cancellation)
                || _closed)
                return;
            if (!string.Equals(keyword, SearchText.Text.Trim(), StringComparison.Ordinal)
                || filter != ParseSelectedFilter())
            {
                _itemsDirty = true;
                return;
            }

            _items = result.Items;
            ItemsList.ItemsSource = _items;
            _lastKeyword = keyword;
            _lastFilter = filter;
            _itemsDirty = false;
            ApplyPendingSelection();
            StatusText.Text = $"{result.TotalCount:N0} 条";
            UpdateButtons();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = $"刷新失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
                _refreshCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ApplyPendingSelection()
    {
        var selectedId = _pendingSelectedId ?? (ItemsList.SelectedItem as ClipboardItem)?.Id;
        _pendingSelectedId = null;
        ItemsList.SelectedItem = _items.FirstOrDefault(item => item.Id == selectedId) ?? _items.FirstOrDefault();
    }

    private ClipboardFilter ParseSelectedFilter()
        => Enum.TryParse<ClipboardFilter>(SelectedTag(FilterCombo), out var parsed)
            ? parsed
            : ClipboardFilter.All;

    private async void SearchRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _searchRefreshTimer.Stop();
        await RefreshItemsAsync(true);
    }

    public void ApplyFavoriteState(string id, bool isFavorite)
    {
        var item = _items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is not null) item.IsFavorite = isFavorite;
        _itemsDirty = true;
        UpdateButtons();
    }

    public void ApplyNoteState(string id, string note)
    {
        var item = _items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is not null) item.Note = note;
        _itemsDirty = true;
    }

    public void SetStatus(string message) => StatusText.Text = message;

    private void PositionNearCursor()
    {
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var cursor = Forms.Cursor.Position;
        var work = Forms.Screen.FromPoint(cursor).WorkingArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Max(work.Left / scale, Math.Min(cursor.X / scale - width / 2, work.Right / scale - width));
        Top = Math.Max(work.Top / scale, Math.Min(cursor.Y / scale - 36, work.Bottom / scale - height));
    }

    private async Task UseSelectedAsync(bool plain, bool paste)
    {
        if (_useInProgress || ItemsList.SelectedItem is not ClipboardItem item) return;
        _useInProgress = true;
        try
        {
            if (paste)
            {
                _searchRefreshTimer.Stop();
                _refreshCancellation?.Cancel();
                Hide();
            }
            await _useItem(item, plain, paste, _targetWindow);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(UseSelectedAsync), ex);
            if (!paste && IsVisible) StatusText.Text = ex.Message;
        }
        finally
        {
            _useInProgress = false;
        }
    }

    private void UpdateButtons()
    {
        var item = ItemsList.SelectedItem as ClipboardItem;
        FavoriteButton.IsEnabled = item is not null;
        FavoriteButton.Content = item?.IsFavorite == true ? "取消收藏" : "收藏";
        FavoriteButton.ToolTip = item?.IsFavorite == true ? "取消收藏当前条目" : "收藏当前条目";
        NoteButton.IsEnabled = item is not null;
        DeleteButton.IsEnabled = item is not null;
        OcrButton.IsEnabled = item?.IsImage == true;
    }

    private void SearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _itemsDirty = true;
        _searchRefreshTimer.Stop();
        _searchRefreshTimer.Start();
    }

    private async void SearchText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.X || Keyboard.Modifiers != ModifierKeys.Control || sender is not TextBox textBox)
            return;
        if (textBox.SelectionLength <= 0) return;
        e.Handled = true;
        await ClipboardSearchCutService.CutAsync(textBox, message => StatusText.Text = message);
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _searchRefreshTimer.Stop();
        MarkDirty();
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject source
            || IsInteractiveSource(source)) return;
        e.Handled = true;
        if (_autoPaste) await UseSelectedAsync(false, true);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e) => await UseSelectedAsync(false, true);
    private async void PastePlain_Click(object sender, RoutedEventArgs e) => await UseSelectedAsync(true, true);

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipboardItem item || !item.IsImage) return;
        StatusText.Text = "正在识别...";
        try
        {
            await _ocrItem(item);
            _changed();
            RefreshItems(item.Id);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipboardItem item) return;
        await _setFavorite(item, !item.IsFavorite);
    }

    private async void InlineFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipboardItem item }) return;
        e.Handled = true;
        ItemsList.SelectedItem = item;
        await _setFavorite(item, !item.IsFavorite);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipboardItem item) return;
        _repository.Delete(item);
        _items = _items.Where(candidate => candidate.Id != item.Id).ToArray();
        ItemsList.ItemsSource = _items;
        ItemsList.SelectedItem = _items.FirstOrDefault();
        UpdateButtons();
        _changed();
        RefreshItems();
    }

    private async void Note_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipboardItem item) return;
        await EditNoteAsync(item);
    }

    private async void InlineNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipboardItem item }) return;
        e.Handled = true;
        ItemsList.SelectedItem = item;
        await EditNoteAsync(item);
    }

    private async Task EditNoteAsync(ClipboardItem item)
    {
        _modalOpen = true;
        try
        {
            var dialog = new NoteWindow(item.Note) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await _setNote(item, dialog.Note, item.Note);
        }
        finally { _modalOpen = false; }
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await UseSelectedAsync(true, true);
        }
        else if (e.Key == Key.Enter && SearchText.IsKeyboardFocused == false)
        {
            e.Handled = true;
            await UseSelectedAsync(false, true);
        }
        else if (e.Key == Key.Delete && ItemsList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Delete_Click(sender, e);
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_modalOpen) return;
        _searchRefreshTimer.Stop();
        _refreshCancellation?.Cancel();
        Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _searchRefreshTimer.Stop();
        _refreshCancellation?.Cancel();
        Hide();
    }

    private static string SelectedTag(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "All";

    private static bool IsInteractiveSource(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase) return true;
            if (current is ListBoxItem) return false;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }
}
