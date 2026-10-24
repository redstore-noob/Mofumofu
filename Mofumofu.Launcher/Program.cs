using Avalonia;
using Fluid.Avalonia.Acrylic;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mofumofu.Launcher;

class Program
{
    private const string SingleInstanceMutexName = "Mofumofu.Launcher.SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };

        // 单实例开关来自本地设置（设置页可改，重启后生效）
        LocalSettings.Load();

        Mutex? mutex = null;
        if (LocalSettings.Current.SingleInstance)
        {
            mutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);

            if (!isFirstInstance)
            {
                ActivateExistingWindow();
                mutex.Dispose();
                return;
            }
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>把未处理异常写到 %TEMP%\mofu-crash.log（WinExe 没有控制台）。</summary>
    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mofu-crash.log");
            System.IO.File.AppendAllText(path,
                $"{DateTime.Now:O} [{source}] {exception}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响进程退出
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .UseAcrylicPerformanceDefaults()
            .LogToTrace();

    /// <summary>
    /// 第二实例：把已经在跑的窗口拉回前台后退出，避免同时开两个启动器。
    /// </summary>
    private static void ActivateExistingWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var handle = FindWindow(null, "Mofumofu.Launcher");
            if (handle == nint.Zero)
            {
                handle = FindWindow(null, "Mofumofu Launcher");
            }
            if (handle == nint.Zero) return;

            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[single-instance] activate failed: {ex.Message}");
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
