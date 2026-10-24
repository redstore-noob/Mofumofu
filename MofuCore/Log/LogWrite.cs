using System.Text;

namespace MofuCore.Log;

/// <summary>
/// 写入日志的类
/// </summary>
public class LogWrite
{
    private const long MaximumFileSizeBytes = 8 * 1024 * 1024;

    private static readonly object __writeLock = new();
    private static readonly string __fileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".txt";
    private static string __dataRoot = AppContext.BaseDirectory;

    /// <summary>
    /// 启动器数据根目录，日志写在 {DataRoot}/Mofumofu/log 下。
    /// </summary>
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

    /// <summary>
    /// 日志目录：{DataRoot}/Mofumofu/log。
    /// </summary>
    public static string LogDirectory => Path.Combine(__dataRoot, "Mofumofu", "log");

    /// <summary>
    /// 本次运行的日志文件路径，文件名为启动时的时间。
    /// </summary>
    public static string LogFilePath => Path.Combine(LogDirectory, __fileName);

    /// <summary>
    /// 返回一串当前日志记录时间的字符串
    /// </summary>
    /// <returns>当前日期的字符串</returns>
    private static string DateTimeString()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// 写一条日志到本次运行的日志文件，同时输出到控制台。
    /// </summary>
    /// <returns>本次操作是否成功</returns>
    public static bool AddLog(string type, string message)
    {
        return Write(type, message);
    }

    /// <summary>
    /// 写一条日志；写入失败或 type 为 ERROR 时调用 onError（可为 null）。
    /// </summary>
    public static bool AddLogs(string message, Action? onError, string type = "INFO")
    {
        bool result = Write(type, message);
        if (!result || string.Equals(type, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            onError?.Invoke();
        }

        return result;
    }

    /// <summary>
    /// 写一条日志。文件写入加锁，控制台输出在锁外。
    /// </summary>
    public static bool Write(string type, string message)
    {
        string line = $"[{DateTimeString()}][{type}]{message}";
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
            // 控制台不可用时忽略，不影响日志落盘结果
        }

        if (failure is not null)
        {
            try
            {
                Console.WriteLine("当前日志写入失败:" + failure);
            }
            catch
            {
                // 控制台不可用
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 初始化日志系统，写一条 INFO 日志。
    /// </summary>
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
                // 控制台不可用
            }
        }
    }

    /// <summary>
    /// 读取本次运行日志文件的全部内容；尚未产生日志时返回空串。
    /// </summary>
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

    /// <summary>
    /// 把本次运行的日志导出到目标文件；尚未产生日志时写出空文件。
    /// </summary>
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

    /// <summary>
    /// 清空日志目录下的全部日志文件，返回删除数量；目录不存在返回 0，异常返回 -1。
    /// </summary>
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
                            // 单个文件删不掉不影响其它文件
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

    /// <summary>
    /// 异步写一条日志。
    /// </summary>
    public static Task<bool> AddLogAsync(string type, string message, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => AddLog(type, message), cancellationToken);
    }

    /// <summary>
    /// 异步读取本次运行的日志内容。
    /// </summary>
    public static Task<string> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(ReadCurrent, cancellationToken);
    }

    /// <summary>
    /// 异步导出本次运行的日志。
    /// </summary>
    public static Task<bool> ExportCurrentAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ExportCurrent(destinationPath), cancellationToken);
    }

    /// <summary>
    /// 异步清空日志目录。
    /// </summary>
    public static Task<int> ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(ClearLogs, cancellationToken);
    }

    /// <summary>
    /// 日志文件超过大小上限时归档为 .old 文件（只保留一代），失败时放弃本次轮转。
    /// </summary>
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
            // 轮转失败不中断写入
        }
    }
}
