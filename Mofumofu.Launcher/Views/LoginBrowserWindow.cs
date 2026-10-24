using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Web;
using MofuCore.Account;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 正版登录用的内置浏览器窗口：无边框 + 自定义标题栏（可拖动/关闭），内容是一整块 WebView2。
/// 只负责把 Microsoft 授权页显示出来，登录流程本身由 <c>MinecraftTokenHandle.StartBrowserLoginAsync</c> 驱动。
/// </summary>
public sealed class LoginBrowserWindow : Window
{
    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwShow = 5;
    private const int SwHide = 0;

    private readonly Border _anchor;
    private readonly TextBlock _titleText;
    private readonly TextBlock _statusText;
    private readonly TaskCompletionSource<bool> _hostReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private nint _container;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _initialized;
    private bool _initializing;

    public LoginBrowserWindow()
    {
        Title = L.T("Account.Login.WindowTitle", "Microsoft 登录");
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = true;
        CanMinimize = true;
        CanMaximize = false;
        Topmost = false;
        Width = 560;
        Height = 800;
        MinWidth = 420;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeTokens.Brush("NekoSurfaceBrush");

        _titleText = new TextBlock
        {
            Text = Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var closeButton = new Button
        {
            Classes = { "icon" },
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 13,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 36,
            Height = 36,
            Margin = new Thickness(6, 0, 0, 0),
        };
        closeButton.Click += (_, _) => Close();

        var titleBar = new Border
        {
            Height = 44,
            Padding = new Thickness(14, 0, 6, 0),
            Background = ThemeTokens.Brush("NekoTitleBarBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { _titleText, closeButton },
            },
        };
        Grid.SetColumn(closeButton, 1);
        titleBar.PointerPressed += OnTitleBarPressed;

        _anchor = new Border
        {
            Background = ThemeTokens.Brush("NekoCardSoftBrush"),
            ClipToBounds = true,
        };

        _statusText = new TextBlock
        {
            Text = L.T("Account.Login.WindowOpening", "正在打开登录页…"),
            FontSize = 11,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var statusBar = new Border
        {
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            Background = ThemeTokens.Brush("NekoCardSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _statusText,
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children = { titleBar, _anchor, statusBar },
        };
        Grid.SetRow(_anchor, 1);
        Grid.SetRow(statusBar, 2);

        Content = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            Child = layout,
        };

        LayoutUpdated += (_, _) => UpdateWebBounds();
        SizeChanged += (_, _) => UpdateWebBounds();
        Closed += (_, _) =>
        {
            IsClosed = true;
            DisposeHost();
        };
    }

    /// <summary>用户是否已经关闭窗口（登录轮询应据此停止）。</summary>
    public bool IsClosed { get; private set; }

    /// <summary>页面加载完成后状态栏显示的提示语（网易云登录时覆盖）。</summary>
    public string? ReadyStatusText { get; set; }

    /// <summary>
    /// 读取某个站点下所有 Cookie 并拼成请求头（包含 HttpOnly，例如网易云的 MUSIC_U）。
    /// 找不到指定 Cookie 时返回 null。
    /// </summary>
    public async Task<string?> ReadCookieHeaderAsync(
        string url,
        string requiredCookieName,
        CancellationToken cancellationToken = default)
    {
        await EnsureHostAsync(cancellationToken);
        if (_core is null) return null;

        IReadOnlyList<CoreWebView2Cookie> cookies = await _core.CookieManager.GetCookiesAsync(url);
        if (!cookies.Any(cookie => string.Equals(cookie.Name, requiredCookieName, StringComparison.Ordinal)))
        {
            return null;
        }

        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    /// <summary><c>true</c> 表示是登录流程结束后由宿主主动关闭，不是用户关窗。</summary>
    public bool CloseByHost { get; set; }

    /// <summary>WebView2 是否已初始化完成。</summary>
    public bool IsHostReady => _initialized;

    /// <summary>打开登录页；窗口/WebView2 尚未就绪时先等待初始化。</summary>
    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            SetStatus(L.T("Account.Login.WindowLoading", "正在加载登录页…"));
            await EnsureHostAsync(cancellationToken);
            _core?.Navigate(url);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            SetStatus(L.T("Account.Login.WindowError", "内置浏览器启动失败：{0}", error.Message));
            Log($"navigate failed: {error}");
            throw;
        }
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        try
        {
            BeginMoveDrag(e);
        }
        catch (InvalidOperationException)
        {
            // 窗口状态不允许拖动时忽略
        }
    }

    // ---------------- WebView2 宿主 ----------------

    private async Task EnsureHostAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        if (_initializing)
        {
            var started = await _hostReady.Task.WaitAsync(cancellationToken);
            if (!started || !_initialized) throw new InvalidOperationException("WebView2 host unavailable");

            return;
        }

        _initializing = true;
        try
        {
            var parent = nint.Zero;
            for (var attempt = 0; attempt < 40 && parent == nint.Zero; attempt++)
            {
                parent = TryGetPlatformHandle()?.Handle ?? nint.Zero;
                if (parent != nint.Zero) break;
                await Task.Delay(50, cancellationToken);
            }

            if (parent == nint.Zero) throw new InvalidOperationException("window handle is not ready");

            _container = CreateWindowEx(
                0, "STATIC", null, WsChild | WsClipSiblings | WsClipChildren,
                0, 0, 10, 10, parent, nint.Zero, GetModuleHandle(null), nint.Zero);
            if (_container == nint.Zero)
            {
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
            }

            var environment = await WebViewAssets.CreateEnvironmentAsync();
            _controller = await environment.CreateCoreWebView2ControllerAsync(_container);
            _controller.DefaultBackgroundColor = System.Drawing.Color.White;
            _core = _controller.CoreWebView2;
            ConfigureCore(_core);
            HookCoreEvents(_core);

            _initialized = true;
            UpdateWebBounds();
            ShowWindow(_container, SwShow);
            _controller.IsVisible = true;
            _hostReady.TrySetResult(true);
        }
        catch (Exception error)
        {
            _hostReady.TrySetResult(false);
            Log($"host init failed: {error}");
            throw;
        }
    }

