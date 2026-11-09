using Avalonia;
using Avalonia.Media.Imaging;

namespace SnapTranslate.Desktop.Platform;

/// <summary>一次整屏截图的结果：位图 + 它在虚拟屏幕中的像素范围 + 缩放比例。</summary>
public sealed record CapturedScreen(Bitmap Image, PixelRect Bounds, double Scale);

/// <summary>整屏截图能力。Windows/Linux 上返回 null（不支持），测试用 fake 实现。</summary>
public interface IScreenCaptureService
{
    bool IsSupported { get; }

    Task<CapturedScreen?> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>把图片文件识别成文本（macOS 走 Vision 辅助程序）。</summary>
public interface ITextRecognizer
{
    bool IsSupported { get; }

    Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default);
}

/// <summary>截图完成后的动作。</summary>
public enum CaptureAction
{
    Copy,
    Save,
    Pin,
    Ocr,
    Translate
}

/// <summary>用户在截图浮层里确认的结果。</summary>
public sealed record CaptureOutcome(Bitmap Image, PixelRect Region, CaptureAction Action);

/// <summary>剪贴板条目类型。</summary>
public enum ClipboardCaptureKind
{
    Text,
    Image,
    Files
}

/// <summary>从系统剪贴板读到的一条内容。Image 类型时 <see cref="FilePath"/> 指向已落盘的 PNG。</summary>
public sealed record ClipboardCapture(
    ClipboardCaptureKind Kind,
    string Text,
    string? FilePath,
    DateTimeOffset CapturedAt,
    string Hash)
{
    public string Preview => Kind switch
    {
        ClipboardCaptureKind.Text => Text.Length <= 120 ? Text : Text[..120] + "…",
        ClipboardCaptureKind.Files => FilePath ?? "文件",
        _ => "图片"
    };
}

/// <summary>轮询系统剪贴板。真实实现只在 macOS 上可用。</summary>
public interface IClipboardMonitor : IDisposable
{
    bool IsSupported { get; }

    event EventHandler<ClipboardCapture>? Captured;

    void Start();

    void Stop();

    /// <summary>把一条历史重新写回系统剪贴板。</summary>
    Task<bool> CopyToClipboardAsync(ClipboardCapture item, CancellationToken cancellationToken = default);
}

/// <summary>一条全局快捷键。Gesture 用 "Ctrl+Alt+F" 这种通用写法，macOS 侧把 Ctrl 映射成 Command。</summary>
public sealed record GlobalHotkey(string Id, string DisplayName, string Gesture);

/// <summary>全局快捷键。macOS 用 Carbon RegisterEventHotKey 实现，不依赖辅助功能权限。</summary>
public interface IGlobalHotkeyService : IDisposable
{
    bool IsSupported { get; }

    IReadOnlyList<GlobalHotkey> Active { get; }

    event EventHandler<string>? Pressed;

    bool TryRegister(IEnumerable<GlobalHotkey> hotkeys, out string error);

    void UnregisterAll();
}

/// <summary>
/// 进程内的平台服务定位器。默认全部是"不支持"的空实现，
/// 这样在 Windows 上跑无头测试时 UI 代码也能正常构造。
/// </summary>
public static class PlatformServices
{
    public static IScreenCaptureService ScreenCapture { get; set; } = new UnsupportedScreenCaptureService();

    public static ITextRecognizer TextRecognizer { get; set; } = new UnsupportedTextRecognizer();

    public static IClipboardMonitor Clipboard { get; set; } = new UnsupportedClipboardMonitor();

    public static IGlobalHotkeyService Hotkeys { get; set; } = new UnsupportedGlobalHotkeyService();

    /// <summary>测试里把服务换成 fake，返回的句柄用于还原。</summary>
    public static IDisposable Override(
        IScreenCaptureService? screenCapture = null,
        ITextRecognizer? recognizer = null,
        IClipboardMonitor? clipboard = null,
        IGlobalHotkeyService? hotkeys = null)
    {
        var previous = (ScreenCapture, TextRecognizer, Clipboard, Hotkeys);
        if (screenCapture is not null) ScreenCapture = screenCapture;
        if (recognizer is not null) TextRecognizer = recognizer;
        if (clipboard is not null) Clipboard = clipboard;
        if (hotkeys is not null) Hotkeys = hotkeys;
        return new Restore(() => (ScreenCapture, TextRecognizer, Clipboard, Hotkeys) = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            action();
        }
    }
}

public sealed class UnsupportedScreenCaptureService : IScreenCaptureService
{
    public bool IsSupported => false;

    public Task<CapturedScreen?> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<CapturedScreen?>(null);
}

public sealed class UnsupportedTextRecognizer : ITextRecognizer
{
    public bool IsSupported => false;

    public Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("当前平台没有可用的 OCR 引擎。");
}

public sealed class UnsupportedClipboardMonitor : IClipboardMonitor
{
    public bool IsSupported => false;

    public event EventHandler<ClipboardCapture>? Captured
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public Task<bool> CopyToClipboardAsync(ClipboardCapture item, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public void Dispose()
    {
    }
}

public sealed class UnsupportedGlobalHotkeyService : IGlobalHotkeyService
{
    public bool IsSupported => false;

    public IReadOnlyList<GlobalHotkey> Active { get; private set; } = Array.Empty<GlobalHotkey>();

    public event EventHandler<string>? Pressed
    {
        add { }
        remove { }
    }

    public bool TryRegister(IEnumerable<GlobalHotkey> hotkeys, out string error)
    {
        Active = hotkeys.ToArray();
        error = string.Empty;
        return false;
    }

    public void UnregisterAll()
    {
        Active = Array.Empty<GlobalHotkey>();
    }

    public void Dispose()
    {
    }
}
