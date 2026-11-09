using SnapTranslate.Desktop.Clipboard;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Clipboard;

public sealed class MacPasteboardMonitorTests
{
    [Fact]
    public void IsSupportedMatchesCurrentPlatform()
    {
        var runner = new FakeRunner();
        using var monitor = new MacPasteboardMonitor(runner);
        Assert.Equal(OperatingSystem.IsMacOS(), monitor.IsSupported);
    }

    [Fact]
    public async Task UnchangedChangeCountSkipsClipboardRead()
    {
        var runner = new FakeRunner()
            .WithChangeCounts(5, 5, 5)
            .WithCaptures(TextCapture("hello"));
        using var monitor = new MacPasteboardMonitor(runner, TimeSpan.FromMilliseconds(5));
        var captured = new List<SnapTranslate.Desktop.Platform.ClipboardCapture>();
        monitor.Captured += (_, capture) => captured.Add(capture);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Single(captured);
        Assert.Equal("hello", captured[0].Text);
        Assert.Equal(1, runner.ReadCount);
        Assert.Equal(3, runner.ChangeCountCallCount);
    }

    [Fact]
    public async Task SameContentAcrossTwoChangeCountsRaisesOnce()
    {
        var runner = new FakeRunner()
            .WithChangeCounts(1, 2)
            .WithCaptures(TextCapture("重复内容"), TextCapture("重复内容"));
        using var monitor = new MacPasteboardMonitor(runner, TimeSpan.FromMilliseconds(5));
        var captured = new List<SnapTranslate.Desktop.Platform.ClipboardCapture>();
        monitor.Captured += (_, capture) => captured.Add(capture);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Single(captured);
        Assert.Equal(2, runner.ReadCount);
    }

    [Fact]
    public async Task DifferentContentRaisesForEveryChange()
    {
        var runner = new FakeRunner()
            .WithChangeCounts(1, 2)
            .WithCaptures(TextCapture("第一条"), TextCapture("第二条"));
        using var monitor = new MacPasteboardMonitor(runner, TimeSpan.FromMilliseconds(5));
        var captured = new List<SnapTranslate.Desktop.Platform.ClipboardCapture>();
        monitor.Captured += (_, capture) => captured.Add(capture);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(2, captured.Count);
        Assert.Equal("第一条", captured[0].Text);
        Assert.Equal("第二条", captured[1].Text);
    }

    [Fact]
    public async Task EmptyPasteboardRaisesNothingAndResetsDeduplication()
    {
        var runner = new FakeRunner()
            .WithChangeCounts(1, 2, 3)
            .WithCaptures(TextCapture("再次出现"), null, TextCapture("再次出现"));
        using var monitor = new MacPasteboardMonitor(runner, TimeSpan.FromMilliseconds(5));
        var captured = new List<SnapTranslate.Desktop.Platform.ClipboardCapture>();
        monitor.Captured += (_, capture) => captured.Add(capture);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(2, captured.Count);
        Assert.Equal("再次出现", captured[0].Text);
        Assert.Equal("再次出现", captured[1].Text);
    }

    [Fact]
    public async Task ReentrantPollIsIgnored()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .WithChangeCounts(1)
            .WithCaptures(TextCapture("并发"))
            .WithReadGate(gate);
        using var monitor = new MacPasteboardMonitor(runner, TimeSpan.FromMilliseconds(5));
        monitor.Captured += (_, _) => { };

        var first = monitor.PollOnceAsync();
        var second = monitor.PollOnceAsync();

        Assert.True(second.IsCompleted);
        gate.SetResult(true);
        await first;
        Assert.Equal(1, runner.ReadCount);
        Assert.Equal(1, runner.ChangeCountCallCount);
    }

    private static SnapTranslate.Desktop.Platform.ClipboardCapture TextCapture(string text)
        => new(
            SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text,
            text,
            null,
            DateTimeOffset.Now,
            ClipboardHash.Compute(SnapTranslate.Desktop.Platform.ClipboardCaptureKind.Text, text));

    private sealed class FakeRunner : IClipboardHelperRunner
    {
        private readonly Queue<int> _changeCounts = new();
        private readonly Queue<SnapTranslate.Desktop.Platform.ClipboardCapture?> _captures = new();
        private int _lastChangeCount;
        private SnapTranslate.Desktop.Platform.ClipboardCapture? _lastCapture;

        public int ReadCount { get; private set; }

        public int ChangeCountCallCount { get; private set; }

        public TaskCompletionSource<bool>? ReadGate { get; set; }

        public FakeRunner WithChangeCounts(params int[] values)
        {
            foreach (var value in values) _changeCounts.Enqueue(value);
            return this;
        }

        public FakeRunner WithCaptures(params SnapTranslate.Desktop.Platform.ClipboardCapture?[] values)
        {
            foreach (var value in values) _captures.Enqueue(value);
            return this;
        }

        public FakeRunner WithReadGate(TaskCompletionSource<bool> gate)
        {
            ReadGate = gate;
            return this;
        }

        public Task<int> GetChangeCountAsync(CancellationToken cancellationToken = default)
        {
            ChangeCountCallCount++;
            if (_changeCounts.Count > 0) _lastChangeCount = _changeCounts.Dequeue();
            return Task.FromResult(_lastChangeCount);
        }

        public async Task<ClipboardHelperReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (_captures.Count > 0) _lastCapture = _captures.Dequeue();
            if (ReadGate is not null) await ReadGate.Task.ConfigureAwait(false);
            return new ClipboardHelperReadResult(_lastChangeCount, _lastCapture);
        }

        public Task<bool> CopyAsync(SnapTranslate.Desktop.Platform.ClipboardCapture item, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
