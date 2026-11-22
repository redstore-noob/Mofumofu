using Avalonia;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mofumofu.Launcher;

class Program
{
    private const string SingleInstanceMutexName = "Mofumofu.Launcher.SingleInstance";

    public static string[] LaunchArguments { get; private set; } = [];

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        LaunchArguments = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };

        // 单实例开关来自本地设置（设置页可改，重启后生效）
        string? soloVersion = Services.SoloInstallation.Initialize();
        LocalSettings.Load();
        if (soloVersion is not null && string.IsNullOrWhiteSpace(LocalSettings.Current.SelectedInstanceId))
            LocalSettings.Current.SelectedInstanceId = soloVersion;

        Mutex? mutex = null;
        if (LocalSettings.Current.SingleInstance)
        {
            string mutexName = SingleInstanceMutexName;
            if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "solo.json")))
                mutexName += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())));
            mutex = new Mutex(true, mutexName, out var isFirstInstance);

            if (!isFirstInstance)
            {
                // 「重启启动器」时新实例会比旧实例先起来，等旧实例释放单实例锁（最多 10 秒）。
                if (args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase)))
                {
                    mutex.Dispose();
                    isFirstInstance = false;
                    for (int attempt = 0; attempt < 100 && !isFirstInstance; attempt++)
                    {
                        Thread.Sleep(100);
                        mutex = new Mutex(true, mutexName, out isFirstInstance);
                        if (!isFirstInstance) mutex.Dispose();
                    }
                }
            }

            if (!isFirstInstance)
            {
                ActivateExistingWindow();
                mutex?.Dispose();
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
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 优先使用 Windows 默认的 ANGLE/D3D 后端。WGL 的共享纹理路径在部分驱动上
            // 会在 OpenGlControlBase.BeginDraw 中建立不完整的 FBO，早于控件的渲染回调。
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software]
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
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
