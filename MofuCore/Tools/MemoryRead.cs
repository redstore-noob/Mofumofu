using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MofuCore.Tools;

public class MemoryRead
{
    private static readonly object __cpuLock = new();
    private static ulong __lastIdleTime;
    private static ulong __lastTotalTime;
    private static bool __hasCpuSample;

    public static MemorySnapshot Snapshot()
    {
        MemorySnapshot snapshot = new();
        try
        {
            using Process self = Process.GetCurrentProcess();
            snapshot.LauncherMemoryMb = ToMegabytes(self.WorkingSet64);
        }
        catch (Exception e)
        {
            Debug.WriteLine("[Memory] 读取启动器内存失败: " + e.Message);
        }

        foreach (string processName in new[] { "java", "javaw" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (Process process in processes)
            {
                try
                {
                    snapshot.JavaProcessCount++;
                    snapshot.JvmMemoryMb += ToMegabytes(process.WorkingSet64);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return snapshot;
    }

    public static Task<MemorySnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(Snapshot, cancellationToken);
    }

    public static SystemUsage SystemUsageNow()
    {
        SystemUsage usage = new();

        usage.CpuPercent = SampleCpuPercent();

        (long totalBytes, long availableBytes) = ReadPhysicalMemory();
        usage.MemoryTotalBytes = totalBytes;
        availableBytes = Math.Max(0, availableBytes);
        usage.MemoryAvailableBytes = availableBytes;
        usage.MemoryUsedBytes = totalBytes > availableBytes ? totalBytes - availableBytes : 0;
        usage.MemoryTotalGb = totalBytes / 1024d / 1024d / 1024d;
        usage.MemoryUsedGb = usage.MemoryUsedBytes / 1024d / 1024d / 1024d;
        usage.MemoryPercent = totalBytes == 0
            ? 0
            : ClampPercent((double)usage.MemoryUsedBytes / totalBytes * 100d);

        return usage;
    }

    public static Task<SystemUsage> SystemUsageNowAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(SystemUsageNow, cancellationToken);
    }

    public static async Task<double> CpuPercentAsync(TimeSpan? sampleWindow = null, CancellationToken cancellationToken = default)
    {
        TimeSpan window = sampleWindow ?? TimeSpan.FromMilliseconds(200);
        if (window < TimeSpan.FromMilliseconds(50))
        {
            window = TimeSpan.FromMilliseconds(50);
        }

        if (!TryReadCpuTimes(out ulong idleBefore, out ulong totalBefore))
        {
            return 0;
        }

        await Task.Delay(window, cancellationToken).ConfigureAwait(false);

        if (!TryReadCpuTimes(out ulong idleAfter, out ulong totalAfter))
        {
            return 0;
        }

        return ComputeCpuPercent(idleBefore, totalBefore, idleAfter, totalAfter);
    }

    public static DiskUsage GetDiskUsage(string path)
    {
        DiskUsage usage = new() { Path = path };
        try
        {
            string fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return usage;
            }

            DriveInfo drive = new(root);
            if (!drive.IsReady)
            {
                return usage;
            }

            usage.TotalBytes = drive.TotalSize;
            usage.FreeBytes = drive.AvailableFreeSpace;
            usage.UsedPercent = drive.TotalSize == 0
                ? 0
                : ClampPercent((1d - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100d);
        }
        catch (Exception e)
        {
            Debug.WriteLine("[Memory] 读取磁盘占用失败: " + e.Message);
        }

        return usage;
    }

    public static Task<DiskUsage> GetDiskUsageAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetDiskUsage(path), cancellationToken);
    }

