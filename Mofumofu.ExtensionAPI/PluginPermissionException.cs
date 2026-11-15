namespace Mofumofu.ExtensionAPI;

/// <summary>
/// 插件可声明的权限区（强类型）。数值显式固定，禁止依赖隐式序号；
/// 与 <see cref="PluginPermissionZones"/> 的字符串常量一一对应。
/// </summary>
public enum PluginPermission
{
    /// <summary>弹窗 / 轻提示 / 托盘气泡通知。</summary>
    Notify = 0,
    /// <summary>注册小组件 / 页面 / 主题 / 壁纸 / 托盘菜单等 UI 扩展。</summary>
    Widget = 1,
    /// <summary>发起网络下载 / 安装 / 取消 / 暂停等写操作。</summary>
    Network = 2,
    /// <summary>只读的网络查询与下载完成 / 失败通知。</summary>
    NetworkReadonly = 3,
    /// <summary>插件自身配置的读写。</summary>
    Storage = 4,
    /// <summary>实例 / 游戏状态 / mods 的只读查询与事件订阅。</summary>
    Readonly = 5,
    /// <summary>服务器状态查询。</summary>
    ServerStatus = 6,
    /// <summary>实例内容写操作（启停 / 删除模组）。</summary>
    InstanceWrite = 7,
    /// <summary>启动玩家选中的实例（写操作，宿主按 launch 区鉴权）。</summary>
    Launch = 8,
}

/// <summary>
/// 契约层集中定义的权限区名字符串常量。宿主按这些字符串比对 manifest 的 permissions 并做运行时校验，
/// 插件应引用这里的常量而非自行拼写字符串，避免拼写错误；字面值必须与宿主权限检查完全一致，请勿改动。
/// </summary>
public static class PluginPermissionZones
{
    /// <summary>弹窗 / 轻提示 / 托盘气泡通知。</summary>
    public const string Notify = "notify";
    /// <summary>注册小组件 / 页面 / 主题 / 壁纸 / 托盘菜单等 UI 扩展。</summary>
    public const string Widget = "widget";
    /// <summary>发起网络下载 / 安装 / 取消 / 暂停等写操作。</summary>
    public const string Network = "network";
    /// <summary>只读的网络查询与下载完成 / 失败通知。</summary>
    public const string NetworkReadonly = "networkReadonly";
    /// <summary>插件自身配置的读写。</summary>
    public const string Storage = "storage";
    /// <summary>实例 / 游戏状态 / mods 的只读查询与事件订阅。</summary>
    public const string Readonly = "readonly";
    /// <summary>服务器状态查询。</summary>
    public const string ServerStatus = "serverStatus";
    /// <summary>实例内容写操作（启停 / 删除模组）。</summary>
    public const string InstanceWrite = "instanceWrite";
    /// <summary>启动玩家选中的实例；字面值与宿主 PluginPermissions.Launch 一致，请勿改动。</summary>
    public const string Launch = "launch";

    /// <summary>全部已知权限区名（只读快照，不暴露内部可变数组）。</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        Notify, Widget, Network, NetworkReadonly, Storage, Readonly, ServerStatus, InstanceWrite, Launch,
    });

    /// <summary>是否为宿主已知的权限区名（忽略大小写，null 安全，可用于 fail-closed 校验）。</summary>
    public static bool IsKnown(string? zone) =>
        zone is not null && All.Contains(zone, StringComparer.OrdinalIgnoreCase);

    /// <summary>把强类型权限区映射为宿主使用的字符串常量。</summary>
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

    /// <summary>把字符串区名解析为强类型；未知 / null 返回 null（fail-closed 时按未授权处理）。</summary>
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
    /// 所需的权限区名字符串。取值应为 <see cref="PluginPermissionZones"/> 中定义的常量之一；
    /// 可用 <see cref="PluginPermissionZones.IsKnown"/> 校验，未知区名应按未授权处理（fail-closed）。
    /// 需要强类型判断时请用 <see cref="PermissionKind"/>。
    /// </summary>
    public string Permission { get; }

    /// <summary>权限区的强类型表示；宿主下发未知区名时为 null。</summary>
    public PluginPermission? PermissionKind { get; }

    public PluginPermissionException(string permission, string message)
        : base(message)
    {
        Permission = permission;
        PermissionKind = PluginPermissionZones.Match(permission);
    }

    /// <summary>用强类型权限区构造（推荐，避免裸字符串拼写错误）。</summary>
    public PluginPermissionException(PluginPermission permission, string message)
        : this(PluginPermissionZones.NameOf(permission), message) { }
}
