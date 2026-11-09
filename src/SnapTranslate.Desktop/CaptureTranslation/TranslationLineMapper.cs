namespace SnapTranslate.Desktop.CaptureTranslation;

/// <summary>
/// 把翻译服务返回的整段译文按原文行数切分，供对照模式逐行交替显示。
/// 优先使用译文里自带的换行；行数对不上时按原文每行字符数占比重切，尽量在空白或句读处断开。
/// </summary>
public static class TranslationLineMapper
{
    /// <summary>按原文行数拆分译文，返回条数始终等于 <paramref name="sourceLines"/> 的条数。</summary>
    public static IReadOnlyList<string> Split(IReadOnlyList<string> sourceLines, string? translation)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        var count = sourceLines.Count;
        if (count == 0) return Array.Empty<string>();

        var normalized = Normalize(translation);
        if (normalized.Length == 0) return Enumerable.Repeat(string.Empty, count).ToArray();

        var provided = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (provided.Length == count) return provided;
        if (count == 1) return [Clean(normalized)];

        var weights = sourceLines
            .Select(line => Math.Max(1, line.Count(character => !char.IsWhiteSpace(character))))
            .ToArray();
        var totalWeight = weights.Sum(weight => (double)weight);
        var result = new string[count];
        var start = 0;
        var consumed = 0d;
        for (var index = 0; index < count - 1; index++)
        {
            consumed += weights[index];
            var ideal = (int)Math.Round(normalized.Length * consumed / totalWeight);
            var minimum = Math.Min(normalized.Length, start + 1);
            var maximum = Math.Max(minimum, normalized.Length - (count - 1 - index));
            var boundary = FindBoundary(normalized, ideal, minimum, maximum);
            result[index] = Clean(normalized[start..boundary]);
            start = boundary;
        }
        result[^1] = Clean(normalized[start..]);
        return result;
    }

    private static int FindBoundary(string text, int ideal, int minimum, int maximum)
    {
        ideal = Math.Clamp(ideal, minimum, maximum);
        var best = -1;
        var bestScore = int.MaxValue;
        for (var position = minimum; position <= maximum && position < text.Length; position++)
        {
            // 不把代理对从中间切开，避免 emoji 之类字符被拆坏。
            if (position > 0 && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]))
                continue;
            var previous = text[position - 1];
            var priority = previous switch
            {
                '\n' => 0,
                _ when IsSentenceBoundary(previous) => 1,
                _ when char.IsWhiteSpace(previous) => 2,
                _ => -1
            };
            if (priority < 0) continue;
            var score = Math.Abs(position - ideal) * 4 + priority * 3;
            if (score >= bestScore) continue;
            best = position;
            bestScore = score;
        }
        return best >= 0 ? best : ideal;
    }

    private static bool IsSentenceBoundary(char character) =>
        character is '.' or '!' or '?' or ';' or ':' or ','
            or '。' or '！' or '？' or '；' or '：' or '，' or '、';

    private static string Clean(string value) =>
        string.Join(
            " ",
            value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();

    private static string Normalize(string? translation) =>
        (translation ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
}
