using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using MofuCore.Config;
using MofuCore.Launch;
using MofuCore.Tools;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class InstancesPage : MofuPage
{
    private readonly ListView _list;
    private readonly TextView _detail;
    private readonly InstanceService _instances;
    private List<InstanceItem> _items = [];

    public InstancesPage(IMofuShell shell) : base(shell, "实例")
    {
        _instances = new InstanceService(shell.Accounts, shell.Settings);

        _list = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Percent(45),
            Height = Dim.Fill(3),
            Title = " 实例 ",
            BorderStyle = LineStyle.Single,
        };
        _list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>());
        _list.ValueChanged += (_, _) => UpdateDetail();
        _list.Accepted += (_, _) => Launch();

        _detail = new TextView
        {
            X = Pos.Percent(45) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = true,
            Text = "正在扫描实例…",
        };

        Button launch = new() { Text = "启动", X = 1, Y = Pos.AnchorEnd(2) };
        Button stop = new() { Text = "停止", X = 11, Y = Pos.AnchorEnd(2) };
        Button manage = new() { Text = "管理", X = 21, Y = Pos.AnchorEnd(2) };
        Button refresh = new() { Text = "刷新", X = 31, Y = Pos.AnchorEnd(2) };
        Button import = new() { Text = "导入实例", X = 41, Y = Pos.AnchorEnd(2) };
        Button export = new() { Text = "导出", X = 55, Y = Pos.AnchorEnd(2) };

        launch.Accepted += (_, _) => Launch();
        stop.Accepted += (_, _) => Stop();
        manage.Accepted += (_, _) => OpenManageMenu();
        refresh.Accepted += (_, _) => Refresh();
        import.Accepted += (_, _) => ImportInstance();
        export.Accepted += (_, _) => Export();

        Add(_list, _detail, launch, stop, manage, refresh, import, export);
    }

    public override void Refresh()
    {
        Shell.SetStatus("正在扫描实例…");
        Task.Run(() =>
        {
            List<InstanceItem> items = _instances.Scan().ToList();
            Ui.Post(() =>
            {
                _items = items;
                System.Collections.ObjectModel.ObservableCollection<string> source = new();
                foreach (InstanceItem item in items)
                {
                    source.Add(item.Display);
                }

                _list.SetSource(source);
                if (items.Count > 0)
                {
                    int index = items.FindIndex(item => item.Id == Shell.Settings.SelectedInstance);
                    _list.SelectedItem = index >= 0 ? index : 0;
                }
                else
                {
                    _list.SelectedItem = null;
                }

                UpdateDetail();
                Shell.SetStatus($"实例 {items.Count} 个 · 游戏{(GameLaunch.Shared.IsGameRunning ? "运行中" : "未运行")}");
            });
        });
    }

    private InstanceItem? Selected =>
        _list.SelectedItem is int index && index >= 0 && index < _items.Count ? _items[index] : null;

    private void UpdateDetail()
    {
        InstanceItem? item = Selected;
        if (item is null)
        {
            _detail.Text = "没有实例。\n\n到「下载」页安装原版或加载器后，实例会出现在这里。";
            return;
        }

        string javaConfig = YamlRead.LoadInstanceJavaConfig(item.Directory).JavaExecutable;
        MinecraftLaunchResult? running = GameLaunch.Shared.Current;
        string runningText = running is not null && running.VersionId == item.Id
            ? $"运行中（PID {running.Process.Id}）"
            : "未运行";

        _detail.Text = string.Join(Environment.NewLine, new[]
        {
            item.Id,
            new string('─', Math.Min(40, Math.Max(10, item.Id.Length * 2))),
            string.Empty,
            $"类型        {(item.Loader == "Vanilla" ? "原版" : item.Loader)}",
            $"Minecraft   {item.MinecraftVersion}",
            $"模组        {item.ModCount} 个",
            $"占用        {Ui.FormatBytes(item.SizeBytes)}",
            $"状态        {runningText}",
            $"Java        {(string.IsNullOrWhiteSpace(javaConfig) ? "继承全局设置" : javaConfig)}",
            $"目录        {item.Directory}",
            string.Empty,
            "Enter 启动；F5 刷新；Tab 切换按钮。",
        });
    }

    private void Launch()
    {
        InstanceItem? item = Selected;
        if (item is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Shell.Settings.SelectedAccount))
        {
            Ui.Info("启动游戏", "还没有选择账号，请先到「账号」页添加离线账号或登录正版。");
            return;
        }

        Shell.Settings.SelectedInstance = item.Id;
        Shell.Settings.Save();
        _ = LaunchAsync(item.Id);
    }

    private async Task LaunchAsync(string instanceId)
    {
        (bool ok, MinecraftLaunchResult? result) = await Ui.RunTaskAsync(
            "启动 " + instanceId,
            "正在准备启动…",
            (progress, token) => _instances.LaunchAsync(instanceId, progress, token)).ConfigureAwait(false);
        if (ok && result is not null)
        {
            Shell.SetStatus($"游戏已启动：{instanceId} (PID {result.Process.Id})");
            UpdateDetail();
        }
    }

    private void Stop()
    {
        MinecraftLaunchResult? running = GameLaunch.Shared.Current;
        if (running is null)
        {
            Ui.Info("停止游戏", "当前没有正在运行的游戏。");
            return;
        }

        if (Ui.Confirm("停止游戏", $"确定结束 {running.VersionId}（PID {running.Process.Id}）吗？"))
        {
            GameLaunch.Shared.TryStopGame();
            UpdateDetail();
            Shell.SetStatus("已发送结束游戏请求。");
        }
    }

    private void OpenManageMenu()
    {
        InstanceItem? item = Selected;
        if (item is null)
        {
            return;
        }

        string? action = Ui.Select("管理 · " + item.Id,
        [
            "打开实例目录",
            "内容管理（模组 / 资源包 / 光影 / 存档）",
            "实例设置（Java / 内存 / 参数）",
            "复制实例",
            "导出实例",
            "删除实例",
        ]);

        switch (action)
        {
            case "打开实例目录":
                OpenDirectory(item.Directory);
                break;
            case "内容管理（模组 / 资源包 / 光影 / 存档）":
                ManageContent(item);
                break;
            case "实例设置（Java / 内存 / 参数）":
                EditInstanceSettings(item);
                break;
            case "复制实例":
                Duplicate(item);
                break;
            case "导出实例":
                Export();
                break;
            case "删除实例":
                Delete();
                break;
        }
    }

    private void ManageContent(InstanceItem item)
    {
        string? group = Ui.Select("内容管理 · " + item.Id, ["模组 mods", "资源包 resourcepacks", "光影 shaderpacks", "存档 saves", "返回"]);
        if (group is null || group == "返回")
        {
            return;
        }

        if (group == "存档 saves")
        {
            ManageWorlds(item);
            return;
        }

        string directoryName = group.StartsWith("模组", StringComparison.Ordinal) ? "mods"
            : group.StartsWith("资源包", StringComparison.Ordinal) ? "resourcepacks"
            : "shaderpacks";
        string directory = Path.Combine(item.Directory, directoryName);

        while (true)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                Ui.Error("内容管理", exception.Message);
                return;
            }

            List<string> files = Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name)
                .ToList();
            List<string> options = ["＋ 导入文件", .. files, "打开目录", "返回"];
            string? picked = Ui.Select($"{directoryName} · {files.Count} 个文件", options);
            if (picked is null || picked == "返回")
            {
                return;
            }

            if (picked == "打开目录")
            {
                OpenDirectory(directory);
                continue;
            }

            if (picked == "＋ 导入文件")
            {
                string? source = Ui.Prompt("导入文件", "本地 .jar / .zip 文件路径");
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                {
                    continue;
                }

                try
                {
                    string target = Path.Combine(directory, Path.GetFileName(source));
                    File.Copy(source, target, true);
                }
                catch (Exception exception)
                {
                    Ui.Error("导入失败", exception.Message);
                }
                continue;
            }

            string? operation = Ui.Select("文件操作 · " + picked,
                [picked.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "启用" : "停用", "删除", "返回"]);
            try
            {
                if (operation == "停用")
                {
                    File.Move(Path.Combine(directory, picked), Path.Combine(directory, picked + ".disabled"), true);
                }
                else if (operation == "启用")
                {
                    File.Move(Path.Combine(directory, picked),
                        Path.Combine(directory, picked[..^".disabled".Length]), true);
                }
                else if (operation == "删除" &&
                         Ui.Confirm("删除文件", "确定删除 " + picked + " 吗？"))
                {
                    File.Delete(Path.Combine(directory, picked));
                }
            }
            catch (Exception exception)
            {
                Ui.Error("文件操作失败", exception.Message);
            }
        }
    }

    private void ManageWorlds(InstanceItem item)
    {
        string saves = Path.Combine(item.Directory, "saves");
        while (true)
        {
            if (!Directory.Exists(saves))
            {
                Ui.Info("存档管理", "该实例还没有 saves 目录。启动一次游戏后会自动生成。");
                return;
            }

            List<string> worlds = Directory.GetDirectories(saves).Select(Path.GetFileName)
                .Where(name => name is not null).Select(name => name!).OrderBy(name => name).ToList();
            List<string> options = ["打开存档目录", .. worlds, "返回"];
            string? picked = Ui.Select($"存档 · {worlds.Count} 个", options);
            if (picked is null || picked == "返回")
            {
                return;
            }

            if (picked == "打开存档目录")
            {
                OpenDirectory(saves);
                continue;
            }

            string worldDirectory = Path.Combine(saves, picked);
            string? operation = Ui.Select("存档操作 · " + picked, ["打开目录", "创建快照", "删除存档", "返回"]);
            try
            {
                if (operation == "打开目录")
                {
                    OpenDirectory(worldDirectory);
                }
                else if (operation == "创建快照")
                {
                    _ = Ui.RunTaskAsync("创建快照", "正在扫描存档…", async (progress, token) =>
                    {
                        RewindSnapshot snapshot = await RewindSystem.CreateSnapshotAsync(worldDirectory,
                            RewindKind.Save, "TUI 手动快照", null, token).ConfigureAwait(false);
                        progress.Report($"完成：{snapshot.FileCount} 个文件 · {Ui.FormatBytes(snapshot.AddedSize)}");
                    });
                }
                else if (operation == "删除存档" &&
                         Ui.Confirm("删除存档", $"确定删除存档 {picked} 吗？\n此操作不可撤销。"))
                {
                    Directory.Delete(worldDirectory, true);
                }
            }
            catch (Exception exception)
            {
                Ui.Error("存档操作失败", exception.Message);
            }
        }
    }

    private void EditInstanceSettings(InstanceItem item)
    {
        InstanceJavaConfig config = YamlRead.LoadInstanceJavaConfig(item.Directory);
        Dictionary<string, string>? values = Ui.Form("实例设置 · " + item.Id,
            ("java", "Java 路径（留空继承全局）", config.JavaExecutable, false),
            ("min", "最小内存 MB（0 = 继承）", config.MinMemoryMB.ToString(), false),
            ("max", "最大内存 MB（0 = 继承）", config.MaxMemoryMB.ToString(), false),
            ("jvm", "额外 JVM 参数（空格分隔）", string.Join(' ', config.AdditionalJvmArguments), false),
            ("game", "额外游戏参数（空格分隔）", string.Join(' ', config.AdditionalGameArguments), false));
        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values["min"], out int minimum) || !int.TryParse(values["max"], out int maximum) ||
            minimum < 0 || maximum < 0 || (maximum > 0 && maximum < minimum))
        {
            Ui.Error("实例设置", "内存数值不正确。");
            return;
        }

        config.JavaExecutable = values["java"].Trim();
        config.MinMemoryMB = minimum;
        config.MaxMemoryMB = maximum;
        config.AdditionalJvmArguments = SplitArguments(values["jvm"]);
        config.AdditionalGameArguments = SplitArguments(values["game"]);
        if (!YamlRead.SaveInstanceJavaConfig(item.Directory, config))
        {
            Ui.Error("实例设置", "保存失败。");
            return;
        }

        UpdateDetail();
        Ui.Info("实例设置", "已保存。下次启动生效。");
    }

    private static List<string> SplitArguments(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private void Duplicate(InstanceItem item)
    {
        string? name = Ui.Prompt("复制实例", "新实例名", item.Id + "-copy");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string target = Path.Combine(YamlRead.GameDirectory, "versions", name.Trim());
        if (Directory.Exists(target))
        {
            Ui.Error("复制实例", "目标实例已存在。");
            return;
        }

        try
        {
            CopyDirectory(item.Directory, target);
            string json = Path.Combine(target, item.Id + ".json");
            if (File.Exists(json))
            {
                string renamed = Path.Combine(target, name.Trim() + ".json");
                File.Move(json, renamed);
                RewriteVersionId(renamed, name.Trim());
            }

            Shell.Settings.SelectedInstance = name.Trim();
            Shell.Settings.Save();
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("复制实例失败", exception.Message);
        }
    }

    private void ImportInstance()
    {
        string? path = Ui.Prompt("导入实例", "实例 zip 文件路径");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        _ = ImportInstanceAsync(path);
    }

    private async Task ImportInstanceAsync(string path)
    {
        (bool ok, string? instanceId) = await Ui.RunTaskAsync("导入实例", "正在解压…", async (progress, token) =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "mofu-instance-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                progress.Report("解压 " + path);
                System.IO.Compression.ZipFile.ExtractToDirectory(path, temp, true);

                string baseName = Path.GetFileNameWithoutExtension(path);
                string source = temp;
                string jsonName = baseName + ".json";
                string candidate = Path.Combine(temp, baseName, jsonName);
                if (File.Exists(candidate))
                {
                    source = Path.Combine(temp, baseName);
                }
                else if (!File.Exists(Path.Combine(temp, jsonName)))
                {
                    string? found = Directory.EnumerateFiles(temp, "*.json", SearchOption.AllDirectories)
                        .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file) ==
                                                Path.GetFileName(Path.GetDirectoryName(file)));
                    if (found is null)
                    {
                        throw new InvalidOperationException("zip 中没有找到有效的版本 JSON。");
                    }

                    source = Path.GetDirectoryName(found)!;
                    baseName = Path.GetFileName(source);
                }

                string target = Path.Combine(YamlRead.GameDirectory, "versions", baseName);
                if (Directory.Exists(target))
                {
                    throw new InvalidOperationException("实例已存在：" + baseName);
                }

                progress.Report("复制到 versions/" + baseName);
                CopyDirectory(source, target);
                RewriteVersionId(Path.Combine(target, baseName + ".json"), baseName);
                return baseName;
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (Exception)
                {
                }
            }
        }).ConfigureAwait(false);

        if (ok && !string.IsNullOrWhiteSpace(instanceId))
        {
            Shell.Settings.SelectedInstance = instanceId;
            Shell.Settings.Save();
            Refresh();
            Ui.Info("导入完成", "实例已导入：" + instanceId);
        }
    }

    private static void RewriteVersionId(string jsonPath, string instanceId)
    {
        if (!File.Exists(jsonPath))
        {
            return;
        }

        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(jsonPath));
            if (root is JsonObject obj)
            {
                obj["id"] = instanceId;
                File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
            }
        }
        catch (Exception)
        {
            // 非标准 JSON 保持原样，由版本加载器决定是否可用。
        }
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

    private void OpenSelected()
    {
        InstanceItem? item = Selected;
        if (item is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.Directory, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Ui.Error("打开目录失败", exception.Message);
        }
    }

    private void Export()
    {
        InstanceItem? item = Selected;
        if (item is null)
        {
            return;
        }

        string? path = Ui.Prompt("导出实例", "导出为 zip 文件", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), item.Id + ".zip"));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = ExportAsync(item, path);
    }

    private async Task ExportAsync(InstanceItem item, string path)
    {
        await Ui.RunTaskAsync("导出实例", "正在打包实例…", (progress, token) =>
        {
            return Task.Run(() =>
            {
                progress.Report("正在压缩 " + item.Directory);
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                ZipFile.CreateFromDirectory(item.Directory, path, CompressionLevel.Optimal, true);
                progress.Report("已导出到 " + path);
            }, token);
        }).ConfigureAwait(false);
    }

    private void Delete()
    {
        InstanceItem? item = Selected;
        if (item is null)
        {
            return;
        }

        MinecraftLaunchResult? running = GameLaunch.Shared.Current;
        if (running is not null && running.VersionId == item.Id)
        {
            Ui.Info("删除实例", "该实例正在运行，请先停止游戏。");
            return;
        }

        if (!Ui.Confirm("删除实例", $"确定删除实例 {item.Id} 吗？\n\n目录：{item.Directory}\n此操作不可撤销。"))
        {
            return;
        }

        try
        {
            Directory.Delete(item.Directory, true);
            if (Shell.Settings.SelectedInstance == item.Id)
            {
                Shell.Settings.SelectedInstance = string.Empty;
                Shell.Settings.Save();
            }

            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("删除实例失败", exception.Message);
        }
    }
}
