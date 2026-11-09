using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>调用 Swift 剪贴板辅助程序的抽象，便于在测试里替换。</summary>
public interface IClipboardHelperRunner
{
    /// <summary>读取当前剪贴板的 changeCount，失败时抛出异常。</summary>
    Task<int> GetChangeCountAsync(CancellationToken cancellationToken = default);

    /// <summary>读取当前剪贴板内容，空剪贴板时 Capture 为 null。</summary>
    Task<ClipboardHelperReadResult> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>把一条历史写回系统剪贴板，成功返回 true。</summary>
    Task<bool> CopyAsync(Platform.ClipboardCapture item, CancellationToken cancellationToken = default);
}
