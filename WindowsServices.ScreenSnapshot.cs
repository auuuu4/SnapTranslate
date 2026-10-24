using System.Collections.Generic;
using System.Drawing;
using System.Windows.Media.Imaging;

namespace SnapTranslate;

public sealed record ScreenSnapshot(BitmapSource Image, Rectangle Bounds, IReadOnlyList<Rectangle> WindowBounds);
