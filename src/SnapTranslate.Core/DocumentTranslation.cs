using CsvHelper;
using CsvHelper.Configuration;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using HtmlAgilityPack;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SnapTranslate;

public enum DocumentTaskStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Canceled
}

public sealed class DocumentTranslationTask : INotifyPropertyChanged
{
    private DocumentTaskStatus _status = DocumentTaskStatus.Queued;
    private int _progress;
    private string _statusText = "等待中";
    private string _outputPath = string.Empty;
    private string _error = string.Empty;

    public DocumentTranslationTask(string sourcePath, Guid? id = null, DateTime? createdAt = null)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        FileName = Path.GetFileName(sourcePath);
        Format = Path.GetExtension(sourcePath).TrimStart('.').ToUpperInvariant();
        Id = id ?? Guid.NewGuid();
        CreatedAt = createdAt ?? DateTime.Now;
    }

    public Guid Id { get; }
    public string SourcePath { get; }
    public string FileName { get; }
    public string Format { get; }
    public DateTime CreatedAt { get; }
    public CancellationTokenSource Cancellation { get; } = new();

    public DocumentTaskStatus Status
    {
        get => _status;
        set
        {
            if (!SetField(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    public int Progress
    {
        get => _progress;
        set
        {
            if (!SetField(ref _progress, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(ProgressDisplay));
        }
    }

    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (!SetField(ref _outputPath, value)) return;
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    public string Error { get => _error; set => SetField(ref _error, value); }
    public string ProgressDisplay => $"{Progress}%";
    public bool CanCancel => Status is DocumentTaskStatus.Queued or DocumentTaskStatus.Processing;
    public bool CanOpen => Status == DocumentTaskStatus.Completed && File.Exists(OutputPath);
    public string StatusDisplay => Status switch
    {
        DocumentTaskStatus.Queued => "等待",
        DocumentTaskStatus.Processing => "处理中",
        DocumentTaskStatus.Completed => "完成",
        DocumentTaskStatus.Failed => "失败",
        DocumentTaskStatus.Canceled => "已取消",
        _ => Status.ToString()
    };

    public DocumentTaskSnapshot CreateSnapshot() => new()
    {
        Id = Id,
        SourcePath = SourcePath,
        OutputPath = OutputPath,
        Status = Status,
        Progress = Progress,
        StatusText = StatusText,
        Error = Error,
        CreatedAt = CreatedAt
    };

    public static DocumentTranslationTask Restore(DocumentTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var interrupted = snapshot.Status is DocumentTaskStatus.Queued or DocumentTaskStatus.Processing;
        return new DocumentTranslationTask(snapshot.SourcePath, snapshot.Id, snapshot.CreatedAt)
        {
            OutputPath = snapshot.OutputPath,
            Status = interrupted ? DocumentTaskStatus.Failed : snapshot.Status,
            Progress = snapshot.Progress,
            StatusText = interrupted ? "上次退出时任务未完成" : snapshot.StatusText,
            Error = interrupted ? "应用关闭前任务仍在处理中。" : snapshot.Error
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record DocumentTranslationProgress(int Percentage, string Stage);

public sealed class DocumentTranslationService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".html", ".htm",
        ".docx", ".xlsx", ".pptx", ".pdf"
    };

    private readonly ITranslationService _translator;

    public DocumentTranslationService(ITranslationService translator)
    {
        _translator = translator;
    }

    public static string FileDialogFilter =>
        "支持的文档|*.txt;*.md;*.markdown;*.csv;*.tsv;*.html;*.htm;*.docx;*.xlsx;*.pptx;*.pdf|" +
        "Office 文档|*.docx;*.xlsx;*.pptx|文本与网页|*.txt;*.md;*.markdown;*.csv;*.tsv;*.html;*.htm|PDF|*.pdf";

    public static bool IsSupported(string path)
        => File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    public async Task<string> TranslateAsync(
        string sourcePath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSupported(sourcePath)) throw new NotSupportedException($"不支持此文件类型：{Path.GetExtension(sourcePath)}");
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var outputExtension = extension == ".pdf" ? ".txt" : extension;
        var outputPath = BuildOutputPath(sourcePath, settings, outputExtension);
        var temporaryOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(outputPath)}");
        progress?.Report(new DocumentTranslationProgress(2, "正在解析文档"));
        var translationSettings = CreateTranslationSettings(settings);

        try
        {
            switch (extension)
            {
                case ".txt":
                    await TranslateTextFileAsync(sourcePath, temporaryOutputPath, translationSettings, progress, cancellationToken);
                    break;
                case ".md":
                case ".markdown":
                    await TranslateMarkdownAsync(sourcePath, temporaryOutputPath, translationSettings, progress, cancellationToken);
                    break;
                case ".csv":
                case ".tsv":
                    await TranslateCsvAsync(sourcePath, temporaryOutputPath, extension == ".tsv" ? "\t" : ",",
                        translationSettings, progress, cancellationToken);
                    break;
                case ".html":
                case ".htm":
                    await TranslateHtmlAsync(sourcePath, temporaryOutputPath, translationSettings, progress, cancellationToken);
                    break;
                case ".docx":
                    await TranslateWordAsync(sourcePath, temporaryOutputPath, translationSettings, progress, cancellationToken);
                    break;
                case ".xlsx":
                    await TranslateSpreadsheetAsync(sourcePath, temporaryOutputPath, translationSettings, progress, cancellationToken);
                    break;
                case ".pptx":
                    await TranslatePresentationAsync(sourcePath, temporaryOutputPath, translationSettings, progress, cancellationToken);
                    break;
                case ".pdf":
                    await TranslatePdfAsync(sourcePath, temporaryOutputPath, translationSettings, progress, cancellationToken);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutputPath, outputPath);
            progress?.Report(new DocumentTranslationProgress(100, "翻译完成"));
            return outputPath;
        }
        finally
        {
            if (File.Exists(temporaryOutputPath)) File.Delete(temporaryOutputPath);
        }
    }

    private static AppSettings CreateTranslationSettings(AppSettings settings) => new()
    {
        Provider = settings.Provider,
        OllamaEndpoint = settings.OllamaEndpoint,
        OllamaModel = settings.OllamaModel,
        SourceLanguage = settings.DocumentSourceLanguage,
        TargetLanguage = settings.DocumentTargetLanguage,
        DocumentTranslationConcurrency = settings.DocumentTranslationConcurrency
    };

    private static string BuildOutputPath(string sourcePath, AppSettings settings, string extension)
    {
        var directory = string.IsNullOrWhiteSpace(settings.DocumentOutputDirectory)
            ? Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory
            : AppDataPaths.ExpandUserPath(settings.DocumentOutputDirectory);
        Directory.CreateDirectory(directory);
        var suffix = SanitizeSuffix(settings.DocumentOutputSuffix);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath) + suffix;
        var candidate = Path.Combine(directory, baseName + extension);
        if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(sourcePath), AppDataPaths.PathComparison))
            candidate = Path.Combine(directory, baseName + "_translated" + extension);
        if (!File.Exists(candidate)) return candidate;
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SanitizeSuffix(string value)
    {
        var suffix = string.IsNullOrWhiteSpace(value) ? "_translation" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) suffix = suffix.Replace(invalid, '_');
        return suffix.Length == 0 ? "_translation" : suffix;
    }

    private async Task TranslateTextFileAsync(
        string sourcePath,
        string outputPath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (text, encoding) = await ReadTextAsync(sourcePath, cancellationToken);
        var parts = Regex.Split(text, "(\\r\\n|\\n|\\r)");
        var values = parts.Where(part => part is not "\r\n" and not "\n" and not "\r").ToArray();
        var translated = await TranslateTextsAsync(values, settings, progress, cancellationToken);
        var translatedIndex = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index] is "\r\n" or "\n" or "\r") continue;
            parts[index] = translated[translatedIndex++];
        }
        progress?.Report(new DocumentTranslationProgress(96, "正在写入文件"));
        await File.WriteAllTextAsync(outputPath, string.Concat(parts), encoding, cancellationToken);
    }

    private async Task TranslateMarkdownAsync(
        string sourcePath,
        string outputPath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (text, encoding) = await ReadTextAsync(sourcePath, cancellationToken);
        var pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().EnableTrackTrivia().Build();
        var document = Markdown.Parse(text, pipeline);
        var literals = document.Descendants<LiteralInline>()
            .Where(item => item.Span.Start >= 0 && item.Span.End >= item.Span.Start)
            .Select(item => new TextSpan(item.Span.Start, item.Span.End - item.Span.Start + 1,
                text.Substring(item.Span.Start, item.Span.End - item.Span.Start + 1)))
            .DistinctBy(item => (item.Start, item.Length))
            .ToArray();
        var translated = await TranslateTextsAsync(literals.Select(item => item.Text).ToArray(), settings, progress, cancellationToken);
        var builder = new StringBuilder(text);
        for (var index = literals.Length - 1; index >= 0; index--)
            builder.Remove(literals[index].Start, literals[index].Length).Insert(literals[index].Start, translated[index]);
        progress?.Report(new DocumentTranslationProgress(96, "正在写入文件"));
        await File.WriteAllTextAsync(outputPath, builder.ToString(), encoding, cancellationToken);
    }

    private async Task TranslateHtmlAsync(
        string sourcePath,
        string outputPath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (text, encoding) = await ReadTextAsync(sourcePath, cancellationToken);
        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(text);
        var nodes = document.DocumentNode.Descendants()
            .Where(node => node.NodeType == HtmlNodeType.Text
                           && !node.Ancestors().Any(parent => parent.Name is "script" or "style" or "code" or "pre"))
            .Select(node => new HtmlTextUnit(node, SplitWhitespace(HtmlEntity.DeEntitize(node.InnerText))))
            .Where(item => ShouldTranslate(item.Parts.Core))
            .ToArray();
        var translated = await TranslateTextsAsync(nodes.Select(item => item.Parts.Core).ToArray(), settings, progress, cancellationToken);
        for (var index = 0; index < nodes.Length; index++)
            nodes[index].Node.InnerHtml = HtmlEntity.Entitize(nodes[index].Parts.Leading + translated[index] + nodes[index].Parts.Trailing);
        progress?.Report(new DocumentTranslationProgress(96, "正在写入文件"));
        await File.WriteAllTextAsync(outputPath, document.DocumentNode.OuterHtml, encoding, cancellationToken);
    }

    private async Task TranslateCsvAsync(
        string sourcePath,
        string outputPath,
        string delimiter,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null
        };
        var (text, encoding) = await ReadTextAsync(sourcePath, cancellationToken);
        var rows = new List<string[]>();
        using (var reader = new StringReader(text))
        using (var parser = new CsvParser(reader, configuration))
        {
            while (await parser.ReadAsync()) rows.Add(parser.Record?.ToArray() ?? Array.Empty<string>());
        }
        var cells = rows.SelectMany(row => row).ToArray();
        var translated = await TranslateTextsAsync(cells, settings, progress, cancellationToken);
        var offset = 0;
        progress?.Report(new DocumentTranslationProgress(96, "正在写入文件"));
        await using var output = new StreamWriter(outputPath, false, encoding);
        await using var writer = new CsvWriter(output, configuration);
        foreach (var row in rows)
        {
            for (var index = 0; index < row.Length; index++) writer.WriteField(translated[offset++]);
            await writer.NextRecordAsync();
        }
    }

    private async Task TranslateWordAsync(
        string sourcePath,
        string outputPath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await TranslateOpenXmlAsync(sourcePath, outputPath, async temporaryPath =>
        {
            using var document = WordprocessingDocument.Open(temporaryPath, true);
            var main = document.MainDocumentPart ?? throw new InvalidDataException("DOCX 缺少主文档内容。");
            var mainDocument = main.Document ?? throw new InvalidDataException("DOCX 主文档内容为空。");
            var roots = new List<OpenXmlPartRootElement> { mainDocument };
            roots.AddRange(main.HeaderParts.Select(part => part.Header).OfType<OpenXmlPartRootElement>());
            roots.AddRange(main.FooterParts.Select(part => part.Footer).OfType<OpenXmlPartRootElement>());
            if (main.FootnotesPart?.Footnotes is { } footnotes) roots.Add(footnotes);
            if (main.EndnotesPart?.Endnotes is { } endnotes) roots.Add(endnotes);
            var units = roots.SelectMany(root => root.Descendants<W.Paragraph>())
                .Select(paragraph => CreateMutableText(paragraph.Descendants<W.Text>().ToArray()))
                .Where(unit => unit is not null)
                .Cast<MutableText>()
                .ToArray();
            await TranslateMutableTextsAsync(units, settings, progress, cancellationToken);
        }, cancellationToken);
    }

    private async Task TranslatePresentationAsync(
        string sourcePath,
        string outputPath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await TranslateOpenXmlAsync(sourcePath, outputPath, async temporaryPath =>
        {
            using var document = PresentationDocument.Open(temporaryPath, true);
            var presentation = document.PresentationPart ?? throw new InvalidDataException("PPTX 缺少演示文稿内容。");
            var roots = new List<OpenXmlPartRootElement>();
            foreach (var slidePart in presentation.SlideParts)
            {
                roots.Add(slidePart.Slide ?? throw new InvalidDataException("PPTX 幻灯片内容为空。"));
                if (slidePart.NotesSlidePart?.NotesSlide is { } notes) roots.Add(notes);
            }
            var units = roots.SelectMany(root => root.Descendants<A.Paragraph>())
                .Select(paragraph => CreateMutableText(paragraph.Descendants<A.Text>().ToArray()))
                .Where(unit => unit is not null)
                .Cast<MutableText>()
                .ToArray();
            await TranslateMutableTextsAsync(units, settings, progress, cancellationToken);
        }, cancellationToken);
    }

    private async Task TranslateSpreadsheetAsync(
        string sourcePath,
        string outputPath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await TranslateOpenXmlAsync(sourcePath, outputPath, async temporaryPath =>
        {
            using var document = SpreadsheetDocument.Open(temporaryPath, true);
            var workbook = document.WorkbookPart ?? throw new InvalidDataException("XLSX 缺少工作簿内容。");
            var units = new List<MutableText>();
            if (workbook.SharedStringTablePart?.SharedStringTable is { } sharedStrings)
            {
                units.AddRange(sharedStrings.Elements<S.SharedStringItem>()
                    .Select(item => CreateMutableText(item.Descendants<S.Text>().ToArray()))
                    .Where(unit => unit is not null)
                    .Cast<MutableText>());
            }
            foreach (var worksheet in workbook.WorksheetParts)
            {
                var worksheetRoot = worksheet.Worksheet ?? throw new InvalidDataException("XLSX 工作表内容为空。");
                foreach (var cell in worksheetRoot.Descendants<S.Cell>())
                {
                    if (cell.InlineString is not null)
                    {
                        var unit = CreateMutableText(cell.InlineString.Descendants<S.Text>().ToArray());
                        if (unit is not null) units.Add(unit);
                    }
                    else if (cell.CellFormula is null && cell.DataType?.Value == S.CellValues.String && cell.CellValue is not null)
                    {
                        var value = cell.CellValue.Text;
                        units.Add(new MutableText(value, translated => cell.CellValue.Text = translated));
                    }
                }
            }
            await TranslateMutableTextsAsync(units, settings, progress, cancellationToken);
        }, cancellationToken);
    }

    private async Task TranslatePdfAsync(
        string sourcePath,
        string outputPath,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<string>();
        using (var document = PdfDocument.Open(sourcePath))
        {
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pages.Add(page.Text);
            }
        }
        var translated = await TranslateTextsAsync(pages.ToArray(), settings, progress, cancellationToken);
        progress?.Report(new DocumentTranslationProgress(96, "正在写入文件"));
        var output = string.Join(Environment.NewLine + Environment.NewLine, translated.Select((page, index) =>
            $"--- Page {index + 1} ---{Environment.NewLine}{page}"));
        await File.WriteAllTextAsync(outputPath, output, new UTF8Encoding(false), cancellationToken);
    }

    private async Task TranslateOpenXmlAsync(
        string sourcePath,
        string outputPath,
        Func<string, Task> translate,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(outputPath)}");
        try
        {
            File.Copy(sourcePath, temporaryPath, true);
            await translate(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task TranslateMutableTextsAsync(
        IReadOnlyList<MutableText> units,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var translated = await TranslateTextsAsync(units.Select(item => item.Text).ToArray(), settings, progress, cancellationToken);
        for (var index = 0; index < units.Count; index++) units[index].Apply(translated[index]);
        progress?.Report(new DocumentTranslationProgress(96, "正在写入文档结构"));
    }

    private async Task<string[]> TranslateTextsAsync(
        IReadOnlyList<string> values,
        AppSettings settings,
        IProgress<DocumentTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0) throw new InvalidDataException("文档中没有可翻译文本。");
        var output = values.ToArray();
        var groups = Enumerable.Range(0, values.Count)
            .Where(index => ShouldTranslate(values[index]))
            .GroupBy(index => values[index], StringComparer.Ordinal)
            .ToArray();
        if (groups.Length == 0) throw new InvalidDataException("文档中没有可翻译文本。");
        var total = groups.Sum(group => group.Count());
        var completed = 0;
        var lastReportedPercentage = 7;
        var concurrency = Math.Max(1, settings.DocumentTranslationConcurrency);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var lastCompletionTicks = DateTime.UtcNow.Ticks;
        var tasks = groups.Select(async group =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var translated = await TranslateValueAsync(group.Key, settings, cancellationToken);
                foreach (var index in group) output[index] = translated;
                var done = Interlocked.Add(ref completed, group.Count());
                Interlocked.Exchange(ref lastCompletionTicks, DateTime.UtcNow.Ticks);
                var percentage = 8 + (int)Math.Round(done * 86d / total);
                var previousPercentage = Volatile.Read(ref lastReportedPercentage);
                while (percentage > previousPercentage)
                {
                    if (Interlocked.CompareExchange(ref lastReportedPercentage, percentage, previousPercentage)
                        == previousPercentage)
                    {
                        progress?.Report(new DocumentTranslationProgress(percentage, $"正在翻译 {done}/{total}"));
                        break;
                    }
                    previousPercentage = Volatile.Read(ref lastReportedPercentage);
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        var allTasks = Task.WhenAll(tasks);
        while (!allTasks.IsCompleted)
        {
            await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken));
            if (allTasks.IsCompleted) break;
            cancellationToken.ThrowIfCancellationRequested();

            var idle = DateTime.UtcNow - new DateTime(Volatile.Read(ref lastCompletionTicks), DateTimeKind.Utc);
            if (idle < TimeSpan.FromSeconds(15)) continue;
            var done = Volatile.Read(ref completed);
            var percentage = Volatile.Read(ref lastReportedPercentage);
            progress?.Report(new DocumentTranslationProgress(
                percentage,
                $"等待翻译服务响应（{Math.Max(15, (int)idle.TotalSeconds)} 秒） {done}/{total}"));
        }
        await allTasks;
        return output;
    }

    private async Task<string> TranslateValueAsync(string value, AppSettings settings, CancellationToken cancellationToken)
    {
        var parts = SplitLongText(value, 2500);
        var translated = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var whitespace = SplitWhitespace(part);
            if (!ShouldTranslate(whitespace.Core))
            {
                translated.Add(part);
                continue;
            }
            var result = await _translator.TranslateAsync(whitespace.Core, settings, cancellationToken);
            translated.Add(whitespace.Leading + result.Text.Trim() + whitespace.Trailing);
        }
        return string.Concat(translated);
    }

    private static List<string> SplitLongText(string value, int limit)
    {
        if (value.Length <= limit) return new List<string> { value };
        var chunks = new List<string>();
        var start = 0;
        while (start < value.Length)
        {
            var end = Math.Min(value.Length, start + limit);
            if (end < value.Length)
            {
                var split = value.LastIndexOfAny(new[] { '\n', '\r', '。', '！', '？', '.', '!', '?', ' ' }, end - 1, end - start);
                if (split > start + limit / 2) end = split + 1;
            }
            var chunk = value[start..end];
            if (chunk.Length > 0) chunks.Add(chunk);
            start = end;
        }
        return chunks;
    }

    private static bool ShouldTranslate(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || !text.Any(char.IsLetter)) return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out _)) return false;
        return true;
    }

    private static MutableText? CreateMutableText<T>(IReadOnlyList<T> nodes) where T : OpenXmlLeafTextElement
    {
        if (nodes.Count == 0) return null;
        var value = string.Concat(nodes.Select(node => node.Text));
        return new MutableText(value, translated =>
        {
            nodes[0].Text = translated;
            for (var index = 1; index < nodes.Count; index++) nodes[index].Text = string.Empty;
        });
    }

    private static async Task<(string Text, Encoding Encoding)> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return (text, reader.CurrentEncoding);
    }

    private static WhitespaceParts SplitWhitespace(string value)
    {
        var leadingLength = value.Length - value.TrimStart().Length;
        var trailingLength = value.Length - value.TrimEnd().Length;
        var coreLength = Math.Max(0, value.Length - leadingLength - trailingLength);
        return new WhitespaceParts(value[..leadingLength], value.Substring(leadingLength, coreLength),
            trailingLength == 0 ? string.Empty : value[^trailingLength..]);
    }

    private sealed record MutableText(string Text, Action<string> Apply);
    private sealed record TextSpan(int Start, int Length, string Text);
    private sealed record WhitespaceParts(string Leading, string Core, string Trailing);
    private sealed record HtmlTextUnit(HtmlNode Node, WhitespaceParts Parts);
}
