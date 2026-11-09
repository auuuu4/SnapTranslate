namespace SnapTranslate.Desktop.Capture;

/// <summary>
/// 标注的撤销 / 重做历史。只在末尾追加，撤销把元素移到重做栈，
/// 新画一笔会清空重做栈，和常见截图工具的直觉一致。
/// </summary>
public sealed class CaptureHistory<T>
{
    private readonly List<T> _items = new();
    private readonly Stack<T> _redo = new();

    /// <summary>当前可见的条目，按绘制顺序排列。</summary>
    public IReadOnlyList<T> Items => _items;

    public bool CanUndo => _items.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>追加一条新条目。</summary>
    public void Add(T item)
    {
        _items.Add(item);
        _redo.Clear();
    }

    /// <summary>撤销最后一条；没有可撤销项时返回默认值。</summary>
    public T? Undo()
    {
        if (_items.Count == 0) return default;
        var item = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        _redo.Push(item);
        return item;
    }

    /// <summary>重做上一条被撤销的条目；没有可重做项时返回默认值。</summary>
    public T? Redo()
    {
        if (_redo.Count == 0) return default;
        var item = _redo.Pop();
        _items.Add(item);
        return item;
    }

    /// <summary>清空全部历史（重新框选时调用）。</summary>
    public void Clear()
    {
        _items.Clear();
        _redo.Clear();
    }
}
