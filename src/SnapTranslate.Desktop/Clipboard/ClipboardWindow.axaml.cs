using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>剪贴板历史窗口：搜索、类型筛选、复制、收藏、删除、清空。</summary>
public partial class ClipboardWindow : Window
{
    private readonly ClipboardRepository _repository;
    private readonly IClipboardMonitor? _monitor;
    private IReadOnlyList<ClipboardItem> _items = Array.Empty<ClipboardItem>();

    /// <summary>创建窗口；<paramref name="monitor"/> 为空时只能浏览历史，不能写回系统剪贴板。</summary>
    public ClipboardWindow(ClipboardRepository repository, IClipboardMonitor? monitor = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _monitor = monitor;
        InitializeComponent();
        SearchText.TextChanged += (_, _) => RefreshItems();
        FilterCombo.SelectionChanged += (_, _) => RefreshItems();
        ItemsList.SelectionChanged += (_, _) => UpdateButtons();
        Opened += (_, _) =>
        {
            RefreshItems();
            SearchText.Focus();
        };
    }

    /// <summary>供 Avalonia 设计器创建窗口；实际运行时请使用带仓库参数的构造函数。</summary>
    public ClipboardWindow()
        : this(new ClipboardRepository())
    {
    }

    /// <summary>按当前关键词和类型筛选刷新列表。</summary>
    public void RefreshItems(string? selectId = null)
    {
        var keyword = SearchText.Text?.Trim() ?? string.Empty;
        var kind = SelectedFilter();
        var items = _repository.Search(keyword, kind);
        var total = _repository.Count(keyword, kind);

        _items = items;
        ItemsList.ItemsSource = items;
        CountText.Text = items.Count < total
            ? $"{total:N0} 条（显示 {items.Count:N0} 条）"
            : $"{total:N0} 条";
        EmptyState.IsVisible = items.Count == 0;

        var targetId = selectId ?? (ItemsList.SelectedItem as ClipboardItem)?.Id;
        if (targetId is not null) ItemsList.SelectedItem = items.FirstOrDefault(item => item.Id == targetId);
        if (ItemsList.SelectedItem is null) ItemsList.SelectedIndex = items.Count > 0 ? 0 : -1;
        UpdateButtons();
    }

    private ClipboardCaptureKind? SelectedFilter()
        => (FilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Text" => ClipboardCaptureKind.Text,
            "Image" => ClipboardCaptureKind.Image,
            "Files" => ClipboardCaptureKind.Files,
            _ => null
        };

    private void UpdateButtons()
    {
        var item = ItemsList.SelectedItem as ClipboardItem;
        CopyButton.IsEnabled = item is not null;
        FavoriteButton.IsEnabled = item is not null;
        FavoriteButton.Content = item?.Favorite == true ? "取消收藏" : "收藏";
        DeleteButton.IsEnabled = item is not null;
        ClearButton.IsEnabled = _items.Count > 0;
    }

    private async Task CopySelectedAsync(bool closeAfter)
    {
        if (ItemsList.SelectedItem is not ClipboardItem item)
        {
            StatusText.Text = "请先选择一条记录";
            return;
        }
        if (_monitor is null || !_monitor.IsSupported)
        {
            StatusText.Text = "当前平台不支持写回系统剪贴板";
            return;
        }

        CopyButton.IsEnabled = false;
        try
        {
            var copied = await _monitor.CopyToClipboardAsync(item.ToCapture());
            StatusText.Text = copied ? "已复制到剪贴板" : "复制失败";
            if (copied && closeAfter) Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
        finally
        {
            CopyButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => _ = CopySelectedAsync(false);

    private async void ItemsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        await CopySelectedAsync(true);
    }

    private void Favorite_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipboardItem item) return;
        _repository.ToggleFavorite(item.Id);
        RefreshItems(item.Id);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipboardItem item) return;
        _repository.Delete(item.Id);
        RefreshItems();
        StatusText.Text = "已删除一条记录";
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        _repository.Clear();
        RefreshItems();
        StatusText.Text = "已清空剪贴板历史";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
