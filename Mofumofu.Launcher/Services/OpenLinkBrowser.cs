using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Mofumofu.Launcher;

/// <summary>
/// 用系统原生浏览器打开 http/https 链接：
/// Windows 交给 ShellExecute，macOS 用 open，Linux 依次尝试 $BROWSER / xdg-open / gio / sensible-browser / x-www-browser。
/// </summary>
public static class OpenLinkBrowser
{
    /// <summary>打开链接；失败时通过 <paramref name="error"/> 返回原因。</summary>
    public static bool TryOpen(string url, out string error)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsHttpUrl(url))
        {
            error = "Only http/https links are allowed.";
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                error = string.Empty;
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                return TryRun("open", [url], out error);
            }

            if (OperatingSystem.IsLinux())
            {
                return TryLinux(url, out error);
            }

            error = "Unsupported platform.";
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryLinux(string url, out string error)
    {
        foreach ((string file, string[] args) in EnumerateLinuxLaunchers(url))
        {
            if (!TryRun(file, args, out _)) continue;

            error = string.Empty;
            return true;
        }

        error = "No system browser launcher found (tried $BROWSER, xdg-open, gio, sensible-browser, x-www-browser).";
        return false;
    }

    private static IEnumerable<(string File, string[] Args)> EnumerateLinuxLaunchers(string url)
    {
        string? browser = Environment.GetEnvironmentVariable("BROWSER");
        if (!string.IsNullOrWhiteSpace(browser))
        {
            // $BROWSER 可能是用冒号分隔的候选列表，每项还可能带参数
            foreach (string candidate in browser.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                string[] args = new string[parts.Length];
                parts.AsSpan(1).CopyTo(args);
                args[^1] = url;
                yield return (parts[0], args);
            }
        }

        yield return ("xdg-open", [url]);
        yield return ("gio", ["open", url]);
        yield return ("sensible-browser", [url]);
        yield return ("x-www-browser", [url]);
    }

    private static bool TryRun(string file, string[] args, out string error)
    {
        try
        {
            ProcessStartInfo startInfo = new(file) { UseShellExecute = false };
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            Process.Start(startInfo);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}