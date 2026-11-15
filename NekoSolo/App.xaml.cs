using System.Threading;
using System.Windows;
using NekoSolo.Services;

namespace NekoSolo;

public partial class App : Application
{
    // 进程级单实例：多个安装器并发写同一安装目录会互相覆盖，用命名 Mutex 拦住第二个实例。
    private const string SingleInstanceMutexName = @"Local\NekoSolo.SingleInstance";
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        SoloStrings.Initialize();

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在运行：释放句柄、提示后直接退出，不再触发后续的窗口初始化流程。
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show(SoloStrings.T("App.AlreadyRunning"), SoloStrings.T("App.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 正常退出时主动释放并关闭 Mutex，避免遗弃互斥体影响下一次判定。
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (Exception) { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
