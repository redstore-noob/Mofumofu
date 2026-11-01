using MofuCore.Launch;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services;

internal static class MemoryPresentation
{
    public static string Describe(MemoryAllocationResult memory)
    {
        string text = L.T("Memory.Preview", "预计堆内存 {0}–{1} MiB · 系统总内存 {2:0.##} GiB · 当前可用 {3:0.##} GiB",
            memory.MinimumMb, memory.MaximumMb, memory.TotalMb / 1024d, System.Math.Max(0, memory.AvailableMb) / 1024d);
        text += "\n" + L.T("Memory.Hint", "自动模式在启动时重新计算，并为系统和 Java 非堆内存预留空间。手动模式保留设定值。");
        if (memory.TotalMb <= 0 || memory.AvailableMb < 0)
            text += "\n" + L.T("Memory.Unknown", "部分系统内存信息不可用，自动分配使用保守估算。");
        if (memory.BelowRecommendation)
            text += "\n" + L.T("Memory.BelowRecommendation", "当前自动上限低于此规模的建议值 {0} MiB，可能内存不足；请关闭其他应用后重新评估，或减少模组。", memory.RecommendedMb);
        if (!memory.Automatic && memory.AvailableMb >= 0 && memory.MaximumMb > memory.AvailableMb)
            text += "\n" + L.T("Memory.Pressure", "设定上限超过当前可用内存，可能导致系统换页或启动失败。");
        return text;
    }
}
