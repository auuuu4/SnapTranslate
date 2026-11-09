namespace SnapTranslate.Desktop.Platform;

/// <summary>
/// 把 "Ctrl+Alt+F" 这类通用写法解析成 macOS Carbon 的 (修饰键, 虚拟键码)。
/// 纯逻辑、跨平台可测；Ctrl 在 macOS 上按 Command 处理（用户更习惯 Cmd）。
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, ushort KeyCode, string CanonicalText)
{
    public const uint CommandKey = 0x0100;
    public const uint ShiftKey = 0x0200;
    public const uint OptionKey = 0x0800;
    public const uint ControlKey = 0x1000;

    private static readonly Dictionary<string, ushort> VirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 0, ["S"] = 1, ["D"] = 2, ["F"] = 3, ["H"] = 4, ["G"] = 5, ["Z"] = 6, ["X"] = 7,
        ["C"] = 8, ["V"] = 9, ["B"] = 11, ["Q"] = 12, ["W"] = 13, ["E"] = 14, ["R"] = 15, ["Y"] = 16,
        ["T"] = 17, ["1"] = 18, ["2"] = 19, ["3"] = 20, ["4"] = 21, ["6"] = 22, ["5"] = 23, ["9"] = 25,
        ["7"] = 26, ["8"] = 28, ["0"] = 29, ["O"] = 31, ["U"] = 32, ["I"] = 34, ["P"] = 35, ["L"] = 37,
        ["J"] = 38, ["K"] = 40, ["N"] = 45, ["M"] = 46,
        ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118, ["F5"] = 96, ["F6"] = 97,
        ["F7"] = 98, ["F8"] = 100, ["F9"] = 101, ["F10"] = 109, ["F11"] = 103, ["F12"] = 111,
        ["Space"] = 49, ["Return"] = 36, ["Enter"] = 36, ["Tab"] = 48, ["Escape"] = 53, ["Esc"] = 53,
        ["Backspace"] = 51, ["Delete"] = 51, ["Left"] = 123, ["Right"] = 124, ["Down"] = 125, ["Up"] = 126
    };

    public static bool TryParse(string? gesture, out HotkeyGesture result, out string error)
    {
        result = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            error = "快捷键不能为空";
            return false;
        }

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        var keyName = string.Empty;
        var ordered = new List<string>();
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                case "cmd":
                case "command":
                    modifiers |= CommandKey;
                    if (!ordered.Contains("Ctrl")) ordered.Add("Ctrl");
                    break;
                case "shift":
                    modifiers |= ShiftKey;
                    ordered.Add("Shift");
                    break;
                case "alt":
                case "option":
                case "opt":
                    modifiers |= OptionKey;
                    if (!ordered.Contains("Alt")) ordered.Add("Alt");
                    break;
                case "meta":
                case "win":
                    modifiers |= ControlKey;
                    if (!ordered.Contains("Meta")) ordered.Add("Meta");
                    break;
                default:
                    if (keyName.Length > 0)
                    {
                        error = $"快捷键「{gesture}」里有多个主键";
                        return false;
                    }

                    keyName = part;
                    break;
            }
        }

        if (keyName.Length == 0)
        {
            error = $"快捷键「{gesture}」缺少主键";
            return false;
        }

        if (!VirtualKeys.TryGetValue(keyName, out var virtualKey))
        {
            error = $"快捷键「{gesture}」的主键「{keyName}」暂不支持";
            return false;
        }

        if (modifiers == 0)
        {
            error = $"快捷键「{gesture}」需要至少一个修饰键";
            return false;
        }

        var canonical = string.Join("+", ordered.Append(keyName.ToUpperInvariant()));
        result = new HotkeyGesture(modifiers, virtualKey, canonical);
        return true;
    }

    /// <summary>把解析结果还原成展示文本（macOS 上用 Cmd 显示）。</summary>
    public string DisplayText => CanonicalText.Replace("Ctrl", "Cmd", StringComparison.Ordinal);
}
