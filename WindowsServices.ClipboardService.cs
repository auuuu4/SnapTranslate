using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SnapTranslate;

public static class ClipboardService
{
	private static readonly SemaphoreSlim WriteGate = new SemaphoreSlim(1, 1);

	private static int _writeInProgress;

	public static bool IsWriteInProgress => Volatile.Read(in _writeInProgress) != 0;

	public static async Task WaitForWriteCompletionAsync()
	{
		await WriteGate.WaitAsync();
		WriteGate.Release();
	}

	public static Task SetTextAsync(string text)
	{
		return ExecuteWriteAsync(() => NativeTextClipboard.WriteAsync(text));
	}

	public static Task SetImageAsync(BitmapSource image)
	{
		return ExecuteWriteAsync(() => NativeImageClipboard.WriteAsync(image));
	}

	internal static async Task ExecuteWriteAsync(Func<Task> write)
	{
		await WriteGate.WaitAsync();
		Interlocked.Exchange(ref _writeInProgress, 1);
		try
		{
			await write();
		}
		finally
		{
			Interlocked.Exchange(ref _writeInProgress, 0);
			WriteGate.Release();
		}
	}
}
