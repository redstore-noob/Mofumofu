namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 主题读取：拿宿主当前的主题色 / 明暗模式，主题变化时收到通知。
/// 出 UI 的插件用它配色，玩家换主题后自动跟手重画。
/// </summary>
public interface ITheme
{
    /// <summary>当前主题色（#RRGGBB，如 #2F88FF）。主题切换后重新读取即可拿到新值。</summary>
    string AccentColorHex { get; }

    /// <summary>主题模式："dark" / "light"。</summary>
    string ThemeMode { get; }

    /// <summary>是否浅色主题（等价于 ThemeMode == "light"，顺手属性）。</summary>
    bool IsLight { get; }

    /// <summary>玩家切换主题或主题色时触发（UI 线程回调）；停用/卸载时宿主自动退订。</summary>
    event Action ThemeChanged;
}
