using System.Text;
using System.Text.RegularExpressions;

using MofuCore.Config;
namespace MofuCore.Log;

public class LogWrite
{
    private const long MaximumFileSizeBytes = SizeLimits.LogFileMaximumBytes;

    private static readonly object __writeLock = new();
    private static readonly string __fileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".txt";
    private static string __dataRoot = AppContext.BaseDirectory;

    // 日志写入前统一脱敏：只编译一次，避免逐条重编译；设 MatchTimeout 防病态回溯卡死写日志
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    // 分隔符除原有的 : / = 外，还支持纯空白（--accessToken <token>）与引号形式
    // （"accessToken": "jwt"、--accessToken="jwt"）；值前的开引号并入 sep，闭合引号留在原文。
    private static readonly Regex SecretAssignment = new(
        @"\b(?<key>access[_-]?token|refresh[_-]?token|auth[_-]?session|client[_-]?secret|client[_-]?token|api[_-]?key|password|passwd|pwd|authorization|cookie|secret|token)(?<sep>[""']?(?:\s*[:=]\s*|\s+)[""']?)(?<value>[^\s,;""'}\]&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex SecretScheme = new(
        @"\b(?<key>bearer|basic)(?<sep>\s+[""']?)(?<value>[A-Za-z0-9._~+/=-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    public static string DataRoot
    {
        get => __dataRoot;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                __dataRoot = value.Trim();
            }
        }
    }

    public static string LogDirectory => Path.Combine(__dataRoot, "Mofumofu", "log");

    public static string LogFilePath => Path.Combine(LogDirectory, __fileName);

    private static string DateTimeString()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public static bool AddLog(string type, string message)
    {
        return Write(type, message);
    }

    /// <summary>写入失败或 type 为 ERROR 时调用 onError（可为 null）。</summary>
    public static bool AddLogs(string message, Action? onError, string type = "INFO")
    {
        bool result = Write(type, message);
        if (!result || string.Equals(type, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            onError?.Invoke();
        }

        return result;
    }

    /// <summary>转义换行/控制字符防止伪造日志注入，并脱敏令牌与凭据。</summary>
    private static string SanitizeLogText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            if (c == '\r')
            {
                builder.Append("\\r");
            }
            else if (c == '\n')
            {
                builder.Append("\\n");
            }
            else if (c == '\t')
            {
                builder.Append("\\t");
            }
            else if (char.IsControl(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("x4"));
            }
            else
            {
                builder.Append(c);
            }
        }

        string sanitized = builder.ToString();
        try
        {
            sanitized = SecretScheme.Replace(sanitized, m => m.Groups["key"].Value + m.Groups["sep"].Value + "***");
            return SecretAssignment.Replace(sanitized, m => m.Groups["key"].Value + m.Groups["sep"].Value + "***");
        }
        catch (RegexMatchTimeoutException)
        {
            // 脱敏超时时不写出可能含令牌的原文
            return "[日志脱敏超时，内容已丢弃]";
        }
    }

    /// <summary>文件写入加锁，控制台输出在锁外。</summary>
    public static bool Write(string type, string message)
    {
        string line = $"[{DateTimeString()}][{SanitizeLogText(type)}]{SanitizeLogText(message)}";
        Exception? failure = null;
        try
        {
            string path = LogFilePath;
            lock (__writeLock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfTooLarge(path);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception e)
        {
            failure = e;
        }

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
        }

        if (failure is not null)
        {
            try
            {
                Console.WriteLine("当前日志写入失败:" + failure);
            }
            catch
            {
            }

            return false;
        }

        return true;
    }

    public static void Init()
    {
        if (!Write("INFO", "日志系统初始化成功"))
        {
            try
            {
                Console.WriteLine("日志初始化系统失败");
            }
            catch
            {
            }
        }
    }

    public static string ReadCurrent()
    {
        string path = LogFilePath;
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        }
        catch (Exception e)
        {
            AddLog("ERROR", "读取日志失败: " + e.Message);
            return string.Empty;
        }
    }

    public static bool ExportCurrent(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        try
        {
            string destination = Path.GetFullPath(destinationPath.Trim());
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(destination, ReadCurrent(), Encoding.UTF8);
            return true;
        }
        catch (Exception e)
        {
            AddLog("ERROR", "导出日志失败: " + e.Message);
            return false;
        }
    }

    public static int ClearLogs()
    {
        string directory = LogDirectory;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            lock (__writeLock)
            {
                foreach (string pattern in new[] { "*.log", "*.txt" })
                {
                    foreach (string file in Directory.EnumerateFiles(directory, pattern))
                    {
                        try
                        {
                            File.Delete(file);
                            deleted++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            AddLog("ERROR", "清空日志失败: " + e.Message);
            return -1;
        }

        return deleted;
    }

    public static Task<bool> AddLogAsync(string type, string message, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => AddLog(type, message), cancellationToken);
    }

    public static Task<string> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(ReadCurrent, cancellationToken);
    }

    public static Task<bool> ExportCurrentAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ExportCurrent(destinationPath), cancellationToken);
    }

    public static Task<int> ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(ClearLogs, cancellationToken);
    }

    private static void RotateIfTooLarge(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length < MaximumFileSizeBytes)
            {
                return;
            }

            string archived = Path.Combine(file.DirectoryName ?? LogDirectory,
                Path.GetFileNameWithoutExtension(file.Name) + ".old" + file.Extension);
            File.Delete(archived);
            File.Move(path, archived);
        }
        catch
        {
        }
    }
}
