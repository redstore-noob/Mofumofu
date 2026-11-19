using System.Collections.ObjectModel;
using System.Diagnostics;
using MofuCore.Config;
using MofuCore.Tools;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class ToolsPage : MofuPage
{
    private static readonly string[] Actions =
    [
        "资源包 · 查看草稿",
        "资源包 · 新建草稿",
        "资源包 · 导入 zip / 目录",
        "资源包 · 添加文本文件",
        "资源包 · 添加本地文件（PNG / 二进制）",
        "资源包 · 导出为 zip",
        "命令生成器",
        "查看 latest.log",
        "查看崩溃报告",
        "打开数据目录",
    ];

    private readonly ListView _actions;
    private readonly TextView _detail;

    public ToolsPage(IMofuShell shell) : base(shell, "工具")
    {
        ResourcePackMake.DataRoot = YamlRead.DataRoot;

        _actions = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Percent(42),
            Height = Dim.Fill(1),
            Title = " 工具 ",
            BorderStyle = LineStyle.Single,
        };
        _actions.SetSource(new ObservableCollection<string>(Actions));
        _actions.SelectedItem = 0;
        _actions.ValueChanged += (_, _) => UpdateDetail();
        _actions.Accepted += (_, _) => Execute(_actions.SelectedItem ?? -1);

        _detail = new TextView
        {
            X = Pos.Percent(42) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
        };

        Add(_actions, _detail);
    }

    public override void Refresh()
    {
        UpdateDetail();
        Shell.SetStatus("工具 · 资源包草稿、命令生成、日志查看");
    }

    private void UpdateDetail()
    {
        string draftText;
        try
        {
            ResourcePackDraft draft = ResourcePackMake.LoadDraftAsync().GetAwaiter().GetResult();
            draftText = draft.Found
                ? $"{draft.Name} · {draft.Files.Count} 个文件 · 保存于 {draft.SavedAt}"
                : "还没有草稿";
        }
        catch (Exception exception)
        {
            draftText = "读取失败：" + exception.Message;
        }

        _detail.Text = string.Join(Environment.NewLine, new[]
        {
            "工具集",
            new string('─', 30),
            string.Empty,
            "资源包草稿",
            $"  {draftText}",
            $"  目录：{ResourcePackMake.DraftDirectory}",
            string.Empty,
            "日志",
            $"  latest.log：{Path.Combine(YamlRead.GameDirectory, "logs", "latest.log")}",
            $"  崩溃报告：{Path.Combine(YamlRead.GameDirectory, "crash-reports")}",
            string.Empty,
            "说明",
            "  · 资源包工具复用桌面版草稿（MofuCore/ResourcePackMake）。",
            "  · UI 绘制类工具（皮肤、立方体、渐变、可视化模组包编辑）属于图形界面功能，",
            "    未在 TUI 中提供。",
            "  · 命令生成器覆盖常用指令，生成后可复制到游戏内。",
        });
    }

    private void Execute(int index)
    {
        switch (index)
        {
            case 0:
                _ = ShowDraftAsync();
                break;
            case 1:
                _ = CreateDraftAsync();
                break;
            case 2:
                _ = ImportDraftAsync();
                break;
            case 3:
                _ = AddTextFileAsync();
                break;
            case 4:
                _ = AddLocalFileAsync();
                break;
            case 5:
                _ = ExportDraftAsync();
                break;
            case 6:
                CommandGenerator();
                break;
            case 7:
                ShowLatestLog();
                break;
            case 8:
                ShowCrashReport();
                break;
            case 9:
                OpenDirectory(YamlRead.DataRoot);
                break;
        }
    }

    private async Task<ResourcePackProject?> LoadProjectAsync()
    {
        ResourcePackDraft draft = await ResourcePackMake.LoadDraftAsync().ConfigureAwait(false);
        if (!draft.Found)
        {
            return null;
        }

        ResourcePackProject project = ResourcePackMake.CreateDraft(draft.Name);
        project.Root = string.Empty;
        project.Files.AddRange(draft.Files);
        return project;
    }

    private async Task ShowDraftAsync()
    {
        ResourcePackProject? project = await LoadProjectAsync().ConfigureAwait(false);
        if (project is null)
        {
            Ui.Info("资源包草稿", "还没有草稿。可以先新建，或导入现有资源包。");
            return;
        }

        string text = string.Join(Environment.NewLine, new[]
        {
            "名称：" + project.Name,
            "文件数：" + project.Files.Count,
            string.Empty,
        }.Concat(project.Files.OrderBy(file => file.Path).Select(file =>
            $"[{file.Kind}] {file.Path}")));
        Ui.ShowText("资源包草稿", text);
    }

    private async Task CreateDraftAsync()
    {
        string? name = Ui.Prompt("新建资源包草稿", "资源包名称", ResourcePackMake.DefaultPackName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ResourcePackProject project = ResourcePackMake.CreateDraft(name);
            ResourcePackMake.AddTextFile(project, ResourcePackMake.PackMetaName,
                ResourcePackMake.CreatePackMetaJson(34, name));
            await ResourcePackMake.SaveDraftAsync(project).ConfigureAwait(false);
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("新建草稿失败", exception.Message);
        }
    }

    private async Task ImportDraftAsync()
    {
        string? source = Ui.Prompt("导入资源包", "zip 文件或资源包目录路径");
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        (bool ok, ResourcePackProject? project) = await Ui.RunTaskAsync("导入资源包", "正在读取…",
            async (progress, token) =>
            {
                progress.Report("解析 " + source);
                ResourcePackProject imported = await ResourcePackMake.ImportAsync(source, token).ConfigureAwait(false);
                progress.Report($"读取 {imported.Files.Count} 个文件");
                await ResourcePackMake.SaveDraftAsync(imported, token).ConfigureAwait(false);
                return imported;
            }).ConfigureAwait(false);

        if (ok && project is not null)
        {
            Refresh();
            Ui.Info("导入完成", $"{project.Name} · {project.Files.Count} 个文件");
        }
    }

    private async Task AddTextFileAsync()
    {
        ResourcePackProject? project = await LoadProjectAsync().ConfigureAwait(false);
        if (project is null)
        {
            Ui.Info("添加文本文件", "请先新建或导入一个草稿。");
            return;
        }

        string? path = Ui.Prompt("添加文本文件", "包内路径（例如 assets/minecraft/lang/zh_cn.json）");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string? content = Ui.Prompt("文本内容", "文件内容（单行）", string.Empty);
        if (content is null)
        {
            return;
        }

        try
        {
            ResourcePackMake.AddTextFile(project, path, content);
            await ResourcePackMake.SaveDraftAsync(project).ConfigureAwait(false);
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("添加文本文件失败", exception.Message);
        }
    }

    private async Task AddLocalFileAsync()
    {
        ResourcePackProject? project = await LoadProjectAsync().ConfigureAwait(false);
        if (project is null)
        {
            Ui.Info("添加本地文件", "请先新建或导入一个草稿。");
            return;
        }

        string? source = Ui.Prompt("添加本地文件", "本地文件路径");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            return;
        }

        string defaultPath = Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "assets/minecraft/textures/" + Path.GetFileName(source)
            : "assets/minecraft/" + Path.GetFileName(source);
        string? target = Ui.Prompt("包内路径", "写入资源包中的路径", defaultPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
            if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                ResourcePackMake.AddTexture(project, target, bytes);
            }
            else
            {
                ResourcePackMake.AddBinaryFile(project, target, bytes);
            }

            await ResourcePackMake.SaveDraftAsync(project).ConfigureAwait(false);
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("添加文件失败", exception.Message);
        }
    }

    private async Task ExportDraftAsync()
    {
        ResourcePackProject? project = await LoadProjectAsync().ConfigureAwait(false);
        if (project is null)
        {
            Ui.Info("导出资源包", "请先新建或导入一个草稿。");
            return;
        }

        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            (string.IsNullOrWhiteSpace(project.Name) ? "resourcepack" : project.Name) + ".zip");
        string? path = Ui.Prompt("导出资源包", "zip 输出路径", target);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        bool ok = await Ui.RunTaskAsync("导出资源包", "正在打包…", async (progress, token) =>
        {
            string result = await ResourcePackMake.ExportAsync(project, path,
                new Progress<string>(message => progress.Report(message)), token).ConfigureAwait(false);
            progress.Report("已导出：" + result);
        }).ConfigureAwait(false);
        if (ok)
        {
            Ui.Info("导出完成", path);
        }
    }

    private void CommandGenerator()
    {
        string? kind = Ui.Select("命令生成器",
            ["/give 给予物品", "/tp 传送", "/effect 药水效果", "/time 时间", "/weather 天气",
             "/gamemode 游戏模式", "/summon 生成实体", "/enchant 附魔"]);
        if (kind is null)
        {
            return;
        }

        try
        {
            string command = kind.StartsWith("/give", StringComparison.Ordinal) ? BuildGive()
                : kind.StartsWith("/tp", StringComparison.Ordinal) ? BuildTp()
                : kind.StartsWith("/effect", StringComparison.Ordinal) ? BuildEffect()
                : kind.StartsWith("/time", StringComparison.Ordinal) ? BuildTime()
                : kind.StartsWith("/weather", StringComparison.Ordinal) ? BuildWeather()
                : kind.StartsWith("/gamemode", StringComparison.Ordinal) ? BuildGameMode()
                : kind.StartsWith("/summon", StringComparison.Ordinal) ? BuildSummon()
                : BuildEnchant();
            if (!string.IsNullOrWhiteSpace(command))
            {
                Ui.ShowText("命令", "在游戏内输入：" + Environment.NewLine + Environment.NewLine + command);
            }
        }
        catch (Exception exception)
        {
            Ui.Error("命令生成失败", exception.Message);
        }
    }

    private static string BuildGive()
    {
        Dictionary<string, string>? values = Ui.Form("/give",
            ("player", "目标玩家", "@s", false),
            ("item", "物品 ID", "minecraft:diamond", false),
            ("count", "数量", "1", false));
        return values is null ? string.Empty : $"/give {values["player"]} {values["item"]} {values["count"]}";
    }

    private static string BuildTp()
    {
        Dictionary<string, string>? values = Ui.Form("/tp",
            ("player", "目标玩家", "@s", false),
            ("x", "X", "0", false),
            ("y", "Y", "64", false),
            ("z", "Z", "0", false));
        return values is null ? string.Empty : $"/tp {values["player"]} {values["x"]} {values["y"]} {values["z"]}";
    }

    private static string BuildEffect()
    {
        Dictionary<string, string>? values = Ui.Form("/effect",
            ("player", "目标玩家", "@s", false),
            ("effect", "效果 ID", "minecraft:speed", false),
            ("seconds", "持续秒数", "30", false),
            ("amplifier", "等级（0 = I）", "0", false));
        return values is null
            ? string.Empty
            : $"/effect give {values["player"]} {values["effect"]} {values["seconds"]} {values["amplifier"]}";
    }

    private static string BuildTime()
    {
        string? value = Ui.Select("/time set", ["day", "noon", "night", "midnight", "1000", "6000", "18000"]);
        return value is null ? string.Empty : "/time set " + value;
    }

    private static string BuildWeather()
    {
        Dictionary<string, string>? values = Ui.Form("/weather",
            ("type", "clear / rain / thunder", "clear", false),
            ("seconds", "持续秒数", "600", false));
        return values is null ? string.Empty : $"/weather {values["type"]} {values["seconds"]}";
    }

    private static string BuildGameMode()
    {
        Dictionary<string, string>? values = Ui.Form("/gamemode",
            ("mode", "survival / creative / adventure / spectator", "creative", false),
            ("player", "目标玩家（可留空）", "@s", false));
        return values is null ? string.Empty : $"/gamemode {values["mode"]} {values["player"]}";
    }

    private static string BuildSummon()
    {
        Dictionary<string, string>? values = Ui.Form("/summon",
            ("entity", "实体 ID", "minecraft:pig", false),
            ("x", "X", "0", false),
            ("y", "Y", "64", false),
            ("z", "Z", "0", false));
        return values is null ? string.Empty : $"/summon {values["entity"]} {values["x"]} {values["y"]} {values["z"]}";
    }

    private static string BuildEnchant()
    {
        Dictionary<string, string>? values = Ui.Form("/enchant",
            ("player", "目标玩家", "@s", false),
            ("enchantment", "附魔 ID", "minecraft:sharpness", false),
            ("level", "等级", "5", false));
        return values is null ? string.Empty : $"/enchant {values["player"]} {values["enchantment"]} {values["level"]}";
    }

    private void ShowLatestLog()
    {
        string latest = Path.Combine(YamlRead.GameDirectory, "logs", "latest.log");
        if (!File.Exists(latest))
        {
            Ui.Info("latest.log", "还没有日志文件。启动一次游戏后会自动生成。");
            return;
        }

        try
        {
            Ui.ShowText("latest.log", ReadTail(latest, 200_000));
        }
        catch (Exception exception)
        {
            Ui.Error("读取日志失败", exception.Message);
        }
    }

    private void ShowCrashReport()
    {
        string directory = Path.Combine(YamlRead.GameDirectory, "crash-reports");
        if (!Directory.Exists(directory))
        {
            Ui.Info("崩溃报告", "还没有崩溃报告。");
            return;
        }

        List<string> reports = Directory.EnumerateFiles(directory, "*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();
        if (reports.Count == 0)
        {
            Ui.Info("崩溃报告", "还没有崩溃报告。");
            return;
        }

        string? picked = Ui.Select("崩溃报告", reports);
        if (picked is null)
        {
            return;
        }

        try
        {
            Ui.ShowText(picked, ReadTail(Path.Combine(directory, picked), 300_000));
        }
        catch (Exception exception)
        {
            Ui.Error("读取崩溃报告失败", exception.Message);
        }
    }

    private static string ReadTail(string path, int maximumBytes)
    {
        using FileStream stream = File.OpenRead(path);
        long start = Math.Max(0, stream.Length - maximumBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using StreamReader reader = new(stream);
        string text = reader.ReadToEnd();
        return start > 0 ? "…（仅显示文件末尾）\n" + text : text;
    }

    private static void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Ui.Error("打开目录失败", exception.Message);
        }
    }
}
