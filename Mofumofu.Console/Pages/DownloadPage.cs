using System.Collections.ObjectModel;
using System.Diagnostics;
using MofuCore.Config;
using MofuCore.Download.Minecraft;
using MofuCore.Download.Modrinth;
using MofuCore.Java;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

/// <summary>下载页：布局对照桌面版，左侧分类、右侧搜索 + 列表 + 详情 + 操作。</summary>
public sealed class DownloadPage : MofuPage
{
    private enum Mode
    {
        Vanilla,
        Mod,
        Modpack,
        Shader,
        ResourcePack,
        Java,
    }

    private sealed record Entry(string Display, string Detail, Func<Task> Install);

    private readonly ListView _modes;
    private readonly Label _searchLabel;
    private readonly TextField _search;
    private readonly CheckBox _filterRelease;
    private readonly CheckBox _filterSnapshot;
    private readonly CheckBox _filterOld;
    private readonly Label _count;
    private readonly ListView _results;
    private readonly TextView _detail;
    private readonly Button _options;
    private readonly Button _installedJava;
    private Mode _mode = Mode.Vanilla;
    private VendorJavaKind _javaVendor = VendorJavaKind.Zulu;
    private IReadOnlyList<MinecraftVersion>? _versions;
    private readonly List<Entry> _entries = [];

    public DownloadPage(IMofuShell shell) : base(shell, "下载")
    {
        _searchLabel = new Label { Text = "搜索", X = 20, Y = 1 };
        _search = new TextField
        {
            X = 25,
            Y = 1,
            Width = Dim.Fill(30),
        };
        _search.KeyDown += (_, key) =>
        {
            if (key.KeyCode == Terminal.Gui.Drivers.KeyCode.Enter)
            {
                key.Handled = true;
                RefreshCurrent();
            }
        };

        Button refresh = new() { Text = "刷新", X = Pos.AnchorEnd(12), Y = 1 };
        refresh.Accepted += (_, _) =>
        {
            _versions = null;
            RefreshCurrent();
        };

        _options = new() { Text = "选项", X = Pos.AnchorEnd(25), Y = 1, Visible = false };
        _options.Accepted += (_, _) => EditOptions();

        _modes = new ListView
        {
            X = 1,
            Y = 1,
            Width = 19,
            Height = Dim.Fill(1),
            Title = " 下载类型 ",
            BorderStyle = LineStyle.Single,
        };
        _modes.SetSource(new ObservableCollection<string>(
            ["原版游戏", "模组", "整合包", "光影", "材质包", "Java 运行时"]));
        _modes.SelectedItem = 0;
        _modes.ValueChanged += (_, args) =>
        {
            _mode = (args.NewValue ?? 0) switch
            {
                1 => Mode.Mod,
                2 => Mode.Modpack,
                3 => Mode.Shader,
                4 => Mode.ResourcePack,
                5 => Mode.Java,
                _ => Mode.Vanilla,
            };
            RefreshCurrent();
        };

        _filterRelease = new CheckBox { Text = "正式版", X = 20, Y = 2, Value = CheckState.Checked };
        _filterSnapshot = new CheckBox { Text = "快照", X = 30, Y = 2, Value = CheckState.Checked };
        _filterOld = new CheckBox { Text = "历史", X = 39, Y = 2, Value = CheckState.Checked };
        foreach (CheckBox filter in new[] { _filterRelease, _filterSnapshot, _filterOld })
        {
            filter.ValueChanged += (_, _) => RefreshCurrent();
        }

        _count = new Label { Text = "正在读取…", X = 20, Y = 2, Visible = false };

        _results = new ListView
        {
            X = 20,
            Y = 3,
            Width = Dim.Fill(1),
            Height = Dim.Fill(9),
            Title = " 可下载内容 ",
            BorderStyle = LineStyle.Single,
        };
        _results.SetSource(new ObservableCollection<string>());
        _results.ValueChanged += (_, _) => UpdateDetail();
        _results.Accepted += (_, _) => InstallSelected();

        _detail = new TextView
        {
            X = 20,
            Y = Pos.AnchorEnd(7),
            Width = Dim.Fill(1),
            Height = 5,
            ReadOnly = true,
            WordWrap = true,
            Text = "选择列表项查看详情；Enter 安装。",
        };

        Button install = new() { Text = "安装", X = 20, Y = Pos.AnchorEnd(1) };
        _installedJava = new() { Text = "已安装 Java", X = 31, Y = Pos.AnchorEnd(1), Visible = false };
        Button folder = new() { Text = "打开游戏目录", X = 50, Y = Pos.AnchorEnd(1) };
        install.Accepted += (_, _) => InstallSelected();
        _installedJava.Accepted += (_, _) => ManageJavaRuntimes();
        folder.Accepted += (_, _) => OpenGameFolder();

        Add(_modes, _searchLabel, _search, refresh, _options,
            _filterRelease, _filterSnapshot, _filterOld, _count,
            _results, _detail, install, _installedJava, folder);
    }