    private static void ConfigureCore(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
    }

    private void HookCoreEvents(CoreWebView2 core)
    {
        core.NavigationStarting += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => SetStatus(L.T("Account.Login.WindowLoading", "正在加载登录页…")));
        };

        core.NavigationCompleted += (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateWebBounds();
                SetStatus(args.IsSuccess
                    ? ReadyStatusText ?? L.T("Account.Login.WindowReady", "请在内置浏览器中完成 Microsoft 登录")
                    : L.T("Account.Login.WindowError", "内置浏览器启动失败：{0}", args.WebErrorStatus));
            });
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            var title = core.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) return;

            Dispatcher.UIThread.Post(() =>
            {
                _titleText.Text = title;
                Title = title;
            });
        };

        // Microsoft 登录页偶尔会弹新窗口（账号选择等）：留在本窗口里导航，避免出现无主窗口
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(args.Uri)) core.Navigate(args.Uri);
        };

        core.ProcessFailed += (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
                SetStatus(L.T("Account.Login.WindowError", "内置浏览器启动失败：{0}", args.ProcessFailedKind)));
        };
    }

    private void UpdateWebBounds()
    {
        if (!_initialized || _container == nint.Zero || _controller is null) return;

        var point = _anchor.TranslatePoint(new Point(0, 0), this);
        if (point is null) return;

        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var x = (int)Math.Round(point.Value.X * scale);
        var y = (int)Math.Round(point.Value.Y * scale);
        var width = (int)Math.Round(_anchor.Bounds.Width * scale);
        var height = (int)Math.Round(_anchor.Bounds.Height * scale);

        if (width <= 1 || height <= 1)
        {
            ShowWindow(_container, SwHide);
            _controller.IsVisible = false;

            return;
        }

        _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        SetWindowPos(_container, nint.Zero, x, y, width, height, SwpNoActivate | SwpShowWindow);
        _controller.IsVisible = true;
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
    }

    private void DisposeHost()
    {
        try
        {
            _controller?.Close();
        }
        catch (Exception error)
        {
            Log($"controller close failed: {error.Message}");
        }

        _controller = null;
        _core = null;
        _initialized = false;

        if (_container != nint.Zero)
        {
            DestroyWindow(_container);
            _container = nint.Zero;
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "mofu-login.log"),
                $"{DateTime.Now:O} [login-browser] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志失败不影响登录
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}

