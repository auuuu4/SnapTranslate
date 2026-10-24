using System.Threading;
using System.Windows;

namespace SnapTranslate;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 渲染 / 集成测试宿主用：只加载 App.xaml 里的资源，跳过单实例互斥检查，
    /// 也不自动创建主窗口。正常启动时保持 false。
    /// </summary>
    public static bool HeadlessHost { get; set; }

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (HeadlessHost) return;

        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("UnhandledException", args.ExceptionObject as Exception
                ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "Unknown unhandled exception."));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        _mutex = new Mutex(true, "Local\\SnapTranslate.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
