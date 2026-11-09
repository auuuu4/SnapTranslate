using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using SnapTranslate.Desktop.Platform;
using Xunit;

namespace SnapTranslate.Desktop.Tests.Shell;

public sealed class SettingsTabTests
{
    [AvaloniaFact]
    public void ProviderComboListsEverySelectableProvider()
    {
        using var temporary = new TempData();
        var window = new MainWindow(temporary.Path);
        try
        {
            window.Show();
            var combo = window.FindControl<ComboBox>("ProviderCombo");
            Assert.NotNull(combo);
            Assert.Equal(TranslationProviderCatalog.SelectableProviders.Count, combo.ItemCount);
            var first = Assert.IsType<ComboBoxItem>(combo.Items[0]);
            Assert.Equal(TranslationProviderCatalog.DisplayName(TranslationProviderCatalog.SelectableProviders[0]), first.Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CreatingChatProfileAddsRowAndMarksItActive()
    {
        using var temporary = new TempData();
        var window = new MainWindow(temporary.Path);
        try
        {
            window.Show();
            ShowSettingsTab(window);
            var list = window.FindControl<ListBox>("ChatProfileList");
            Assert.NotNull(list);
            Assert.Equal(0, list.ItemCount);

            Click(window, "新建");
            Assert.Equal(1, list.ItemCount);

            var active = window.FindControl<TextBlock>("ActiveProfileText");
            Assert.NotNull(active);
            Assert.Contains("当前渠道", active.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("（未选择）", active.Text, StringComparison.Ordinal);

            Click(window, "删除");
            Assert.Equal(0, list.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResetPromptFillsDefaultTemplate()
    {
        using var temporary = new TempData();
        var window = new MainWindow(temporary.Path);
        try
        {
            window.Show();
            ShowSettingsTab(window);
            var prompt = window.FindControl<TextBox>("TranslationPromptText");
            Assert.NotNull(prompt);
            Click(window, "恢复默认提示词");
            Assert.Equal(TranslationPrompt.Default, prompt.Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static void ShowSettingsTab(Window window)
    {
        var tabs = window.FindControl<TabControl>("MainTabs");
        Assert.NotNull(tabs);
        tabs.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Window window, string content)
    {
        var button = FindAll<Button>(window).FirstOrDefault(item => Equals(item.Content, content));
        Assert.NotNull(button);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static IEnumerable<T> FindAll<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private sealed class TempData : IDisposable
    {
        public TempData()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SnapTranslate.Desktop.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
