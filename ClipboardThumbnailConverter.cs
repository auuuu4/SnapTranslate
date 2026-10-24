using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SnapTranslate;

public sealed class ClipboardThumbnailConverter : IValueConverter
{
    private const int MaxCachedThumbnails = 128;
    private readonly Dictionary<string, (BitmapSource Image, LinkedListNode<string> Node)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private readonly object _gate = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                _recent.Remove(cached.Node);
                _recent.AddFirst(cached.Node);
                return cached.Image;
            }
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96;
                using (var stream = File.OpenRead(path))
                {
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();
                var node = _recent.AddFirst(path);
                _cache[path] = (image, node);
                while (_cache.Count > MaxCachedThumbnails && _recent.Last is { } oldest)
                {
                    _recent.RemoveLast();
                    _cache.Remove(oldest.Value);
                }
                return image;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                return null;
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
