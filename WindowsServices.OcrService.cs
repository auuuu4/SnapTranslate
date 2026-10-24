using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SnapTranslate;

public sealed class OcrService
{
	private sealed record OcrLineCandidate(string Text, double Left, double Top, double Right, double Bottom, int CjkCount, int LatinCount, int Score, bool LatinEngine, int VariantIndex, double Confidence, bool TesseractEngine);

	private sealed record TesseractWord(string Text, int Left, int Top, int Width, int Height, double Confidence);

	private sealed record OcrImageVariant(BitmapSource Image, double ScaleX, double ScaleY);

	private const int MaximumOcrDimension = 2500;

	private static readonly double? enhancedScaleOverride =
		double.TryParse(Environment.GetEnvironmentVariable("SNAPTRANSLATE_OCR_ENHANCE_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var forcedScale) && forcedScale > 1.0
			? forcedScale
			: null;

	private static readonly Lazy<string?> TesseractExecutable = new(FindTesseractExecutable);

	public async Task<string> RecognizeAsync(BitmapSource image)
	{
		return (await RecognizeLayoutAsync(image)).Text;
	}

	public Task<OcrLayoutResult> RecognizeLayoutAsync(BitmapSource image)
	{
		return RecognizeLayoutAsync(image, "auto");
	}

	public async Task<OcrLayoutResult> RecognizeLayoutAsync(BitmapSource image, string sourceLanguage)
	{
		IReadOnlyList<Language> availableRecognizerLanguages = OcrEngine.AvailableRecognizerLanguages;
		Language? chinese = availableRecognizerLanguages.FirstOrDefault((Language x) => x.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
		Language? english = availableRecognizerLanguages.FirstOrDefault((Language x) => x.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
		if (chinese is null && english is null)
		{
			throw new InvalidOperationException("系统未安装可用的 OCR 语言包。请安装中文或英文 OCR 语言功能。");
		}
		string normalizedSource = (string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage.Trim().ToLowerInvariant());
		bool flag;
		switch (normalizedSource)
		{
		case "auto":
		case "zh":
		case "zh-cn":
		case "zh-chs":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		bool useChinese = flag;
		bool useLatin = normalizedSource == "auto" || !useChinese;
		List<OcrLineCandidate> candidates = new List<OcrLineCandidate>();
		IReadOnlyList<OcrImageVariant> variants = CreateVariants(image);
		for (int variantIndex = 0; variantIndex < variants.Count; variantIndex++)
		{
			OcrImageVariant variant = variants[variantIndex];
			using SoftwareBitmap softwareBitmap = await DecodeAsync(variant.Image);
			if (useChinese && chinese is not null)
			{
				OcrEngine obj = OcrEngine.TryCreateFromLanguage(chinese) ?? throw new InvalidOperationException("无法初始化系统 OCR：" + chinese.DisplayName);
				List<OcrLineCandidate> list = candidates;
				list.AddRange(ToCandidates(await obj.RecognizeAsync(softwareBitmap), englishEngine: false, variant.ScaleX, variant.ScaleY, variantIndex));
			}
			if (useLatin && english is not null && !string.Equals(english.LanguageTag, chinese?.LanguageTag, StringComparison.OrdinalIgnoreCase))
			{
				OcrEngine obj2 = OcrEngine.TryCreateFromLanguage(english) ?? throw new InvalidOperationException("无法初始化系统 OCR：" + english.DisplayName);
				List<OcrLineCandidate> list = candidates;
				list.AddRange(ToCandidates(await obj2.RecognizeAsync(softwareBitmap), englishEngine: true, variant.ScaleX, variant.ScaleY, variantIndex));
			}
		}
		if (useLatin && (normalizedSource != "auto" || (double)candidates.Sum((OcrLineCandidate item) => item.LatinCount) > (double)candidates.Sum((OcrLineCandidate item) => item.CjkCount) * 1.35))
		{
			List<OcrLineCandidate> list = candidates;
			list.AddRange(await TryRecognizeWithTesseractAsync(image));
		}
		IReadOnlyList<OcrTextLine> readOnlyList = MergeCandidates(candidates, normalizedSource);
		string text = string.Join(Environment.NewLine, readOnlyList.Select((OcrTextLine item) => item.Text)).Trim();
		return new OcrLayoutResult(readOnlyList, text, DetectLanguage(text));
	}

	private static async Task<SoftwareBitmap> DecodeAsync(BitmapSource image)
	{
		byte[] value = ScreenshotService.ToPng(image);
		using InMemoryRandomAccessStream randomAccess = new InMemoryRandomAccessStream();
		using (DataWriter writer = new DataWriter(randomAccess))
		{
			writer.WriteBytes(value);
			await writer.StoreAsync();
			await writer.FlushAsync();
			writer.DetachStream();
		}
		randomAccess.Seek(0uL);
		return await (await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccess)).GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
	}

	private static IReadOnlyList<OcrImageVariant> CreateVariants(BitmapSource image)
	{
		int maxSide = Math.Max(image.PixelWidth, image.PixelHeight);
		double val = Math.Max(1.0, Math.Min(2.5, 2500.0 / (double)maxSide));
		List<OcrImageVariant> list = new List<OcrImageVariant> { CreateVariant(ScaleImage(image, val), image) };

		// 小字号是识别错误的根源：偏旁被拆成两个字、笔画相近的字认错。
		// 这里再补一档「放得更大 + 对比度拉伸 + 锐化」，只在图片本身够小时加，避免拖慢大图。
		// 试过再加一档 Otsu 二值化：低对比度样张反而被它带偏，整体 CER 变差，所以没有保留。
		double enhancedScale = ResolveEnhancedScale(maxSide, val);
		if (enhancedScale > 0)
		{
			BitmapSource scaled = ScaleImage(image, enhancedScale);
			list.Add(CreateVariant(EnhanceForSmallText(scaled), image));
		}

		if (IsPredominantlyDark(image))
		{
			list.Add(CreateVariant(ScaleImage(InvertGrayscale(image), val), image));
			if (enhancedScale > 0)
				list.Add(CreateVariant(EnhanceForSmallText(ScaleImage(InvertGrayscale(image), enhancedScale)), image));
		}

		return list;
	}

	private static double ResolveEnhancedScale(int maxSide, double baseScale)
	{
		if (enhancedScaleOverride is { } forced) return forced > baseScale ? forced : baseScale;
		if (maxSide > 1400) return 0;
		double target = Math.Min(3800.0 / maxSide, 4.0);
		double scale = Math.Min(target, 2600.0 / maxSide);
		return scale >= baseScale * 1.25 ? scale : 0;
	}

	private static OcrImageVariant CreateVariant(BitmapSource variant, BitmapSource reference)
		=> new(variant,
			(double)variant.PixelWidth / (double)reference.PixelWidth,
			(double)variant.PixelHeight / (double)reference.PixelHeight);

	/// <summary>灰度化 + 2%/98% 直方图拉伸 + 3x3 反锐化，专治小字号的细笔画和低对比度。</summary>
	private static BitmapSource EnhanceForSmallText(BitmapSource image)
	{
		FormatConvertedBitmap converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0.0);
		int width = converted.PixelWidth;
		int height = converted.PixelHeight;
		int stride = width * 4;
		byte[] pixels = new byte[stride * height];
		converted.CopyPixels(pixels, stride, 0);

		byte[] gray = new byte[width * height];
		int[] histogram = new int[256];
		for (int index = 0, offset = 0; index < pixels.Length; index += 4, offset++)
		{
			int luma = (pixels[index + 2] * 54 + pixels[index + 1] * 183 + pixels[index] * 19) >> 8;
			gray[offset] = (byte)luma;
			histogram[luma]++;
		}

		int low = Percentile(histogram, gray.Length, 0.02);
		int high = Percentile(histogram, gray.Length, 0.98);
		if (high - low >= 16)
		{
			double factor = 255.0 / (high - low);
			for (int offset = 0; offset < gray.Length; offset++)
				gray[offset] = (byte)Math.Clamp((int)Math.Round((gray[offset] - low) * factor), 0, 255);
		}

		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				int offset = y * width + x;
				int sum = gray[offset] * 5;
				if (x > 0) sum -= gray[offset - 1];
				if (x < width - 1) sum -= gray[offset + 1];
				if (y > 0) sum -= gray[offset - width];
				if (y < height - 1) sum -= gray[offset + width];
				byte sharpened = (byte)Math.Clamp(sum, 0, 255);
				int pixel = offset * 4;
				pixels[pixel] = sharpened;
				pixels[pixel + 1] = sharpened;
				pixels[pixel + 2] = sharpened;
				pixels[pixel + 3] = byte.MaxValue;
			}
		}

		BitmapSource result = BitmapSource.Create(width, height, image.DpiX, image.DpiY, PixelFormats.Bgra32, null, pixels, stride);
		((Freezable)result).Freeze();
		return result;
	}

	private static int Percentile(int[] histogram, int total, double fraction)
	{
		int target = (int)Math.Clamp(total * fraction, 0, total - 1);
		int seen = 0;
		for (int value = 0; value < histogram.Length; value++)
		{
			seen += histogram[value];
			if (seen > target) return value;
		}
		return histogram.Length - 1;
	}

	private static BitmapSource ScaleImage(BitmapSource image, double scale)
	{
		if (scale <= 1.01)
		{
			return image;
		}
		TransformedBitmap transformedBitmap = new TransformedBitmap(image, new ScaleTransform(scale, scale));
		((Freezable)transformedBitmap).Freeze();
		return transformedBitmap;
	}

	private static bool IsPredominantlyDark(BitmapSource image)
	{
		FormatConvertedBitmap formatConvertedBitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0.0);
		int pixelWidth = formatConvertedBitmap.PixelWidth;
		int pixelHeight = formatConvertedBitmap.PixelHeight;
		int num = pixelWidth * 4;
		byte[] array = new byte[num * pixelHeight];
		formatConvertedBitmap.CopyPixels(array, num, 0);
		long num2 = 0L;
		int num3 = 0;
		int num4 = Math.Max(1, pixelWidth / 80);
		int num5 = Math.Max(1, pixelHeight / 50);
		for (int i = 0; i < pixelHeight; i += num5)
		{
			for (int j = 0; j < pixelWidth; j += num4)
			{
				int num6 = i * num + j * 4;
				num2 += array[num6 + 2] * 54 + array[num6 + 1] * 183 + array[num6] * 19 >> 8;
				num3++;
			}
		}
		if (num3 > 0)
		{
			return num2 / num3 < 115;
		}
		return false;
	}

