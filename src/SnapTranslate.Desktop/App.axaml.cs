using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnapTranslate.Desktop.Platform;

namespace SnapTranslate.Desktop;

public partial class App : Application
{
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            PlatformBootstrap.Configure();
            var window = new MainWindow();
            desktop.MainWindow = window;
            // 关掉窗口不退出进程，靠托盘菜单退出（与 Windows 客户端一致）。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _trayIcon = CreateTrayIcon(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static TrayIcon? CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        try
        {
            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SnapTranslate/Assets/AppIcon.png")));
            var menu = new NativeMenu();

            var showItem = new NativeMenuItem("显示主窗口");
            showItem.Click += (_, _) => window.ShowFromBackground();
            menu.Add(showItem);

            var clipboardItem = new NativeMenuItem("剪贴板历史");
            clipboardItem.Click += (_, _) => window.ShowClipboardHistory();
            menu.Add(clipboardItem);

            menu.Add(new NativeMenuItemSeparator());

            var quitItem = new NativeMenuItem("退出 SnapTranslate");
            quitItem.Click += (_, _) =>
            {
                window.AllowRealClose();
                desktop.Shutdown();
            };
            menu.Add(quitItem);

            return new TrayIcon
            {
                Icon = icon,
                ToolTipText = "SnapTranslate",
                Menu = menu,
                IsVisible = true
            };
        }
        catch (Exception)
        {
            // 没有托盘（部分 Linux 桌面）时不影响主窗口使用。
            return null;
        }
    }
}
