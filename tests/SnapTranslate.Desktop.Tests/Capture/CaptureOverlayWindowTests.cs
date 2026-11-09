using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using SnapTranslate.Desktop.Capture;
using SnapTranslate.Desktop.Platform;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Capture;

public sealed class CaptureOverlayWindowTests
{
    [AvaloniaFact]
    public void OverlayRendersAndShowsToolbarAfterSelection()
    {
        var screen = CaptureTestImages.CreateScreen(800, 600);
        var window = new CaptureOverlayWindow(screen);
        try
        {
            window.Show();

            // 初始状态：铺满整屏并显示提示与取消按钮
            var initialFrame = window.CaptureRenderedFrame();
            Assert.NotNull(initialFrame);
            Assert.Equal(800, initialFrame.PixelSize.Width);
            Assert.Equal(600, initialFrame.PixelSize.Height);
            var cancel = window.FindControl<Button>("CancelButton");
            Assert.NotNull(cancel);
            Assert.True(cancel.Bounds.Width > 0, "未框选时也应该能看到取消按钮。");

            // 反向拖拽（右下 → 左上）框选，验证浮层正常响应指针事件
            window.MouseDown(new Point(520, 380), MouseButton.Left);
            window.MouseMove(new Point(120, 80));
            window.MouseUp(new Point(120, 80), MouseButton.Left);

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(new PixelRect(120, 80, 400, 300), window.SelectedRegion.GetValueOrDefault());

            foreach (var name in new[]
                     {
                         "SaveButton", "PinButton", "CopyButton", "RectangleToolButton", "MosaicToolButton", "UndoButton"
                     })
            {
                var button = window.FindControl<Button>(name);
                Assert.NotNull(button);
                Assert.True(button.Bounds.Width > 0, $"{name} 应该可见。");
            }

            // 工具条必须完整落在窗口内，不能被裁切
            var toolbar = window.FindControl<Border>("Toolbar");
            Assert.NotNull(toolbar);
            var left = Canvas.GetLeft(toolbar);
            var top = Canvas.GetTop(toolbar);
            Assert.True(left >= 0 && top >= 0, "工具条不能被放到窗口外。");
            Assert.True(
                left + toolbar.Bounds.Width <= window.Bounds.Width + 0.5,
                $"工具条右边超出窗口：left={left} width={toolbar.Bounds.Width} window={window.Bounds.Width}");
            Assert.True(
                top + toolbar.Bounds.Height <= window.Bounds.Height + 0.5,
                $"工具条下边超出窗口：top={top} height={toolbar.Bounds.Height} window={window.Bounds.Height}");
        }
        finally
        {
            if (window.IsVisible) window.Close();
            screen.Image.Dispose();
        }
    }

    [AvaloniaFact]
    public void CompleteButtonProducesCroppedOutcome()
    {
        var screen = CaptureTestImages.CreateScreen(800, 600);
        var window = new CaptureOverlayWindow(screen);
        CaptureOutcome? outcome = null;
        try
        {
            window.Show();
            window.MouseDown(new Point(100, 50), MouseButton.Left);
            window.MouseMove(new Point(500, 350));
            window.MouseUp(new Point(500, 350), MouseButton.Left);

            var complete = window.FindControl<Button>("CompleteButton");
            Assert.NotNull(complete);
            complete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(window.OutcomeTask.IsCompleted, "点击完成后应该立即返回结果。");
            var completed = window.OutcomeTask.Result;
            Assert.NotNull(completed);
            outcome = completed;
            Assert.Equal(CaptureAction.Copy, completed!.Action);
            Assert.Equal(new PixelRect(100, 50, 400, 300), completed.Region);
            Assert.Equal(400, completed.Image.PixelSize.Width);
            Assert.Equal(300, completed.Image.PixelSize.Height);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            outcome?.Image.Dispose();
            screen.Image.Dispose();
        }
    }

    [AvaloniaFact]
    public void EscapeCancelsOverlay()
    {
        var screen = CaptureTestImages.CreateScreen(640, 480);
        var window = new CaptureOverlayWindow(screen);
        try
        {
            window.Show();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

            Assert.True(window.OutcomeTask.IsCompleted, "按 Esc 应该结束截图任务。");
            Assert.Null(window.OutcomeTask.Result);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            screen.Image.Dispose();
        }
    }
}