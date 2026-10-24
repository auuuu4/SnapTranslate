using System.Windows;

namespace SnapTranslate;

public partial class ResultWindow : Window
{
    public ResultWindow()
    {
        InitializeComponent();
        SetBusy("正在识别...");
    }

    public void SetBusy(string message)
    {
        StatusText.Text = message;
    }

    public void SetSource(string text)
    {
        SourceText.Text = text;
        StatusText.Text = string.Empty;
    }

    public void SetTranslation(string text, string provider)
    {
        TranslationText.Text = text;
        ProviderText.Text = provider;
        StatusText.Text = string.Empty;
    }

    public void SetError(string message)
    {
        StatusText.Text = message;
    }

    private async void CopySource_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SourceText.Text)) await CopyTextAsync(SourceText.Text, "已复制原文");
    }

    private async void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TranslationText.Text)) await CopyTextAsync(TranslationText.Text, "已复制译文");
    }

    private async Task CopyTextAsync(string text, string successMessage)
    {
        try
        {
            await ClipboardService.SetTextAsync(text);
            StatusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }
}
