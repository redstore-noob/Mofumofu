namespace Mofumofu.ExtensionAPI;

/// <summary>
/// 插件调用了未声明或已被玩家关闭权限的能力时，宿主抛出本异常。
/// 插件可在 manifest 声明 permissions 后重试，或引导玩家到 设置→插件 打开对应开关。
/// </summary>
public sealed class PluginPermissionException : Exception
{
    /// <summary>所需的权限区名（如 notify / widget / network）。</summary>
    public string Permission { get; }

    public PluginPermissionException(string permission, string message)
        : base(message)
    {
        Permission = permission;
    }
}