    public override void Refresh()
    {
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        bool vanilla = _mode == Mode.Vanilla;
        _filterRelease.Visible = vanilla;
        _filterSnapshot.Visible = vanilla;
        _filterOld.Visible = vanilla;
        _count.Visible = !vanilla;
        if (!vanilla)
        {
            _count.Text = "加载中…";
        }
        _options.Visible = _mode == Mode.Java;
        _installedJava.Visible = _mode == Mode.Java;

        Shell.SetStatus("正在获取下载列表…");
        _results.SetSource(new ObservableCollection<string>(["加载中…"]));
        Task.Run(async () =>
        {
            try
            {
                List<Entry> entries = _mode switch
                {
                    Mode.Mod => await LoadResourceEntriesAsync("mod").ConfigureAwait(false),
                    Mode.Modpack => await LoadResourceEntriesAsync("modpack").ConfigureAwait(false),
                    Mode.Shader => await LoadResourceEntriesAsync("shader").ConfigureAwait(false),
                    Mode.ResourcePack => await LoadResourceEntriesAsync("resourcepack").ConfigureAwait(false),
                    Mode.Java => await LoadJavaEntriesAsync().ConfigureAwait(false),
                    _ => await LoadVanillaEntriesAsync().ConfigureAwait(false),
                };

                Ui.Post(() =>
                {
                    _entries.Clear();
                    _entries.AddRange(entries);
                    _results.SetSource(new ObservableCollection<string>(entries.Select(entry => entry.Display)));
                    _results.SelectedItem = entries.Count > 0 ? 0 : null;
                    _count.Text = $"共 {entries.Count} 项";
                    UpdateDetail();
                    Shell.SetStatus($"{ModeName()} · {entries.Count} 项");
                });
            }
            catch (Exception exception)
            {
                Ui.Post(() =>
                {
                    _entries.Clear();
                    _results.SetSource(new ObservableCollection<string>(["加载失败：" + exception.Message]));
                    _detail.Text = exception.Message;
                    Shell.SetStatus("下载列表加载失败");
                });
            }
        });
    }

    private string ModeName() => _mode switch
    {
        Mode.Mod => "模组",
        Mode.Modpack => "整合包",
        Mode.Shader => "光影",
        Mode.ResourcePack => "材质包",
        Mode.Java => "Java 运行时",
        _ => "原版游戏",
    };

