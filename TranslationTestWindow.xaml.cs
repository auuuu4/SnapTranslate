using System.Windows;

namespace SnapTranslate;

/// <summary>
/// 「测试翻译」窗口：左边原文、右边译文，直接调用当前设置里的通道。
/// 用的是设置页里还没保存的表单值，所以可以边调参数边试，不需要先保存。
/// </summary>
public partial class TranslationTestWindow : Window
{
    private const string SampleText = "Good morning! The quick brown fox jumps over the lazy dog.";

    private readonly TranslationService _translator;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cancellation;

    public TranslationTestWindow(TranslationService translator, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(settings);
        _translator = translator;
        _settings = settings;
        InitializeComponent();
        ProviderText.Text = TranslationProviderCatalog.Describe(settings);
        SourceText.Text = SampleText;
        Loaded += (_, _) =>
        {
            SourceText.Focus();
            SourceText.SelectAll();
        };
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceText.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "请先输入要翻译的文本。";
            SourceText.Focus();
            return;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        TranslateButton.IsEnabled = false;
        TranslationText.Text = string.Empty;
        StatusText.Text = "正在翻译…";
        try
        {
            var progress = new Progress<string>(partial => TranslationText.Text = partial);
            var result = await _translator.TranslateAsync(text, _settings, progress, cancellation.Token);
            TranslationText.Text = result.Text;
            StatusText.Text = "翻译完成 · " + result.Provider;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            StatusText.Text = "翻译失败：" + ex.Message;
            AppLog.Error(nameof(TranslationTestWindow), ex);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            TranslateButton.IsEnabled = true;
            cancellation.Dispose();
        }
    }

    private async void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranslationText.Text)) return;
        try
        {
            await ClipboardService.SetTextAsync(TranslationText.Text);
            StatusText.Text = "已复制译文";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        base.OnClosed(e);
    }
}