	private static BitmapSource InvertGrayscale(BitmapSource image)
	{
		FormatConvertedBitmap formatConvertedBitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0.0);
		int pixelWidth = formatConvertedBitmap.PixelWidth;
		int pixelHeight = formatConvertedBitmap.PixelHeight;
		int num = pixelWidth * 4;
		byte[] array = new byte[num * pixelHeight];
		formatConvertedBitmap.CopyPixels(array, num, 0);
		for (int i = 0; i < array.Length; i += 4)
		{
			int num2 = array[i + 2] * 54 + array[i + 1] * 183 + array[i] * 19 >> 8;
			array[i + 2] = (array[i + 1] = (array[i] = (byte)(255 - num2)));
			array[i + 3] = byte.MaxValue;
		}
		BitmapSource bitmapSource = BitmapSource.Create(pixelWidth, pixelHeight, image.DpiX, image.DpiY, PixelFormats.Bgra32, null, array, num);
		((Freezable)bitmapSource).Freeze();
		return bitmapSource;
	}

	private static async Task<IReadOnlyList<OcrLineCandidate>> TryRecognizeWithTesseractAsync(BitmapSource image)
	{
		string? executable = TesseractExecutable.Value;
		if (executable == null)
		{
			return Array.Empty<OcrLineCandidate>();
		}
		string temporaryPath = Path.Combine(Path.GetTempPath(), $"SnapTranslate-ocr-{Guid.NewGuid():N}.png");
		try
		{
			double scale = Math.Clamp(2500.0 / Math.Max(image.PixelWidth, image.PixelHeight), 1.0, 2.0);
			BitmapSource tesseractImage = ScaleImage(image, scale);
			double scaleX = (double)tesseractImage.PixelWidth / image.PixelWidth;
			double scaleY = (double)tesseractImage.PixelHeight / image.PixelHeight;
			await File.WriteAllBytesAsync(temporaryPath, ScreenshotService.ToPng(tesseractImage));
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				FileName = executable,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			processStartInfo.ArgumentList.Add(temporaryPath);
			processStartInfo.ArgumentList.Add("stdout");
			processStartInfo.ArgumentList.Add("-l");
			processStartInfo.ArgumentList.Add("eng");
			processStartInfo.ArgumentList.Add("--psm");
			processStartInfo.ArgumentList.Add("6");
			processStartInfo.ArgumentList.Add("tsv");
			using Process? process = Process.Start(processStartInfo);
			if (process == null)
			{
				return Array.Empty<OcrLineCandidate>();
			}
			Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorTask = process.StandardError.ReadToEndAsync();
			using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6.0));
			try
			{
				await process.WaitForExitAsync(timeout.Token);
			}
			catch (OperationCanceledException)
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
				return Array.Empty<OcrLineCandidate>();
			}
			string output = await outputTask;
			await errorTask;
			if (process.ExitCode != 0)
			{
				return Array.Empty<OcrLineCandidate>();
			}
			return ParseTesseractTsv(output, tesseractImage.PixelWidth, tesseractImage.PixelHeight)
				.Select(item => item with
				{
					Left = item.Left / scaleX,
					Top = item.Top / scaleY,
					Right = item.Right / scaleX,
					Bottom = item.Bottom / scaleY
				})
				.ToArray();
		}
		catch
		{
			return Array.Empty<OcrLineCandidate>();
		}
		finally
		{
			try
			{
				File.Delete(temporaryPath);
			}
			catch
			{
			}
		}
	}

	private static IReadOnlyList<OcrLineCandidate> ParseTesseractTsv(string tsv, int imageWidth, int imageHeight)
	{
		Dictionary<(int, int, int, int), List<TesseractWord>> dictionary = new Dictionary<(int, int, int, int), List<TesseractWord>>();
		foreach (string item in tsv.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			string[] array = item.Split('\t');
			if (array.Length < 12 || array[0] != "5" || !int.TryParse(array[1], out var result) || !int.TryParse(array[2], out var result2) || !int.TryParse(array[3], out var result3) || !int.TryParse(array[4], out var result4) || !int.TryParse(array[6], out var result5) || !int.TryParse(array[7], out var result6) || !int.TryParse(array[8], out var result7) || !int.TryParse(array[9], out var result8) || !double.TryParse(array[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var result9))
			{
				continue;
			}
			string text = array[11].Trim();
			if (text.Length != 0 && result7 > 0 && result8 > 0 && !(result9 < 0.0))
			{
				(int, int, int, int) key = (result, result2, result3, result4);
				if (!dictionary.TryGetValue(key, out var value))
				{
					value = (dictionary[key] = new List<TesseractWord>());
				}
				value.Add(new TesseractWord(text, result5, result6, result7, result8, result9));
			}
		}
		List<OcrLineCandidate> list2 = new List<OcrLineCandidate>();
		foreach (List<TesseractWord> value2 in dictionary.Values)
		{
			double num = value2.Average((TesseractWord item) => item.Confidence);
			int num2 = value2.Min((TesseractWord item) => item.Left);
			int num3 = value2.Min((TesseractWord item) => item.Top);
			int num4 = value2.Max((TesseractWord item) => item.Left + item.Width);
			int num5 = value2.Max((TesseractWord item) => item.Top + item.Height);
			if (num < 25.0 || (double)(num5 - num3) > Math.Max(20.0, (double)imageHeight * 0.18) || (double)(num4 - num2) > (double)imageWidth * 0.96)
			{
				continue;
			}
			string text2 = string.Join(" ", value2.Select((TesseractWord item) => item.Text)).Trim();
			int num6 = text2.Count((char ch) => ch >= '㐀' && ch <= '鿿');
			int num7 = text2.Count(delegate(char ch)
			{
				switch (ch)
				{
				case 'A':
				case 'B':
				case 'C':
				case 'D':
				case 'E':
				case 'F':
				case 'G':
				case 'H':
				case 'I':
				case 'J':
				case 'K':
				case 'L':
				case 'M':
				case 'N':
				case 'O':
				case 'P':
				case 'Q':
				case 'R':
				case 'S':
				case 'T':
				case 'U':
				case 'V':
				case 'W':
				case 'X':
				case 'Y':
				case 'Z':
				case 'a':
				case 'b':
				case 'c':
				case 'd':
				case 'e':
				case 'f':
				case 'g':
				case 'h':
				case 'i':
				case 'j':
				case 'k':
				case 'l':
				case 'm':
				case 'n':
				case 'o':
				case 'p':
				case 'q':
				case 'r':
				case 's':
				case 't':
				case 'u':
				case 'v':
				case 'w':
				case 'x':
				case 'y':
				case 'z':
					return true;
				default:
					return false;
				}
			});
			int num8 = text2.Count(char.IsDigit);
			int num9 = text2.Count((char ch) => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch));
			int num10 = num6 * 6 + num7 * 3 + num8 * 2 - num9 * 5;
			if (num6 == 0)
			{
				num10 += 20;
			}
			list2.Add(new OcrLineCandidate(text2, num2, num3, num4, num5, num6, num7, num10, LatinEngine: true, 100, num, TesseractEngine: true));
		}
		return list2;
	}

	private static string? FindTesseractExecutable()
	{
		return (from directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			select Path.Combine(directory.Trim().Trim('"'), "tesseract.exe")).Concat(new string[1]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe")
		}).FirstOrDefault(File.Exists);
	}

	private static IEnumerable<OcrLineCandidate> ToCandidates(OcrResult result, bool englishEngine, double scaleX, double scaleY, int variantIndex)
	{
		foreach (OcrLine line in result.Lines)
		{
			OcrWord[] array = line.Words.ToArray();
			if (array.Length == 0)
			{
				continue;
			}
			string text = string.Join(" ", array.Select((OcrWord word) => word.Text)).Trim();
			if (text.Length == 0)
			{
				continue;
			}
			double num = array.Min((OcrWord word) => word.BoundingRect.X);
			double num2 = array.Min((OcrWord word) => word.BoundingRect.Y);
			double num3 = array.Max((OcrWord word) => word.BoundingRect.X + word.BoundingRect.Width);
			double num4 = array.Max((OcrWord word) => word.BoundingRect.Y + word.BoundingRect.Height);
			int num5 = text.Count((char ch) => ch >= '㐀' && ch <= '鿿');
			int num6 = text.Count(delegate(char ch)
			{
				switch (ch)
				{
				case 'A':
				case 'B':
				case 'C':
				case 'D':
				case 'E':
				case 'F':
				case 'G':
				case 'H':
				case 'I':
				case 'J':
				case 'K':
				case 'L':
				case 'M':
				case 'N':
				case 'O':
				case 'P':
				case 'Q':
				case 'R':
				case 'S':
				case 'T':
				case 'U':
				case 'V':
				case 'W':
				case 'X':
				case 'Y':
				case 'Z':
				case 'a':
				case 'b':
				case 'c':
				case 'd':
				case 'e':
				case 'f':
				case 'g':
				case 'h':
				case 'i':
				case 'j':
				case 'k':
				case 'l':
				case 'm':
				case 'n':
				case 'o':
				case 'p':
				case 'q':
				case 'r':
				case 's':
				case 't':
				case 'u':
				case 'v':
				case 'w':
				case 'x':
				case 'y':
				case 'z':
					return true;
				default:
					return false;
				}
			});
			int num7 = text.Count(char.IsDigit);
			int num8 = text.Count((char ch) => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch));
			int num9 = num5 * 6 + num6 * 3 + num7 * 2 - num8 * 5;
			if (num5 >= 2 && !englishEngine)
			{
				num9 += 20;
			}
			if (num5 == 0 && englishEngine)
			{
				num9 += 20;
			}
			yield return new OcrLineCandidate(text, num / scaleX, num2 / scaleY, num3 / scaleX, num4 / scaleY, num5, num6, num9, englishEngine, variantIndex, 50.0, TesseractEngine: false);
		}
	}

	private static IReadOnlyList<OcrTextLine> MergeCandidates(List<OcrLineCandidate> candidates, string sourceLanguage)
	{
		List<List<OcrLineCandidate>> list = new List<List<OcrLineCandidate>>();
		foreach (OcrLineCandidate candidate in candidates.OrderBy((OcrLineCandidate item) => item.Top))
		{
			List<OcrLineCandidate>[] matchingGroups = list
				.Where(items => items.Any(item => IsDuplicateRegion(item, candidate)))
				.ToArray();
			if (matchingGroups.Length == 0)
			{
				list.Add(new List<OcrLineCandidate> { candidate });
			}
			else
			{
				List<OcrLineCandidate> primary = matchingGroups[0];
				primary.Add(candidate);
				foreach (List<OcrLineCandidate> other in matchingGroups.Skip(1))
				{
					primary.AddRange(other);
					list.Remove(other);
				}
			}
		}
		bool flag2;
		if (sourceLanguage != "auto")
		{
			bool flag;
			switch (sourceLanguage)
			{
			case "zh":
			case "zh-cn":
			case "zh-chs":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			flag2 = !flag;
		}
		else
		{
			flag2 = (double)candidates.Sum((OcrLineCandidate item) => item.LatinCount) > (double)candidates.Sum((OcrLineCandidate item) => item.CjkCount) * 1.35;
		}
		bool preferLatin = flag2;
		return (from @group in list
			orderby @group.Min((OcrLineCandidate item) => item.Top)
			select SelectCandidate(@group, preferLatin) into item
			orderby item.Top, item.Left
			select new OcrTextLine(Regex.Replace(item.Text, "(?<=[\\u3400-\\u9fff])\\s+(?=[\\u3400-\\u9fff])", string.Empty), item.Left, item.Top, Math.Max(1.0, item.Right - item.Left), Math.Max(1.0, item.Bottom - item.Top))).ToArray();
	}

	private static OcrLineCandidate SelectCandidate(IReadOnlyList<OcrLineCandidate> group, bool preferLatin)
	{
		// 同一行常常有多个变体/引擎给出的结果：先按「出现次数」投票，
		// 否则只要多认出一个偏旁（湖泊 → 湖氵日）字符数就更多，反而会赢。
		OcrLineCandidate? voted = (from item in @group
			group item by item.Text into same
			orderby same.Count() descending, same.Max(candidate => CandidateQuality(candidate, preferLatin)) descending
			select same.OrderByDescending(candidate => CandidateQuality(candidate, preferLatin)).First()).FirstOrDefault();
		int longestLength = @group.Max(item => item.Text.Length);
		bool hasAgreement = voted is not null
			&& @group.Count(item => string.Equals(item.Text, voted.Text, StringComparison.Ordinal)) > 1
			&& @group.Select(item => item.Text).Distinct(StringComparer.Ordinal).Count() > 1
			&& voted.Text.Length >= longestLength * 0.7;
		if (hasAgreement && voted is not null)
		{
			return voted;
		}

		if (preferLatin && group.Any((OcrLineCandidate item) => item.LatinCount > 0))
		{
			OcrLineCandidate ocrLineCandidate = (from item in @group
				where item.LatinCount > 0
				orderby CandidateQuality(item, preferLatin: true) descending, item.TesseractEngine descending, item.LatinEngine descending, item.VariantIndex descending
				select item).First();
			OcrLineCandidate? ocrLineCandidate2 = (from item in @group
				where item.CjkCount >= 2 && item.CjkCount >= item.LatinCount
				orderby CandidateQuality(item, preferLatin: false) descending
				select item).FirstOrDefault();
			if (ocrLineCandidate2 is not null && ocrLineCandidate2.CjkCount * 3 >= ocrLineCandidate.LatinCount * 2 && CandidateQuality(ocrLineCandidate2, preferLatin: false) >= CandidateQuality(ocrLineCandidate, preferLatin: true))
			{
				return ocrLineCandidate2;
			}

			// 中英混排：英文引擎会把汉字拆成一串字母（识别 → iRBlJ），
			// 中文引擎一般连英文一起认全，所以只要两者覆盖同一区域、中文引擎结果还更长，就用中文引擎的。
			OcrLineCandidate? mixedCjk = (from item in @group
				where item.CjkCount >= 2 && !item.LatinEngine
				orderby CandidateQuality(item, preferLatin: false) descending
				select item).FirstOrDefault();
			if (mixedCjk is not null)
			{
				double latinWidth = Math.Max(1.0, ocrLineCandidate.Right - ocrLineCandidate.Left);
				double cjkWidth = Math.Max(1.0, mixedCjk.Right - mixedCjk.Left);
				bool sameRegion = Math.Abs(latinWidth - cjkWidth) <= Math.Max(10.0, latinWidth * 0.35);
				if (sameRegion && mixedCjk.Text.Length >= ocrLineCandidate.Text.Length * 0.8)
				{
					return mixedCjk;
				}
			}

			return ocrLineCandidate;
		}
		if (!preferLatin && group.Any((OcrLineCandidate item) => item.CjkCount > 0))
		{
			return (from item in @group
				orderby item.CjkCount - item.LatinCount descending, item.LatinEngine, item.Score descending, item.VariantIndex descending
				select item).First();
		}
		return (from item in @group
			orderby CandidateQuality(item, preferLatin: false) descending, item.VariantIndex descending
			select item).First();
	}

	private static double CandidateQuality(OcrLineCandidate item, bool preferLatin)
	{
		int glyphCount = item.CjkCount + item.LatinCount + item.Text.Count(char.IsDigit);
		double width = Math.Max(1.0, item.Right - item.Left);
		double densityBonus = Math.Min(40.0, glyphCount / width * 300.0);
		int scriptScore = preferLatin ? item.Score - item.CjkCount * 12 : item.Score;
		return scriptScore + item.Confidence * 0.45 + densityBonus;
	}

	private static bool IsDuplicateRegion(OcrLineCandidate first, OcrLineCandidate second)
	{
		return AreDuplicateOcrRegions(first.Left, first.Top, first.Right, first.Bottom, second.Left, second.Top, second.Right, second.Bottom);
	}

	internal static bool AreDuplicateOcrRegions(double firstLeft, double firstTop, double firstRight, double firstBottom, double secondLeft, double secondTop, double secondRight, double secondBottom)
	{
		double num = Math.Max(0.0, Math.Min(firstRight, secondRight) - Math.Max(firstLeft, secondLeft));
		double num2 = Math.Max(0.0, Math.Min(firstBottom, secondBottom) - Math.Max(firstTop, secondTop));
		double val = Math.Max(1.0, firstRight - firstLeft);
		double val2 = Math.Max(1.0, secondRight - secondLeft);
		double val3 = Math.Max(1.0, firstBottom - firstTop);
		double val4 = Math.Max(1.0, secondBottom - secondTop);
		double num3 = (firstTop + firstBottom) / 2.0;
		double num4 = (secondTop + secondBottom) / 2.0;
		if (Math.Abs(num3 - num4) <= Math.Max(4.0, Math.Min(val3, val4) * 0.6) && num / Math.Min(val, val2) >= 0.8)
		{
			return num2 / Math.Min(val3, val4) >= 0.65;
		}
		return false;
	}

	internal static string DetectLanguage(string text)
	{
		int num = text.Count(delegate(char ch)
		{
			switch (ch)
			{
			case 'A':
			case 'B':
			case 'C':
			case 'D':
			case 'E':
			case 'F':
			case 'G':
			case 'H':
			case 'I':
			case 'J':
			case 'K':
			case 'L':
			case 'M':
			case 'N':
			case 'O':
			case 'P':
			case 'Q':
			case 'R':
			case 'S':
			case 'T':
			case 'U':
			case 'V':
			case 'W':
			case 'X':
			case 'Y':
			case 'Z':
			case 'a':
			case 'b':
			case 'c':
			case 'd':
			case 'e':
			case 'f':
			case 'g':
			case 'h':
			case 'i':
			case 'j':
			case 'k':
			case 'l':
			case 'm':
			case 'n':
			case 'o':
			case 'p':
			case 'q':
			case 'r':
			case 's':
			case 't':
			case 'u':
			case 'v':
			case 'w':
			case 'x':
			case 'y':
			case 'z':
				return true;
			default:
				return false;
			}
		});
		int num2 = text.Count((char ch) => ch >= '㐀' && ch <= '鿿');
		int num3 = text.Count((char ch) => ch >= '\u3040' && ch <= 'ヿ');
		int num4 = text.Count((char ch) => ch >= '가' && ch <= '\ud7af');
		int num5 = text.Count((char ch) => ch >= 'Ѐ' && ch <= 'ӿ');
		if (num3 > 0 && num3 * 2 + num2 > Math.Max(num, Math.Max(num4, num5)))
		{
			return "ja";
		}
		if (num4 > Math.Max(num, Math.Max(num2, num5)))
		{
			return "ko";
		}
		if (num5 > Math.Max(num, Math.Max(num2, num4)))
		{
			return "ru";
		}
		if (num2 <= num)
		{
			return "en";
		}
		return "zh";
	}
}
