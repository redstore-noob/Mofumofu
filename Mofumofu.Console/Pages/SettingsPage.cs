using System.Diagnostics;
using MofuCore.Config;
using MofuCore.Java;
using MofuCore.Server;
using MofuCore.Tools;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class SettingsPage : MofuPage
{
    private static readonly string[] Actions =
    [
        "游戏目录",
        "下载源",
        "首选 Java（选择 / 自动检索 / 手动添加）",
        "自动下载 Java 运行时",
        "内存分配",
        "游戏窗口尺寸 / 全屏",
        "版本隔离（新实例）",
        "启动前校验游戏文件",
        "回档（自动快照）设置",
        "打开配置目录",
        "打开服务器目录",
    ];

    private readonly ListView _list;
    private readonly TextView _detail;

    public SettingsPage(IMofuShell shell) : base(shell, "设置")
    {
        _list = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Percent(42),
            Height = Dim.Fill(1),
            Title = " 设置项 ",
            BorderStyle = LineStyle.Single,
        };
        _list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(Actions));
        _list.SelectedItem = 0;
        _list.ValueChanged += (_, _) => UpdateDetail();

        _detail = new TextView
        {
            X = Pos.Percent(42) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
        };

        Add(_list, _detail);
        _list.Accepted += (_, _) => Execute(_list.SelectedItem ?? -1);
    }

    public override void Refresh()
    {
        UpdateDetail();
        Shell.SetStatus("设置 · Enter 编辑当前项");
    }

    private void UpdateDetail()
    {
        GlobalLaunchSettings global = YamlRead.LoadGlobalLaunchSettings();
        RewindSettings rewind = RewindSystem.LoadSettings();
        int index = _list.SelectedItem ?? -1;
        string title = index >= 0 && index < Actions.Length ? Actions[index] : string.Empty;

        _detail.Text = string.Join(Environment.NewLine, new[]
        {
            title,
            new string('─', Math.Max(10, title.Length + 8)),
            string.Empty,
            $"游戏目录      {YamlRead.GameDirectory}",
            $"下载源        {Shell.Settings.DownloadSource}（BMCL 镜像 / Official 官方）",
            $"首选 Java     {(string.IsNullOrWhiteSpace(YamlRead.JavaExecutable) ? "（未设置，启动时自动检测）" : YamlRead.JavaExecutable)}",
            $"内存          {global.MinimumMemoryMb} – {global.MaximumMemoryMb} MB{(global.AutomaticMemory ? "（自动）" : string.Empty)}",
            $"窗口          {global.WindowWidth} × {global.WindowHeight}{(global.LaunchFullscreen ? " · 全屏" : string.Empty)}",
            $"版本隔离      {(YamlRead.DefaultVersionIsolation ? "开启" : "关闭")}",
            $"启动前校验    {(Shell.Settings.VerifyBeforeLaunch ? "开启" : "关闭")}",
            $"自动快照      {(rewind.AutoSnapshotEnabled ? $"开启 · 每 {rewind.AutoSnapshotIntervalSeconds / 60} 分钟" : "关闭")}",
            $"快照保留      最多 {rewind.MaximumAutoSnapshots} 份 / {Ui.FormatBytes(rewind.MaximumBytes)}" +
                $"{(rewind.KeepDays > 0 ? $" · {rewind.KeepDays} 天" : string.Empty)}",
            $"救砖保障      {(rewind.RescueEnabled ? "开启" : "关闭")}",
            $"服务器目录    {ServerManage.ServerRootDirectory}",
            $"配置目录      {YamlRead.ConfigDirectory}",
            string.Empty,
            "Enter 编辑当前选中的设置项。",
        });
    }

    private void Execute(int index)
    {
        switch (index)
        {
            case 0:
                EditGameDirectory();
                break;
            case 1:
                EditDownloadSource();
                break;
            case 2:
                EditJava();
                break;
            case 3:
                InstallJava();
                break;
            case 4:
                EditMemory();
                break;
            case 5:
                EditWindow();
                break;
            case 6:
                YamlRead.SaveDefaultVersionIsolation(!YamlRead.DefaultVersionIsolation);
                Refresh();
                break;
            case 7:
                Shell.Settings.VerifyBeforeLaunch = !Shell.Settings.VerifyBeforeLaunch;
                Shell.Settings.Save();
                Refresh();
                break;
            case 8:
                EditRewind();
                break;
            case 9:
                OpenDirectory(YamlRead.ConfigDirectory);
                break;
            case 10:
                OpenDirectory(ServerManage.ServerRootDirectory);
                break;
        }
    }

    private void EditGameDirectory()
    {
        string? path = Ui.Prompt("游戏目录", "Minecraft 根目录（.minecraft）", YamlRead.GameDirectory);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            YamlRead.SaveGameDirectory(path);
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("保存游戏目录失败", exception.Message);
        }
    }

    private void EditDownloadSource()
    {
        string? choice = Ui.Select("下载源", ["BMCL 镜像", "Official 官方"],
            Shell.Settings.DownloadSource == "Official" ? 1 : 0);
        if (choice is null)
        {
            return;
        }

        Shell.Settings.DownloadSource = choice.StartsWith("Official", StringComparison.Ordinal) ? "Official" : "BMCL";
        Shell.Settings.Save();
        Shell.Settings.ApplyDownloadSource();
        Refresh();
    }

    private void EditJava()
    {
        string? choice = Ui.Select("Java 运行时", ["选择已保存的 Java", "自动检索本机 Java", "手动输入 java 路径"], 1);
        if (choice is null)
        {
            return;
        }

        if (choice == "选择已保存的 Java")
        {
            IReadOnlyList<JavaPathItem> paths = YamlRead.GetJavaPaths();
            if (paths.Count == 0)
            {
                Ui.Info("首选 Java", "还没有保存任何 Java，请先自动检索或手动添加。");
                return;
            }

            string? picked = Ui.Select("选择首选 Java", paths.Select(item => item.DisplayName).ToList());
            if (picked is null)
            {
                return;
            }

            JavaPathItem? item = paths.FirstOrDefault(path => path.DisplayName == picked);
            if (item is not null)
            {
                YamlRead.SetPrimaryJava(item.Path);
                Refresh();
            }

            return;
        }

        if (choice == "手动输入 java 路径")
        {
            string? path = Ui.Prompt("添加 Java", "java 可执行文件路径", YamlRead.JavaExecutable);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            JavaRuntime? runtime = JavaPathSearch.Detect(path);
            if (runtime is null)
            {
                Ui.Error("添加 Java 失败", "该路径不是可用的 java 可执行文件。");
                return;
            }

            YamlRead.AddJava(runtime.JavaPath, runtime.VersionString);
            YamlRead.SetPrimaryJava(runtime.JavaPath);
            Refresh();
            return;
        }

        _ = DetectJavaAsync();
    }

    private async Task DetectJavaAsync()
    {
        (bool ok, IReadOnlyList<JavaRuntime>? runtimes) = await Ui.RunTaskAsync(
            "自动检索 Java",
            "正在扫描 PATH、JAVA_HOME 与游戏目录 runtime…",
            async (progress, token) =>
            {
                string runtimeRoot = JavaDownload.GetRuntimeDirectory(YamlRead.GameDirectory);
                progress.Report("扫描常见安装目录与游戏 runtime…");
                IReadOnlyList<JavaRuntime> found = await JavaPathSearch.FindAllJavaAsync(runtimeRoot, token)
                    .ConfigureAwait(false);
                progress.Report($"找到 {found.Count} 个 Java。");
                return found;
            }).ConfigureAwait(false);

        if (!ok || runtimes is null || runtimes.Count == 0)
        {
            return;
        }

        string? picked = Ui.Select("选择要保存的 Java",
            runtimes.Select(runtime => $"{runtime.DisplayName} · {runtime.JavaPath}").ToList());
        if (picked is null)
        {
            return;
        }

        JavaRuntime? chosen = runtimes.FirstOrDefault(runtime =>
            $"{runtime.DisplayName} · {runtime.JavaPath}" == picked);
        if (chosen is null)
        {
            return;
        }

        YamlRead.AddJava(chosen.JavaPath, chosen.VersionString);
        YamlRead.SetPrimaryJava(chosen.JavaPath);
        Refresh();
        Ui.Info("首选 Java", $"已设置：{chosen.DisplayName}\n{chosen.JavaPath}");
    }

    private void InstallJava()
    {
        string? majorText = Ui.Select("自动下载 Java 运行时", ["Java 21（1.20.5+）", "Java 25（26.x）", "Java 17（1.17–1.20.4）", "Java 8（旧版本）"], 0);
        if (majorText is null)
        {
            return;
        }

        int major = majorText.Contains("25") ? 25 : majorText.Contains("17") ? 17 : majorText.Contains("8") ? 8 : 21;
        _ = InstallJavaAsync(major);
    }

    private async Task InstallJavaAsync(int major)
    {
        (bool ok, InstalledJavaRuntime? runtime) = await Ui.RunTaskAsync(
            $"下载 Java {major}",
            "正在从 Mojang 获取运行时清单…",
            async (progress, token) =>
            {
                InstalledJavaRuntime? installed = await JavaDownload
                    .InstallForMajorVersionAsync(major, YamlRead.GameDirectory,
                        new Progress<JavaRuntimeInstallProgress>(item =>
                            progress.Report($"{item.Phase} {item.CompletedBytes * 100.0 / Math.Max(1, item.TotalBytes):0.#}%")),
                        token)
                    .ConfigureAwait(false);
                return installed;
            }).ConfigureAwait(false);

        if (ok && runtime is not null)
        {
            YamlRead.AddJava(runtime.JavaExecutablePath,
                string.IsNullOrWhiteSpace(runtime.VersionName) ? major.ToString() : runtime.VersionName);
            YamlRead.SetPrimaryJava(runtime.JavaExecutablePath);
            Refresh();
            Ui.Info("Java 已安装", $"{runtime.VersionName}\n{runtime.JavaExecutablePath}");
        }
        else if (ok)
        {
            Ui.Info("Java 运行时", $"当前平台没有可用的 Java {major} 组件。");
        }
    }

    private void EditMemory()
    {
        GlobalLaunchSettings settings = YamlRead.LoadGlobalLaunchSettings();
        Dictionary<string, string>? values = Ui.Form("内存分配",
            ("min", "最小内存（MB）", settings.MinimumMemoryMb.ToString(), false),
            ("max", "最大内存（MB）", settings.MaximumMemoryMb.ToString(), false));
        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values["min"], out int minimum) || !int.TryParse(values["max"], out int maximum) ||
            minimum <= 0 || maximum < minimum)
        {
            Ui.Error("内存分配", "请输入有效数字：最大内存必须大于等于最小内存。");
            return;
        }

        settings.MinimumMemoryMb = minimum;
        settings.MaximumMemoryMb = maximum;
        settings.AutomaticMemory = false;
        YamlRead.SaveGlobalLaunchSettings(settings);
        Shell.Settings.MinimumMemoryMb = minimum;
        Shell.Settings.MaximumMemoryMb = maximum;
        Shell.Settings.Save();
        Refresh();
    }

    private void EditWindow()
    {
        GlobalLaunchSettings settings = YamlRead.LoadGlobalLaunchSettings();
        Dictionary<string, string>? values = Ui.Form("游戏窗口",
            ("width", "宽度", settings.WindowWidth.ToString(), false),
            ("height", "高度", settings.WindowHeight.ToString(), false),
            ("fullscreen", "全屏启动（true / false）", settings.LaunchFullscreen ? "true" : "false", false));
        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values["width"], out int width) || !int.TryParse(values["height"], out int height))
        {
            Ui.Error("游戏窗口", "宽度和高度必须是数字。");
            return;
        }

        settings.WindowWidth = width;
        settings.WindowHeight = height;
        settings.LaunchFullscreen = values["fullscreen"].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!YamlRead.SaveGlobalLaunchSettings(settings))
        {
            Ui.Error("游戏窗口", "保存失败：窗口尺寸超出允许范围。");
            return;
        }

        Refresh();
    }

    private void EditRewind()
    {
        RewindSettings settings = RewindSystem.LoadSettings();
        Dictionary<string, string>? values = Ui.Form("回档设置",
            ("auto", "自动快照（true / false）", settings.AutoSnapshotEnabled ? "true" : "false", false),
            ("interval", "间隔（秒）", settings.AutoSnapshotIntervalSeconds.ToString(), false),
            ("max", "自动快照上限（份）", settings.MaximumAutoSnapshots.ToString(), false),
            ("bytes", "空间上限（MB）", Math.Max(0, settings.MaximumBytes / 1024 / 1024).ToString(), false),
            ("days", "保留天数（0 = 不限）", settings.KeepDays.ToString(), false),
            ("rescue", "崩溃救砖（true / false）", settings.RescueEnabled ? "true" : "false", false));
        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values["interval"], out int interval) ||
            !int.TryParse(values["max"], out int max) ||
            !long.TryParse(values["bytes"], out long megabytes) ||
            !int.TryParse(values["days"], out int days))
        {
            Ui.Error("回档设置", "数值格式不正确。");
            return;
        }

        settings.AutoSnapshotEnabled = values["auto"].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        settings.AutoSnapshotIntervalSeconds = Math.Max(60, interval);
        settings.MaximumAutoSnapshots = Math.Max(1, max);
        settings.MaximumBytes = Math.Max(0, megabytes) * 1024 * 1024;
        settings.KeepDays = Math.Max(0, days);
        settings.RescueEnabled = values["rescue"].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        RewindSystem.SaveSettings(settings);
        Refresh();
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
