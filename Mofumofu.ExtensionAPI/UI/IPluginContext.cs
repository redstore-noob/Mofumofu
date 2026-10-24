using Avalonia.Controls;
using Mofumofu.ExtensionAPI.Actions;
using Mofumofu.ExtensionAPI.Downloads;
using Mofumofu.ExtensionAPI.Game;
using Mofumofu.ExtensionAPI.Java;
using Mofumofu.ExtensionAPI.Tools;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 宿主提供给插件的能力集合（组合根）：插件在 Initialize 里拿到它，
/// 顺着属性就能摸到所有能力域，想开放什么能力就在这里加一个属性。
/// </summary>
public interface IPluginContext
{
    /// <summary>弹窗 / 轻提示</summary>
    IPopup Popup { get; }

    /// <summary>往已有页面注入内容（横幅 / 页脚 / 悬浮层）</summary>
    IPageContentHost Pages { get; }

    /// <summary>注册新的导航页面</summary>
    IPageRegister PageRegister { get; }

    /// <summary>往组件盒添加自定义小组件</summary>
    IWidgetAdd Widget { get; }

    /// <summary>接管启动器壁纸背景</summary>
    IWallpaperEdit Wallpaper { get; }

    /// <summary>加载 axaml 样式覆盖主题</summary>
    IStyleLoad Style { get; }

    /// <summary>游戏启动与实例查询</summary>
    IGameLaunch Game { get; }

    /// <summary>下载并安装实例</summary>
    IInstanceDownload Downloads { get; }

    /// <summary>下载任务的查询 / 操作（按 4 位数字编号）与完成/失败通知</summary>
    IDownloadAction DownloadAction { get; }

    /// <summary>资源（模组 / 光影 / 资源包）搜索与安装到实例</summary>
    IResourceDownload ResourceDownload { get; }

    /// <summary>Java 运行时下载与查询</summary>
    IJavaDownload Java { get; }

    /// <summary>实例内容管理（mods 启停 / 删除）</summary>
    IInstanceAction InstanceAction { get; }

    /// <summary>任务栏右侧图标扩展（点击弹窗或跳页面）</summary>
    ITaskbarTray Taskbar { get; }

    /// <summary>主题读取（主题色 / 明暗模式 + 变化通知）</summary>
    ITheme Theme { get; }

    /// <summary>系统托盘常驻（托盘菜单项 + 气泡通知）</summary>
    ISystemTray SystemTray { get; }

    /// <summary>插件自己的配置存储（每个插件独立 config.yaml）</summary>
    IConfig Config { get; }

    /// <summary>
    /// 写一条宿主日志（不弹窗，不打扰玩家），适合调试信息和详细过程。
    /// </summary>
    void Log(string message);

    /// <summary>
    /// 创建一个启动器内置图标（IconPark 图标，如 "IconHome"、"IconFluentMusic"）。
    /// 图标颜色跟随宿主主题，无需插件处理。
    /// </summary>
    /// <param name="kind">图标名，完整列表见 <see cref="IconKinds"/></param>
    /// <param name="size">图标边长（像素），默认 16</param>
    /// <returns>图标控件；kind 不存在时返回 null，调用方请做好空值兜底</returns>
    Control? CreateIcon(string kind, double size = 16);

    /// <summary>查询某个图标名是否存在，创建前可先校验。</summary>
    bool HasIcon(string kind);

    /// <summary>全部可用图标名。宿主新增图标后会自动出现在列表里。</summary>
    IReadOnlyList<string> IconKinds { get; }
}
