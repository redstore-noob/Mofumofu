namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 主题读取：拿宿主当前的主题色 / 明暗模式。
/// </summary>
public interface ITheme
{
    /// <summary>#RRGGBB，如 #2F88FF；主题切换后重新读取即可拿到新值。</summary>
    string AccentColorHex { get; }

    /// <summary>"dark" / "light"。</summary>
    string ThemeMode { get; }

    bool IsLight { get; }

    /// <summary>UI 线程回调；停用/卸载时宿主自动退订。</summary>
    event Action ThemeChanged;
}