/// <summary>
/// 主窗口内的登录处理过程面板：6 个阶段（登录页授权 → Microsoft 令牌 → Xbox Live → XSTS →
/// Minecraft 令牌 → 获取档案）逐项打勾；结束显示账号名 + 皮肤大头，失败显示原因并可重试。
/// </summary>
public sealed class LoginProgressPanel : UserControl
{
    private static readonly (LoginStep Step, string Key, string Fallback)[] Steps =
    {
        (LoginStep.Authorized, "Account.Login.Step.Authorized", "登录页授权"),
        (LoginStep.MicrosoftToken, "Account.Login.Step.MicrosoftToken", "Microsoft 令牌"),
        (LoginStep.XboxLive, "Account.Login.Step.XboxLive", "Xbox Live"),
        (LoginStep.Xsts, "Account.Login.Step.Xsts", "XSTS"),
        (LoginStep.Minecraft, "Account.Login.Step.Minecraft", "Minecraft 令牌"),
        (LoginStep.Done, "Account.Login.Step.Profile", "获取档案"),
    };

    private readonly TextBlock _headline;
    private readonly TextBlock[] _marks = new TextBlock[Steps.Length];
    private readonly TextBlock[] _labels = new TextBlock[Steps.Length];
    private readonly StackPanel _successRow;
    private readonly Border _headFrame;
    private readonly Image _headImage;
    private readonly TextBlock _accountName;
    private readonly Button _cancelButton;
    private readonly Button _retryButton;
    private int _activeIndex = -1;
    private bool _running = true;

