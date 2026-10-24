using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTranslate;

public static class ClipboardSearchCutService
{
	public static Task<bool> CutAsync(TextBox textBox, Action<string>? reportError = null)
	{
		return CutAsync(textBox, ClipboardService.SetTextAsync, reportError);
	}

	public static async Task<bool> CutAsync(TextBox textBox, Func<string, Task> writeText, Action<string>? reportError = null)
	{
		ArgumentNullException.ThrowIfNull(textBox, "textBox");
		ArgumentNullException.ThrowIfNull(writeText, "writeText");
		if (textBox.IsReadOnly || textBox.SelectionLength <= 0)
		{
			return false;
		}
		string originalText = textBox.Text;
		int selectionStart = textBox.SelectionStart;
		int selectionLength = textBox.SelectionLength;
		string selectedText = textBox.SelectedText;
		textBox.SelectedText = string.Empty;
		string textAfterCut = textBox.Text;
		try
		{
			await writeText(selectedText);
			return true;
		}
		catch (Exception ex)
		{
			if (string.Equals(textBox.Text, textAfterCut, StringComparison.Ordinal))
			{
				textBox.Text = originalText;
				textBox.Select(selectionStart, selectionLength);
			}
			reportError?.Invoke("剪切失败：" + ex.Message);
			return false;
		}
	}
}
