using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Web.WebView2.Core;
using Mofumofu.Launcher.Web;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 统一的登录浏览窗口：竖版、标题栏与主窗口一致、不可缩放，底部只有后退 / 前进。
/// Windows 上用 WebView2 内嵌登录页（避免 Edge 实例提权冲突），其它平台返回 null 由调用方回退到系统浏览器。
/// </summary>
public partial class LoginBrowserWindow : Window
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShow = 5;

    private readonly string _url;
    private readonly Action? _cancel;
    private readonly Action? _fallback;

    private nint _container;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _closed;
    private bool _suppressCancel;

    private LoginBrowserWindow(string url, Action? cancel, Action? fallback)
    {
        _url = url;
        _cancel = cancel;
        _fallback = fallback;

        InitializeComponent();

        Title = L.T("Account.Login.DialogTitle", "Microsoft 登录");
        Opened += async (_, _) => await AttachAsync();
        SizeChanged += (_, _) => UpdateBounds();
        Closed += (_, _) =>
        {
            _closed = true;
            if (!_suppressCancel) _cancel?.Invoke();
        };
    }

    /// <summary>创建并显示登录窗口；非 Windows 返回 null，由调用方回退到系统浏览器。</summary>
    public static LoginBrowserWindow? Show(string url, Action? cancel = null, Action? fallback = null)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var window = new LoginBrowserWindow(url, cancel, fallback);
        window.Show();
        return window;
    }

    /// <summary>登录流程结束（成功或取消）后关闭窗口，不再触发取消回调。</summary>
    public void CloseSilently()
    {
        _suppressCancel = true;
        if (!_closed) Close();
    }

    private async Task AttachAsync()
    {
        try
        {
            // 窗口刚 Show 出来时平台句柄不一定已经建好，等一会儿再嵌，
            // 否则 CreateWindowEx 拿到的 parent 是 0，登录窗口会静默退回系统浏览器。
            nint parent = 0;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                parent = TryGetPlatformHandle()?.Handle ?? 0;
                if (parent != nint.Zero) break;

                Log($"waiting for window handle (attempt {attempt + 1})");
                await Task.Delay(120);
                if (_closed) return;
            }

            if (parent == nint.Zero)
            {
                Log("no window handle, giving up");
                Fail();
                return;
            }

            _container = CreateWindowEx(
                0, "STATIC", null,
                WsChild | WsVisible | WsClipSiblings | WsClipChildren,
                0, 0, 10, 10, parent, nint.Zero, GetModuleHandle(null), nint.Zero);
            if (_container == nint.Zero)
            {
                Log($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                Fail();
                return;
            }

            var environment = await WebViewAssets.CreateEnvironmentAsync();
            if (_closed) return;

            var controller = await environment.CreateCoreWebView2ControllerAsync(_container);
            if (_closed)
            {
                controller.Close();
                return;
            }

            _controller = controller;
            // 登录页都是浅色，避免加载前闪一块深色底。
            controller.DefaultBackgroundColor = System.Drawing.Color.White;

            var core = controller.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // 登录页里新开窗口的链接（隐私声明等）留在当前窗口，避免弹出没有导航栏的子窗口。
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };
            core.NavigationCompleted += (_, _) => UpdateNavigationState();
            core.SourceChanged += (_, _) => UpdateNavigationState();

            _core = core;
            core.Navigate(_url);

            LayoutUpdated += (_, _) => UpdateBounds();
            UpdateBounds();
        }
        catch (Exception exception)
        {
            // 内嵌登录失败会静默回退到系统浏览器，留下原因才查得动。
            Log("attach failed: " + exception);
            Fail();
        }
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "mofu-login-browser.log"),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响登录流程
        }
    }

    private void Fail()
    {
        CloseSilently();
        _fallback?.Invoke();
    }

    private void UpdateBounds()
    {
        if (_closed || _container == nint.Zero || _controller is null) return;

        if (WebHost.TranslatePoint(new Point(0, 0), this) is not { } point)
        {
            HideWebContent();
            return;
        }

        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var x = (int)Math.Round(point.X * scale);
        var y = (int)Math.Round(point.Y * scale);
        var width = (int)Math.Round(WebHost.Bounds.Width * scale);
        var height = (int)Math.Round(WebHost.Bounds.Height * scale);
        if (width <= 1 || height <= 1)
        {
            HideWebContent();
            return;
        }

        SetWindowPos(_container, nint.Zero, x, y, width, height, SwpNoActivate | SwpShowWindow);
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        _controller.IsVisible = true;
    }

    private void HideWebContent()
    {
        if (_controller is not null) _controller.IsVisible = false;
        if (_container != nint.Zero) ShowWindow(_container, SwHide);
    }

    private void UpdateNavigationState()
    {
        if (_core is not { } core) return;

        BackButton.IsEnabled = core.CanGoBack;
        ForwardButton.IsEnabled = core.CanGoForward;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_core is { CanGoBack: true } core) core.GoBack();
    }

    private void OnForwardClick(object? sender, RoutedEventArgs e)
    {
        if (_core is { CanGoForward: true } core) core.GoForward();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;

        if (_controller is { } controller)
        {
            _controller = null;
            _core = null;
            try
            {
                controller.Close();
            }
            catch
            {
                // 关闭时 WebView2 可能已自行释放，忽略。
            }
        }

        if (_container != nint.Zero)
        {
            DestroyWindow(_container);
            _container = nint.Zero;
        }

        base.OnClosed(e);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string? windowName,
        int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint insertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
