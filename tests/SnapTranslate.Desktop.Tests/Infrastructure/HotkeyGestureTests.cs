using SnapTranslate.Desktop.Platform;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Infrastructure;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F", "Ctrl+Alt+F")]
    [InlineData("ctrl+alt+f", "Ctrl+Alt+F")]
    [InlineData("Cmd+Shift+A", "Ctrl+Shift+A")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("Ctrl+Alt+1", "Ctrl+Alt+1")]
    public void ParsesSupportedGestures(string gesture, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out var parsed, out var error), error);
        Assert.Equal(canonical, parsed.CanonicalText);
        Assert.True(parsed.Modifiers != 0);
    }

    [Fact]
    public void MapsLettersToCarbonVirtualKeyCodes()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+F", out var parsed, out _));
        Assert.Equal((ushort)3, parsed.KeyCode);
        Assert.Equal(HotkeyGesture.CommandKey | HotkeyGesture.OptionKey, parsed.Modifiers);
    }

    [Theory]
    [InlineData("F", "修饰键")]
    [InlineData("Ctrl+", "主键")]
    [InlineData("", "不能为空")]
    [InlineData("Ctrl+Alt+ZZ9", "暂不支持")]
    public void RejectsInvalidGestures(string gesture, string expectedMessagePart)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out var error));
        Assert.Contains(expectedMessagePart, error, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayTextUsesCommandOnMacConvention()
    {
        HotkeyGesture.TryParse("Ctrl+Shift+V", out var parsed, out _);
        Assert.Equal("Cmd+Shift+V", parsed.DisplayText);
    }

    [Fact]
    public void UnsupportedPlatformReportsReasonInsteadOfThrowing()
    {
        // macOS 上会真的向系统注册快捷键，CI 里没有交互会话，跳过以免污染运行环境。
        if (OperatingSystem.IsMacOS()) return;
        var service = new CarbonHotkeyService();
        var hotkeys = new[] { new GlobalHotkey("screenshot", "截图", "Ctrl+Alt+A") };
        var ok = service.TryRegister(hotkeys, out var error);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        service.Dispose();
    }
}
