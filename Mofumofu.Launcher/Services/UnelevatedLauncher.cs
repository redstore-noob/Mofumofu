using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mofumofu.Launcher;

/// <summary>
/// 借用 explorer.exe 的令牌启动进程，让子进程回到当前用户的普通权限。
/// 启动器以管理员身份运行时，Process.Start 派生出的浏览器会继承管理员令牌，
/// 与桌面上已经在跑的普通权限浏览器撞车（Edge 会提示"现有实例正在以提升的权限运行"）。
/// 只改配置文件目录或交给 explorer 转发都绕不开这点，必须换令牌启动。
/// </summary>
internal static class UnelevatedLauncher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimaryType = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint SePrivilegeEnabled = 0x00000002;

    public static bool TryStart(string file, IReadOnlyList<string> args, out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Only Windows is supported.";
            return false;
        }

        IntPtr shellProcess = IntPtr.Zero;
        IntPtr shellToken = IntPtr.Zero;
        IntPtr userToken = IntPtr.Zero;
        try
        {
            int? pid = FindShellProcessId();
            if (pid is null)
            {
                error = "No explorer.exe running in current session.";
                return false;
            }

            shellProcess = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid.Value);
            if (shellProcess == IntPtr.Zero)
            {
                error = Describe("OpenProcess", Marshal.GetLastWin32Error());
                return false;
            }

            if (!OpenProcessToken(shellProcess, TokenAssignPrimary | TokenDuplicate | TokenQuery,
                    out shellToken))
            {
                error = Describe("OpenProcessToken", Marshal.GetLastWin32Error());
                return false;
            }

            if (!DuplicateTokenEx(shellToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation,
                    TokenPrimaryType, out userToken))
            {
                error = Describe("DuplicateTokenEx", Marshal.GetLastWin32Error());
                return false;
            }

            string commandLine = BuildCommandLine(file, args);
            // 两段权限在提权进程里默认是禁用状态，CreateProcessAsUser 需要它们。
            EnablePrivilege("SeAssignPrimaryTokenPrivilege");
            EnablePrivilege("SeIncreaseQuotaPrivilege");

            var startup = NewStartupInfo();
            if (CreateProcessAsUser(userToken, file, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, IntPtr.Zero, null, ref startup, out ProcessInformation info))
            {
                CloseHandle(info.hProcess);
                CloseHandle(info.hThread);
                error = string.Empty;
                return true;
            }

            int firstError = Marshal.GetLastWin32Error();

            // CreateProcessAsUser 被策略拦下时退到 CreateProcessWithTokenW（只需 SeImpersonate）。
            startup = NewStartupInfo();
            if (CreateProcessWithTokenW(userToken, 0, file, commandLine, CreateUnicodeEnvironment,
                    IntPtr.Zero, null, ref startup, out info))
            {
                CloseHandle(info.hProcess);
                CloseHandle(info.hThread);
                error = string.Empty;
                return true;
            }

            error = Describe("CreateProcessWithTokenW", Marshal.GetLastWin32Error()) +
                    " / " + Describe("CreateProcessAsUser", firstError);
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            if (shellProcess != IntPtr.Zero) CloseHandle(shellProcess);
        }
    }

    private static int? FindShellProcessId()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (Process process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (process.SessionId != session) continue;

                int id = process.Id;
                return id;
            }
            catch
            {
                // 进程可能刚好退出，继续找下一个。
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static StartupInfo NewStartupInfo() => new()
    {
        cb = Marshal.SizeOf<StartupInfo>(),
        lpDesktop = @"winsta0\default",
    };

    private static string BuildCommandLine(string file, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder(Quote(file));
        foreach (string arg in args)
        {
            builder.Append(' ').Append(Quote(arg));
        }

        return builder.ToString();
    }

    /// <summary>按 CreateProcess 的解析规则加引号，避免带空格的临时目录被拆成多个参数。</summary>
    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.IndexOfAny([' ', '\t', '"']) < 0) return value;

        var builder = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    break;
                case '"':
                    builder.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    break;
                default:
                    builder.Append('\\', backslashes).Append(c);
                    backslashes = 0;
                    break;
            }
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static string Describe(string api, int code) =>
        $"{api} failed (Win32 {code}: {Marshal.GetPInvokeErrorMessage(code)})";

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out PrivilegeLuid luid)) return;

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrivilegeLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public PrivilegeLuid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
        IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName,
        string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags,
        string applicationName, string commandLine, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out PrivilegeLuid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAllPrivileges,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