    private async Task<List<Entry>> LoadVanillaEntriesAsync()
    {
        _versions ??= await MinecraftDownload.GetVersionsAsync().ConfigureAwait(false);
        string query = _search.Text?.ToString()?.Trim() ?? string.Empty;
        HashSet<string> installed = InstalledVersionIds();
        List<Entry> entries = [];
        foreach (MinecraftVersion version in _versions)
        {
            bool isOld = version.Type is "old_beta" or "old_alpha";
            bool visible = version.Type switch
            {
                "release" => _filterRelease.Value == CheckState.Checked,
                "snapshot" => _filterSnapshot.Value == CheckState.Checked,
                _ => _filterOld.Value == CheckState.Checked,
            };
            if (!visible || (query.Length > 0 &&
                             !version.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string type = isOld ? "历史版本" : version.TypeDisplay;
            string suffix = installed.Contains(version.Id) ? " · 已安装" : string.Empty;
            entries.Add(new Entry(
                $"{version.Id} · {type} · {version.ReleaseTime:yyyy-MM-dd}{suffix}",
                $"Minecraft {version.Id}\n类型：{type}\n发布时间：{version.ReleaseTime:yyyy-MM-dd HH:mm}" +
                (installed.Contains(version.Id) ? "\n状态：已安装" : string.Empty),
                () => InstallVanillaAsync(version)));
        }

        return entries;
    }

    private HashSet<string> InstalledVersionIds()
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        string directory = Path.Combine(YamlRead.GameDirectory, "versions");
        if (!Directory.Exists(directory))
        {
            return ids;
        }

        foreach (string path in Directory.EnumerateDirectories(directory))
        {
            ids.Add(Path.GetFileName(path));
        }

        return ids;
    }

    /// <summary>原版安装：先选加载器（对应桌面版的本体安装面板），再装游戏本体。</summary>
    private async Task InstallVanillaAsync(MinecraftVersion version)
    {
        string[] loaders = ["原版（仅安装游戏本体）", "Fabric", "Quilt", "Forge", "NeoForge", "OptiFine"];
        string? choice = Ui.Select($"安装 Minecraft {version.Id} · 选择加载器", loaders);
        if (choice is null)
        {
            return;
        }

        if (choice.StartsWith("原版", StringComparison.Ordinal))
        {
            MinecraftDownload download = new(YamlRead.GameDirectory);
            bool vanillaOk = await Ui.RunTaskAsync("安装原版 " + version.Id, "正在下载游戏文件…",
                async (progress, token) =>
                {
                    await download.InstallAsync(version,
                        new Progress<MinecraftInstallProgress>(item => progress.Report(
                            $"[{item.StageIndex}/7] {item.StageName} · {item.Detail} · {item.Percentage:0.#}%")),
                        token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            if (vanillaOk)
            {
                Shell.Settings.SelectedInstance = version.Id;
                Shell.Settings.Save();
                Shell.SetStatus($"已安装原版 {version.Id}");
                RefreshCurrent();
                Ui.Info("安装完成", $"Minecraft {version.Id} 已安装。\n实例已设为当前，可到「实例」页启动。");
            }

            return;
        }

        ModLoaderKind kind = choice switch
        {
            "Quilt" => ModLoaderKind.Quilt,
            "Forge" => ModLoaderKind.Forge,
            "NeoForge" => ModLoaderKind.NeoForge,
            "OptiFine" => ModLoaderKind.OptiFine,
            _ => ModLoaderKind.Fabric,
        };
        string display = ModLoaderInstaller.DisplayName(kind);

        (bool fetched, IReadOnlyList<ModLoaderVersionInfo>? loadersList) = await Ui.RunTaskAsync(
            display + " 版本",
            $"正在获取 {display} 支持 {version.Id} 的版本…",
            (progress, token) => ModLoaderInstaller.GetVersionsAsync(kind, version.Id, token)).ConfigureAwait(false);
        if (!fetched || loadersList is null || loadersList.Count == 0)
        {
            if (fetched)
            {
                Ui.Info("加载器版本", $"{display} 没有支持 Minecraft {version.Id} 的版本。");
            }

            return;
        }

        string? picked = Ui.Select("选择 " + display + " 版本", loadersList.Take(40)
            .Select(item => $"{item.LoaderVersion} · MC {item.MinecraftVersion}" +
                            (item.IsStable ? string.Empty : " · 测试版"))
            .ToList());
        if (picked is null)
        {
            return;
        }

        ModLoaderVersionInfo? loader = loadersList.Take(40).FirstOrDefault(item =>
            $"{item.LoaderVersion} · MC {item.MinecraftVersion}" +
            (item.IsStable ? string.Empty : " · 测试版") == picked);
        if (loader is null)
        {
            return;
        }

        string? instanceName = Ui.Prompt("实例名称", "留空使用默认名称", string.Empty);
        if (instanceName is null)
        {
            return;
        }

        (bool ok, string? instanceId) = await Ui.RunTaskAsync("安装 " + display, "正在安装加载器…",
            async (progress, token) =>
            {
                string id = await ModLoaderInstaller.InstallAsync(loader, YamlRead.GameDirectory,
                    string.IsNullOrWhiteSpace(instanceName) ? null : instanceName,
                    new Progress<ModLoaderInstallProgress>(item => progress.Report(
                        $"[{item.StageIndex}] {item.StageName} · {item.Detail} · {item.Percentage:0.#}%")),
                    token).ConfigureAwait(false);
                return id;
            }).ConfigureAwait(false);
        if (ok && !string.IsNullOrWhiteSpace(instanceId))
        {
            Shell.Settings.SelectedInstance = instanceId;
            Shell.Settings.Save();
            Shell.SetStatus($"已安装 {display} · {instanceId}");
            RefreshCurrent();
            Ui.Info("安装完成", $"实例已创建：{instanceId}\n可到「实例」页启动。");
        }
    }

    private async Task<List<Entry>> LoadResourceEntriesAsync(string type)
    {
        InstanceItem? instance = FindSelectedInstance();
        string? gameVersion = type == "modpack" ? null : instance?.MinecraftVersion;
        string? loader = type != "mod" ? null : LoaderCategory(instance?.Loader ?? string.Empty);
        string query = _search.Text?.ToString()?.Trim() ?? string.Empty;

        ModrinthSearchResult page = string.IsNullOrWhiteSpace(query)
            ? await ModrinthResourceSearch.BrowseAsync(type, gameVersion,
                loader is null ? null : new[] { loader }, 50).ConfigureAwait(false)
            : await ModrinthResourceSearch.SearchPageAsync(query, type, gameVersion,
                loader is null ? null : new[] { loader }, 50).ConfigureAwait(false);

        List<Entry> entries = [];
        foreach (ModrinthProject project in page.Hits)
        {
            entries.Add(new Entry(
                $"{project.Title} · {project.TypeDisplay} · {project.DownloadsDisplay}",
                $"{project.Title}\n{project.Description}\n\n类型：{project.TypeDisplay}  下载：{project.DownloadsDisplay}\n项目 ID：{project.ProjectId}" +
                (instance is null ? "\n提示：安装前请先在「实例」页选中实例。" : $"\n目标实例：{instance.Id}"),
                () => InstallModrinthAsync(project, instance)));
        }

        return entries;
    }

    private async Task<List<Entry>> LoadJavaEntriesAsync()
    {
        IReadOnlyList<VendorJavaCandidate> candidates = await VendorJavaInstaller
            .QueryAvailableVersionsAsync(_javaVendor).ConfigureAwait(false);
        List<Entry> entries = [];
        foreach (VendorJavaCandidate candidate in candidates)
        {
            entries.Add(new Entry(
                $"{candidate.DisplayName} · {candidate.DetailText}",
                $"供应商：{candidate.VendorName}\n主版本：{candidate.MajorVersion}\n构建：{candidate.BuildVersion}\n" +
                $"大小：{(candidate.SizeBytes > 0 ? Ui.FormatBytes(candidate.SizeBytes) : "未知")}",
                () => InstallJavaAsync(candidate)));
        }

        return entries;
    }

    private async Task InstallJavaAsync(VendorJavaCandidate candidate)
    {
        (bool ok, VendorJavaRuntime? installed) = await Ui.RunTaskAsync(
            "下载 " + candidate.DisplayName,
            "正在下载 Java 运行时…",
            async (progress, token) =>
            {
                VendorJavaInstaller installer = new(YamlRead.EnsureGameDirectory());
                return await installer.InstallCandidateAsync(candidate,
                    new Progress<VendorJavaProgress>(item => progress.Report(
                        $"{item.Phase} · {Ui.FormatBytes(item.CompletedBytes)}" +
                        (item.TotalBytes > 0
                            ? $"/{Ui.FormatBytes(item.TotalBytes)} · {item.CompletedBytes * 100.0 / item.TotalBytes:0.#}%"
                            : string.Empty))),
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        if (ok && installed is not null)
        {
            YamlRead.AddJava(installed.JavaExecutablePath, installed.MajorVersion?.ToString() ?? candidate.BuildVersion);
            YamlRead.SetPrimaryJava(installed.JavaExecutablePath);
            Shell.SetStatus($"Java 已安装：{installed.DisplayName}");
            RefreshCurrent();
            Ui.Info("Java 已安装", $"{installed.DisplayName}\n{installed.JavaExecutablePath}\n\n已设为默认 Java。");
        }
    }

    /// <summary>管理 runtime 目录下的已安装 Java（对应桌面版 Java 页的已安装列表）。</summary>
    private void ManageJavaRuntimes()
    {
        string gameDirectory = YamlRead.EnsureGameDirectory();
        IReadOnlyList<VendorJavaRuntime> runtimes = VendorJavaInstaller.GetInstalledRuntimes(gameDirectory);
        if (runtimes.Count == 0)
        {
            Ui.Info("已安装 Java", "runtime 目录下还没有已安装的 Java 运行时。");
            return;
        }

        string? picked = Ui.Select("已安装的 Java", runtimes
            .Select(runtime => $"{runtime.DisplayName} · {runtime.JavaExecutablePath}")
            .ToList());
        if (picked is null)
        {
            return;
        }

        VendorJavaRuntime? runtime = runtimes.FirstOrDefault(item =>
            $"{item.DisplayName} · {item.JavaExecutablePath}" == picked);
        if (runtime is null)
        {
            return;
        }

        string? action = Ui.Select(runtime.DisplayName, ["设为默认 Java", "删除运行时"]);
        if (action == "设为默认 Java")
        {
            YamlRead.AddJava(runtime.JavaExecutablePath, runtime.MajorVersion?.ToString() ?? "unknown");
            YamlRead.SetPrimaryJava(runtime.JavaExecutablePath);
            Shell.SetStatus($"默认 Java：{runtime.DisplayName}");
            Ui.Info("默认 Java", $"已设为 {runtime.DisplayName}\n{runtime.JavaExecutablePath}");
            return;
        }

        if (action == "删除运行时" &&
            Ui.Confirm("删除 Java", $"确定删除 {runtime.DisplayName}？\n{runtime.DirectoryPath}"))
        {
            try
            {
                new VendorJavaInstaller(gameDirectory).DeleteRuntime(runtime.DirectoryPath);
                Shell.SetStatus($"已删除 Java：{runtime.DisplayName}");
                RefreshCurrent();
            }
            catch (Exception exception)
            {
                Ui.Error("删除失败", exception.Message);
            }
        }
    }

    private async Task InstallModrinthAsync(ModrinthProject project, InstanceItem? instance)
    {
        string type = string.IsNullOrWhiteSpace(project.ProjectType) ? Shell.Settings.ModrinthType : project.ProjectType;
        if (type == "modpack")
        {
            await InstallModpackAsync(project).ConfigureAwait(false);
            return;
        }

        if (instance is null)
        {
            Ui.Info("安装资源", "请先到「实例」页选中一个实例，再安装模组 / 资源包 / 光影。");
            return;
        }

        string? subdirectory = type switch
        {
            "mod" => "mods",
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            _ => null,
        };
        if (subdirectory is null)
        {
            Ui.Info("安装资源", $"TUI 暂不支持直接安装 {project.TypeDisplay}。");
            return;
        }

        string loader = type == "mod" ? LoaderCategory(instance.Loader) ?? string.Empty : string.Empty;
        IReadOnlyList<ModrinthVersion> versions = await ModrinthResourceDownload
            .GetVersionsAsync(project.ProjectId, new[] { instance.MinecraftVersion },
                string.IsNullOrWhiteSpace(loader) ? null : new[] { loader }).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            Ui.Info("安装资源", $"没有找到适配 {instance.MinecraftVersion} {(string.IsNullOrWhiteSpace(loader) ? string.Empty : loader)} 的版本。");
            return;
        }

        string? picked = Ui.Select("选择版本", versions.Take(20)
            .Select(version => $"{version.DisplayName} · {version.VersionTypeDisplay} · {version.DateDisplay}")
            .ToList());
        if (picked is null)
        {
            return;
        }

        ModrinthVersion? chosen = versions.Take(20)
            .FirstOrDefault(version => $"{version.DisplayName} · {version.VersionTypeDisplay} · {version.DateDisplay}" == picked);
        if (chosen is null)
        {
            return;
        }

        string target = Path.Combine(instance.Directory, subdirectory);
        (bool ok, ModrinthDownloadResult? result) = await Ui.RunTaskAsync(
            "安装 " + project.Title,
            "正在下载并校验文件…",
            async (progress, token) =>
            {
                return await ModrinthResourceDownload.DownloadWithDependenciesAsync(
                    chosen.Id, target, type == "mod", instance.MinecraftVersion,
                    string.IsNullOrWhiteSpace(loader) ? null : loader,
                    new Progress<ModrinthDownloadProgress>(item =>
                        progress.Report($"{item.Completed}/{item.Total} · {item.Detail}")),
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (ok && result is not null)
        {
            string warnings = result.Warnings.Count > 0
                ? "\n\n警告：\n" + string.Join("\n", result.Warnings.Take(5))
                : string.Empty;
            Shell.SetStatus($"已安装 {project.Title} · {result.Files.Count} 个文件");
            Ui.Info("安装完成", $"已安装到 {target}\n文件数：{result.Files.Count}{warnings}");
        }
    }

    private async Task InstallModpackAsync(ModrinthProject project)
    {
        IReadOnlyList<ModrinthVersion> versions = await ModrinthResourceDownload
            .GetVersionsAsync(project.ProjectId, null, null).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            Ui.Info("安装整合包", "没有可用版本。");
            return;
        }

        string? picked = Ui.Select("选择整合包版本", versions.Take(20)
            .Select(version => $"{version.DisplayName} · {version.VersionTypeDisplay} · {version.DateDisplay}")
            .ToList());
        if (picked is null)
        {
            return;
        }

        ModrinthVersion? chosen = versions.Take(20)
            .FirstOrDefault(version => $"{version.DisplayName} · {version.VersionTypeDisplay} · {version.DateDisplay}" == picked);
        if (chosen is null)
        {
            return;
        }

        (bool ok, string? instanceId) = await Ui.RunTaskAsync(
            "安装整合包 " + project.Title,
            "正在下载 .mrpack…",
            async (progress, token) =>
            {
                string gameDirectory = YamlRead.GameDirectory;
                string downloads = Path.Combine(gameDirectory, "downloads");
                Directory.CreateDirectory(downloads);
                ModrinthVersionFile? primary = chosen.Files.FirstOrDefault(file => file.Primary)
                                               ?? chosen.Files.FirstOrDefault();
                if (primary is null)
                {
                    throw new InvalidOperationException("整合包没有可下载的主文件。");
                }

                progress.Report("下载主文件…");
                string mrpack = await ModrinthResourceDownload
                    .DownloadVersionFileAsync(primary, downloads, null, token).ConfigureAwait(false);

                string instanceName = SanitizeDirectoryName(
                    string.IsNullOrWhiteSpace(chosen.Name) ? chosen.VersionNumber : chosen.Name);
                string instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);
                Directory.CreateDirectory(instanceDirectory);
                progress.Report("解压 overrides 并下载依赖…");

                ModpackInstallResult result = await ModrinthBundleInstall.InstallAsync(
                    mrpack,
                    instanceDirectory,
                    new ModpackInstallOptions
                    {
                        InstallLoader = true,
                        JavaExecutable = YamlRead.JavaExecutable,
                    },
                    new Progress<ModpackInstallProgress>(item =>
                        progress.Report($"{item.Phase} · {item.CompletedFiles}/{item.TotalFiles}")),
                    token).ConfigureAwait(false);

                string innerVersions = Path.Combine(instanceDirectory, "versions");
                if (Directory.Exists(innerVersions))
                {
                    string gameVersions = Path.Combine(gameDirectory, "versions");
                    Directory.CreateDirectory(gameVersions);
                    foreach (string directory in Directory.GetDirectories(innerVersions))
                    {
                        CopyDirectory(directory, Path.Combine(gameVersions, Path.GetFileName(directory)));
                    }
                }

                if (result.Errors.Count > 0)
                {
                    throw new InvalidOperationException(string.Join("；", result.Errors));
                }

                progress.Report($"完成：{result.InstalledFiles} 个文件，加载器 {result.InstalledLoaderVersionId}");
                return instanceName;
            }).ConfigureAwait(false);

        if (ok && !string.IsNullOrWhiteSpace(instanceId))
        {
            Shell.Settings.SelectedInstance = instanceId;
            Shell.Settings.Save();
            Shell.SetStatus($"整合包已安装：{instanceId}");
            RefreshCurrent();
            Ui.Info("安装完成", $"整合包已安装：{instanceId}\n可到「实例」页启动。");
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

    private static string SanitizeDirectoryName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().Trim('.');
        return name.Length == 0 ? "modpack" : name;
    }

    private InstanceItem? FindSelectedInstance()
    {
        if (string.IsNullOrWhiteSpace(Shell.Settings.SelectedInstance))
        {
            return null;
        }

        return new InstanceService(Shell.Accounts, Shell.Settings).Scan()
            .FirstOrDefault(item => item.Id == Shell.Settings.SelectedInstance);
    }

    private static string? LoaderCategory(string loader)
    {
        return loader switch
        {
            "Fabric" => "fabric",
            "Quilt" => "quilt",
            "Forge" => "forge",
            "NeoForge" => "neoforge",
            "OptiFine" => "optifine",
            _ => null,
        };
    }

    private void UpdateDetail()
    {
        if (_mode == Mode.Java)
        {
            string java = string.IsNullOrWhiteSpace(YamlRead.JavaExecutable) ? "自动检测" : YamlRead.JavaExecutable;
            int installed = VendorJavaInstaller.GetInstalledRuntimes(YamlRead.EnsureGameDirectory()).Count;
            string header = $"默认 Java：{java}\n平台：{VendorJavaInstaller.GetPlatformDisplayName()}  " +
                            $"当前厂商：{VendorJavaRuntime.VendorDisplayName(_javaVendor)}  " +
                            $"已安装：{installed} 个";
            int index = _results.SelectedItem ?? -1;
            _detail.Text = index >= 0 && index < _entries.Count
                ? header + "\n\n" + _entries[index].Detail
                : header + "\n\nEnter 下载选中的运行时；「选项」切换厂商，「已安装 Java」管理本机运行时。";
            return;
        }

        int selected = _results.SelectedItem ?? -1;
        _detail.Text = selected >= 0 && selected < _entries.Count
            ? _entries[selected].Detail
            : "没有可显示的内容。";
    }

    private void InstallSelected()
    {
        int index = _results.SelectedItem ?? -1;
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        _ = _entries[index].Install();
    }

    private void EditOptions()
    {
        if (_mode != Mode.Java)
        {
            Ui.Info("选项", "当前分类没有可调整的选项。");
            return;
        }

        string? vendor = Ui.Select("Java 厂商", ["Zulu", "Temurin", "Oracle"]);
        if (vendor is null)
        {
            return;
        }

        _javaVendor = vendor == "Temurin"
            ? VendorJavaKind.Temurin
            : vendor == "Oracle"
                ? VendorJavaKind.Oracle
                : VendorJavaKind.Zulu;
        RefreshCurrent();
    }

    private static void OpenGameFolder()
    {
        try
        {
            string directory = YamlRead.EnsureGameDirectory();
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Ui.Error("打开目录失败", exception.Message);
        }
    }
}
