using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop.Clipboard;

/// <summary>
/// macOS 剪贴板监听：每 700ms 读一次 changeCount，变化时才读取内容并发出 Captured 事件。
/// 同一条内容重复复制（哈希不变）只发一次事件；非 macOS 上 IsSupported 为 false。
/// </summary>
public sealed class MacPasteboardMonitor : IClipboardMonitor
{
    /// <summary>默认轮询间隔。</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(700);

    private readonly IClipboardHelperRunner _runner;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private int _lastChangeCount = -1;
    private string? _lastHash;
    private int _polling;
    private bool _disposed;

    /// <summary>创建监听；<paramref name="pollInterval"/> 为空时使用 700ms。</summary>
    public MacPasteboardMonitor(IClipboardHelperRunner runner, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _pollInterval = pollInterval is { } interval && interval > TimeSpan.Zero ? interval : DefaultPollInterval;
    }

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    public event EventHandler<Platform.ClipboardCapture>? Captured;

    /// <inheritdoc />
    public void Start()
    {
        if (!IsSupported) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_cancellation is not null) return;
            var source = new CancellationTokenSource();
            _cancellation = source;
            _ = Task.Run(() => RunLoopAsync(source), source.Token);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _cancellation;
            _cancellation = null;
        }
        if (source is null) return;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 循环已结束并释放了取消源。
        }
    }

    /// <summary>
    /// 执行一次轮询。正常由后台循环调用，测试里可以直接调用来稳定验证 changeCount 与去重逻辑。
    /// </summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        // 重入保护：上一轮还没读完时不并发启动新的辅助程序。
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try
        {
            if (_disposed) return;
            var changeCount = await _runner.GetChangeCountAsync(cancellationToken).ConfigureAwait(false);
            if (changeCount < 0 || changeCount == _lastChangeCount) return;

            ClipboardHelperReadResult result;
            try
            {
                result = await _runner.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 单次读取失败时保留旧 changeCount，下一次轮询重试。
                return;
            }

            var capture = result.Capture;
            if (capture is null)
            {
                _lastChangeCount = changeCount;
                _lastHash = null;
                return;
            }

            _lastChangeCount = result.ChangeCount > 0 ? result.ChangeCount : changeCount;
            if (string.Equals(capture.Hash, _lastHash, StringComparison.Ordinal)) return;
            _lastHash = capture.Hash;
            Captured?.Invoke(this, capture);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CopyToClipboardAsync(Platform.ClipboardCapture item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsSupported) return false;
        var copied = await _runner.CopyAsync(item, cancellationToken).ConfigureAwait(false);
        if (!copied) return false;

        // 自己写回剪贴板也会让 changeCount 变化，记下来避免把刚复制的内容重复采集。
        _lastHash = item.Hash;
        try
        {
            var changeCount = await _runner.GetChangeCountAsync(cancellationToken).ConfigureAwait(false);
            if (changeCount >= 0) _lastChangeCount = changeCount;
        }
        catch (Exception)
        {
            // 刷新 changeCount 失败不影响写回结果。
        }
        return true;
    }

    /// <summary>释放监听并停止后台轮询。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private async Task RunLoopAsync(CancellationTokenSource source)
    {
        try
        {
            await PollLoopAsync(source.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, source)) _cancellation = null;
            }
            source.Dispose();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            do
            {
                try
                {
                    await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // 单次轮询失败不终止监听。
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // 监听停止。
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
