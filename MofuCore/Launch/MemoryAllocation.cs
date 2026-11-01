using MofuCore.Config;
using MofuCore.Tools;

namespace MofuCore.Launch;

/// <summary>启动和设置预览共用的堆内存策略。所有容量均为 MiB。</summary>
public static class MemoryAllocation
{
    public const int MinimumMb = 1024;
    public const int DefaultMaximumMb = 4096;
    public const int MaximumMb = 1048576;

    public static int NormalizeMaximum(int value) => Math.Clamp(value, MinimumMb, MaximumMb);

    public static MemoryAllocationResult Resolve(GlobalLaunchSettings global, InstanceJavaConfig? instance = null,
        int modCount = 0, bool modded = false)
    {
        var (total, available) = MemoryRead.ReadPhysicalMemory();
        return Calculate(global, instance, total / 1048576, available < 0 ? -1 : available / 1048576, modCount, modded);
    }

    /// <summary>纯计算入口；自动模式预留系统和 JVM 非堆开销，手动模式保留用户选择。</summary>
    public static MemoryAllocationResult Calculate(GlobalLaunchSettings global, InstanceJavaConfig? instance,
        long totalMb, long availableMb, int modCount = 0, bool modded = false)
    {
        bool automatic = instance?.AutomaticMemory ??
            (instance is { MaxMemoryMB: > 0 } ? false : global.AutomaticMemory);
        int recommended = modCount switch
        {
            >= 250 => 12288,
            >= 150 => 10240,
            >= 80 => 8192,
            >= 30 => 6144,
            _ => modded || modCount > 0 ? 5120 : DefaultMaximumMb,
        };
        int maximum;
        if (automatic)
        {
            if (totalMb > 0)
            {
                // 总量约束始终生效；无法读取可用量时仅使用总量约束。
                long budget = Math.Min(totalMb / 2, totalMb - Math.Max(1024, totalMb / 4));
                if (availableMb >= 0)
                    budget = Math.Min(budget, Math.Max(0, availableMb - Math.Clamp(totalMb / 16, 768, 2048)));
                maximum = (int)Math.Clamp(Math.Min(recommended, budget) / 256 * 256, MinimumMb, MaximumMb);
            }
            else
            {
                maximum = Math.Min(recommended, DefaultMaximumMb);
            }
        }
        else
        {
            maximum = NormalizeMaximum(instance is { MaxMemoryMB: > 0 }
                ? instance.MaxMemoryMB : global.MaximumMemoryMb);
        }

        int minimum = automatic ? MinimumMb : Math.Clamp(
            instance is { MinMemoryMB: > 0 } ? instance.MinMemoryMB : global.MinimumMemoryMb,
            MinimumMb, maximum);
        return new(minimum, maximum, automatic, totalMb, availableMb) { RecommendedMb = recommended };
    }

    public static int SliderMaximum(long totalMb, int configuredMb) =>
        NormalizeMaximum((int)Math.Min(MaximumMb, Math.Max(totalMb > 0 ? totalMb / 256 * 256 : 16384, configuredMb)));
}

public sealed record MemoryAllocationResult(int MinimumMb, int MaximumMb, bool Automatic,
    long TotalMb, long AvailableMb)
{
    public int RecommendedMb { get; init; }

    public bool BelowRecommendation => Automatic && MaximumMb < RecommendedMb;
}
