using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SnapTranslate;

public static class ScreenshotService
{
	private delegate bool EnumWindowsProc(nint handle, nint parameter);

	private struct NativeRect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	public static ScreenSnapshot CaptureCurrentMonitor()
	{
		return Capture(Screen.FromPoint(Cursor.Position).Bounds);
	}

	public static ScreenSnapshot CaptureVirtualScreen()
	{
		return Capture(SystemInformation.VirtualScreen);
	}

	public static BitmapSource CaptureRegion(Rectangle bounds)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0)
		{
			throw new ArgumentOutOfRangeException("bounds", "截图区域必须具有有效尺寸。");
		}
		return CaptureImage(bounds);
	}

	private static ScreenSnapshot Capture(Rectangle bounds)
	{
		return new ScreenSnapshot(CaptureImage(bounds), bounds, EnumerateVisibleWindows(bounds));
	}

	private static BitmapSource CaptureImage(Rectangle bounds)
	{
		using Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
		using (Graphics graphics = Graphics.FromImage(bitmap))
		{
			graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
		}
		return ToBitmapSource(bitmap);
	}

	private static IReadOnlyList<Rectangle> EnumerateVisibleWindows(Rectangle desktopBounds)
	{
		List<Rectangle> windows = new List<Rectangle>();
		uint currentProcess = (uint)Environment.ProcessId;
		EnumWindows(delegate(nint handle, nint _)
		{
			if (!IsWindowVisible(handle) || IsIconic(handle))
			{
				return true;
			}
			GetWindowThreadProcessId(handle, out var processId);
			if (processId == currentProcess)
			{
				return true;
			}
			int value = 0;
			if (DwmGetWindowAttribute(handle, 14, out value, 4) == 0 && value != 0)
			{
				return true;
			}
			if (!GetWindowRect(handle, out var rect))
			{
				return true;
			}
			Rectangle item = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
			item.Intersect(desktopBounds);
			if (item.Width >= 40 && item.Height >= 30)
			{
				windows.Add(item);
			}
			return true;
		}, IntPtr.Zero);
		return windows;
	}

	private static BitmapSource ToBitmapSource(Bitmap bitmap)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		nint hbitmap = bitmap.GetHbitmap();
		try
		{
			BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			((Freezable)bitmapSource).Freeze();
			return bitmapSource;
		}
		finally
		{
			DeleteObject(hbitmap);
		}
	}

	public static byte[] ToPng(BitmapSource image)
	{
		PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
		pngBitmapEncoder.Frames.Add(BitmapFrame.Create(image));
		using MemoryStream memoryStream = new MemoryStream();
		pngBitmapEncoder.Save(memoryStream);
		return memoryStream.ToArray();
	}

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteObject(nint handle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(nint handle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(nint handle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(nint handle, out NativeRect rect);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
