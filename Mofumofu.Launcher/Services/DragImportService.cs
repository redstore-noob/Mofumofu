using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MofuCore.Config;
using MofuCore.Download.CurseForge;
using MofuCore.Download.Modrinth;
using MofuCore.Server;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services.Music;
using Mofumofu.Launcher.Services.Plugins;
using Mofumofu.Launcher.Services.Wallpaper;
using Mofumofu.Launcher.Views;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services;

/// <summary>拖入文件被识别出来的种类。</summary>
public enum DropImportKind
{
    ModrinthModpack,
    CurseForgeModpack,
    PrismModpack,
    Mod,
    ModBundle,
    ResourcePack,
    ShaderPack,
    World,
    Plugin,
    ServerPack,
    Skin,
    Wallpaper,
    Music,
    InstanceDirectory,
    WorldDirectory,
    ResourcePackDirectory,
    Unknown,
}

/// <summary>导入目标的作用域：新建实例 / 当前实例的内容目录 / 启动器全局。</summary>
public enum DropImportScope
{
    NewInstance,
    InstanceContent,
    Global,
}

/// <summary>一次拖入的单个文件的导入计划（识别结果 + 建议落点）。</summary>
public sealed record DropImportPlan(
    string SourcePath,
    DropImportKind Kind,
    string KindLabel,
    string Folder,
    DropImportScope Scope,
    bool IsDirectory)
{
    public string FileName => Path.GetFileName(SourcePath.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>实例下拉里的一项。</summary>
public sealed record DropInstanceChoice(string Id, string Name);

/// <summary>拖放发生时的上下文：当前页面、默认实例、可选实例列表、弹窗锚点。</summary>
public sealed record DropImportContext(
    string PageId,
    string? InstanceId,
    IReadOnlyList<DropInstanceChoice> Instances,
    Control? Anchor);

/// <summary>
/// 全局拖拽导入：把「文件」翻译成「该去哪儿」，确认后执行。
/// <para>
/// 识别纯靠扩展名 + zip 内的特征文件（modrinth.index.json / manifest.json / level.dat /
/// pack.mcmeta / shaders/ / *.jar …），不猜文件名，也不联网。执行层复用 MofuCore 已有的
/// 整合包安装管线与启动器现有的存储位置，避免再造一套导入逻辑。
/// </para>
/// </summary>
public static class DragImportService
{
    /// <summary>导入完成后通知页面刷新（实例页会重新扫描内容列表）。</summary>
    public static event Action? Imported;

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    private static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma"];

    /// <summary>未识别类型时允许手动指定的落点（与实例相对）。</summary>
    public static readonly (string Folder, string Label)[] OverrideFolders =
    [
        ("mods", "模组 · mods/"),
        ("resourcepacks", "资源包 · resourcepacks/"),
        ("shaderpacks", "光影包 · shaderpacks/"),
        ("saves", "存档 · saves/"),
        ("", "实例根目录"),
    ];

    // ------------------------------------------------------------------
    // 1. 从拖拽数据里取本地路径
    // ------------------------------------------------------------------

    /// <summary>Avalonia 12 的强类型 DataTransfer：取所有能落到本地磁盘的文件/文件夹路径。</summary>
    public static IReadOnlyList<string> ExtractPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        try
        {
            if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return paths;

            foreach (IDataTransferItem item in e.DataTransfer.Items)
            {
                try
                {
                    if (item.TryGetRaw(DataFormat.File) is not IStorageItem storage) continue;
                    string? path = storage.TryGetLocalPath();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (File.Exists(path) || Directory.Exists(path)) paths.Add(path);
                }
                catch
                {
                    // 某些平台在 DragOver 阶段还拿不到真实文件，忽略即可
                }
            }
        }
        catch
        {
            // 拖拽数据尚未就绪
        }

        return paths;
    }

    // ------------------------------------------------------------------
    // 2. 识别
    // ------------------------------------------------------------------

    public static IReadOnlyList<DropImportPlan> Classify(IReadOnlyList<string> paths)
        => paths.Select(ClassifyOne).ToList();

    private static DropImportPlan ClassifyOne(string raw)
    {
        string path = Path.GetFullPath(raw);
        bool directory = Directory.Exists(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (directory) return ClassifyDirectory(path);

        if (extension == ".mrpack")
            return Plan(path, DropImportKind.ModrinthModpack, L.T("Drop.Kind.ModrinthModpack", "Modrinth 整合包"), "", DropImportScope.NewInstance);

        if (extension == ".nekoex")
            return Plan(path, DropImportKind.Plugin, L.T("Drop.Kind.Plugin", "插件包"), "", DropImportScope.Global);

        if (extension == ".mofuser")
            return Plan(path, DropImportKind.ServerPack, L.T("Drop.Kind.ServerPack", "服务器整包"), "", DropImportScope.Global);

        if (extension is ".jar" or ".litemod")
            return Plan(path, DropImportKind.Mod, L.T("Drop.Kind.Mod", "模组"), "mods", DropImportScope.InstanceContent);

        if (extension == ".zip") return InspectZip(path);

        if (ImageExtensions.Contains(extension))
        {
            // 64×64 / 64×32 的 PNG 是标准皮肤贴图，其它图片一律当壁纸
            return SkinTextureService.TryValidateSkinFile(path, out _)
                ? Plan(path, DropImportKind.Skin, L.T("Drop.Kind.Skin", "皮肤"), "", DropImportScope.Global)
                : Plan(path, DropImportKind.Wallpaper, L.T("Drop.Kind.Wallpaper", "壁纸图片"), "", DropImportScope.Global);
        }

        if (AudioExtensions.Contains(extension))
            return Plan(path, DropImportKind.Music, L.T("Drop.Kind.Music", "本地音乐"), "", DropImportScope.Global);

        return Plan(path, DropImportKind.Unknown, L.T("Drop.Kind.Unknown", "未识别文件"), "mods", DropImportScope.InstanceContent);
    }

    private static DropImportPlan ClassifyDirectory(string path)
    {
        string name = Path.GetFileName(path);
        if (File.Exists(Path.Combine(path, "level.dat")))
            return Plan(path, DropImportKind.WorldDirectory, L.T("Drop.Kind.WorldDirectory", "存档文件夹"), "saves", DropImportScope.InstanceContent, true);

        if (File.Exists(Path.Combine(path, "pack.mcmeta")))
            return Plan(path, DropImportKind.ResourcePackDirectory, L.T("Drop.Kind.ResourcePackDirectory", "资源包文件夹"), "resourcepacks",
                DropImportScope.InstanceContent, true);

        // .minecraft / 含 versions 的目录 = 其他启动器的实例根目录
        if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(Path.Combine(path, "versions")) ||
            Directory.Exists(Path.Combine(path, ".minecraft", "versions")))
            return Plan(path, DropImportKind.InstanceDirectory, L.T("Drop.Kind.InstanceDirectory", "实例目录"), "", DropImportScope.NewInstance, true);

        return Plan(path, DropImportKind.Unknown, L.T("Drop.Kind.Directory", "文件夹"), "", DropImportScope.InstanceContent, true);
    }

    private static DropImportPlan InspectZip(string path)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            var names = archive.Entries
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToList();

            bool HasRoot(string file) => names.Any(name =>
                name.Equals(file, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase));
            bool HasRootFolder(string folder) => names.Any(name =>
                name.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
            bool HasShaderSources() => names.Any(name =>
                name.Contains("/shaders/", StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".fsh", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase)));

            if (HasRoot("modrinth.index.json"))
                return Plan(path, DropImportKind.ModrinthModpack, L.T("Drop.Kind.ModrinthModpack", "Modrinth 整合包"), "", DropImportScope.NewInstance);
            if (HasRoot("manifest.json"))
                return Plan(path, DropImportKind.CurseForgeModpack, L.T("Drop.Kind.CurseForgeModpack", "CurseForge 整合包"), "", DropImportScope.NewInstance);
            if (HasRoot("mmc-pack.json") || HasRoot("instance.cfg"))
                return Plan(path, DropImportKind.PrismModpack, L.T("Drop.Kind.PrismModpack", "Prism / MultiMC 实例包"), "", DropImportScope.NewInstance);
            if (HasRoot("server.json"))
                return Plan(path, DropImportKind.ServerPack, L.T("Drop.Kind.ServerPack", "服务器整包"), "", DropImportScope.Global);
            if (HasRoot("info.yaml") || HasRoot("plugin.yaml"))
                return Plan(path, DropImportKind.Plugin, L.T("Drop.Kind.Plugin", "插件包"), "", DropImportScope.Global);
            if (HasRoot("level.dat"))
                return Plan(path, DropImportKind.World, L.T("Drop.Kind.World", "存档"), "saves", DropImportScope.InstanceContent);
            if (HasRootFolder("shaders") || HasShaderSources())
                return Plan(path, DropImportKind.ShaderPack, L.T("Drop.Kind.ShaderPack", "光影包"), "shaderpacks", DropImportScope.InstanceContent);
            if (HasRoot("pack.mcmeta"))
                return Plan(path, DropImportKind.ResourcePack, L.T("Drop.Kind.ResourcePack", "资源包"), "resourcepacks", DropImportScope.InstanceContent);
            if (names.Any(name => name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
                return Plan(path, DropImportKind.ModBundle, L.T("Drop.Kind.ModBundle", "模组合集"), "mods", DropImportScope.InstanceContent);
        }
        catch
        {
            // 加密/损坏的 zip 走「未识别」，让用户在弹窗里自己选落点
        }

        return Plan(path, DropImportKind.Unknown, L.T("Drop.Kind.UnknownZip", "未识别压缩包"), "mods", DropImportScope.InstanceContent);
    }

    private static DropImportPlan Plan(string path, DropImportKind kind, string label,
        string folder, DropImportScope scope, bool directory = false)
        => new(path, kind, label, folder, scope, directory);

    /// <summary>给拖拽浮层用的一句话概览，例如「2 个模组 · 1 个资源包」。</summary>
    public static string Describe(IReadOnlyList<DropImportPlan> plans)
        => string.Join(" · ", plans
            .GroupBy(plan => plan.KindLabel)
            .Select(group => group.Count() == 1
                ? group.Key
                : L.T("Drop.Count", "{0} 个{1}", group.Count(), group.Key)));

    // ------------------------------------------------------------------
    // 3. 确认弹窗
    // ------------------------------------------------------------------

    /// <summary>按识别结果弹确认框；纯整合包直接安装，不打断操作。</summary>
    public static void ConfirmAndImport(IReadOnlyList<DropImportPlan> plans, DropImportContext context)
    {
        if (plans.Count == 0) return;

        // 全是整合包 / 实例目录：直接新建实例，保持原来「拖进去就装」的手感
        if (plans.All(plan => plan.Scope == DropImportScope.NewInstance))
        {
            _ = ExecuteAsync(plans, context, null, null);
            return;
        }

        bool needsInstance = plans.Any(plan =>
            plan.Scope == DropImportScope.InstanceContent || plan.Kind == DropImportKind.Unknown);
        if (needsInstance && context.Instances.Count == 0)
        {
            var globals = plans.Where(plan => plan.Scope == DropImportScope.Global).ToList();
            if (globals.Count > 0) _ = ExecuteAsync(globals, context, null, null);
            Shell.Toast(L.T("Drop.NoInstance", "还没有可用实例，请先在「实例」里创建一个实例"));
            return;
        }

        var panel = MofuDialog.Stack(420, 12);

        string summary = L.T("Drop.Summary", "检测到 {0} 个文件：{1}",
            plans.Count, Describe(plans));
        var folders = plans
            .Where(plan => plan.Scope == DropImportScope.InstanceContent &&
                           !string.IsNullOrEmpty(plan.Folder))
            .Select(plan => plan.Folder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (folders.Length > 0)
        {
            summary += "\n" + L.T("Drop.TargetFolders", "内容目录：{0}", string.Join(" / ", folders));
        }

        panel.Children.Add(MofuDialog.Message(summary));

        string fileList = string.Join("\n", plans.Take(6).Select(plan => "· " + plan.FileName));
        if (plans.Count > 6) fileList += "\n…";
        panel.Children.Add(new TextBlock
        {
            Text = fileList,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 19,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        });

        ComboBox? instanceBox = null;
        if (needsInstance)
        {
            instanceBox = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            int selected = 0;
            for (int i = 0; i < context.Instances.Count; i++)
            {
                var choice = context.Instances[i];
                instanceBox.Items.Add(new ComboBoxItem { Content = choice.Name, Tag = choice.Id });
                if (!string.IsNullOrWhiteSpace(context.InstanceId) &&
                    string.Equals(choice.Id, context.InstanceId, StringComparison.OrdinalIgnoreCase)) selected = i;
            }

            instanceBox.SelectedIndex = selected;
            panel.Children.Add(MofuDialog.Field(
                L.T("Drop.TargetInstance", "安装到实例"), instanceBox));
        }

        ComboBox? folderBox = null;
        if (plans.Any(plan => plan.Kind == DropImportKind.Unknown))
        {
            folderBox = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            foreach (var (folder, label) in OverrideFolders)
            {
                folderBox.Items.Add(new ComboBoxItem { Content = label, Tag = folder });
            }

            // 压缩包/类 jar 文件默认当模组，其它杂项默认放实例根目录
            bool looksLikeMod = plans
                .Where(plan => plan.Kind == DropImportKind.Unknown)
                .Any(plan => Path.GetExtension(plan.SourcePath).ToLowerInvariant() is ".jar" or ".zip" or ".litemod");
            folderBox.SelectedIndex = looksLikeMod ? 0 : OverrideFolders.Length - 1;
            panel.Children.Add(MofuDialog.Field(
                L.T("Drop.UnknownTarget", "未识别的文件放到"), folderBox));
        }

        if (plans.Any(plan => plan.Kind is DropImportKind.ModrinthModpack or DropImportKind.CurseForgeModpack))
        {
            panel.Children.Add(MofuDialog.Notice(
                L.T("Drop.ModpackNotice", "整合包会作为新实例安装到 versions/ 下，安装过程需要下载模组与加载器。"),
                "NekoAccent"));
        }

        var cancel = MofuDialog.Secondary(L.T("Common.Cancel", "取消"),
            () => MofuDialog.Close(panel));
        var confirm = MofuDialog.Primary(L.T("Drop.Confirm", "导入"), () =>
        {
            MofuDialog.Close(panel);

            string? instanceId = null;
            if (instanceBox?.SelectedItem is ComboBoxItem item)
            {
                instanceId = item.Tag as string ?? item.Content?.ToString();
            }

            string? overrideFolder = folderBox?.SelectedItem is ComboBoxItem folderItem
                ? folderItem.Tag as string ?? "mods"
                : null;

            _ = ExecuteAsync(plans, context, instanceId, overrideFolder);
        });
        panel.Children.Add(MofuDialog.Footer(cancel, confirm));

        Shell.Dialog(panel, L.T("Drop.Title", "导入拖入的文件"), context.Anchor);
    }

    // ------------------------------------------------------------------
    // 4. 执行
    // ------------------------------------------------------------------

    private static async Task ExecuteAsync(IReadOnlyList<DropImportPlan> plans,
        DropImportContext context, string? instanceId, string? overrideFolder)
    {
        int done = 0;
        var failures = new List<string>();

        foreach (DropImportPlan plan in plans)
        {
            try
            {
                await ImportOneAsync(plan, instanceId, overrideFolder);
                done++;
            }
            catch (Exception error)
            {
                failures.Add($"{plan.FileName}：{error.Message}");
            }
        }

        try
        {
            Imported?.Invoke();
            LaunchBarRefresh.Request();
        }
        catch
        {
            // 页面刷新失败不影响已经落盘的文件
        }

        if (failures.Count > 0)
        {
            Shell.Toast(L.T("Drop.PartialFailed", "已完成 {0} 个，失败 {1} 个：{2}",
                done, failures.Count, failures[0]));
        }
        else if (done > 0)
        {
            Shell.Toast(L.T("Drop.Done", "已导入 {0} 个文件", done));
        }
    }

    private static async Task ImportOneAsync(DropImportPlan plan, string? instanceId, string? overrideFolder)
    {
        switch (plan.Kind)
        {
            case DropImportKind.ModrinthModpack:
                await InstallModrinthModpackAsync(plan.SourcePath);
                return;
            case DropImportKind.CurseForgeModpack:
                await InstallCurseForgeModpackAsync(plan.SourcePath);
                return;
            case DropImportKind.Plugin:
                await NekoexInstaller.InstallAsync(plan.SourcePath);
                return;
            case DropImportKind.ServerPack:
                await ServerPackage.ImportAsync(plan.SourcePath);
                return;
            case DropImportKind.Skin:
                SaveSkin(plan.SourcePath);
                return;
            case DropImportKind.Wallpaper:
                AddWallpaper(plan.SourcePath);
                return;
            case DropImportKind.Music:
                AddMusic(plan.SourcePath);
                return;
            default:
                // 复制 / 解压都放到线程池，大文件不卡 UI
                await Task.Run(() => ImportFileSync(plan, instanceId, overrideFolder));
                return;
        }
    }

    /// <summary>内容目录：与下载页在线安装保持同一套版本隔离规则。</summary>
    private static string ResolveContentDirectory(string gameDirectory, string? instanceId, string folder)
    {
        string root = gameDirectory;
        if (!string.IsNullOrWhiteSpace(instanceId) && YamlRead.DefaultVersionIsolation)
        {
            root = Path.Combine(gameDirectory, "versions", instanceId);
        }

        string directory = folder.Length == 0 ? root : Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ImportFileSync(DropImportPlan plan, string? instanceId, string? overrideFolder)
    {
        string gameDirectory = YamlRead.EnsureGameDirectory();
        string folder = plan.Kind == DropImportKind.Unknown
            ? overrideFolder ?? "mods"
            : plan.Folder;

        switch (plan.Kind)
        {
            case DropImportKind.InstanceDirectory:
                ImportInstanceDirectory(plan.SourcePath, gameDirectory);
                return;
            case DropImportKind.PrismModpack:
                ImportInstanceArchive(plan.SourcePath, gameDirectory);
                return;
        }

        string targetRoot = ResolveContentDirectory(gameDirectory, instanceId, folder);

        switch (plan.Kind)
        {
            case DropImportKind.World:
                ImportWorldZip(plan.SourcePath, targetRoot);
                break;
            case DropImportKind.ModBundle:
                ExtractJarEntries(plan.SourcePath, targetRoot);
                break;
            case DropImportKind.WorldDirectory:
            case DropImportKind.ResourcePackDirectory:
                CopyDirectory(plan.SourcePath,
                    UniqueDirectory(Path.Combine(targetRoot, SafeName(plan.FileName))));
                break;
            default:
                // Mod / 资源包 / 光影包 / 未识别：MC 原生支持目录里的 zip 与 jar，直接复制
                string target = UniqueFile(Path.Combine(targetRoot, SafeName(plan.FileName)));
                File.Copy(plan.SourcePath, target, overwrite: false);
                break;
        }
    }

    private static void ImportWorldZip(string zipPath, string savesRoot)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "mofumofu-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            ZipFile.ExtractToDirectory(zipPath, temporary, overwriteFiles: false);

            string worldRoot;
            string worldName;
            if (File.Exists(Path.Combine(temporary, "level.dat")))
            {
                worldRoot = temporary;
                worldName = Path.GetFileNameWithoutExtension(zipPath);
            }
            else
            {
                string? candidate = Directory.GetDirectories(temporary)
                    .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "level.dat")));
                if (candidate is null)
                {
                    throw new InvalidOperationException(
                        L.T("Drop.WorldNoLevel", "压缩包里找不到 level.dat"));
                }

                worldRoot = candidate;
                worldName = Path.GetFileName(candidate);
            }

            CopyDirectory(worldRoot, UniqueDirectory(Path.Combine(savesRoot, SafeName(worldName))));
        }
        finally
        {
            try { Directory.Delete(temporary, true); } catch { /* 临时目录清理失败不影响导入 */ }
        }
    }

    private static void ExtractJarEntries(string zipPath, string modsRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase)) continue;

            // 只取文件名，避免压缩包里的 ../ 路径穿越
            string target = UniqueFile(Path.Combine(modsRoot, SafeName(entry.Name)));
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static void ImportInstanceDirectory(string source, string gameDirectory)
    {
        string? versionsRoot = Directory.Exists(Path.Combine(source, "versions"))
            ? Path.Combine(source, "versions")
            : Directory.Exists(Path.Combine(source, ".minecraft", "versions"))
                ? Path.Combine(source, ".minecraft", "versions")
                : null;

        string targetRoot = Path.Combine(gameDirectory, "versions");
        Directory.CreateDirectory(targetRoot);

        if (versionsRoot is not null)
        {
            // 与实例页「导入 .minecraft 目录」一致：只搬带 <id>.json 的版本目录
            foreach (string directory in Directory.GetDirectories(versionsRoot))
            {
                string id = Path.GetFileName(directory);
                if (!File.Exists(Path.Combine(directory, id + ".json"))) continue;
                string destination = Path.Combine(targetRoot, id);
                if (Directory.Exists(destination)) continue;
                CopyDirectory(directory, destination);
            }
        }
        else
        {
            string id = SafeName(Path.GetFileName(source));
            CopyDirectory(source, UniqueDirectory(Path.Combine(targetRoot, id)));
        }
    }

    private static void ImportInstanceArchive(string zipPath, string gameDirectory)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "mofumofu-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            ZipFile.ExtractToDirectory(zipPath, temporary, overwriteFiles: true);

            // Prism/MultiMC 的实例包一般把游戏文件放在 .minecraft/ 里
            string source = Directory.Exists(Path.Combine(temporary, ".minecraft"))
                ? Path.Combine(temporary, ".minecraft")
                : temporary;

            string id = SafeName(Path.GetFileNameWithoutExtension(zipPath));
            CopyDirectory(source, UniqueDirectory(Path.Combine(gameDirectory, "versions", id)));
        }
        finally
        {
            try { Directory.Delete(temporary, true); } catch { /* 忽略 */ }
        }
    }

    private static void SaveSkin(string path)
    {
        string account = GameState.SelectedAccount;
        if (string.IsNullOrWhiteSpace(account)) account = "local";
        SkinTextureService.SaveCustomSkin(path, account);
    }

    private static void AddWallpaper(string path)
    {
        string directory = WallpaperDefaults.WallpaperDirectory;
        Directory.CreateDirectory(directory);
        string target = UniqueFile(Path.Combine(directory, SafeName(Path.GetFileName(path))));
        File.Copy(path, target, overwrite: false);
        WallpaperService.Instance.AddImages([target]);
        if (!WallpaperService.Instance.IsEnabled) WallpaperService.Instance.SetEnabled(true);
    }

    private static void AddMusic(string path)
    {
        string directory = Path.Combine(YamlRead.DataRoot, YamlRead.DataDirectoryName, "music");
        Directory.CreateDirectory(directory);
        File.Copy(path, UniqueFile(Path.Combine(directory, SafeName(Path.GetFileName(path)))), overwrite: false);

        var settings = MusicStore.Load();
        if (!settings.Folders.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            settings.Folders.Add(directory);
            MusicStore.Save(settings);
        }
    }

    // ------------------------------------------------------------------
    // 5. 整合包安装（与下载大厅共用 MofuCore 管线）
    // ------------------------------------------------------------------

    private static async Task InstallModrinthModpackAsync(string packagePath)
    {
        string displayName = SafeName(Path.GetFileNameWithoutExtension(packagePath));
        string gameDirectory = YamlRead.EnsureGameDirectory();
        var task = DownloadTaskService.Start(displayName,
            L.T("Download.Version.Preparing", "准备下载"));

        try
        {
            string instanceDirectory = Path.Combine(gameDirectory, "versions", displayName);
            bool existed = Directory.Exists(instanceDirectory);
            Directory.CreateDirectory(instanceDirectory);
            if (!existed) task.RegisterCleanup(instanceDirectory);

            var progress = new Progress<ModpackInstallProgress>(value => task.Report(
                value.TotalFiles > 0 ? Math.Clamp(value.CompletedFiles * 100.0 / value.TotalFiles, 0, 100) : 0,
                value.Phase));

            await ModrinthBundleInstall.InstallAsync(
                packagePath,
                instanceDirectory,
                new ModpackInstallOptions
                {
                    InstallLoader = true,
                    JavaExecutable = YamlRead.JavaExecutable,
                },
                progress,
                task.Token);

            task.Complete();
            LaunchBarRefresh.Request();
            Shell.Toast(L.T("Download.Resource.InstallDone", "已安装「{0}」", displayName));
        }
        catch (OperationCanceledException)
        {
            task.MarkCancelled();
            Shell.Toast(L.T("Download.Resource.InstallCancelled", "已取消安装「{0}」", displayName));
        }
        catch (Exception error)
        {
            task.Fail(error.Message);
            Shell.Toast(L.T("Download.Resource.InstallFailed", "安装「{0}」失败：{1}", displayName, error.Message));
        }
    }

    private static async Task InstallCurseForgeModpackAsync(string packagePath)
    {
        string apiKey = LocalSettings.Current.CurseForgeApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Shell.Toast(L.T("Download.Modpack.Drop.CurseForgeNoKey",
                "CurseForge 整合包需要 API Key，请先到「设置 → 下载」填写"));
            return;
        }

        string displayName = SafeName(Path.GetFileNameWithoutExtension(packagePath));
        string gameDirectory = YamlRead.EnsureGameDirectory();
        var task = DownloadTaskService.Start(displayName,
            L.T("Download.Version.Preparing", "准备下载"));

        try
        {
            string instanceDirectory = Path.Combine(gameDirectory, "versions", displayName);
            bool existed = Directory.Exists(instanceDirectory);
            Directory.CreateDirectory(instanceDirectory);
            if (!existed) task.RegisterCleanup(instanceDirectory);

            var progress = new Progress<ModpackInstallProgress>(value => task.Report(
                value.TotalFiles > 0
                    ? Math.Clamp(value.CompletedFiles * 100.0 / value.TotalFiles, 0, 100)
                    : 0,
                value.Phase));

            ModpackInstallResult result = await CurseForgeBundleInstall.InstallAsync(
                packagePath,
                instanceDirectory,
                apiKey,
                new ModpackInstallOptions
                {
                    InstallLoader = true,
                    JavaExecutable = YamlRead.JavaExecutable,
                },
                progress,
                task.Token);

            task.Complete();
            LaunchBarRefresh.Request();
            Shell.Toast(result.Warnings.Count > 0
                ? L.T("Download.Modpack.Drop.CurseForgePartial",
                    "已安装「{0}」，但有 {1} 个文件未装上（作者可能关闭了第三方下载）",
                    displayName, result.Warnings.Count)
                : L.T("Download.Resource.InstallDone", "已安装「{0}」", displayName));
        }
        catch (OperationCanceledException)
        {
            task.MarkCancelled();
            Shell.Toast(L.T("Download.Resource.InstallCancelled", "已取消安装「{0}」", displayName));
        }
        catch (Exception error)
        {
            task.Fail(error.Message);
            Shell.Toast(L.T("Download.Resource.InstallFailed", "安装「{0}」失败：{1}", displayName, error.Message));
        }
    }

    // ------------------------------------------------------------------
    // 6. 小工具
    // ------------------------------------------------------------------

    private static string SafeName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().Trim('.');
        return name.Length == 0 ? "import" : name;
    }

    private static string UniqueFile(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string candidate = path;
        for (int suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
        }

        return candidate;
    }

    private static string UniqueDirectory(string path)
    {
        string candidate = path;
        for (int suffix = 2; Directory.Exists(candidate); suffix++)
        {
            candidate = $"{path} ({suffix})";
        }

        return candidate;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
