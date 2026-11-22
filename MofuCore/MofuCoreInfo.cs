using System.Reflection;

namespace MofuCore;

public static class MofuCoreInfo
{
    public static string Version { get; } =
        typeof(MofuCoreInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "Mofu_Snapshot";
}
