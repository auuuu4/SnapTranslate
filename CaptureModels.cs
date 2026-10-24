using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SnapTranslate;

public enum CaptureResultAction
{
    Copy,
    Save,
    Pin,
    Ocr,
    Translate,
    SilentOcr
}

public enum CaptureTool
{
    Select,
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Text,
    Mosaic
}

public enum SelectionHandle
{
    None,
    Move,
    NorthWest,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West
}

public static class CaptureGeometry
{
    public const double MinimumSize = 8;

    public static Rect Normalize(Point first, Point second)
        => new(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));

    public static Rect Clamp(Rect rect, Size bounds)
    {
        var width = Math.Min(Math.Max(MinimumSize, rect.Width), Math.Max(MinimumSize, bounds.Width));
        var height = Math.Min(Math.Max(MinimumSize, rect.Height), Math.Max(MinimumSize, bounds.Height));
        var x = Math.Clamp(rect.X, 0, Math.Max(0, bounds.Width - width));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, bounds.Height - height));
        return new Rect(x, y, width, height);
    }

    public static Rect Move(Rect original, Vector delta, Size bounds)
        => Clamp(new Rect(original.X + delta.X, original.Y + delta.Y, original.Width, original.Height), bounds);

    public static Rect Resize(Rect original, SelectionHandle handle, Vector delta, Size bounds)
    {
        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;

        if (handle is SelectionHandle.NorthWest or SelectionHandle.West or SelectionHandle.SouthWest) left += delta.X;
        if (handle is SelectionHandle.NorthEast or SelectionHandle.East or SelectionHandle.SouthEast) right += delta.X;
        if (handle is SelectionHandle.NorthWest or SelectionHandle.North or SelectionHandle.NorthEast) top += delta.Y;
        if (handle is SelectionHandle.SouthWest or SelectionHandle.South or SelectionHandle.SouthEast) bottom += delta.Y;

        left = Math.Clamp(left, 0, bounds.Width);
        right = Math.Clamp(right, 0, bounds.Width);
        top = Math.Clamp(top, 0, bounds.Height);
        bottom = Math.Clamp(bottom, 0, bounds.Height);

        if (right - left < MinimumSize)
        {
            if (handle is SelectionHandle.NorthWest or SelectionHandle.West or SelectionHandle.SouthWest)
                left = right - MinimumSize;
            else
                right = left + MinimumSize;
        }
        if (bottom - top < MinimumSize)
        {
            if (handle is SelectionHandle.NorthWest or SelectionHandle.North or SelectionHandle.NorthEast)
                top = bottom - MinimumSize;
            else
                bottom = top + MinimumSize;
        }

        return Clamp(new Rect(left, top, right - left, bottom - top), bounds);
    }
}

public sealed class CaptureHistory<T>
{
    private readonly List<T> _undo = new();
    private readonly List<T> _redo = new();

    public IReadOnlyList<T> Items => _undo;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Add(T item)
    {
        _undo.Add(item);
        _redo.Clear();
    }

    public T? Undo()
    {
        if (_undo.Count == 0) return default;
        var item = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(item);
        return item;
    }

    public T? Redo()
    {
        if (_redo.Count == 0) return default;
        var item = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(item);
        return item;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
