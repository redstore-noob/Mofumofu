using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Web.WebView2.Core;

namespace Mofumofu.Launcher.Web;

/// <summary>
/// 局部 WebView2 宿主：在主窗口里为某个 Avalonia 控件区域创建子 HWND，
/// 再让 WebView2 挂到子 HWND 上。用于账户页的 3D 皮肤预览等局部场景。
/// </summary>
public sealed class WebRegionHost : IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShow = 5;

    private readonly Control _anchor;
    private readonly string _relativePage;
    private readonly string _previewName;

    private TopLevel? _topLevel;
    private nint _container;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _initialized;
    private bool _ready;
    private bool _attached;
    private bool _suspended;
    private string _pendingScript = string.Empty;

    // 弹窗打开时统一挂起所有 WebView2 子窗口：它们是独立 HWND，永远压在 Avalonia 内容
    // （包括弹窗遮罩）之上，只能整体隐藏，弹窗关闭后再恢复。
    private static readonly List<WebRegionHost> LiveHosts = new();
    private static int _suspendDepth;

    public WebRegionHost(Control anchor, string relativePage, string previewName)
    {
        _anchor = anchor;
        _relativePage = relativePage;
        _previewName = previewName;

        lock (LiveHosts)
        {
            LiveHosts.Add(this);
            _suspended = _suspendDepth > 0;
        }
    }

    /// <summary>临时隐藏所有 WebView2 区域（例如打开弹窗时）。</summary>
    public static void SuspendAll()
    {
        List<WebRegionHost> hosts;
        lock (LiveHosts)
        {
            _suspendDepth++;
            hosts = LiveHosts.ToList();
        }

        foreach (var host in hosts)
        {
            host.SetSuspended(true);
        }
    }

    /// <summary>弹窗关闭后恢复之前隐藏的 WebView2 区域。</summary>
    public static void ResumeAll()
    {
        List<WebRegionHost> hosts;
        lock (LiveHosts)
        {
            if (_suspendDepth > 0) _suspendDepth--;
            if (_suspendDepth > 0) return;

            hosts = LiveHosts.ToList();
        }

        foreach (var host in hosts)
        {
            host.SetSuspended(false);
        }
    }

    private void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (!_initialized) return;

        if (suspended)
        {
            HideContainer();
        }
        else
        {
            ShowContainer();
        }
    }

    /// <summary>页面通过 window.chrome.webview.postMessage 发回的字符串消息。</summary>
    public event Action<string>? MessageReceived;

    public async System.Threading.Tasks.Task AttachAsync()
    {
        if (_attached) return;
        _attached = true;

        try
        {
            await AttachCoreAsync();
        }
        catch (Exception ex)
        {
            Log($"attach failed: {ex}");
            _attached = false;
        }
    }

    private async System.Threading.Tasks.Task AttachCoreAsync()
    {
        if (!_initialized)
        {
            _topLevel = FindTopLevel();
            var platformHandle = _topLevel?.TryGetPlatformHandle();

            // 页面刚挂进可视树时窗口句柄可能还没准备好，短暂重试几次
            for (var attempt = 0;
                 attempt < 10 && (platformHandle is null || platformHandle.Handle == nint.Zero);
                 attempt++)
            {
                Log($"waiting for window handle (attempt {attempt + 1})");
                await System.Threading.Tasks.Task.Delay(120);
                _topLevel = FindTopLevel();
                platformHandle = _topLevel?.TryGetPlatformHandle();
            }

            if (platformHandle?.Handle is not { } parent || parent == nint.Zero)
            {
                Log($"no top level / parent handle: topLevel={_topLevel?.GetType().Name ?? "null"}");
                _attached = false;
                return;
            }

            _container = CreateWindowEx(
                0, "STATIC", null,
                WsChild | WsVisible | WsClipSiblings | WsClipChildren,
                0, 0, 10, 10, parent, nint.Zero, GetModuleHandle(null), nint.Zero);

            if (_container == nint.Zero)
            {
                Log($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                _attached = false;
                return;
            }

            Log($"creating environment, parent={parent}, container={_container}");
            var environment = await WebViewAssets.CreateEnvironmentAsync();
            _controller = await environment.CreateCoreWebView2ControllerAsync(_container);
            // 皮肤预览区域必须有自己画的不透明深色底：
            // WebView2 是独立子 HWND，透明像素会直接透出桌面，而不是 Avalonia 面板底色。
            _controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 13, 19, 28);
            _core = _controller.CoreWebView2;
            WebViewAssets.ConfigureCore(_core, _previewName);

            _core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    MessageReceived?.Invoke(e.TryGetWebMessageAsString());
                }
                catch
                {
                    // 消息不是字符串（例如 JSON 对象）时忽略
                }
            };

            _core.NavigationCompleted += (_, args) =>
            {
                Log($"navigation completed success={args.IsSuccess} status={args.WebErrorStatus}");
                if (!args.IsSuccess) return;
                _ready = true;
                if (!string.IsNullOrWhiteSpace(_pendingScript))
                {
                    _ = _core.ExecuteScriptAsync(_pendingScript);
                }
            };

            if (_core is { } core)
            {
                core.Navigate(BuildPageUrl());
            }

            _topLevel.LayoutUpdated += OnLayoutUpdated;
            _topLevel.SizeChanged += OnSizeChanged;
            _initialized = true;
            Log($"initialized, navigating {BuildPageUrl()}");
        }

        ShowContainer();
        UpdateBounds();
    }

    private string BuildPageUrl()
    {
        var url = WebViewAssets.Url(_relativePage);
        var previewDir = Environment.GetEnvironmentVariable("MOFU_WEBVIEW_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(previewDir))
        {
            url += (url.Contains('?') ? "&" : "?") + "preview=1";
        }

        return url;
    }

    private TopLevel? FindTopLevel() =>
        _anchor.GetVisualAncestors().OfType<TopLevel>().FirstOrDefault()
        ?? TopLevel.GetTopLevel(_anchor);

    public void Detach()
    {
        _attached = false;
        HideContainer();
        if (_topLevel is not null)
        {
            _topLevel.LayoutUpdated -= OnLayoutUpdated;
            _topLevel.SizeChanged -= OnSizeChanged;
        }
    }

    /// <summary>把账号皮肤信息推给页面（页面提供 window.__applySkin）。</summary>
    public void ApplySkin(string name, string variant, bool slim, bool cape)
    {
        var json = JsonSerializer.Serialize(new
        {
            name,
            variant,
            slim,
            cape,
        });

        Execute($"window.__applySkin && window.__applySkin({json});");
    }

    /// <summary>页面是否已加载完成，可以安全执行脚本。</summary>
    public bool IsReady => _ready && _core is not null;

    public string PageUrl => BuildPageUrl();

    /// <summary>在页面里执行一段脚本；页面未就绪时排队，就绪后立刻补执行。</summary>
    public void Execute(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return;

        _pendingScript = script;
        if (_ready && _core is not null)
        {
            _ = _core.ExecuteScriptAsync(script);
        }
    }

    /// <summary>按通道推一条 JSON 消息：页面侧对应 window.__mofuPush(channel, payload)。</summary>
    public void Post(string channel, object payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        var channelJson = JsonSerializer.Serialize(channel);
        Execute($"window.__mofuPush && window.__mofuPush({channelJson}, {payloadJson});");
    }

    /// <summary>页面底色（WebView2 是独立 HWND，透明像素会直接透出桌面）。</summary>
    public void SetBackground(System.Drawing.Color color)
    {
        if (_controller is not null)
        {
            _controller.DefaultBackgroundColor = color;
        }
    }


    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateBounds();

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateBounds();

    private void UpdateBounds()
    {
        if (!_initialized || _container == nint.Zero || _topLevel is null) return;

        if (_suspended || !_attached || !_anchor.IsEffectivelyVisible)
        {
            HideContainer();
            return;
        }

        var point = _anchor.TranslatePoint(new Point(0, 0), _topLevel);
        if (point is null)
        {
            HideContainer();
            return;
        }

        var scale = _topLevel.RenderScaling <= 0 ? 1 : _topLevel.RenderScaling;
        var x = (int)Math.Round(point.Value.X * scale);
        var y = (int)Math.Round(point.Value.Y * scale);
        var width = (int)Math.Round(_anchor.Bounds.Width * scale);
        var height = (int)Math.Round(_anchor.Bounds.Height * scale);
        if (width <= 1 || height <= 1)
        {
            HideContainer();
            return;
        }

        // 与最近的 ScrollViewer 视口求交：完全在视口内才显示，滚出视口就隐藏，
        // 避免 WebView 窗口盖到页头 / 其他内容上。
        var scrollViewer = _anchor.FindAncestorOfType<ScrollViewer>();
        if (scrollViewer is not null)
        {
            var viewportPoint = scrollViewer.TranslatePoint(new Point(0, 0), _topLevel);
            if (viewportPoint is { } vp)
            {
                var vx = (int)Math.Round(vp.X * scale);
                var vy = (int)Math.Round(vp.Y * scale);
                var vw = (int)Math.Round(scrollViewer.Bounds.Width * scale);
                var vh = (int)Math.Round(scrollViewer.Bounds.Height * scale);

                if (x < vx || y < vy || x + width > vx + vw + 1 || y + height > vy + vh + 1)
                {
                    HideContainer();
                    return;
                }
            }
        }

        _controller!.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        SetWindowPos(_container, nint.Zero, x, y, width, height,
            SwpNoActivate | SwpShowWindow);
        _controller.IsVisible = true;
    }

    private void ShowContainer()
    {
        if (_suspended || _container == nint.Zero) return;
        ShowWindow(_container, SwShow);
        if (_controller is not null) _controller.IsVisible = true;
        UpdateBounds();
    }

    private void HideContainer()
    {
        if (_controller is not null) _controller.IsVisible = false;
        if (_container != nint.Zero) ShowWindow(_container, SwHide);
    }

    public void Dispose()
    {
        lock (LiveHosts)
        {
            LiveHosts.Remove(this);
        }

        Detach();
        _controller?.Close();
        _controller = null;
        _core = null;

        if (_container != nint.Zero)
        {
            DestroyWindow(_container);
            _container = nint.Zero;
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

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "mofu-webview.log"),
                $"{DateTime.Now:O} [region] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响运行
        }
    }
}
