using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace Mofumofu.Launcher.Web;

/// <summary>
/// 局部 WebView2 页面（皮肤预览 / 3D 文字）共用的环境与本地资源服务。
/// 复用之前整窗 WebView 方案：本地虚拟域 + WebResourceRequested 回包，不依赖网络。
/// </summary>
public static class WebViewAssets
{
    public const string VirtualHost = "mofumofu.local";

    /// <summary>开发期的磁盘 WebAssets 目录；存在时优先走磁盘（改网页即时生效），发布环境自动落到嵌入资源。</summary>
    public static string? Root =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "WebAssets"))
            ? Path.Combine(AppContext.BaseDirectory, "WebAssets")
            : null;

    public static string Url(string relativePath) =>
        $"https://{VirtualHost}/{relativePath.TrimStart('/')}";

    /// <summary>创建 WebView2 环境（独立用户数据目录，可选调试端口）。</summary>
    public static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mofumofu.Launcher",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        // 本机可能有全局 WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS（例如统一 9223 调试端口），
        // 这里只在自身进程内摘掉端口，避免和别的 WebView2 应用抢端口。
        var inherited = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
        if (!string.IsNullOrWhiteSpace(inherited))
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                inherited, @"--remote-debugging-port=(\d+|0)", " ").Trim();
            Environment.SetEnvironmentVariable(
                "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", cleaned, EnvironmentVariableTarget.Process);
        }

        var arguments = "--disable-features=CalculateNativeWinOcclusion";
        var debugPort = Environment.GetEnvironmentVariable("MOFU_WEBVIEW_DEBUG_PORT");
        if (!string.IsNullOrWhiteSpace(debugPort))
        {
            arguments += $" --remote-debugging-port={debugPort}";
        }

        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments };
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }

    /// <summary>挂上本地资源服务、通用设置与可选的验收截图捕获。</summary>
    public static void ConfigureCore(CoreWebView2 core, string previewName)
    {
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsWebMessageEnabled = true;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        core.AddWebResourceRequestedFilter(
            $"https://{VirtualHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        AttachPreviewCapture(core, previewName);
    }

    private static void OnWebResourceRequested(
        object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;

        Uri uri;
        try
        {
            uri = new Uri(e.Request.Uri);
        }
        catch
        {
            return;
        }

        if (!uri.Host.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase)) return;

        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (string.IsNullOrEmpty(relative)) relative = "index.html";
        var relativePath = relative.Replace('/', Path.DirectorySeparatorChar);

        // 1) 开发期磁盘文件优先（WebAssets 文件夹存在且含该文件）
        var root = Root;
        if (root is not null)
        {
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                var bytes = File.ReadAllBytes(full);
                var headers = $"Content-Type: {ContentTypeFor(full)}\r\nCache-Control: no-store";
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(bytes, writable: false), 200, "OK", headers);
                return;
            }
        }

        // 2) 嵌入资源（avares://，发布环境）：磁盘目录不存在时从这里取
        var avaresUri = new Uri($"avares://Mofumofu.Launcher/WebAssets/{relative}");
        try
        {
            using var stream = AssetLoader.Open(avaresUri);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;

            var contentType = ContentTypeFor(relative);
            e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(memory.ToArray(), writable: false), 200, "OK",
                $"Content-Type: {contentType}\r\nCache-Control: no-store");
        }
        catch
        {
            e.Response = core.Environment.CreateWebResourceResponse(
                null, 404, "Not Found", "Content-Type: text/plain");
        }
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };

    /// <summary>验收用：页面加载完成后把 WebView2 内容截成 PNG（MOFU_WEBVIEW_PREVIEW_DIR）。</summary>
    private static void AttachPreviewCapture(CoreWebView2 core, string previewName)
    {
        var previewDir = Environment.GetEnvironmentVariable("MOFU_WEBVIEW_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(previewDir)) return;

        core.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess) return;

            try
            {
                // 等页面真正画出一帧（skinview3d / three.js 都要先建 canvas 并渲染），
                // 否则截到的是空白页（0 字节或极小文件）。
                await WaitForRenderedFrameAsync(core);

                Directory.CreateDirectory(previewDir);
                var path = Path.Combine(previewDir, previewName + ".png");

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    await using (var stream = File.Create(path))
                    {
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                    }

                    if (new FileInfo(path).Length > 1024) return;
                    await Task.Delay(500);
                }
            }
            catch
            {
                // 验收截图失败不影响功能
            }
        };
    }

    private static async Task WaitForRenderedFrameAsync(CoreWebView2 core)
    {
        const string probe =
            "(function(){try{return (document.readyState==='complete' && document.querySelector('canvas')!==null)?'1':'0';}catch(e){return '0';}})()";

        for (var attempt = 0; attempt < 32; attempt++)
        {
            await Task.Delay(250);
            try
            {
                var result = await core.ExecuteScriptAsync(probe);
                if (result is not null && result.Contains('1'))
                {
                    await Task.Delay(900);
                    return;
                }
            }
            catch
            {
                // 页面还没就绪，继续等
            }
        }

        await Task.Delay(1500);
    }
}
