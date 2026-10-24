using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace SnapTranslate;

public static class StartupService
{
	private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	public static void SetEnabled(bool enabled)
	{
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true) ?? Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
		if (enabled)
		{
			string? text = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
			if (!string.IsNullOrWhiteSpace(text))
			{
				registryKey.SetValue("SnapTranslate", "\"" + text + "\"");
			}
		}
		else
		{
			registryKey.DeleteValue("SnapTranslate", throwOnMissingValue: false);
		}
	}
}
