using SnapTranslate;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        // 渲染宿主只加载 App.xaml 资源，不去抢用户正在运行实例的单实例互斥体。
        App.HeadlessHost = true;
        if (args.Length >= 2 && string.Equals(args[0], "--ocr-file", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(args[1]);
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            var sourceLanguage = args.Length >= 3 ? args[2] : "auto";
            BitmapSource sourceImage = image;
            if (args.Length >= 7
                && int.TryParse(args[3], out var cropX)
                && int.TryParse(args[4], out var cropY)
                && int.TryParse(args[5], out var cropWidth)
                && int.TryParse(args[6], out var cropHeight))
            {
                sourceImage = new CroppedBitmap(image, new System.Windows.Int32Rect(cropX, cropY, cropWidth, cropHeight));
                sourceImage.Freeze();
            }
            var layout = await new OcrService().RecognizeLayoutAsync(sourceImage, sourceLanguage);
            var overlayBounds = ScreenshotTranslationWindow.CalculateOverlayBounds(
                layout.Lines, 1, 1, sourceImage.PixelWidth, sourceImage.PixelHeight);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                path,
                width = sourceImage.PixelWidth,
                height = sourceImage.PixelHeight,
                sourceLanguage,
                layout.DetectedLanguage,
                layout.Text,
                lines = layout.Lines,
                activeOverlayRegions = overlayBounds.Count(bounds => !bounds.IsEmpty),
                overlappingOverlayPairs = CountOverlappingRegions(overlayBounds)
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length >= 2 && string.Equals(args[0], "--clipboard-image-latency", StringComparison.OrdinalIgnoreCase))
        {
            RunImageClipboardLatencyTest(Path.GetFullPath(args[1]));
            return 0;
        }
        if (args.Length >= 4 && string.Equals(args[0], "--translate-text", StringComparison.OrdinalIgnoreCase))
        {
            var requestSettings = new AppSettings
            {
                Provider = TranslationProvider.Youdao,
                SourceLanguage = args[1],
                TargetLanguage = args[2]
            };
            var result = await new TranslationService().TranslateAsync(args[3], requestSettings);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Contains("--pinned-image", StringComparer.OrdinalIgnoreCase))
        {
            TestPinnedImageRegression(artifacts);
            Console.WriteLine(JsonSerializer.Serialize(new { pinnedImagePassed = true }));
            return 0;
        }
        if (args.Contains("--keyboard-hook-live", StringComparer.OrdinalIgnoreCase))
        {
            TestLowLevelKeyboardHookHost();
            Console.WriteLine(JsonSerializer.Serialize(new { keyboardHookLivePassed = true }));
            return 0;
        }
        if (args.Contains("--offline", StringComparer.OrdinalIgnoreCase))
        {
            TestCaptureLogic();
            TestHotkeyExecutionGate();
            TestPinnedImageRegression(artifacts);
            var offlineClipboardPassed = await TestClipboardRepositoryAsync(artifacts);
            var offlineDocumentPassed = await TestDocumentTranslationAsync(artifacts);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                pinnedImagePassed = true,
                offlineClipboardPassed,
                offlineDocumentPassed
            }));
            return offlineClipboardPassed && offlineDocumentPassed ? 0 : 1;
        }
        if (args.Contains("--clipboard-image-formats", StringComparer.OrdinalIgnoreCase))
        {
            return await RunClipboardImageFormatsTestAsync();
        }
        if (args.Contains("--ocr-eval", StringComparer.OrdinalIgnoreCase))
        {
            return await RunOcrEvaluationAsync(artifacts);
        }
        if (args.Contains("--live-translation", StringComparer.OrdinalIgnoreCase))
        {
            Exception? liveError = null;
            var liveThread = new Thread(() =>
            {
                try { RunLiveTranslation(artifacts); }
                catch (Exception ex) { liveError = ex; }
            });
            liveThread.SetApartmentState(ApartmentState.STA);
            liveThread.Start();
            liveThread.Join();
            if (liveError is not null) throw new InvalidOperationException("Live translation failed", liveError);
            return 0;
        }
        if (args.Contains("--live-document", StringComparer.OrdinalIgnoreCase))
        {
            await RunLiveDocumentTranslationAsync(artifacts);
            return 0;
        }
        if (args.Contains("--clipboard-latency", StringComparer.OrdinalIgnoreCase))
        {
            RunClipboardLatencyTest();
            return 0;
        }
        if (args.Contains("--stress-input-layout", StringComparer.OrdinalIgnoreCase))
        {
            Exception? stressError = null;
            var stressThread = new Thread(() =>
            {
                try { RunInputLayoutStress(); }
                catch (Exception ex) { stressError = ex; }
            });
            stressThread.SetApartmentState(ApartmentState.STA);
            stressThread.Start();
            stressThread.Join();
            if (stressError is not null) throw new InvalidOperationException("Input layout stress failed", stressError);
            return 0;
        }
        if (args.Contains("--restore-latest-clipboard", StringComparer.OrdinalIgnoreCase))
        {
            await RestoreLatestClipboardItemAsync();
            return 0;
        }
        if (args.Contains("--restore-latest-text-clipboard", StringComparer.OrdinalIgnoreCase))
        {
            await RestoreLatestTextClipboardItemAsync();
            return 0;
        }
        if (args.Any(arg => arg.StartsWith("--render-", StringComparison.OrdinalIgnoreCase)))
        {
            Exception? renderError = null;
            var renderMode = args.Contains("--render-capture-compact", StringComparer.OrdinalIgnoreCase)
                ? "capture-compact"
                : args.Contains("--render-capture", StringComparer.OrdinalIgnoreCase)
                ? "capture"
                : args.Contains("--render-translation-compact", StringComparer.OrdinalIgnoreCase)
                ? "translation-compact"
                : args.Contains("--render-translation", StringComparer.OrdinalIgnoreCase)
                ? "translation"
                : args.Contains("--render-tasks", StringComparer.OrdinalIgnoreCase)
                ? "tasks"
                : args.Contains("--render-quick", StringComparer.OrdinalIgnoreCase)
                ? "quick"
                : args.Contains("--render-settings", StringComparer.OrdinalIgnoreCase)
                ? "settings"
                : args.Contains("--render-input", StringComparer.OrdinalIgnoreCase) ? "input" : "main";
            var thread = new Thread(() =>
            {
                    try
                    {
                        TestCaptureLogic();
                        RenderUi(artifacts, renderMode);
                    }
                catch (Exception ex) { renderError = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (renderError is not null) throw new InvalidOperationException("UI render failed", renderError);
            return 0;
        }
        var imagePath = Path.Combine(artifacts, "ocr-input.png");
        var resultPath = Path.Combine(artifacts, "result.json");

        using (var bitmap = new Bitmap(1400, 360, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var english = new Font("Arial", 62, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
        using (var chinese = new Font("Microsoft YaHei", 62, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString("Hello OCR 123", english, Brushes.Black, 50, 45);
            graphics.DrawString("截图翻译测试", chinese, Brushes.Black, 50, 175);
            bitmap.Save(imagePath, ImageFormat.Png);
        }

        BitmapImage source;
        using (var stream = File.OpenRead(imagePath))
        {
            source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream;
            source.EndInit();
            source.Freeze();
        }

        var ocrLayout = await new OcrService().RecognizeLayoutAsync(source);
        var recognized = ocrLayout.Text;
        var settings = new AppSettings { Provider = TranslationProvider.Auto, TargetLanguage = "en" };
        var translated = await new TranslationService().TranslateAsync("截图翻译测试", settings);
        var clipboardPassed = await TestClipboardRepositoryAsync(artifacts);
        var documentPassed = await TestDocumentTranslationAsync(artifacts);
        var report = new
        {
            recognized,
            translation = translated.Text,
            provider = translated.Provider,
            layoutLines = ocrLayout.Lines.Count,
            ocrPassed = recognized.Contains("Hello", StringComparison.OrdinalIgnoreCase)
                        && recognized.Contains("截图翻译测试", StringComparison.Ordinal),
            layoutPassed = ocrLayout.Lines.Count >= 2
                           && ocrLayout.Lines.All(line => line.Width > 0 && line.Height > 0
                                                          && line.X >= 0 && line.Y >= 0),
            translationPassed = translated.Text.Length > 0 && !translated.Text.Contains("截图翻译测试", StringComparison.Ordinal),
            clipboardPassed,
            documentPassed
        };
        File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(resultPath));
        return report.ocrPassed && report.layoutPassed && report.translationPassed
               && report.clipboardPassed && report.documentPassed ? 0 : 1;
    }

    /// <summary>OCR 评测：固定样张跑一遍识别，输出每条与总体的字符错误率（CER）。</summary>
    private static async Task<int> RunOcrEvaluationAsync(string artifacts)
    {
        var samples = OcrEvalSamples.Create(artifacts);
        var service = new OcrService();
        double sumErrors = 0;
        double sumChars = 0;
        Console.WriteLine("case".PadRight(26) + "CER".PadLeft(8) + "   text");
        foreach (var sample in samples)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(sample.Path);
            image.EndInit();
            image.Freeze();

            string actual;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                actual = await service.RecognizeAsync(image);
            }
            catch (Exception ex)
            {
                Console.WriteLine(sample.Name.PadRight(26) + "   ERROR " + ex.Message);
                continue;
            }

            var expected = OcrEvalSamples.Squash(sample.Expected);
            var got = OcrEvalSamples.Squash(actual);
            var errors = OcrEvalSamples.EditDistance(expected, got);
            sumErrors += errors;
            sumChars += expected.Length;
            var cer = expected.Length == 0 ? 0 : (double)errors / expected.Length;
            stopwatch.Stop();
            Console.WriteLine(sample.Name.PadRight(26) + cer.ToString("P1").PadLeft(8)
                + (stopwatch.ElapsedMilliseconds + "ms").PadLeft(7) + "   " + got);
        }

        var overall = sumChars == 0 ? 0 : sumErrors / sumChars;
        Console.WriteLine(new string('-', 56));
        Console.WriteLine(("overall CER " + overall.ToString("P2")).PadRight(26) + ("chars " + (int)sumChars).PadLeft(8));
        return 0;
    }

    private sealed record OcrEvalSample(string Name, string Path, string Expected);

    private static class OcrEvalSamples
    {
        public static string Squash(string value)
            => new(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

        public static int EditDistance(string left, string right)
        {
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var j = 0; j <= right.Length; j++) previous[j] = j;
            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                (previous, current) = (current, previous);
            }
            return previous[right.Length];
        }

        public static IReadOnlyList<OcrEvalSample> Create(string artifacts)
        {
            var root = Path.Combine(artifacts, "ocr-eval");
            Directory.CreateDirectory(root);
            return
            [
                Make(root, "zh-18-white", "窗口智能选择、多屏捕获、标注与 OCR 操作", 18, 420,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(30, 30, 30)),
                Make(root, "zh-14-white", "设置中心：翻译方式与快捷键", 14, 320,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(30, 30, 30)),
                Make(root, "zh-12-small", "这是一个很小的十二号字体测试", 12, 300,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(20, 20, 20)),
                Make(root, "zh-radical", "湖泊请假树木说话设计", 20, 320,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(20, 20, 20)),
                Make(root, "zh-dark-bg", "深色背景上的白色文字识别", 18, 340,
                    System.Drawing.Color.FromArgb(24, 26, 28), System.Drawing.Color.FromArgb(235, 238, 240)),
                Make(root, "zh-low-contrast", "低对比度灰色文字也需要认出来", 16, 340,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(150, 150, 150)),
                Make(root, "zh-punct", "确定？取消！保存、退出。", 18, 280,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(25, 25, 25)),
                Make(root, "en-18-white", "SnapTranslate OCR regression 2025", 18, 420,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(25, 25, 25)),
                Make(root, "mixed", "OCR 识别 accuracy 测试 2025", 16, 340,
                    System.Drawing.Color.White, System.Drawing.Color.FromArgb(25, 25, 25)),
                Make(root, "zh-tinted", "半透明遮罩上的文字", 15, 260,
                    System.Drawing.Color.FromArgb(250, 244, 240), System.Drawing.Color.FromArgb(40, 45, 48))
            ];
        }

        private static OcrEvalSample Make(string root, string name, string text, int fontSize, int width,
            System.Drawing.Color background, System.Drawing.Color foreground)
        {
            using var bitmap = new System.Drawing.Bitmap(width, 64);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(background);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = new System.Drawing.Font("Microsoft YaHei UI", fontSize, System.Drawing.GraphicsUnit.Pixel);
                using var brush = new System.Drawing.SolidBrush(foreground);
                graphics.DrawString(text, font, brush, new System.Drawing.PointF(8, 12));
            }
            var path = Path.Combine(root, name + ".png");
            bitmap.Save(path, ImageFormat.Png);
            return new OcrEvalSample(name, path, text);
        }
    }

    /// <summary>验证写进剪贴板的图片同时带 PNG 格式，避免接收方（微信/QQ/浏览器）认不出图片。</summary>
    private static Task<int> RunClipboardImageFormatsTestAsync()
    {
        var result = 1;
        // 剪贴板（OLE）要求 STA：async Main 的续体可能落在非 STA 线程，这里单独开一个 STA 线程
        var thread = new Thread(() =>
        {
            const int width = 120;
            const int height = 60;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x30;
                pixels[i + 1] = 0x80;
                pixels[i + 2] = 0xD0;
                pixels[i + 3] = 0xFF;
            }
            var image = BitmapSource.Create(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze();

            ClipboardService.SetImageAsync(image).GetAwaiter().GetResult();
            Thread.Sleep(200);

            var data = System.Windows.Forms.Clipboard.GetDataObject();
            var formats = data?.GetFormats(false) ?? Array.Empty<string>();
            Console.WriteLine("clipboard formats: " + string.Join(", ", formats));
            var hasPng = formats.Any(item => string.Equals(item, "PNG", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(item, "image/png", StringComparison.OrdinalIgnoreCase));
            var hasDib = formats.Any(item => item.Contains("Dib", StringComparison.OrdinalIgnoreCase)
                                  || item.Contains("Bitmap", StringComparison.OrdinalIgnoreCase));
            var roundTrip = System.Windows.Forms.Clipboard.GetImage();
            Console.WriteLine($"png={hasPng} dib={hasDib} roundtrip={(roundTrip is null ? "null" : $"{roundTrip.Width}x{roundTrip.Height}")}");
            result = hasPng && hasDib && roundTrip is not null && roundTrip.Width == width && roundTrip.Height == height ? 0 : 1;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return Task.FromResult(result);
    }

    private static async Task<bool> TestClipboardRepositoryAsync(string artifacts)
    {
        var testRoot = Path.Combine(artifacts, "clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        using var repository = new ClipboardRepository(Path.Combine(testRoot, "native"));
        const string firstText = "SnapTranslate clipboard regression item";
        var first = new ClipboardCapture(
            ClipboardItemKind.Text,
            firstText,
            firstText,
            string.Empty,
            firstText.Length,
            0,
            0,
            ClipboardRepository.HashContent(ClipboardItemKind.Text, firstText));
        var item = repository.Upsert(first, 100, 0);
        repository.Upsert(first, 100, 0);
        await repository.SetNoteAsync(item.Id, "regression-note");
        var firstFavoriteWrite = repository.SetFavoriteAsync(item.Id, false);
        var finalFavoriteWrite = repository.SetFavoriteAsync(item.Id, true);
        await Task.WhenAll(firstFavoriteWrite, finalFavoriteWrite);

        var search = repository.Search("regression-note", ClipboardFilter.Favorite);
        return repository.Count() == 1
               && search.Count == 1
               && search[0].IsFavorite
               && search[0].UseCount == 2;
    }

    private static async Task<bool> TestDocumentTranslationAsync(string artifacts)
    {
        var root = Path.Combine(artifacts, "documents-" + Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "created", "translations");
        Directory.CreateDirectory(root);
        var files = new List<string>();

        var textPath = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(textPath, "Hello document\r\nSecond line");
        files.Add(textPath);

        var markdownPath = Path.Combine(root, "guide.md");
        await File.WriteAllTextAsync(markdownPath, "# Welcome\n\nThis is **important**.\n\n`keep_code`");
        files.Add(markdownPath);

        var csvPath = Path.Combine(root, "items.csv");
        await File.WriteAllTextAsync(csvPath, "Name,Description\r\nWidget,Useful tool");
        files.Add(csvPath);

        var htmlPath = Path.Combine(root, "page.html");
        await File.WriteAllTextAsync(htmlPath, "<html><body><h1>Hello page</h1><code>keep_code</code><p>Useful tool</p></body></html>");
        files.Add(htmlPath);

        var wordPath = Path.Combine(root, "report.docx");
        using (var document = WordprocessingDocument.Create(wordPath, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("Quarterly report")))));
            main.Document.Save();
        }
        files.Add(wordPath);

        var sheetPath = Path.Combine(root, "table.xlsx");
        using (var document = SpreadsheetDocument.Create(sheetPath, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text("Revenue summary")) })));
            var sheets = workbook.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Data" });
            workbook.Workbook.Save();
        }
        files.Add(sheetPath);

        var presentationPath = Path.Combine(root, "slides.pptx");
        using (var document = PresentationDocument.Create(presentationPath, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            var slide = presentation.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.Shape(new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("Project overview"))))))));
            slide.Slide.Save();
            var slideIds = presentation.Presentation.AppendChild(new P.SlideIdList());
            slideIds.Append(new P.SlideId { Id = 256U, RelationshipId = presentation.GetIdOfPart(slide) });
            presentation.Presentation.Save();
        }
        files.Add(presentationPath);

        var pdfPath = Path.Combine(root, "brief.pdf");
        CreateSimplePdf(pdfPath, "Hello PDF document");
        files.Add(pdfPath);

        var settings = new AppSettings
        {
            DocumentSourceLanguage = "auto",
            DocumentTargetLanguage = "zh-CN",
            DocumentOutputDirectory = outputDirectory,
            DocumentOutputSuffix = "_translation"
        };
        var service = new DocumentTranslationService(new FakeTranslationService());
        var outputs = new List<string>();
        foreach (var file in files)
            outputs.Add(await service.TranslateAsync(file, settings, null, CancellationToken.None));

        var textPassed = (await File.ReadAllTextAsync(outputs[0])).Contains("[zh-CN] Hello document", StringComparison.Ordinal);
        var markdown = await File.ReadAllTextAsync(outputs[1]);
        var markdownPassed = markdown.Contains("**[zh-CN] important**", StringComparison.Ordinal)
                             && markdown.Contains("`keep_code`", StringComparison.Ordinal);
        var csvPassed = (await File.ReadAllTextAsync(outputs[2])).Contains("[zh-CN] Widget", StringComparison.Ordinal);
        var html = await File.ReadAllTextAsync(outputs[3]);
        var htmlPassed = html.Contains("[zh-CN] Hello page", StringComparison.Ordinal)
                         && html.Contains("<code>keep_code</code>", StringComparison.Ordinal);
        bool wordPassed;
        using (var document = WordprocessingDocument.Open(outputs[4], false))
        {
            var mainDocument = document.MainDocumentPart?.Document
                               ?? throw new InvalidDataException("Translated DOCX is missing its main document.");
            wordPassed = mainDocument.InnerText.Contains("[zh-CN] Quarterly report", StringComparison.Ordinal);
        }
        bool sheetPassed;
        using (var document = SpreadsheetDocument.Open(outputs[5], false))
        {
            var worksheet = document.WorkbookPart?.WorksheetParts.FirstOrDefault()?.Worksheet
                            ?? throw new InvalidDataException("Translated XLSX is missing its worksheet.");
            sheetPassed = worksheet.InnerText.Contains("[zh-CN] Revenue summary", StringComparison.Ordinal);
        }
        bool presentationPassed;
        using (var document = PresentationDocument.Open(outputs[6], false))
        {
            var slide = document.PresentationPart?.SlideParts.FirstOrDefault()?.Slide
                        ?? throw new InvalidDataException("Translated PPTX is missing its slide.");
            presentationPassed = slide.InnerText.Contains("[zh-CN] Project overview", StringComparison.Ordinal);
        }
        var pdfPassed = (await File.ReadAllTextAsync(outputs[7])).Contains("[zh-CN] Hello PDF document", StringComparison.Ordinal);

        var localPath = Path.Combine(root, "local.txt");
        await File.WriteAllTextAsync(localPath, "Local output");
        settings.DocumentOutputDirectory = string.Empty;
        var localOutput = await service.TranslateAsync(localPath, settings, null, CancellationToken.None);
        var localOutputPassed = Path.GetDirectoryName(localOutput) == root
                                && Path.GetFileName(localOutput) == "local_translation.txt";

        return Directory.Exists(outputDirectory)
               && outputs.All(File.Exists)
               && textPassed && markdownPassed && csvPassed && htmlPassed
               && wordPassed && sheetPassed && presentationPassed && pdfPassed && localOutputPassed;
    }

    private static void RenderUi(string artifacts, string renderMode)
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var sampleImagePath = Path.Combine(artifacts, "clipboard-preview.png");
        using (var bitmap = new Bitmap(240, 150, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Microsoft YaHei", 26, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.FromArgb(223, 244, 237));
            graphics.DrawString("OCR 预览", font, Brushes.DarkSlateGray, 38, 52);
            bitmap.Save(sampleImagePath, ImageFormat.Png);
        }
        var sampleItems = new[]
        {
            CreateSample("1", ClipboardItemKind.Text, "Native clipboard history stays outside the UI process.", "文本示例"),
            CreateSample("2", ClipboardItemKind.Html, "<b>Formatted content</b>", "HTML 与富文本"),
            CreateSample("3", ClipboardItemKind.Files, JsonSerializer.Serialize(new[] { @"C:\Work\report.docx", @"C:\Work\image.png" }), "文件组"),
            CreateImageSample("4", sampleImagePath)
        };

        if (renderMode == "input")
        {
            var inputWindow = new MainWindow();
            app.MainWindow = inputWindow;
            inputWindow.Show();
            ((System.Windows.Controls.TabControl)inputWindow.FindName("MainTabs")).SelectedIndex = 0;
            var inputBox = (System.Windows.Controls.TextBox)(inputWindow.FindName("InputText")
                ?? throw new InvalidOperationException("translation input box missing"));
            var outputBox = (System.Windows.Controls.TextBox)(inputWindow.FindName("InputResultText")
                ?? throw new InvalidOperationException("translation output box missing"));
            // 空内容时点「复制原文」应该给提示，而不是往剪贴板写空串
            var copyHandler = typeof(MainWindow).GetMethod("CopyInput_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("copy source handler missing");
            copyHandler.Invoke(inputWindow, new object?[] { inputWindow.FindName("CopyInputButton"), new System.Windows.RoutedEventArgs() });
            PumpLayout(inputWindow);
            var footer = inputWindow.FindName("FooterStatus") as System.Windows.Controls.TextBlock;
            Require(footer?.Text.Contains("没有可复制", StringComparison.Ordinal) == true,
                $"empty source reports instead of copying ({footer?.Text})");

            inputBox.Text = "Good morning! The quick brown fox jumps over the lazy dog.";
            outputBox.Text = "早上好！敏捷的棕色狐狸跳过了那只懒狗。";
            PumpLayout(inputWindow);
            var copyInput = (System.Windows.Controls.Button)(inputWindow.FindName("CopyInputButton")
                ?? throw new InvalidOperationException("copy source button missing"));
            var copyResult = (System.Windows.Controls.Button)(inputWindow.FindName("CopyResultButton")
                ?? throw new InvalidOperationException("copy result button missing"));
            Require(copyInput.IsVisible && copyInput.ActualWidth > 0 && copyInput.ActualHeight > 0,
                "copy source button is laid out on the translation page");
            Require(copyResult.IsVisible && copyResult.ActualWidth > 0 && copyResult.ActualHeight > 0,
                "copy result button is laid out on the translation page");
            Require(string.Equals(copyInput.Content?.ToString(), "复制原文", StringComparison.Ordinal),
                "copy source button keeps a stable label");
            Require(string.Equals(copyResult.Content?.ToString(), "复制译文", StringComparison.Ordinal),
                "copy result button keeps a stable label");
            RenderWindow(inputWindow, Path.Combine(artifacts, "translation-page.png"));
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(inputWindow, true);
            inputWindow.Close();
            Console.WriteLine("Translation page render passed");
            return;
        }

        if (renderMode is "capture" or "capture-compact")
        {
            var compactOnly = renderMode == "capture-compact";
            var background = CreateCaptureBackground(1366, 768);
            var snapshot = new ScreenSnapshot(background, new Rectangle(0, 0, 1366, 768),
                new[] { new Rectangle(120, 80, 960, 590), new Rectangle(760, 170, 500, 430) });
            var capture = new CaptureOverlayWindow(snapshot, CaptureResultAction.Translate,
                compactOnly
                    ? new System.Windows.Rect(155, 115, 180, 96)
                    : new System.Windows.Rect(155, 115, 870, 500));
            capture.Show();
            PumpLayout(capture);
            typeof(CaptureOverlayWindow).GetMethod("UpdateSelectionVisuals", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(capture, null);
            capture.InvalidateMeasure();
            capture.InvalidateArrange();
            PumpLayout(capture);
            if (compactOnly)
            {
                var moreTools = (System.Windows.Controls.Button)(capture.FindName("MoreToolsButton")
                    ?? throw new InvalidOperationException("compact capture more-tools button missing"));
                var compactToolbar = (System.Windows.FrameworkElement)(capture.FindName("CompactToolbar")
                    ?? throw new InvalidOperationException("compact capture toolbar missing"));
                var compactComplete = (System.Windows.Controls.Button)(capture.FindName("CompactCompleteButton")
                    ?? throw new InvalidOperationException("compact capture complete button missing"));
                var compactBorder = (System.Windows.FrameworkElement)(capture.FindName("SelectionBorder")
                    ?? throw new InvalidOperationException("compact capture selection missing"));
                Require(moreTools.Visibility == System.Windows.Visibility.Visible,
                    "compact capture exposes more tools");
                Require(compactToolbar.Visibility == System.Windows.Visibility.Visible
                        && compactToolbar.ActualWidth is > 200 and <= 280,
                    $"compact capture toolbar width ({compactToolbar.ActualWidth})");
                Require(Math.Abs(compactBorder.ActualWidth - 180) < 1
                        && Math.Abs(compactBorder.ActualHeight - 96) < 1,
                    "compact capture selection dimensions");
                var isToolbarSource = typeof(CaptureOverlayWindow)
                    .GetMethod("IsToolbarSource", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("capture toolbar hit-test method missing");
                Require((bool)(isToolbarSource.Invoke(capture, new object?[] { compactComplete }) ?? false),
                    "compact capture complete button is excluded from selection input");
                RenderWindow(capture, Path.Combine(artifacts, "capture-compact.png"));

                // 展开标注工具：必须留在同一窗口内（曾经用 Popup，点击会漏到遮罩层把选区重置掉）
                var toolsPanel = (System.Windows.FrameworkElement)(capture.FindName("CompactToolsPanel")
                    ?? throw new InvalidOperationException("compact tools panel missing"));
                Require(toolsPanel.Visibility == System.Windows.Visibility.Collapsed,
                    "compact tools panel starts collapsed");
                var moreToolsClick = typeof(CaptureOverlayWindow)
                    .GetMethod("MoreTools_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("more tools handler missing");
                moreToolsClick.Invoke(capture, new object?[] { moreTools, new System.Windows.RoutedEventArgs() });
                PumpLayout(capture);
                Require(toolsPanel.Visibility == System.Windows.Visibility.Visible,
                    "compact tools panel expands in place");
                var rectangleTool = (System.Windows.Controls.Button)(capture.FindName("RectangleToolButton")
                    ?? throw new InvalidOperationException("rectangle tool button missing"));
                Require(IsDescendantOf(rectangleTool, compactToolbar),
                    "annotation tools live inside the capture window, not a popup");
                Require((bool)(isToolbarSource.Invoke(capture, new object?[] { rectangleTool }) ?? false),
                    "annotation tool clicks are excluded from selection input");
                RenderWindow(capture, Path.Combine(artifacts, "capture-compact-tools.png"));

                // 选完工具后工具面板收起，且当前工具被记住
                var toolButtonClick = typeof(CaptureOverlayWindow)
                    .GetMethod("ToolButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("tool button handler missing");
                toolButtonClick.Invoke(capture, new object?[] { rectangleTool, new System.Windows.RoutedEventArgs() });
                PumpLayout(capture);
                Require(toolsPanel.Visibility == System.Windows.Visibility.Collapsed,
                    "picking a tool collapses the expanded panel");
                var activeTool = typeof(CaptureOverlayWindow)
                    .GetField("_tool", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(capture);
                Require(activeTool?.ToString() == "Rectangle",
                    $"picked tool is remembered ({activeTool})");
                RenderWindow(capture, Path.Combine(artifacts, "capture-compact-picked.png"));

                capture.Close();
                Console.WriteLine("Compact capture UI render passed");
                return;
            }
            var annotationHistory = (CaptureHistory<CaptureAnnotation>)(typeof(CaptureOverlayWindow)
                .GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(capture)
                ?? throw new InvalidOperationException("capture annotation history missing"));
            annotationHistory.Add(new BoxAnnotation(CaptureTool.Rectangle, new System.Windows.Rect(165, 165, 585, 54),
                System.Windows.Media.Color.FromRgb(229, 57, 53), 4));
            annotationHistory.Add(new ArrowAnnotation(new System.Windows.Point(780, 185), new System.Windows.Point(930, 250),
                System.Windows.Media.Color.FromRgb(35, 137, 218), 5));
            annotationHistory.Add(new PenAnnotation(new List<System.Windows.Point>
                {
                    new(180, 565), new(225, 545), new(270, 570), new(320, 540), new(370, 558)
                }, System.Windows.Media.Color.FromRgb(24, 165, 114), 5));
            annotationHistory.Add(new TextAnnotation(new System.Windows.Point(650, 495), "标注文字", 
                System.Windows.Media.Color.FromRgb(229, 57, 53), 24));
            annotationHistory.Add(new MosaicAnnotation(new System.Windows.Rect(750, 330, 180, 70), 14));
            typeof(CaptureOverlayWindow).GetMethod("RebuildAnnotations", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(capture, null);
            PumpLayout(capture);
            var canvas = (System.Windows.FrameworkElement)(typeof(CaptureOverlayWindow).GetField("SelectionCanvas",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(capture)
                ?? throw new InvalidOperationException("capture canvas missing"));
            var border = (System.Windows.FrameworkElement)(typeof(CaptureOverlayWindow).GetField("SelectionBorder",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(capture)
                ?? throw new InvalidOperationException("capture border missing"));
            var toolbar = (System.Windows.FrameworkElement)(typeof(CaptureOverlayWindow).GetField("Toolbar",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(capture)
                ?? throw new InvalidOperationException("capture toolbar missing"));
            Require(border.Visibility == System.Windows.Visibility.Visible, "capture selection visible");
            Require(toolbar.Visibility == System.Windows.Visibility.Visible, "capture toolbar visible");
            var selectionHandles = new[] { "HandleNW", "HandleN", "HandleNE", "HandleE", "HandleSE", "HandleS", "HandleSW", "HandleW" }
                .Select(name => (System.Windows.Controls.Primitives.Thumb)(capture.FindName(name)
                    ?? throw new InvalidOperationException($"capture resize handle missing: {name}")))
                .ToArray();
            Require(selectionHandles.All(handle => handle.Visibility == System.Windows.Visibility.Visible
                                                      && handle.ActualWidth >= 8
                                                      && handle.ActualHeight >= 8),
                "capture has eight visible resize handles");
            Require(Math.Abs(border.Width - 870) < 1 && Math.Abs(border.Height - 500) < 1, "capture selection dimensions");
            Require(border.ActualWidth > 800 && toolbar.ActualWidth > 500,
                $"capture arranged ({canvas.ActualWidth}x{canvas.ActualHeight}; {border.ActualWidth}, {toolbar.ActualWidth})");
            var shadeTop = (System.Windows.FrameworkElement)(typeof(CaptureOverlayWindow).GetField("ShadeTop",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(capture)
                ?? throw new InvalidOperationException("capture shade missing"));
            Require(Math.Abs(shadeTop.Height - 115) < 1, $"capture mask cutout ({shadeTop.Height})");
            RenderWindow(capture, Path.Combine(artifacts, "capture-workbench.png"));
            var composed = (BitmapSource)(typeof(CaptureOverlayWindow)
                .GetMethod("ComposeResult", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(capture, null)
                ?? throw new InvalidOperationException("capture composition missing"));
            Require(composed.PixelWidth == 870 && composed.PixelHeight == 500, "capture composition dimensions");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(composed));
            using (var stream = File.Create(Path.Combine(artifacts, "capture-composed.png"))) encoder.Save(stream);
            capture.Close();
            Console.WriteLine("Capture UI render passed");
            return;
        }

        if (renderMode is "translation" or "translation-compact")
        {
            var compactOnly = renderMode == "translation-compact";
            var background = CreateTranslationBackground(920, 280);
            var lines = new[]
            {
                new OcrTextLine("ChatGPT Plus prepaid subscription.", 46, 38, 520, 31),
                new OcrTextLine("This card can only be used in the United States.", 46, 105, 710, 25),
                new OcrTextLine("A region mismatch is the most common activation failure.", 46, 151, 780, 25)
            };
            var layout = new OcrLayoutResult(lines, string.Join(Environment.NewLine, lines.Select(line => line.Text)), "en");
            var translation = new[]
            {
                "ChatGPT Plus 预付订阅卡。",
                "此卡仅可在美国地区使用。",
                "地区不匹配是激活失败最常见的原因。"
            };
            var window = new ScreenshotTranslationWindow(
                background,
                compactOnly ? new Rectangle(180, 120, 240, 120) : new Rectangle(180, 120, 920, 280),
                layout,
                translation);
            window.Show();
            PumpLayout(window);
            var overlay = (System.Windows.Controls.Canvas)(window.FindName("TextOverlay")
                ?? throw new InvalidOperationException("translation overlay missing"));
            var captureSurface = (System.Windows.Controls.Grid)(window.FindName("CaptureSurface")
                ?? throw new InvalidOperationException("translation capture surface missing"));
            var moveHitSurface = (System.Windows.Controls.Border)(window.FindName("MoveHitSurface")
                ?? throw new InvalidOperationException("translation move hit surface missing"));
            var toolbar = (System.Windows.FrameworkElement)(window.FindName("TranslationToolbar")
                ?? throw new InvalidOperationException("translation toolbar missing"));
            var sourceImage = (System.Windows.Controls.Image)(window.FindName("SourceImage")
                ?? throw new InvalidOperationException("translation source image missing"));
            var sourceCombo = (System.Windows.Controls.ComboBox)(window.FindName("SourceLanguageCombo")
                ?? throw new InvalidOperationException("translation source language missing"));
            var targetCombo = (System.Windows.Controls.ComboBox)(window.FindName("TargetLanguageCombo")
                ?? throw new InvalidOperationException("translation target language missing"));
            Require(overlay.Children.Count == lines.Length, "translation line overlays");
            Require(compactOnly ? toolbar.ActualHeight >= 56 : toolbar.ActualWidth >= 700,
                $"translation toolbar adapts to selection ({toolbar.ActualWidth}x{toolbar.ActualHeight})");

            // 关闭按钮必须始终可见：以前工具条折行后被固定高度裁掉，用户只能按 Esc
            var closeButton = (System.Windows.Controls.Button)(window.FindName("CloseButton")
                ?? throw new InvalidOperationException("translation close button missing"));
            Require(closeButton.IsVisible && closeButton.ActualWidth >= 24 && closeButton.ActualHeight >= 24,
                $"translation close button is visible ({closeButton.ActualWidth}x{closeButton.ActualHeight})");
            Require(closeButton.TranslatePoint(new System.Windows.Point(0, 0), window) is { X: > 0, Y: > 0 },
                "translation close button sits inside the window");

            // 工具条内容不能被裁：实测高度要不小于排版高度
            var toolbarLayout = (System.Windows.FrameworkElement)(window.FindName("TranslationToolbarLayout")
                ?? throw new InvalidOperationException("translation toolbar layout missing"));
            Require(toolbarLayout.ActualHeight <= toolbar.ActualHeight - 6,
                $"translation toolbar content is not clipped ({toolbarLayout.ActualHeight} in {toolbar.ActualHeight})");
            Require(sourceCombo.IsHitTestVisible && sourceCombo.Focusable, "translation source language is interactive");
            Require((targetCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "zh-CN",
                "translation target defaults to Simplified Chinese");
            Require(sourceImage.Visibility == System.Windows.Visibility.Collapsed,
                "translation mode does not display the captured bitmap");
            Require(moveHitSurface.IsHitTestVisible
                    && moveHitSurface.ActualWidth >= captureSurface.ActualWidth - 1
                    && moveHitSurface.ActualHeight >= captureSurface.ActualHeight - 1
                    && moveHitSurface.Background.Opacity > 0,
                "translation empty area has a full-size drag surface");
            var resizeHandles = new[] { "ResizeNW", "ResizeN", "ResizeNE", "ResizeE", "ResizeSE", "ResizeS", "ResizeSW", "ResizeW" }
                .Select(name => (System.Windows.Controls.Primitives.Thumb)(window.FindName(name)
                    ?? throw new InvalidOperationException($"translation resize handle missing: {name}")))
                .ToArray();
            Require(resizeHandles.All(handle => handle.Visibility == System.Windows.Visibility.Visible
                                                   && handle.ActualWidth >= 10
                                                   && handle.ActualHeight >= 10),
                "translation has eight visible resize handles");
            var draggingField = typeof(ScreenshotTranslationWindow)
                .GetField("_dragging", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("translation dragging state missing");
            var applyDisplay = typeof(ScreenshotTranslationWindow)
                .GetMethod("ApplyDisplayVisuals", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("translation display state missing");
            draggingField.SetValue(window, true);
            applyDisplay.Invoke(window, null);
            Require(overlay.Visibility == System.Windows.Visibility.Collapsed
                    && sourceImage.Visibility == System.Windows.Visibility.Collapsed,
                "translation content hides while dragging");
            draggingField.SetValue(window, false);
            applyDisplay.Invoke(window, null);
            var translatedSettings = (AppSettings)(typeof(ScreenshotTranslationWindow)
                .GetMethod("CreateTranslationSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(window, new object[] { "ja", "zh-CN" })
                ?? throw new InvalidOperationException("translation settings mapping missing"));
            Require(translatedSettings.SourceLanguage == "ja" && translatedSettings.TargetLanguage == "zh-CN",
                "translation language settings are forwarded");
            Require(ScreenshotTranslationWindow.FormatSourceStatus("en", "zh") == "en"
                    && ScreenshotTranslationWindow.FormatSourceStatus("auto", "zh") == "自动(zh)",
                "translation status reflects selected source language");
            ((System.Windows.Controls.Button)window.FindName("OriginalModeButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(overlay.Children.Count == 0, "original mode hides overlays");
            Require(sourceImage.Visibility == System.Windows.Visibility.Visible, "original mode displays source image");
            ((System.Windows.Controls.Button)window.FindName("CompareModeButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(overlay.Children.Count == lines.Length, "compare mode overlays");
            ((System.Windows.Controls.Button)window.FindName("TranslationModeButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpLayout(window);
            Require(overlay.Visibility == System.Windows.Visibility.Visible, "translation overlay is visible");
            Require(overlay.Children.OfType<System.Windows.FrameworkElement>().All(item => item.ActualWidth > 0),
                $"translation overlay lines are arranged ({overlay.ActualWidth}x{overlay.ActualHeight}; "
                + string.Join(",", overlay.Children.OfType<System.Windows.FrameworkElement>()
                    .Select(item => $"{item.Width}/{item.ActualWidth}x{item.ActualHeight}:{item.Visibility}")) + ")");
            RenderWindow(window, Path.Combine(artifacts,
                compactOnly ? "screenshot-translation-compact.png" : "screenshot-translation.png"));
            window.Close();
            Console.WriteLine("Translation UI render passed");
            return;
        }

        if (renderMode == "quick")
        {
            var quickData = Path.Combine(artifacts, "quick-data-" + Guid.NewGuid().ToString("N"));
            bool? persistedFavorite = null;
            using var repository = new ClipboardRepository(quickData);
            foreach (var item in sampleItems)
            {
                repository.Upsert(new ClipboardCapture(item.Kind, item.Value, item.SearchText, item.Subtype,
                    item.Size, item.Width, item.Height, item.ContentHash), 100, 0);
            }
            var quick = new ClipboardWindow(
                repository,
                (_, _, _, _) => Task.CompletedTask,
                _ => Task.CompletedTask,
                () => { },
                (item, isFavorite) =>
                {
                    persistedFavorite = isFavorite;
                    item.IsFavorite = isFavorite;
                    return Task.CompletedTask;
                },
                (item, note, _) =>
                {
                    item.Note = note;
                    return Task.CompletedTask;
                });
            var modalOpenField = typeof(ClipboardWindow).GetField(
                "_modalOpen",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("quick clipboard modal state field missing");
            modalOpenField.SetValue(quick, true);
            Require(modalOpenField.GetValue(quick) is true, "quick clipboard test keeps the window active");
            quick.ShowForTarget(IntPtr.Zero, true);
            var quickItems = (System.Windows.Controls.ListBox)(quick.FindName("ItemsList")
                ?? throw new InvalidOperationException("quick clipboard item list missing"));
            PumpUntil(quick, () => quickItems.Items.Count == sampleItems.Length, TimeSpan.FromSeconds(5));
            PumpLayout(quick);
            var quickSearch = (System.Windows.Controls.TextBox)(quick.FindName("SearchText")
                ?? throw new InvalidOperationException("quick clipboard search box missing"));
            var quickSearchRow = (System.Windows.FrameworkElement)(quick.FindName("SearchBarRow")
                ?? throw new InvalidOperationException("quick clipboard search row missing"));
            var quickSearchContent = quickSearch.Template.FindName("PART_ContentHost", quickSearch) as System.Windows.FrameworkElement;
            Require(quickSearchRow.ActualHeight >= 68
                    && quickSearch.ActualHeight >= 42
                    && quickSearchContent is { ActualHeight: > 20 }
                    && quickSearch.Foreground.Opacity > 0.9,
                "quick clipboard search text has stable visible layout");
            var favoriteButtons = FindVisualChildren<System.Windows.Controls.Button>(quick)
                .Where(button => button.DataContext is ClipboardItem
                                 && button.Content?.ToString() is "★" or "☆")
                .ToList();
            var noteButtons = FindVisualChildren<System.Windows.Controls.Button>(quick)
                .Where(button => button.DataContext is ClipboardItem
                                 && System.Windows.Automation.AutomationProperties.GetName(button) is "添加备注" or "编辑备注")
                .ToList();
            Require(favoriteButtons.Count > 0
                    && favoriteButtons.All(button => button.ActualWidth >= 40 && button.ActualHeight >= 40),
                "quick clipboard exposes large inline favorite buttons ("
                + string.Join(",", favoriteButtons.Select(button => $"{button.ActualWidth}x{button.ActualHeight}"))
                + $"; visible={quick.IsVisible}; list={quickItems.ActualWidth}x{quickItems.ActualHeight}; "
                + $"modal={modalOpenField.GetValue(quick)}; "
                + $"containers={Enumerable.Range(0, quickItems.Items.Count).Count(index => quickItems.ItemContainerGenerator.ContainerFromIndex(index) is not null)})");
            Require(noteButtons.Count > 0
                    && noteButtons.All(button => button.IsEnabled && button.ActualHeight >= 30),
                "quick clipboard exposes note dialog buttons");
            var firstFavorite = favoriteButtons[0];
            var firstFavoriteItem = (ClipboardItem)firstFavorite.DataContext;
            var initialFavorite = firstFavoriteItem.IsFavorite;
            firstFavorite.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpLayout(quick);
            Require(firstFavoriteItem.IsFavorite != initialFavorite
                    && persistedFavorite == firstFavoriteItem.IsFavorite,
                "quick clipboard favorite updates optimistically");
            RenderWindow(quick, Path.Combine(artifacts, "clipboard-quick.png"));
            quickSearch.Text = "Visible search text 搜索";
            quickSearch.Select(quickSearch.Text.Length, 0);
            PumpLayout(quick);
            Require(quickSearch.Text == "Visible search text 搜索" && quickSearch.SelectionLength == 0,
                "quick clipboard search retains typed text");
            quickSearch.Text = "cut target";
            quickSearch.Select(0, 3);
            string? cutText = null;
            var cutSucceeded = ClipboardSearchCutService.CutAsync(quickSearch, text =>
            {
                cutText = text;
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            Require(cutSucceeded && cutText == "cut" && quickSearch.Text == " target",
                "quick clipboard search cuts selection before clipboard callback");
            quickSearch.Text = "restore target";
            quickSearch.Select(0, 7);
            var failedCut = ClipboardSearchCutService.CutAsync(quickSearch,
                _ => Task.FromException(new InvalidOperationException("test clipboard busy")))
                .GetAwaiter().GetResult();
            Require(!failedCut && quickSearch.Text == "restore target" && quickSearch.SelectionLength == 7,
                "quick clipboard search restores selection after write failure");

            quickSearch.Clear();
            PumpUntil(quick, () => quickItems.Items.Count == sampleItems.Length, TimeSpan.FromSeconds(5));
            var itemsSourceBeforeHiddenMutation = quickItems.ItemsSource;
            quick.Hide();
            var hiddenMutationText = "clipboard hidden refresh " + Guid.NewGuid().ToString("N");
            repository.Upsert(new ClipboardCapture(
                ClipboardItemKind.Text,
                hiddenMutationText,
                hiddenMutationText,
                string.Empty,
                hiddenMutationText.Length,
                0,
                0,
                ClipboardRepository.HashContent(ClipboardItemKind.Text, hiddenMutationText)), 100, 0);
            quick.MarkDirty();
            Thread.Sleep(220);
            PumpPendingDispatcherOperations(quick);
            Require(ReferenceEquals(quickItems.ItemsSource, itemsSourceBeforeHiddenMutation)
                    && quickItems.Items.Count == sampleItems.Length,
                "hidden quick clipboard defers a dirty refresh");

            quick.ShowForTarget(IntPtr.Zero, true);
            PumpUntil(quick, () => quickItems.Items.Count == sampleItems.Length + 1, TimeSpan.FromSeconds(5));
            Require(quickItems.Items.Cast<ClipboardItem>().Any(item => item.Value == hiddenMutationText),
                "quick clipboard refreshes after it is shown again");
            var cleanItemsSource = quickItems.ItemsSource;
            for (var index = 0; index < 8; index++) quick.ShowForTarget(IntPtr.Zero, true);
            PumpLayout(quick);
            Require(ReferenceEquals(quickItems.ItemsSource, cleanItemsSource)
                    && quickItems.Items.Count == sampleItems.Length + 1,
                "repeated clean quick clipboard activation reuses the loaded snapshot");
            quick.Close();
            Console.WriteLine("Quick UI render passed");
            return;
        }

        if (renderMode == "tasks")
        {
            var tasks = new[]
            {
                new DocumentTranslationTask(@"C:\Docs\product-guide.docx")
                    { Status = DocumentTaskStatus.Processing, Progress = 64, StatusText = "正在翻译 18/28" },
                new DocumentTranslationTask(@"C:\Docs\pricing.xlsx")
                    { Status = DocumentTaskStatus.Queued, Progress = 0, StatusText = "等待处理" },
                new DocumentTranslationTask(@"C:\Docs\manual.pdf")
                    { Status = DocumentTaskStatus.Completed, Progress = 100, StatusText = "manual_translation.txt" }
            };
            var taskWindow = new MainWindow();
            app.MainWindow = taskWindow;
            taskWindow.Show();
            ((System.Windows.Controls.TabControl)taskWindow.FindName("MainTabs")).SelectedIndex = 2;
            ((System.Windows.Controls.DataGrid)taskWindow.FindName("DocumentTaskGrid")).ItemsSource = tasks;
            ((System.Windows.Controls.TextBlock)taskWindow.FindName("DocumentTaskCountText")).Text = "2 个进行中 · 3 个任务";
            PumpLayout(taskWindow);
            RenderWindow(taskWindow, Path.Combine(artifacts, "document-tasks.png"));
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(taskWindow, true);
            taskWindow.Close();
            Console.WriteLine("Document task UI render passed");
            return;
        }

        var main = new MainWindow();
        app.MainWindow = main;
        main.Show();
        if (main.FindName("MainTabs") is System.Windows.Controls.TabControl tabs)
            tabs.SelectedIndex = renderMode == "settings" ? 5 : 3;
        if (renderMode == "main")
        {
            if (main.FindName("ClipboardGrid") is System.Windows.Controls.DataGrid grid) grid.ItemsSource = sampleItems;
            if (main.FindName("ClipboardCountText") is System.Windows.Controls.TextBlock count) count.Text = "4 条";
        }
        PumpLayout(main);
        if (renderMode == "main")
        {
            var mainSearch = (System.Windows.Controls.TextBox)(main.FindName("ClipboardSearchText")
                ?? throw new InvalidOperationException("main clipboard search box missing"));
            var mainSearchContent = mainSearch.Template.FindName("PART_ContentHost", mainSearch) as System.Windows.FrameworkElement;
            Require(mainSearch.ActualHeight >= 42 && mainSearchContent is { ActualHeight: > 20 },
                "main clipboard search text has stable visible layout");
            var historySearch = (System.Windows.Controls.TextBox)(main.FindName("HistorySearchText")
                ?? throw new InvalidOperationException("translation history search box missing"));
            historySearch.Text = "history cut target";
            historySearch.Select(8, 3);
            string? historyCutText = null;
            var historyCutSucceeded = ClipboardSearchCutService.CutAsync(historySearch, text =>
            {
                historyCutText = text;
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            Require(historyCutSucceeded && historyCutText == "cut" && historySearch.Text == "history  target",
                "translation history search cuts selection before clipboard callback");
            var inlineFavorites = FindVisualChildren<System.Windows.Controls.Button>(main)
                .Where(button => button.DataContext is ClipboardItem
                                 && button.Content?.ToString() is "★" or "☆")
                .ToList();
            var noteButtons = FindVisualChildren<System.Windows.Controls.Button>(main)
                .Where(button => button.DataContext is ClipboardItem
                                 && System.Windows.Automation.AutomationProperties.GetName(button) is "添加备注" or "编辑备注")
                .ToList();
            Require(inlineFavorites.Count >= sampleItems.Length
                    && inlineFavorites.All(button => button.ActualWidth >= 40 && button.ActualHeight >= 40),
                "main clipboard exposes large inline favorite buttons");
            Require(noteButtons.Count >= sampleItems.Length
                    && noteButtons.All(button => button.IsEnabled && button.ActualHeight >= 30),
                "main clipboard exposes note dialog buttons");
        }
        RenderWindow(main, Path.Combine(artifacts, renderMode == "settings" ? "settings-top.png" : "clipboard-main.png"));
        var settingsScroll = renderMode == "settings"
            ? main.FindName("SettingsScrollViewer") as System.Windows.Controls.ScrollViewer
            : null;
        if (settingsScroll is not null)
        {
            Require(settingsScroll.ScrollableHeight > 0, "settings page is vertically scrollable");
            var concurrencyText = main.FindName("DocumentTranslationConcurrencyText") as System.Windows.Controls.TextBox;
            Require(concurrencyText is not null, "document translation concurrency setting exists");
            Require(int.TryParse(concurrencyText!.Text, out var concurrency) && concurrency > 0,
                "document translation concurrency is a positive integer");
            var silentOcrEditor = main.FindName("SilentOcrShowEditorCheck") as System.Windows.Controls.CheckBox;
            Require(silentOcrEditor is not null && IsDescendantOf(silentOcrEditor, settingsScroll),
                "silent OCR editor setting exists in the settings scroll area");
            var pinnedImageHotkey = main.FindName("PinnedImageHotkeyText") as System.Windows.Controls.TextBox;
            Require(pinnedImageHotkey is not null && IsDescendantOf(pinnedImageHotkey, settingsScroll),
                "pinned image hotkey setting exists in the settings scroll area");
            var pinnedImageEdge = main.FindName("PinnedImageEdgeEffectCombo") as System.Windows.Controls.ComboBox;
            Require(pinnedImageEdge is not null && IsDescendantOf(pinnedImageEdge, settingsScroll),
                "pinned image edge setting exists in the settings scroll area");
            var browse = main.FindName("DocumentOutputBrowseButton") as System.Windows.Controls.Button;
            Require(string.Equals(browse?.Content?.ToString(), "浏览", StringComparison.Ordinal),
                "document output browse button uses stable text");
            foreach (var controlName in new[]
                     {
                         "NotifyScreenshotActionsCheck",
                         "NotifyOcrResultsCheck",
                         "NotifyErrorsCheck"
                     })
            {
                var checkBox = main.FindName(controlName) as System.Windows.Controls.CheckBox;
                Require(checkBox is not null, $"{controlName} exists");
                Require(checkBox!.ActualWidth > 0 && checkBox.ActualHeight > 0,
                    $"{controlName} is laid out");
                Require(IsDescendantOf(checkBox, settingsScroll),
                    $"{controlName} belongs to the settings scroll area");
            }
            var keepOnTop = (System.Windows.Controls.CheckBox)(main.FindName("KeepOnTopCheck")
                ?? throw new InvalidOperationException("keep-on-top checkbox missing"));
            Require(IsDescendantOf(keepOnTop, settingsScroll), "keep-on-top setting belongs to the settings scroll area");
            var originalTopmost = keepOnTop.IsChecked == true;
            keepOnTop.IsChecked = true;
            PumpLayout(main);
            Require(main.Topmost, "keep-on-top setting makes the main window topmost");
            keepOnTop.IsChecked = false;
            PumpLayout(main);
            Require(!main.Topmost, "clearing keep-on-top restores the normal window");
            keepOnTop.IsChecked = originalTopmost;
            PumpLayout(main);

            var providerCombo = (System.Windows.Controls.ComboBox)(main.FindName("ProviderCombo")
                ?? throw new InvalidOperationException("translation provider combo missing"));
            var chatApiPanel = (System.Windows.FrameworkElement)(main.FindName("ChatApiPanel")
                ?? throw new InvalidOperationException("chat api settings panel missing"));
            var promptBox = (System.Windows.Controls.TextBox)(main.FindName("TranslationPromptText")
                ?? throw new InvalidOperationException("translation prompt box missing"));
            Require(IsDescendantOf(promptBox, settingsScroll), "translation prompt belongs to the settings scroll area");
            Require(promptBox.Text.Length > 0, "translation prompt shows the effective template");

            var originalProvider = providerCombo.SelectedItem;
            SelectProviderByTag(providerCombo, "ChatApi");
            PumpLayout(main);
            Require(chatApiPanel.Visibility == System.Windows.Visibility.Visible,
                "chat api settings show for the custom api provider");
            Require(providerCombo.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                    .All(item => !string.Equals(item.Tag?.ToString(), "Ollama", StringComparison.Ordinal)),
                "ollama is merged into the custom api provider");
            var profileList = main.FindName("ChatProfileList") as System.Windows.Controls.ListBox;
            Require(profileList is not null, "chat api profile list exists");
            Require(IsDescendantOf(profileList!, chatApiPanel), "chat api profile list belongs to the chat api panel");
            var autoChainPanel = (System.Windows.FrameworkElement)(main.FindName("AutoChainPanel")
                ?? throw new InvalidOperationException("auto chain panel missing"));
            var autoChainList = (System.Windows.Controls.ListBox)(main.FindName("AutoChainList")
                ?? throw new InvalidOperationException("auto chain list missing"));
            SelectProviderByTag(providerCombo, "Auto");
            PumpLayout(main);
            Require(autoChainPanel.Visibility == System.Windows.Visibility.Visible
                    && chatApiPanel.Visibility == System.Windows.Visibility.Collapsed,
                "auto provider shows only the auto chain panel");
            Require(autoChainList.Items.Count > 0, "auto chain starts with at least one channel");
            var firstChainItem = autoChainList.Items[0] as System.Windows.Controls.ListBoxItem;
            Require(firstChainItem?.Content?.ToString() is { } firstLabel
                    && firstLabel.StartsWith("1. ", StringComparison.Ordinal),
                "auto chain entries are numbered");

            providerCombo.SelectedItem = originalProvider;
            PumpLayout(main);

            // ScrollViewer 在这个无交互的渲染环境里不响应 ScrollToEnd，
            // 直接渲染滚动区内容，得到完整的一页设置截图。
            if (settingsScroll.Content is System.Windows.FrameworkElement settingsContent)
                RenderElement(settingsContent, Path.Combine(artifacts, "settings-full.png"));

            var testSettings = new AppSettings
            {
                Provider = TranslationProvider.ChatApi,
                TargetLanguage = "zh-CN"
            };
            testSettings.ChatApiProfiles.Add(new ChatApiProfile
            {
                Name = "演示渠道",
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5-mini",
                Format = ChatApiFormat.Responses
            });
            testSettings.ActiveChatApiProfileId = testSettings.ChatApiProfiles[0].Id;
            var testWindow = new TranslationTestWindow(new TranslationService(), testSettings)
            {
                Owner = main
            };
            testWindow.Show();
            PumpLayout(testWindow);
            RenderWindow(testWindow, Path.Combine(artifacts, "test-translation.png"));
            testWindow.Close();
            Console.WriteLine("Test translation window render passed");

        }
        typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(main, true);
        main.Close();
        Console.WriteLine("Main UI render passed");
    }

    private static void TestPinnedImageRegression(string artifacts)
    {
        Exception? regressionError = null;
        var thread = new Thread(() =>
        {
            try
            {
                TestPinnedImageRegressionOnSta(artifacts);
            }
            catch (Exception ex)
            {
                regressionError = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (regressionError is not null)
            throw new InvalidOperationException("Pinned image regression failed", regressionError);
    }

    private static void TestPinnedImageRegressionOnSta(string artifacts)
    {
        TestPinnedImageSettingsPersistence();

        var app = new System.Windows.Application();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var image = CreatePinnedImageTestBitmap();
        var gradientWindow = new PinnedImageWindow(image, PinnedImageEdgeEffect.Gradient);
        var plainWindow = new PinnedImageWindow(image, PinnedImageEdgeEffect.None);

        try
        {
            gradientWindow.Left -= 80;
            plainWindow.Left += 80;
            gradientWindow.Show();
            plainWindow.Show();
            PumpLayout(gradientWindow);
            PumpLayout(plainWindow);

            Require(gradientWindow.IsVisible && plainWindow.IsVisible,
                $"two pinned image windows coexist (gradient visible={gradientWindow.IsVisible}, "
                + $"loaded={gradientWindow.IsLoaded}; plain visible={plainWindow.IsVisible}, "
                + $"loaded={plainWindow.IsLoaded}; windows={app.Windows.Count})");
            Require(app.Windows.OfType<PinnedImageWindow>().Count() == 2,
                "application tracks both pinned image windows");

            var gradientFrame = (System.Windows.Controls.Border)(gradientWindow.FindName("PinFrame")
                ?? throw new InvalidOperationException("gradient pinned image frame missing"));
            var plainFrame = (System.Windows.Controls.Border)(plainWindow.FindName("PinFrame")
                ?? throw new InvalidOperationException("plain pinned image frame missing"));
            Require(gradientFrame.BorderBrush is System.Windows.Media.LinearGradientBrush
                    && gradientFrame.Effect is System.Windows.Media.Effects.DropShadowEffect
                    && gradientFrame.BorderThickness.Left > 0
                    && gradientFrame.Margin.Left > 0,
                "gradient pinned image edge is enabled");
            Require(plainFrame.Effect is null
                    && plainFrame.BorderThickness == new System.Windows.Thickness(0)
                    && plainFrame.Margin == new System.Windows.Thickness(0)
                    && plainFrame.BorderBrush is System.Windows.Media.SolidColorBrush { Color.A: 0 },
                "plain pinned image edge is disabled");

            RenderWindow(gradientWindow, Path.Combine(artifacts, "pinned-image-gradient.png"));
            RenderWindow(plainWindow, Path.Combine(artifacts, "pinned-image-none.png"));

            plainWindow.Activate();
            plainWindow.Focus();
            var presentationSource = System.Windows.PresentationSource.FromVisual(plainWindow)
                ?? throw new InvalidOperationException("pinned image presentation source missing");
            var escape = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                presentationSource,
                Environment.TickCount,
                System.Windows.Input.Key.Escape)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            };
            plainWindow.RaiseEvent(escape);
            gradientWindow.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

            Require(escape.Handled, "pinned image Escape is handled");
            Require(!plainWindow.IsVisible && gradientWindow.IsVisible,
                "Escape closes only the current pinned image window");
            Require(app.Windows.OfType<PinnedImageWindow>().Count() == 1,
                "one pinned image window remains after Escape");
        }
        finally
        {
            if (plainWindow.IsVisible) plainWindow.Close();
            if (gradientWindow.IsVisible) gradientWindow.Close();
            app.Shutdown();
        }
    }

    private static void TestPinnedImageSettingsPersistence()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "SnapTranslate.PinnedImageSmoke",
            Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var edgeEffect in Enum.GetValues<PinnedImageEdgeEffect>())
            {
                var expectedHotkey = edgeEffect == PinnedImageEdgeEffect.Gradient
                    ? "Ctrl+Alt+P"
                    : "Ctrl+Shift+P";
                new SettingsService(directory).SaveSettings(new AppSettings
                {
                    PinnedImageHotkey = expectedHotkey,
                    PinnedImageEdgeEffect = edgeEffect
                });

                var reopened = new SettingsService(directory).LoadSettings();
                Require(reopened.PinnedImageHotkey == expectedHotkey
                        && reopened.PinnedImageEdgeEffect == edgeEffect,
                    $"pinned image settings persist after reopen ({edgeEffect})");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static BitmapSource CreatePinnedImageTestBitmap()
    {
        const int width = 32;
        const int height = 20;
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * bytesPerPixel;
                pixels[offset] = (byte)(70 + x * 3);
                pixels[offset + 1] = (byte)(120 + y * 5);
                pixels[offset + 2] = (byte)(30 + x * 4);
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        var image = BitmapSource.Create(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        image.Freeze();
        return image;
    }

    private static bool IsDescendantOf(System.Windows.DependencyObject child, System.Windows.DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }

        return false;
    }

    private static void SelectProviderByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) continue;
            combo.SelectedItem = item;
            return;
        }

        throw new InvalidOperationException($"provider option {tag} missing");
    }

    private static BitmapSource CreateCaptureBackground(int width, int height)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 241, 244)), null,
                new System.Windows.Rect(0, 0, width, height));
            drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 34, 40)), null,
                new System.Windows.Rect(0, 0, width, 62));
            drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)), null,
                new System.Windows.Rect(120, 80, 960, 590));
            drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 125, 100)), null,
                new System.Windows.Rect(120, 80, 960, 46));
            var typeface = new System.Windows.Media.Typeface("Microsoft YaHei UI");
            drawing.DrawText(new System.Windows.Media.FormattedText("SnapTranslate 截图工作台渲染测试", System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight, typeface, 26, System.Windows.Media.Brushes.White, 1), new System.Windows.Point(150, 86));
            drawing.DrawText(new System.Windows.Media.FormattedText("窗口智能选择、多屏捕获、标注与 OCR/翻译操作", System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight, typeface, 25, System.Windows.Media.Brushes.Black, 1), new System.Windows.Point(165, 170));
            for (var index = 0; index < 7; index++)
                drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 225, 230)), null,
                    new System.Windows.Rect(165, 235 + index * 48, 710 - index * 28, 15));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateTranslationBackground(int width, int height)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, width, height));
            drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 248, 250)), null,
                new System.Windows.Rect(24, 20, width - 48, height - 40));
            var title = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI"),
                System.Windows.FontStyles.Normal, System.Windows.FontWeights.SemiBold, System.Windows.FontStretches.Normal);
            var body = new System.Windows.Media.Typeface("Segoe UI");
            drawing.DrawText(new System.Windows.Media.FormattedText("ChatGPT Plus prepaid subscription.",
                System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                title, 28, System.Windows.Media.Brushes.Black, 1), new System.Windows.Point(46, 35));
            drawing.DrawText(new System.Windows.Media.FormattedText("This card can only be used in the United States.",
                System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                body, 22, System.Windows.Media.Brushes.Black, 1), new System.Windows.Point(46, 103));
            drawing.DrawText(new System.Windows.Media.FormattedText("A region mismatch is the most common activation failure.",
                System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                body, 22, System.Windows.Media.Brushes.Black, 1), new System.Windows.Point(46, 149));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void CreateSimplePdf(string path, string text)
    {
        var streamText = $"BT /F1 18 Tf 72 720 Td ({text.Replace("(", "\\(").Replace(")", "\\)")}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(streamText)} >>\nstream\n{streamText}\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF");
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
    }

    private static void RunLiveTranslation(string artifacts)
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var image = CreateTranslationBackground(920, 280);
        var settings = new AppSettings
        {
            Provider = TranslationProvider.Youdao,
            TargetLanguage = "zh-CN"
        };
        var window = new ScreenshotTranslationWindow(
            image,
            new Rectangle(180, 120, image.PixelWidth, image.PixelHeight),
            new OcrService(),
            new TranslationService(),
            settings);
        var frame = new DispatcherFrame();
        Exception? error = null;
        var completed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            error = new TimeoutException("Screenshot translation did not complete within 45 seconds.");
            frame.Continue = false;
        };
        window.TranslationCompleted += (source, translation, provider) =>
        {
            try
            {
                PumpLayout(window);
                RenderWindow(window, Path.Combine(artifacts, "live-screenshot-translation.png"));
                Require(!string.IsNullOrWhiteSpace(source), "live source text");
                Require(!string.IsNullOrWhiteSpace(translation), "live translated text");
                File.WriteAllText(Path.Combine(artifacts, "live-screenshot-translation.json"),
                    JsonSerializer.Serialize(new { source, translation, provider }, new JsonSerializerOptions { WriteIndented = true }));
                completed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        window.Show();
        timer.Start();
        Dispatcher.PushFrame(frame);
        window.Close();
        app.Shutdown();
        if (error is not null) throw error;
        Require(completed, "live translation completion");
        Console.WriteLine("Live screenshot translation passed");
    }

    private static async Task RunLiveDocumentTranslationAsync(string artifacts)
    {
        var testRoot = Path.Combine(artifacts, "live-document");
        Directory.CreateDirectory(testRoot);
        var sourcePath = Path.Combine(testRoot, "release-notes.txt");
        await File.WriteAllTextAsync(sourcePath,
            "The document translation task preserves the source file and writes a translated copy.",
            new UTF8Encoding(false));

        var settings = new AppSettings
        {
            Provider = TranslationProvider.Youdao,
            DocumentSourceLanguage = "auto",
            DocumentTargetLanguage = "zh-CN",
            DocumentOutputDirectory = string.Empty,
            DocumentOutputSuffix = "_translation"
        };
        var service = new DocumentTranslationService(new TranslationService());
        var stages = new List<DocumentTranslationProgress>();
        var outputPath = await service.TranslateAsync(sourcePath, settings,
            new SynchronousProgress<DocumentTranslationProgress>(stages.Add), CancellationToken.None);
        var translated = await File.ReadAllTextAsync(outputPath);

        Require(Path.GetDirectoryName(outputPath) == testRoot, "live document output directory");
        Require(Path.GetFileName(outputPath).StartsWith("release-notes_translation", StringComparison.Ordinal),
            "live document output suffix");
        Require(!translated.Contains("The document translation task preserves", StringComparison.OrdinalIgnoreCase),
            "live document translated content");
        Require(translated.Any(ch => ch is >= '\u3400' and <= '\u9fff'), "live document Chinese output");
        Require(stages.Any(stage => stage.Percentage == 100), "live document completion progress");

        var reportPath = Path.Combine(testRoot, "result.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            sourcePath,
            outputPath,
            translated,
            progress = stages.Select(stage => new { stage.Percentage, stage.Stage })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(reportPath));
    }

    private static void RunClipboardLatencyTest()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string? originalText = null;
            try
            {
                if (!System.Windows.Clipboard.ContainsText())
                {
                    Console.WriteLine("Clipboard latency skipped: current clipboard is not text.");
                    return;
                }
                originalText = System.Windows.Clipboard.GetText();
                var marker = "SnapTranslate clipboard latency " + Guid.NewGuid().ToString("N");
                var stopwatch = Stopwatch.StartNew();
                NativeTextClipboard.WriteAsync(marker).GetAwaiter().GetResult();
                var immediate = System.Windows.Clipboard.GetText() == marker;
                stopwatch.Stop();
                Require(immediate, "native clipboard text is immediately readable");
                Require(stopwatch.ElapsedMilliseconds < 350,
                    $"native clipboard write latency ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"Clipboard latency passed: {stopwatch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                try
                {
                    if (originalText is not null)
                        NativeTextClipboard.WriteAsync(originalText).GetAwaiter().GetResult();
                }
                catch (Exception ex) { error ??= ex; }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Clipboard latency test failed", error);
    }

    private static void RunInputLayoutStress()
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        app.MainWindow = window;
        window.Show();
        try
        {
            var tabs = (System.Windows.Controls.TabControl)(window.FindName("MainTabs")
                ?? throw new InvalidOperationException("main tabs missing"));
            var input = (System.Windows.Controls.TextBox)(window.FindName("InputText")
                ?? throw new InvalidOperationException("translation input missing"));
            var result = window.FindName("InputResultText") as System.Windows.Controls.TextBox
                ?? throw new InvalidOperationException("translation result text box missing");
            var tasks = (System.Windows.Controls.DataGrid)(window.FindName("DocumentTaskGrid")
                ?? throw new InvalidOperationException("document task grid missing"));
            var task = new DocumentTranslationTask(@"C:\Docs\large.xlsx")
            {
                Status = DocumentTaskStatus.Processing,
                Progress = 1,
                StatusText = "正在翻译 1/3000"
            };

            tabs.SelectedIndex = 0;
            tasks.ItemsSource = new[] { task };
            input.Text = string.Join(Environment.NewLine,
                Enumerable.Range(1, 1_200).Select(index => $"Stress input line {index:D4}"));
            result.Text = string.Join(Environment.NewLine,
                Enumerable.Range(1, 1_200).Select(index => $"Streamed result line {index:D4}"));
            input.Focus();
            System.Windows.Input.Keyboard.Focus(input);
            PumpLayout(window);
            var inputScroll = input.Template.FindName("PART_ContentHost", input)
                as System.Windows.Controls.ScrollViewer
                ?? throw new InvalidOperationException("translation input scroll host missing");
            Require(inputScroll.ScrollableHeight > 0, "translation input supports vertical scrolling");
            var resultScroll = result.Template.FindName("PART_ContentHost", result)
                as System.Windows.Controls.ScrollViewer
                ?? throw new InvalidOperationException("translation result scroll host missing");
            Require(resultScroll.ScrollableHeight > 0
                    && result.AcceptsReturn
                    && result.TextWrapping == System.Windows.TextWrapping.Wrap
                    && result.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Auto,
                "translation result uses a multiline scroll host");

            System.Windows.Input.Keyboard.ClearFocus();
            input.ScrollToHome();
            result.ScrollToHome();
            PumpPendingDispatcherOperations(window);
            var initialResultOffset = resultScroll.VerticalOffset;
            inputScroll.ScrollToVerticalOffset(Math.Min(320, inputScroll.ScrollableHeight));
            PumpPendingDispatcherOperations(window);
            Require(inputScroll.VerticalOffset > 0
                    && Math.Abs(resultScroll.VerticalOffset - initialResultOffset) < 0.5,
                $"translation input scrolls without moving the result pane "
                + $"(input={inputScroll.VerticalOffset:0.##}/{inputScroll.ScrollableHeight:0.##}, "
                + $"result={resultScroll.VerticalOffset:0.##}, baseline={initialResultOffset:0.##})");
            var inputEndOffset = inputScroll.VerticalOffset;
            resultScroll.ScrollToVerticalOffset(Math.Min(320, resultScroll.ScrollableHeight));
            PumpPendingDispatcherOperations(window);
            Require(resultScroll.VerticalOffset > 0
                    && Math.Abs(inputScroll.VerticalOffset - inputEndOffset) < 0.5,
                $"translation result scrolls without moving the input pane "
                + $"(result={resultScroll.VerticalOffset:0.##}/{resultScroll.ScrollableHeight:0.##}, "
                + $"input={inputScroll.VerticalOffset:0.##}, baseline={inputEndOffset:0.##})");

            input.Text = "prefix selected suffix";
            input.Select(7, 8);
            string? cutPayload = null;
            var cutSucceeded = ClipboardSearchCutService.CutAsync(input, text =>
            {
                cutPayload = text;
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            Require(cutSucceeded && cutPayload == "selected" && input.Text == "prefix  suffix",
                "translation input Ctrl+X path removes the selection before clipboard completion");
            Require(typeof(MainWindow).GetMethod("InputText_PreviewKeyDown",
                        BindingFlags.Instance | BindingFlags.NonPublic) is not null,
                "translation input keeps its Ctrl+X preview handler");

            TestThrottledTranslationUpdates(window, result);

            for (var index = 1; index <= 3_000; index++)
            {
                System.Windows.Input.ApplicationCommands.SelectAll.Execute(null, input);
                System.Windows.Documents.EditingCommands.MoveToDocumentEnd.Execute(null, input);
                task.Progress = index % 101;
                task.StatusText = $"正在翻译 {index}/3000";
                result.Text = $"Translated preview {index}{Environment.NewLine}"
                              + "用于验证异步结果替换不会参与 TextBox 选区布局。";
                if (index % 20 == 0) PumpLayout(window);
            }

            PumpLayout(window);
            Require(input.CaretIndex == input.Text.Length, "input editing commands remain responsive");
            Require(result.IsReadOnly && result.Text.StartsWith("Translated preview 3000", StringComparison.Ordinal),
                "translation result remains readable after concurrent-style updates");
            Console.WriteLine("Input TextBox selection/layout stress passed (3000 updates)");
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, true);
            window.Close();
            app.Shutdown();
        }
    }

    private static void TestThrottledTranslationUpdates(
        MainWindow window,
        System.Windows.Controls.TextBox result)
    {
        var progressType = typeof(MainWindow).GetNestedType("ThrottledUiProgress",
            BindingFlags.NonPublic) ?? throw new InvalidOperationException("translation UI throttler missing");
        var constructor = progressType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault() ?? throw new InvalidOperationException("translation UI throttler constructor missing");

        var applied = new List<string>();
        using (var progress = (IDisposable)constructor.Invoke(new object[]
               {
                   window.Dispatcher,
                   (Action<string>)(value =>
                   {
                       applied.Add(value);
                       result.Text = value;
                   }),
                   TimeSpan.FromMilliseconds(40)
               }))
        {
            var reporter = (IProgress<string>)progress;
            for (var index = 0; index < 200; index++) reporter.Report($"fragment-{index:D3}");
            Require(applied.Count == 0, "stream fragments are not synchronously rendered one by one");
            PumpUntil(window, () => applied.Count > 0, TimeSpan.FromSeconds(2));
            Require(applied.Count == 1 && applied[0] == "fragment-199",
                "stream UI updates are throttled and coalesce to the latest fragment");
        }

        var requestField = typeof(MainWindow).GetField("_inputTranslationRequestId",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("translation request generation field missing");
        requestField.SetValue(window, 1);
        var generationApplied = new List<string>();

        IDisposable CreateGenerationProgress(int requestId)
            => (IDisposable)constructor.Invoke(new object[]
            {
                window.Dispatcher,
                (Action<string>)(value =>
                {
                    var activeRequest = (int)(requestField.GetValue(window) ?? -1);
                    if (requestId != activeRequest) return;
                    generationApplied.Add(value);
                    result.Text = value;
                }),
                TimeSpan.FromMilliseconds(40)
            });

        using var oldProgress = CreateGenerationProgress(1);
        ((IProgress<string>)oldProgress).Report("obsolete-result");
        requestField.SetValue(window, 2);
        using var currentProgress = CreateGenerationProgress(2);
        ((IProgress<string>)currentProgress).Report("current-result");
        PumpUntil(window, () => generationApplied.Count > 0, TimeSpan.FromSeconds(2));
        Thread.Sleep(80);
        PumpLayout(window);
        Require(generationApplied.SequenceEqual(new[] { "current-result" })
                && result.Text == "current-result",
            "a delayed fragment from an older translation request cannot overwrite the current result");
    }

    private static void PumpUntil(System.Windows.Window window, Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            Thread.Sleep(10);
            PumpPendingDispatcherOperations(window);
        }
        Require(condition(), $"dispatcher condition completed within {timeout.TotalSeconds:0.#} seconds");
    }

    private static void PumpPendingDispatcherOperations(System.Windows.Window window)
    {
        var frame = new DispatcherFrame();
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void RunImageClipboardLatencyTest(string imagePath)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RunImageClipboardLatencyTestCore(imagePath); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Image clipboard latency test failed", error);
    }

    private static void RunImageClipboardLatencyTestCore(string imagePath)
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string? originalText = null;
        HwndSource? listener = null;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
                throw new InvalidOperationException("Image clipboard latency test requires a text clipboard so it can restore the original content.");
            originalText = System.Windows.Clipboard.GetText();
            using var stream = File.OpenRead(imagePath);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            var deferredUpdates = 0;
            listener = new HwndSource(new HwndSourceParameters("SnapTranslate.ClipboardRegression")
            {
                Width = 1,
                Height = 1,
                WindowStyle = unchecked((int)0x80000000)
            });
            listener.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x031D && ClipboardService.IsWriteInProgress) deferredUpdates++;
                return IntPtr.Zero;
            });
            Require(AddClipboardFormatListener(listener.Handle), "register clipboard regression listener");

            var stopwatch = new Stopwatch();
            Exception? writeError = null;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                stopwatch.Start();
                try
                {
                    await ClipboardWriteService.WriteAsync(new ClipboardItem
                    {
                        Kind = ClipboardItemKind.Image,
                        Value = imagePath
                    }, false);
                }
                catch (Exception ex) { writeError = ex; }
                finally
                {
                    stopwatch.Stop();
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            if (writeError is not null) throw writeError;
            var copiedImage = System.Windows.Clipboard.GetImage();
            Require(copiedImage is not null
                    && copiedImage.PixelWidth == image.PixelWidth
                    && copiedImage.PixelHeight == image.PixelHeight,
                "native clipboard image is immediately readable");
            Require(stopwatch.ElapsedMilliseconds < 1200,
                $"image clipboard write latency ({stopwatch.ElapsedMilliseconds} ms)");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                deferredUpdates,
                image.PixelWidth,
                image.PixelHeight
            }));
        }
        finally
        {
            if (listener is not null)
            {
                RemoveClipboardFormatListener(listener.Handle);
                listener.Dispose();
            }
            if (originalText is not null)
                NativeTextClipboard.WriteAsync(originalText).GetAwaiter().GetResult();
        }
    }

    private static async Task RestoreLatestClipboardItemAsync()
    {
        using var repository = new ClipboardRepository();
        var latest = repository.Search(string.Empty, ClipboardFilter.All, 1000)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault() ?? throw new InvalidOperationException("Clipboard history is empty.");
        await ClipboardWriteService.WriteAsync(latest, false);
        Console.WriteLine($"Restored latest clipboard history item ({latest.Kind}, {latest.UpdatedAt:O}).");
    }

    private static async Task RestoreLatestTextClipboardItemAsync()
    {
        using var repository = new ClipboardRepository();
        var latest = repository.Search(string.Empty, ClipboardFilter.Text, 1000)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault() ?? throw new InvalidOperationException("Clipboard text history is empty.");
        await ClipboardWriteService.WriteAsync(latest, false);
        Console.WriteLine($"Restored latest text clipboard history item ({latest.UpdatedAt:O}).");
    }


    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SynchronousProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }

    private static void TestCaptureLogic()
    {
        var normalized = CaptureGeometry.Normalize(new System.Windows.Point(90, 70), new System.Windows.Point(10, 20));
        Require(normalized == new System.Windows.Rect(10, 20, 80, 50), "selection normalization");
        var bounds = new System.Windows.Size(200, 120);
        var moved = CaptureGeometry.Move(new System.Windows.Rect(150, 80, 70, 60), new System.Windows.Vector(80, 80), bounds);
        Require(moved.Right <= bounds.Width && moved.Bottom <= bounds.Height, "selection move clamp");
        foreach (var handle in Enum.GetValues<SelectionHandle>().Where(item => item is not SelectionHandle.None and not SelectionHandle.Move))
        {
            var resized = CaptureGeometry.Resize(new System.Windows.Rect(40, 30, 100, 60), handle,
                new System.Windows.Vector(handle.ToString().Contains("West") ? 20 : 15, handle.ToString().Contains("North") ? 15 : 10), bounds);
            Require(resized.Width >= CaptureGeometry.MinimumSize && resized.Height >= CaptureGeometry.MinimumSize,
                $"resize {handle}");
        }

        var desktop = new Rectangle(0, 0, 500, 300);
        var original = new Rectangle(100, 80, 200, 100);
        foreach (var handle in Enum.GetValues<SelectionHandle>().Where(item => item is not SelectionHandle.None and not SelectionHandle.Move))
        {
            var resized = ScreenshotTranslationWindow.ResizeScreenBounds(
                original, handle, new System.Windows.Vector(30, 20), desktop);
            Require(desktop.Contains(resized) && resized.Width >= 48 && resized.Height >= 32,
                $"translation resize bounds {handle}");
        }
        var northWest = ScreenshotTranslationWindow.ResizeScreenBounds(
            original, SelectionHandle.NorthWest, new System.Windows.Vector(-1000, -1000), desktop);
        Require(northWest.Left == desktop.Left && northWest.Top == desktop.Top
                && northWest.Right == original.Right && northWest.Bottom == original.Bottom,
            "translation northwest resize clamps to desktop");
        var southEast = ScreenshotTranslationWindow.ResizeScreenBounds(
            original, SelectionHandle.SouthEast, new System.Windows.Vector(1000, 1000), desktop);
        Require(southEast.Right == desktop.Right && southEast.Bottom == desktop.Bottom
                && southEast.Left == original.Left && southEast.Top == original.Top,
            "translation southeast resize clamps to desktop");
        var minimum = ScreenshotTranslationWindow.ResizeScreenBounds(
            original, SelectionHandle.NorthWest, new System.Windows.Vector(1000, 1000), desktop);
        Require(minimum.Width == 48 && minimum.Height == 32, "translation resize minimum size");

        var denseLines = new[]
        {
            new OcrTextLine("left", 10, 10, 80, 14),
            new OcrTextLine("right", 75, 10, 80, 14),
            new OcrTextLine("below", 10, 20, 80, 14)
        };
        var denseBounds = ScreenshotTranslationWindow.CalculateOverlayBounds(denseLines, 1, 1, 220, 100);
        Require(denseBounds[0].Right <= denseBounds[1].Left,
            "translation bounds do not overlap the next column");
        Require(denseBounds[0].Bottom <= denseBounds[2].Top,
            "translation bounds do not overlap the next row");
        var fittedFont = ScreenshotTranslationWindow.FitFontSize(
            new[] { "A much longer translated sentence that must fit" }, 70, 13);
        Require(fittedFont < 8, $"translation font shrinks for dense regions ({fittedFont})");

        var contextualLayout = new OcrLayoutResult(
            denseLines,
            string.Join(Environment.NewLine, denseLines.Select(line => line.Text)),
            "en");
        var contextualTranslator = new FakeTranslationService(_ => "左侧译文\n右侧译文\n下方译文");
        var contextualResult = ScreenshotTranslationWindow.TranslateLayoutAsync(
            contextualTranslator,
            contextualLayout,
            new AppSettings { SourceLanguage = "en", TargetLanguage = "zh-CN" }).GetAwaiter().GetResult();
        Require(contextualTranslator.CallCount == 1
                && contextualTranslator.LastInput == contextualLayout.Text,
            "screenshot translation sends the complete OCR context once");
        Require(contextualResult.Lines.SequenceEqual(new[] { "左侧译文", "右侧译文", "下方译文" }),
            "screenshot translation preserves provider line mapping");
        var mergedTranslation = ScreenshotTranslationWindow.MapTranslationToLines(
            denseLines,
            "左侧译文。右侧译文。下方译文。");
        Require(mergedTranslation.Length == denseLines.Length
                && mergedTranslation.All(line => !string.IsNullOrWhiteSpace(line))
                && string.Concat(mergedTranslation) == "左侧译文。右侧译文。下方译文。",
            "screenshot translation maps merged provider output to every OCR region");
        Require(OcrService.AreDuplicateOcrRegions(
                28, 65, 700, 79,
                28, 65.2, 357, 78.8),
            "OCR merges a contained partial candidate on the same text line");
        Require(!OcrService.AreDuplicateOcrRegions(
                28, 65, 320, 79,
                340, 65, 700, 79),
            "OCR keeps separate adjacent text regions");
        Require(OcrService.DetectLanguage("The English content is dominant. 已运行命令") == "en"
                && OcrService.DetectLanguage("这是一段中文内容，其中包含 API 和 HTTP 名称。") == "zh",
            "OCR detects the dominant language on mixed-language screens");

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var captureWidth = Math.Min(16, virtualScreen.Width);
        var captureHeight = Math.Min(12, virtualScreen.Height);
        var region = ScreenshotService.CaptureRegion(new Rectangle(
            virtualScreen.Left, virtualScreen.Top, captureWidth, captureHeight));
        Require(region.PixelWidth == captureWidth && region.PixelHeight == captureHeight,
            "capture region dimensions");
        var history = new CaptureHistory<string>();
        history.Add("first");
        history.Add("second");
        Require(history.Undo() == "second" && history.CanRedo, "capture undo");
        Require(history.Redo() == "second" && history.Items.Count == 2, "capture redo");
    }

    private static void TestHotkeyExecutionGate()
    {
        var gate = new HotkeyExecutionGate();
        var debounce = TimeSpan.FromMilliseconds(120);
        Require(gate.TryEnter(5, debounce, out var firstLease), "hotkey gate accepts the first request");
        Require(!gate.TryEnter(5, debounce, out _), "hotkey gate merges a request while dispatch is pending");
        Require(gate.TryEnter(6, debounce, out var otherLease), "hotkey gate keeps different commands independent");
        gate.Exit(otherLease);
        gate.Exit(firstLease);
        Require(!gate.TryEnter(5, debounce, out _), "hotkey gate rejects a duplicate inside the debounce window");
        Thread.Sleep(180);
        Require(gate.TryEnter(5, debounce, out var laterLease), "hotkey gate accepts a later physical invocation");
        gate.Reset();
        Require(gate.TryEnter(5, TimeSpan.Zero, out var resetLease), "hotkey gate reset clears pending and debounce state");
        gate.Exit(laterLease);
        Require(!gate.TryEnter(5, TimeSpan.Zero, out _), "stale hotkey leases cannot release a newer request");
        gate.Exit(resetLease);

        var maximumAge = TimeSpan.FromMilliseconds(450);
        Require(HotkeyEventTiming.IsFresh(1_000, 1_449, maximumAge),
            "hotkey timing accepts a recent native event");
        Require(!HotkeyEventTiming.IsFresh(1_000, 1_451, maximumAge),
            "hotkey timing rejects an expired native event");
        Require(HotkeyEventTiming.IsFresh(uint.MaxValue - 50, 25, TimeSpan.FromMilliseconds(100)),
            "hotkey timing handles the native tick counter wrapping");
        Require(PasteService.NativeInputSize == (IntPtr.Size == 8 ? 40 : 28),
            "automatic paste uses the platform Win32 INPUT structure size");
    }

    private static void TestLowLevelKeyboardHookHost()
    {
        const int eventCount = 3;
        const int wmKeyDown = 0x0100;
        const int wmKeyUp = 0x0101;
        const uint vkF24 = 0x87;
        var markerValue = 0x53540000u ^ unchecked((uint)Environment.ProcessId) ^ unchecked((uint)Environment.TickCount);
        if (markerValue == 0) markerValue = 1;
        var marker = new UIntPtr(markerValue);
        var uiThreadId = Environment.CurrentManagedThreadId;
        var callbackThreadId = 0;
        var callbackThreadMismatch = 0;
        var callbackCount = 0;
        var sentAt = new long[eventCount];
        var callbackAt = new long[eventCount];
        var senderCancellationRequested = 0;
        Exception? senderError = null;

        using var sinkSeen = new ManualResetEventSlim(false);
        using var senderReady = new ManualResetEventSlim(false);
        using var releaseSender = new ManualResetEventSlim(false);
        using var callbacksCompleted = new CountdownEvent(eventCount);

        bool IsTaggedF24(LowLevelKeyboardHookEvent keyboardEvent) =>
            keyboardEvent.VirtualKey == vkF24
            && keyboardEvent.ExtraInfo == marker
            && keyboardEvent.Message is wmKeyDown or wmKeyUp;

        using var sink = new LowLevelKeyboardHookHost(keyboardEvent =>
        {
            if (!IsTaggedF24(keyboardEvent)) return false;
            if (keyboardEvent.Message == wmKeyDown) sinkSeen.Set();
            return true;
        });
        using var subject = new LowLevelKeyboardHookHost(keyboardEvent =>
        {
            if (!IsTaggedF24(keyboardEvent)) return false;
            if (keyboardEvent.Message == wmKeyDown)
            {
                var index = Interlocked.Increment(ref callbackCount) - 1;
                var currentThreadId = Environment.CurrentManagedThreadId;
                var priorThreadId = Interlocked.CompareExchange(ref callbackThreadId, currentThreadId, 0);
                if (priorThreadId != 0 && priorThreadId != currentThreadId)
                    Volatile.Write(ref callbackThreadMismatch, 1);
                if ((uint)index < eventCount)
                {
                    Volatile.Write(ref callbackAt[index], Stopwatch.GetTimestamp());
                    callbacksCompleted.Signal();
                }
            }
            return true;
        });

        Thread? sender = null;
        try
        {
            sink.Start();
            subject.Start();
            Require(sink.IsRunning && subject.IsRunning, "keyboard hook hosts start on dedicated message threads");

            sender = new Thread(() =>
            {
                try
                {
                    senderReady.Set();
                    releaseSender.Wait();
                    Thread.Sleep(50);
                    for (var index = 0; index < eventCount; index++)
                    {
                        if (Volatile.Read(ref senderCancellationRequested) != 0) return;
                        Volatile.Write(ref sentAt[index], Stopwatch.GetTimestamp());
                        SendTaggedF24(marker);
                        if (index + 1 < eventCount) Thread.Sleep(25);
                    }
                }
                catch (Exception ex)
                {
                    senderError = ex;
                }
            })
            {
                IsBackground = true,
                Name = "SnapTranslate keyboard hook smoke sender"
            };
            sender.Start();
            Require(senderReady.Wait(TimeSpan.FromSeconds(2)), "keyboard hook sender becomes ready");

            var uiBlockStarted = Stopwatch.GetTimestamp();
            long uiBlockEnded;
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                releaseSender.Set();
                Thread.Sleep(900);
                uiBlockEnded = Stopwatch.GetTimestamp();
            }

            Require(sender.Join(TimeSpan.FromSeconds(2)), "keyboard hook sender completes within timeout");
            if (senderError is not null)
                throw new InvalidOperationException("F24 SendInput failed during keyboard hook smoke test.", senderError);

            Require(callbacksCompleted.CurrentCount == 0 && Volatile.Read(ref callbackCount) == eventCount,
                "keyboard hook consumes repeated F24 events while the UI thread is blocked");
            Require(Volatile.Read(ref callbackThreadId) != 0
                    && Volatile.Read(ref callbackThreadId) != uiThreadId
                    && Volatile.Read(ref callbackThreadMismatch) == 0,
                "keyboard hook callbacks stay on one non-UI message thread");
            Require(!sinkSeen.IsSet, "subject hook swallows tagged F24 before the safety sink");

            var deliveryBudgetTicks = (long)(Stopwatch.Frequency * 0.5);
            for (var index = 0; index < eventCount; index++)
            {
                var sent = Volatile.Read(ref sentAt[index]);
                var received = Volatile.Read(ref callbackAt[index]);
                var elapsedMilliseconds = (received - sent) * 1000d / Stopwatch.Frequency;
                Require(sent >= uiBlockStarted
                        && received >= sent
                        && received < uiBlockEnded
                        && received - sent <= deliveryBudgetTicks,
                    $"keyboard hook F24 event {index + 1} arrives during UI blocking ({elapsedMilliseconds:F1} ms)");
            }
            Require(subject.LastCallbackException is null && subject.MessageLoopError == 0,
                "subject keyboard hook completes without callback or message-loop errors");

            subject.Stop();
            subject.Stop();
            Require(!subject.IsRunning, "keyboard hook stop is complete and idempotent");

            sinkSeen.Reset();
            var countAfterStop = Volatile.Read(ref callbackCount);
            SendTaggedF24(marker);
            Require(sinkSeen.Wait(TimeSpan.FromSeconds(2)),
                "safety sink receives tagged F24 after the subject hook stops");
            Require(Volatile.Read(ref callbackCount) == countAfterStop,
                "stopped keyboard hook leaves no callback behind");
            Require(subject.LastCallbackException is null && subject.MessageLoopError == 0,
                "stopped keyboard hook leaves no callback or message-loop error");

            sink.Stop();
            sink.Stop();
            Require(!sink.IsRunning && sink.LastCallbackException is null && sink.MessageLoopError == 0,
                "safety sink stops cleanly and idempotently");
        }
        finally
        {
            Volatile.Write(ref senderCancellationRequested, 1);
            releaseSender.Set();
            if (sender is { IsAlive: true }
                && !sender.Join(TimeSpan.FromSeconds(2)))
                throw new TimeoutException("Keyboard hook smoke sender did not stop within two seconds.");
            try
            {
                subject.Stop();
            }
            finally
            {
                sink.Stop();
            }
        }
    }

    private static void SendTaggedF24(UIntPtr marker)
    {
        const ushort vkF24 = 0x87;
        const uint inputKeyboard = 1;
        const uint keyeventfKeyup = 0x0002;
        var inputs = new[]
        {
            new NativeInput
            {
                Type = inputKeyboard,
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput { VirtualKey = vkF24, ExtraInfo = marker }
                }
            },
            new NativeInput
            {
                Type = inputKeyboard,
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput
                    {
                        VirtualKey = vkF24,
                        Flags = keyeventfKeyup,
                        ExtraInfo = marker
                    }
                }
            }
        };
        var inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (inserted == inputs.Length) return;

        var error = Marshal.GetLastWin32Error();
        var release = new[] { inputs[1] };
        _ = SendInput(1, release, Marshal.SizeOf<NativeInput>());
        throw new InvalidOperationException(
            $"SendInput inserted {inserted} of {inputs.Length} tagged F24 events (Win32 {error}).");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Capture regression failed: {name}");
    }

    private static int CountOverlappingRegions(IReadOnlyList<System.Windows.Rect> bounds)
    {
        var count = 0;
        for (var first = 0; first < bounds.Count; first++)
        {
            if (bounds[first].IsEmpty) continue;
            for (var second = first + 1; second < bounds.Count; second++)
            {
                if (bounds[second].IsEmpty) continue;
                var intersection = System.Windows.Rect.Intersect(bounds[first], bounds[second]);
                if (!intersection.IsEmpty && intersection.Width > 0.5 && intersection.Height > 0.5) count++;
            }
        }
        return count;
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject root, string name)
        where T : System.Windows.FrameworkElement
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name) return match;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private static ClipboardItem CreateSample(string id, ClipboardItemKind kind, string value, string note)
    {
        var now = DateTimeOffset.Now;
        return new ClipboardItem
        {
            Id = id,
            Kind = kind,
            Value = value,
            SearchText = kind == ClipboardItemKind.Files ? "report.docx image.png" : value,
            Note = note,
            Size = value.Length,
            CreatedAt = now,
            UpdatedAt = now,
            ContentHash = ClipboardRepository.HashContent(kind, value)
        };
    }

    private static ClipboardItem CreateImageSample(string id, string path)
    {
        var now = DateTimeOffset.Now;
        return new ClipboardItem
        {
            Id = id,
            Kind = ClipboardItemKind.Image,
            Value = path,
            SearchText = "OCR 图片预览",
            Note = "截图",
            Size = new FileInfo(path).Length,
            Width = 240,
            Height = 150,
            CreatedAt = now,
            UpdatedAt = now,
            ContentHash = ClipboardRepository.HashContent(ClipboardItemKind.Image, path)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, NativeInput[] inputs, int sizeOfInput);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    private static void PumpLayout(System.Windows.Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        window.UpdateLayout();
    }

    private static void RenderElement(System.Windows.FrameworkElement element, string path)
    {
        // 渲染滚动区内容时，ActualWidth 与内部排版宽度可能差一个滚动条宽度，
        // 右侧边缘会略有裁切；这个截图只用于人工检查整页结构。
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RenderWindow(System.Windows.Window window, string path)
    {
        var visual = window.Content as System.Windows.FrameworkElement ?? window;
        // 内容尺寸在无消息循环的渲染宿主里可能比实际排版尺寸小，取窗口尺寸兜底，避免右侧/底部被裁。
        var width = Math.Max(1, (int)Math.Ceiling(Math.Max(visual.ActualWidth, window.ActualWidth)));
        var height = Math.Max(1, (int)Math.Ceiling(Math.Max(visual.ActualHeight, window.ActualHeight)));
        // 直接 Render(content) 会带上内容在窗口里的偏移（标题栏/边框），右侧和下侧被裁掉；
        // 用 VisualBrush 从 (0,0) 开始画，窗口截图才是完整的。
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var brush = new System.Windows.Media.VisualBrush(visual)
            {
                Stretch = System.Windows.Media.Stretch.None,
                AlignmentX = System.Windows.Media.AlignmentX.Left,
                AlignmentY = System.Windows.Media.AlignmentY.Top
            };
            context.DrawRectangle(brush, null, new System.Windows.Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class FakeTranslationService : ITranslationService
    {
        private readonly Func<string, string>? _translate;

        public FakeTranslationService(Func<string, string>? translate = null)
        {
            _translate = translate;
        }

        public int CallCount { get; private set; }
        public string? LastInput { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            string text,
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastInput = text;
            var translated = _translate?.Invoke(text) ?? $"[{settings.TargetLanguage}] {text}";
            return Task.FromResult(new TranslationResult(translated, "Local test", settings.SourceLanguage));
        }
    }
}
