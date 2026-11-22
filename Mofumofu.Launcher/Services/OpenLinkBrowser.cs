using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Mofumofu.Launcher;

public static class OpenLinkBrowser
{
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
                // 启动器以管理员权限运行时，ShellExecute 会让浏览器继承提权，
                // 与已在运行的普通权限浏览器冲突（Edge 会提示“现有实例正在以提升的权限运行”）。
                // 用普通权限的 explorer 转发链接，浏览器就会回到当前用户的权限级别。
                if (IsElevated() &&
                    (UnelevatedLauncher.TryStart("explorer.exe", [url], out _) ||
                     TryRun("explorer.exe", [url], out _)))
                {
                    error = string.Empty;
                    return true;
                }

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

    public static bool TryOpenLogin(string url, out string error)
    {
        // Microsoft 登录用全新的临时 Edge 配置目录，避免复用用户正在使用的实例。
        // 真正要紧的是权限：提权进程直接 Process.Start 出来的 Edge 也带管理员令牌，
        // 新实例和旧实例权限对不上，Edge 就会弹“现有实例正在以提升的权限运行”。
        // 所以提权时先换成 explorer.exe 的普通权限令牌再启动。
        if (OperatingSystem.IsWindows() && FindEdge() is { } edge)
        {
            string profile = Path.Combine(Path.GetTempPath(), "Mofumofu",
                $"EdgeLogin-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(profile);
                string[] args =
                [
                    $"--user-data-dir={profile}",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--inprivate",
                    "--new-window",
                    url,
                ];
                bool started = IsElevated()
                    ? UnelevatedLauncher.TryStart(edge, args, out error)
                    : TryRun(edge, args, out error);
                if (started)
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }

        return TryOpen(url, out error);
    }

    private static string? FindEdge()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string path = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(path)) return path;
        }

        return null;
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

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr token)) return false;

        try
        {
            return GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _) &&
                   elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int informationClass, out uint information,
        int informationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}