    public LoginProgressPanel()
    {
        Width = 380;

        _headline = new TextBlock
        {
            Text = L.T("Account.Login.Waiting", "等待在内置浏览器中完成授权…"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var stepPanel = new StackPanel { Spacing = 7 };
        for (var i = 0; i < Steps.Length; i++)
        {
            _marks[i] = new TextBlock
            {
                Text = "○",
                FontSize = 13,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                Width = 18,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _labels[i] = new TextBlock
            {
                Text = L.T(Steps[i].Key, Steps[i].Fallback),
                FontSize = 12,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            stepPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { _marks[i], _labels[i] },
            });
        }

        _headImage = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            IsVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(_headImage, BitmapInterpolationMode.None);

        _headFrame = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            ClipToBounds = true,
            IsVisible = false,
            Child = _headImage,
        };

        _accountName = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _successRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            IsVisible = false,
            Children = { _headFrame, _accountName },
        };

        _cancelButton = new Button
        {
            Content = L.T("Account.Login.Cancel", "取消登录"),
            Classes = { "ghost" },
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
        };
        _cancelButton.Click += (_, _) => OnCancelClicked();

        _retryButton = new Button
        {
            Content = L.T("Account.Login.Retry", "重试"),
            Classes = { "primary" },
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            IsVisible = false,
        };
        _retryButton.Click += (_, _) => RetryRequested?.Invoke();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _cancelButton, _retryButton },
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _headline,
                stepPanel,
                new Border
                {
                    Height = 1,
                    Background = ThemeTokens.Brush("NekoBorderBrush"),
                },
                _successRow,
                buttonRow,
            },
        };

        SetActive(0);
    }

    /// <summary>失败/取消后点「重试」。</summary>
    public event Action? RetryRequested;

    /// <summary>运行中点「取消登录」：宿主应取消登录任务。</summary>
    public event Action? CancelRequested;

    /// <summary>非运行状态下点关闭：宿主应关闭对话框。</summary>
    public event Action? CloseRequested;

    /// <summary>阶段推进：该阶段及之前的行打勾，下一行置为「进行中」。</summary>
    public void Advance(LoginStep step)
    {
        var index = IndexOf(step);
        if (index < 0) return;

        for (var i = 0; i <= index; i++) SetDone(i);

        if (index + 1 < Steps.Length)
        {
            SetActive(index + 1);
        }
        else
        {
            _activeIndex = -1;
            _headline.Text = L.T("Account.Login.Processing", "正在完成登录…");
        }
    }

    /// <summary>登录成功：全部打勾，显示账号名与皮肤大头。</summary>
    public void ShowSuccess(string accountName, Bitmap? head)
    {
        _running = false;
        _activeIndex = -1;

        for (var i = 0; i < Steps.Length; i++) SetDone(i);

        _headline.Text = L.T("Account.Login.Success", "登录成功：{0}", accountName);
        _headline.Foreground = ThemeTokens.Brush("NekoSuccessBrush");

        _successRow.IsVisible = true;
        _accountName.Text = accountName;

        if (head is not null)
        {
            _headImage.Source = head;
            _headImage.IsVisible = true;
            _headFrame.IsVisible = true;
        }

        _cancelButton.Content = L.T("Common.Close", "关闭");
        _retryButton.IsVisible = false;
    }

    /// <summary>登录失败：当前阶段标红，显示原因并允许重试。</summary>
    public void ShowFailure(string reason)
    {
        _running = false;

        if (_activeIndex >= 0 && _activeIndex < Steps.Length)
        {
            _marks[_activeIndex].Text = "✕";
            _marks[_activeIndex].Foreground = ThemeTokens.Brush("NekoDangerBrush");
            _labels[_activeIndex].Foreground = ThemeTokens.Brush("NekoDangerBrush");
        }

        _headline.Text = L.T("Account.Login.Failed", "登录失败：{0}", reason);
        _headline.Foreground = ThemeTokens.Brush("NekoDangerBrush");

        _cancelButton.Content = L.T("Common.Close", "关闭");
        _retryButton.IsVisible = true;
    }

    /// <summary>登录被取消。</summary>
    public void ShowCancelled()
    {
        _running = false;
        _headline.Text = L.T("Account.Login.Cancelled", "已取消登录");
        _headline.Foreground = ThemeTokens.Brush("NekoMutedBrush");
        _cancelButton.Content = L.T("Common.Close", "关闭");
        _retryButton.IsVisible = false;
    }

    private void OnCancelClicked()
    {
        if (_running)
        {
            CancelRequested?.Invoke();

            return;
        }

        CloseRequested?.Invoke();
    }

    private static int IndexOf(LoginStep step)
    {
        for (var i = 0; i < Steps.Length; i++)
        {
            if (Steps[i].Step == step) return i;
        }

        return -1;
    }

    private void SetDone(int index)
    {
        if (index < 0 || index >= Steps.Length) return;

        _marks[index].Text = "✓";
        _marks[index].Foreground = ThemeTokens.Brush("NekoSuccessBrush");
        _labels[index].Foreground = ThemeTokens.Brush("NekoTextBrush");
    }

    private void SetActive(int index)
    {
        _activeIndex = index;
        if (index < 0 || index >= Steps.Length) return;

        _marks[index].Text = "●";
        _marks[index].Foreground = ThemeTokens.Brush("NekoAccentBrush");
        _labels[index].Foreground = ThemeTokens.Brush("NekoTextBrush");
    }
}

