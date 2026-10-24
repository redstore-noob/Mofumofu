using System.Globalization;

namespace NekoSolo.Services;

/// <summary>
/// NekoSolo 自带的轻量本地化：只有两种语言、键量很小，不引入主启动器的 Localization 体系。
/// 语言取系统 UI 语言，可用 NEKOSOLO_LANG=zh-CN / en-US 强制。
/// </summary>
public static class SoloStrings
{
    private static bool _english;

    public static bool IsEnglish => _english;

    public static void Initialize()
    {
        string? forced = Environment.GetEnvironmentVariable("NEKOSOLO_LANG");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            _english = forced.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            return;
        }

        _english = !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase);
    }

    public static string T(string key) => (_english ? En : Zh).TryGetValue(key, out string? value) ? value : key;

    public static string F(string key, params object[] args)
    {
        string format = T(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["App.Title"] = "NekoSolo",
        ["App.Subtitle"] = "单整合包启动器",
        ["App.PoweredBy"] = "Powered by Mofumofu Developer",
        ["App.Version"] = "版本 {0}",
        ["Nav.Install"] = "安装",
        ["Nav.About"] = "关于",

        ["Step.Check"] = "检查安装包",
        ["Step.Verify"] = "校验文件完整性",
        ["Step.Extract"] = "释放启动器",
        ["Step.Config"] = "写入启动配置",
        ["Step.Minecraft"] = "补全游戏文件",
        ["Step.Finish"] = "完成安装",

        ["Welcome.Title"] = "准备安装 {0}",
        ["Welcome.Desc"] = "这个安装包会把整合包与启动器释放到你选择的位置，之后双击桌面图标即可开玩。",
        ["Welcome.PackName"] = "整合包",
        ["Welcome.McVersion"] = "Minecraft",
        ["Welcome.Loader"] = "加载器",
        ["Welcome.PackVersion"] = "整合包版本",
        ["Welcome.Author"] = "作者",
        ["Welcome.Unknown"] = "未标注",
        ["Welcome.Target"] = "安装位置",
        ["Welcome.Browse"] = "浏览…",
        ["Welcome.TargetTip"] = "建议放在空间充足的非系统盘；目录为空或不存在时会自动创建。",
        ["Welcome.OptShortcut"] = "创建桌面快捷方式",
        ["Welcome.OptLaunch"] = "安装完成后立即启动",
        ["Welcome.OptPortable"] = "便携模式（配置与存档都留在安装目录）",
        ["Welcome.Size"] = "预计占用 {0}",
        ["Welcome.SizeUnknown"] = "体积未知（未检测到安装载荷）",
        ["Welcome.Contents"] = "安装包内容",
        ["Welcome.ContentsValue"] = "{0} 个文件 · {1}",
        ["Welcome.Installed"] = "该目录已安装过 {0} v{1}，继续安装会覆盖现有文件。",
                ["Welcome.FromRelease"] = "从 GitHub Releases 获取",
        ["Welcome.ReleaseChecking"] = "正在查询 GitHub Releases…",
        ["Welcome.ReleaseNone"] = "该仓库还没有发布 Release，请先到 {0} 查看。",
        ["Welcome.ReleaseNoAsset"] = "Release {0} 中没有找到可安装的 zip 包。",
        ["Welcome.ReleaseFound"] = "发现 {0}：{1}（{2}）",
        ["Welcome.ReleaseDownloading"] = "正在下载 {0}…",
        ["Welcome.ReleaseReady"] = "安装包已就绪：{0}",
        ["Welcome.ReleaseMissing"] = "没有可用的 GitHub Release 安装包，请到 {0} 下载后使用。",
        ["Welcome.ReleaseFailed"] = "GitHub Releases 获取失败：{0}",
["Welcome.Start"] = "开始安装",
        ["Welcome.Cancel"] = "取消",
        ["Welcome.DiskFree"] = "目标盘剩余 {0}",

        ["Progress.Title"] = "正在安装",
        ["Progress.Stage"] = "{0} · {1}",
        ["Progress.Preparing"] = "准备中",
        ["Progress.Cancel"] = "取消安装",
        ["Progress.Cancelling"] = "正在取消…",
        ["Progress.Log"] = "安装日志",
        ["Progress.Speed"] = "{0}/s",
        ["Progress.Elapsed"] = "已用时 {0}",
        ["Progress.Files"] = "{0} 个文件",
        ["Progress.Lines"] = "{0} 行",

        ["Done.Success"] = "安装完成",
        ["Done.SuccessDesc"] = "{0} 已安装到目标目录，可以开始游戏了。",
        ["Done.Failed"] = "安装失败",
        ["Done.FailedDesc"] = "安装没有完成，你可以查看日志后重试。",
        ["Done.Cancelled"] = "已取消安装",
        ["Done.CancelledDesc"] = "安装过程被中断，已释放的文件会保留在目标目录。",
        ["Done.Files"] = "文件数",
        ["Done.Size"] = "写入大小",
        ["Done.Elapsed"] = "总耗时",
        ["Done.Launch"] = "启动游戏",
        ["Done.OpenFolder"] = "打开安装目录",
        ["Done.Retry"] = "重试",
        ["Done.Close"] = "关闭",

        ["About.Title"] = "关于 NekoSolo",
        ["About.Intro"] = "NekoSolo 是 Mofumofu 生态里的单整合包启动器：一个 exe 带一份整合包，安装完直接开玩，不需要先装启动器。",
        ["About.Creator"] = "创作者信息",
        ["About.Format"] = "安装包格式",
        ["About.FormatValue"] = "NekoSolo 安装包 v1（内嵌载荷 + 32 字节尾标）",
        ["About.Back"] = "返回",
        ["About.Copy"] = "复制信息",
        ["About.OpenFolder"] = "打开安装目录",
        ["About.ExportLog"] = "导出日志",
        ["About.ViewConfig"] = "查看 solo.json",
        ["About.Copied"] = "信息已复制到剪贴板",
        ["About.LogSaved"] = "日志已保存：{0}",

        ["Dialog.BrowseTitle"] = "选择安装位置",
        ["Dialog.Error"] = "出错了",
        ["Dialog.CancelConfirm"] = "确定要取消安装吗？",
        ["Dialog.CancelConfirmDesc"] = "已经释放的文件不会自动删除。",
        ["Dialog.Yes"] = "确定取消",
        ["Dialog.No"] = "继续安装",

        ["Error.NoPayload"] = "没有在自身尾部找到 NekoSolo 安装载荷（这个 exe 可能只是空壳）。",
        ["Error.BadPayload"] = "安装载荷校验失败：{0}",
        ["Error.Manifest"] = "安装包清单读取失败：{0}",
        ["Error.Target"] = "请选择一个有效的安装位置。",
        ["Error.NoSpace"] = "目标磁盘空间不足（需要 {0}，剩余 {1}）。",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["App.Title"] = "NekoSolo",
        ["App.Subtitle"] = "Single-modpack launcher",
        ["App.PoweredBy"] = "Powered by Mofumofu Developer",
        ["App.Version"] = "Version {0}",
        ["Nav.Install"] = "Install",
        ["Nav.About"] = "About",

        ["Step.Check"] = "Reading package",
        ["Step.Verify"] = "Verifying integrity",
        ["Step.Extract"] = "Unpacking launcher",
        ["Step.Config"] = "Writing configuration",
        ["Step.Minecraft"] = "Completing game files",
        ["Step.Finish"] = "Finishing",

        ["Welcome.Title"] = "Ready to install {0}",
        ["Welcome.Desc"] = "This package unpacks the modpack and its launcher into the folder you choose. Double-click the shortcut afterwards to play.",
        ["Welcome.PackName"] = "Modpack",
        ["Welcome.McVersion"] = "Minecraft",
        ["Welcome.Loader"] = "Loader",
        ["Welcome.PackVersion"] = "Pack version",
        ["Welcome.Author"] = "Author",
        ["Welcome.Unknown"] = "Not specified",
        ["Welcome.Target"] = "Install location",
        ["Welcome.Browse"] = "Browse…",
        ["Welcome.TargetTip"] = "Pick a drive with free space. The folder is created when missing.",
        ["Welcome.OptShortcut"] = "Create a desktop shortcut",
        ["Welcome.OptLaunch"] = "Launch after installing",
        ["Welcome.OptPortable"] = "Portable mode (configs and saves stay in the install folder)",
        ["Welcome.Size"] = "About {0} required",
        ["Welcome.SizeUnknown"] = "Size unknown (no payload detected)",
        ["Welcome.Contents"] = "Package contents",
        ["Welcome.ContentsValue"] = "{0} files · {1}",
        ["Welcome.Installed"] = "{0} v{1} is already installed here; continuing overwrites existing files.",
                ["Welcome.FromRelease"] = "Get from GitHub Releases",
        ["Welcome.ReleaseChecking"] = "Checking GitHub Releases…",
        ["Welcome.ReleaseNone"] = "No Release published yet. See {0}.",
        ["Welcome.ReleaseNoAsset"] = "Release {0} has no installable zip asset.",
        ["Welcome.ReleaseFound"] = "Found {0}: {1} ({2})",
        ["Welcome.ReleaseDownloading"] = "Downloading {0}…",
        ["Welcome.ReleaseReady"] = "Package ready: {0}",
        ["Welcome.ReleaseMissing"] = "No installable GitHub Release package. Download it from {0}.",
        ["Welcome.ReleaseFailed"] = "GitHub Releases request failed: {0}",
["Welcome.Start"] = "Start installing",
        ["Welcome.Cancel"] = "Cancel",
        ["Welcome.DiskFree"] = "{0} free on target drive",

        ["Progress.Title"] = "Installing",
        ["Progress.Stage"] = "{0} · {1}",
        ["Progress.Preparing"] = "Preparing",
        ["Progress.Cancel"] = "Cancel installation",
        ["Progress.Cancelling"] = "Cancelling…",
        ["Progress.Log"] = "Install log",
        ["Progress.Speed"] = "{0}/s",
        ["Progress.Elapsed"] = "{0} elapsed",
        ["Progress.Files"] = "{0} files",
        ["Progress.Lines"] = "{0} lines",

        ["Done.Success"] = "Installation complete",
        ["Done.SuccessDesc"] = "{0} is installed and ready to play.",
        ["Done.Failed"] = "Installation failed",
        ["Done.FailedDesc"] = "The installation did not finish. Check the log and try again.",
        ["Done.Cancelled"] = "Installation cancelled",
        ["Done.CancelledDesc"] = "The process was interrupted; files already written are kept.",
        ["Done.Files"] = "Files",
        ["Done.Size"] = "Written",
        ["Done.Elapsed"] = "Elapsed",
        ["Done.Launch"] = "Launch game",
        ["Done.OpenFolder"] = "Open install folder",
        ["Done.Retry"] = "Retry",
        ["Done.Close"] = "Close",

        ["About.Title"] = "About NekoSolo",
        ["About.Intro"] = "NekoSolo is the single-modpack launcher of the Mofumofu ecosystem: one exe carries one modpack, install and play — no launcher setup first.",
        ["About.Creator"] = "Creator",
        ["About.Format"] = "Package format",
        ["About.FormatValue"] = "NekoSolo package v1 (embedded payload + 32-byte trailer)",
        ["About.Back"] = "Back",
        ["About.Copy"] = "Copy info",
        ["About.OpenFolder"] = "Open install folder",
        ["About.ExportLog"] = "Export log",
        ["About.ViewConfig"] = "View solo.json",
        ["About.Copied"] = "Info copied to clipboard",
        ["About.LogSaved"] = "Log saved: {0}",

        ["Dialog.BrowseTitle"] = "Choose install location",
        ["Dialog.Error"] = "Something went wrong",
        ["Dialog.CancelConfirm"] = "Cancel the installation?",
        ["Dialog.CancelConfirmDesc"] = "Files already unpacked are not removed.",
        ["Dialog.Yes"] = "Cancel installation",
        ["Dialog.No"] = "Keep installing",

        ["Error.NoPayload"] = "No NekoSolo payload found in this executable (it may be a bare stub).",
        ["Error.BadPayload"] = "Payload verification failed: {0}",
        ["Error.Manifest"] = "Cannot read the package manifest: {0}",
        ["Error.Target"] = "Choose a valid install location.",
        ["Error.NoSpace"] = "Not enough space on the target drive (need {0}, {1} free).",
    };
}