    public static string FormatBytes(long bytes)
    {
        double value = Math.Abs((double)bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    private static double SampleCpuPercent()
    {
        if (!TryReadCpuTimes(out ulong idle, out ulong total))
        {
            return 0;
        }

        lock (__cpuLock)
        {
            if (!__hasCpuSample)
            {
                __lastIdleTime = idle;
                __lastTotalTime = total;
                __hasCpuSample = true;
                return 0;
            }

            double percent = ComputeCpuPercent(__lastIdleTime, __lastTotalTime, idle, total);
            __lastIdleTime = idle;
            __lastTotalTime = total;
            return percent;
        }
    }

    private static double ComputeCpuPercent(ulong idleBefore, ulong totalBefore, ulong idleAfter, ulong totalAfter)
    {
        ulong idleDelta = idleAfter > idleBefore ? idleAfter - idleBefore : 0;
        ulong totalDelta = totalAfter > totalBefore ? totalAfter - totalBefore : 0;
        if (totalDelta == 0)
        {
            return 0;
        }

        return ClampPercent((1d - (double)idleDelta / totalDelta) * 100d);
    }

    private static bool TryReadCpuTimes(out ulong idleTime, out ulong totalTime)
    {
        idleTime = 0;
        totalTime = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
                {
                    return false;
                }

                // Windows 的 kernel 时间已包含 idle
                idleTime = idle.ToUInt64();
                totalTime = kernel.ToUInt64() + user.ToUInt64();
                return true;
            }

            return TryReadProcStat(out idleTime, out totalTime);
        }
        catch (Exception e)
        {
            Debug.WriteLine("[Memory] 读取 CPU 时间失败: " + e.Message);
            return false;
        }
    }

    private static bool TryReadProcStat(out ulong idleTime, out ulong totalTime)
    {
        idleTime = 0;
        totalTime = 0;
        const string statPath = "/proc/stat";
        if (!File.Exists(statPath))
        {
            return false;
        }

        string[] lines = File.ReadAllLines(statPath);
        string? cpuLine = lines.FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal));
        if (cpuLine is null)
        {
            return false;
        }

        string[] parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            return false;
        }

        ulong idle = 0;
        ulong total = 0;
        for (int i = 1; i < parts.Length; i++)
        {
            if (!ulong.TryParse(parts[i], out ulong value))
            {
                continue;
            }

            total += value;
            // idle + iowait 计为空闲
            if (i == 4 || i == 5)
            {
                idle += value;
            }
        }

        idleTime = idle;
        totalTime = total;
        return total > 0;
    }

    public static (long TotalBytes, long AvailableBytes) ReadPhysicalMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MEMORYSTATUSEX status = new() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                return GlobalMemoryStatusEx(ref status)
                    ? ((long)status.ullTotalPhys, (long)status.ullAvailPhys)
                    : (0, 0);
            }

            if (TryReadProcMemInfo(out ulong total, out ulong available))
            {
                return ((long)total, (long)available);
            }

            // 其它平台兜底：用 GC 可见的可用内存作为总量，可用量无法取得
            long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return availableMemory > 0 ? (availableMemory, -1) : (0, -1);
        }
        catch (Exception e)
        {
            Debug.WriteLine("[Memory] 读取物理内存失败: " + e.Message);
            return (0, 0);
        }
    }

    private static bool TryReadProcMemInfo(out ulong totalBytes, out ulong availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        const string memInfoPath = "/proc/meminfo";
        if (!File.Exists(memInfoPath))
        {
            return false;
        }

        ulong freeBytes = 0;
        foreach (string line in File.ReadLines(memInfoPath))
        {
            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string valueText = parts[1].Replace("kB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!ulong.TryParse(valueText, out ulong kilobytes))
            {
                continue;
            }

            switch (parts[0])
            {
                case "MemTotal":
                    totalBytes = kilobytes * 1024;
                    break;
                case "MemAvailable":
                    availableBytes = kilobytes * 1024;
                    break;
                case "MemFree":
                    freeBytes = kilobytes * 1024;
                    break;
            }
        }

        if (availableBytes == 0)
        {
            availableBytes = freeBytes;
        }

        return totalBytes > 0;
    }

    private static double ToMegabytes(long bytes)
    {
        return bytes / 1024d / 1024d;
    }

    private static double ClampPercent(double value)
    {
        return value < 0 ? 0 : value > 100 ? 100 : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly ulong ToUInt64()
        {
            return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
}

public class MemorySnapshot
{
    public double LauncherMemoryMb { get; set; }

    public double JvmMemoryMb { get; set; }

    public int JavaProcessCount { get; set; }
}

public class SystemUsage
{
    public double CpuPercent { get; set; }

    public double MemoryPercent { get; set; }

    public double MemoryUsedGb { get; set; }

    public double MemoryTotalGb { get; set; }

    public long MemoryUsedBytes { get; set; }

    public long MemoryAvailableBytes { get; set; }

    public long MemoryTotalBytes { get; set; }
}

public class DiskUsage
{
    public string Path { get; set; } = string.Empty;

    public long TotalBytes { get; set; }

    public long FreeBytes { get; set; }

    public double UsedPercent { get; set; }
}
