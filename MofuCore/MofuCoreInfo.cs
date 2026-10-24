using System.Reflection;

namespace MofuCore;

/// <summary>MofuCore 内核版本（取程序集 InformationalVersion）。</summary>
public static class MofuCoreInfo
{
    public static string Version { get; } =
        typeof(MofuCoreInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.1.0";
}
