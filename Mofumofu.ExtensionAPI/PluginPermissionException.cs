namespace Mofumofu.ExtensionAPI;

/// <summary>
/// 插件可声明的权限区（强类型）。数值显式固定，禁止依赖隐式序号；
/// 与 <see cref="PluginPermissionZones"/> 的字符串常量一一对应。
/// </summary>
public enum PluginPermission
{
    Notify = 0,
    Widget = 1,
    Network = 2,
    NetworkReadonly = 3,
    Storage = 4,
    Readonly = 5,
    ServerStatus = 6,
    InstanceWrite = 7,
    Launch = 8,
}

/// <summary>
/// 权限区名字符串常量。宿主按这些字符串比对 manifest 的 permissions 并做运行时校验，
/// 插件应引用这里的常量而非自行拼写；字面值必须与宿主权限检查完全一致，请勿改动。
/// </summary>
public static class PluginPermissionZones
{
    public const string Notify = "notify";
    public const string Widget = "widget";
    public const string Network = "network";
    public const string NetworkReadonly = "networkReadonly";
    public const string Storage = "storage";
    public const string Readonly = "readonly";
    public const string ServerStatus = "serverStatus";
    public const string InstanceWrite = "instanceWrite";
    /// <summary>字面值与宿主 PluginPermissions.Launch 一致，请勿改动。</summary>
    public const string Launch = "launch";

    /// <summary>不暴露内部可变数组。</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        Notify, Widget, Network, NetworkReadonly, Storage, Readonly, ServerStatus, InstanceWrite, Launch,
    });

    /// <summary>忽略大小写，null 安全，可用于 fail-closed 校验。</summary>
    public static bool IsKnown(string? zone) =>
        zone is not null && All.Contains(zone, StringComparer.OrdinalIgnoreCase);

    /// <summary>未在下表中列出的值一律抛异常，因此 null / 未知区名需先经 <see cref="Match"/> 过滤。</summary>
    public static string NameOf(PluginPermission permission) => permission switch
    {
        PluginPermission.Notify => Notify,
        PluginPermission.Widget => Widget,
        PluginPermission.Network => Network,
        PluginPermission.NetworkReadonly => NetworkReadonly,
        PluginPermission.Storage => Storage,
        PluginPermission.Readonly => Readonly,
        PluginPermission.ServerStatus => ServerStatus,
        PluginPermission.InstanceWrite => InstanceWrite,
        PluginPermission.Launch => Launch,
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    /// <summary>未知 / null 返回 null（fail-closed 时按未授权处理）。</summary>
    public static PluginPermission? Match(string? zone)
    {
        foreach (PluginPermission value in Enum.GetValues<PluginPermission>())
            if (string.Equals(NameOf(value), zone, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }
}

/// <summary>
/// 插件调用了未声明或已被玩家关闭权限的能力时，宿主抛出本异常。
/// 插件可在 manifest 声明 permissions 后重试，或引导玩家到 设置→插件 打开对应开关。
/// </summary>
public sealed class PluginPermissionException : Exception
{
    /// <summary>
    /// 所需权限区名；取值应为 <see cref="PluginPermissionZones"/> 中的常量。
    /// 未知区名应按未授权处理（fail-closed），强类型判断用 <see cref="PermissionKind"/>。
    /// </summary>
    public string Permission { get; }

    /// <summary>宿主下发未知区名时为 null。</summary>
    public PluginPermission? PermissionKind { get; }

    public PluginPermissionException(string permission, string message)
        : base(message)
    {
        Permission = permission;
        PermissionKind = PluginPermissionZones.Match(permission);
    }

    /// <summary>推荐用这个重载，避免裸字符串拼写错误。</summary>
    public PluginPermissionException(PluginPermission permission, string message)
        : this(PluginPermissionZones.NameOf(permission), message) { }
}
