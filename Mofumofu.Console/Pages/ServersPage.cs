using System.Collections.ObjectModel;
using System.Diagnostics;
using MofuCore.Config;
using MofuCore.Download.Minecraft;
using MofuCore.Java;
using MofuCore.Server;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class ServersPage : MofuPage
{
    private readonly ListView _list;
    private readonly TextView _detail;
    private List<ServerInfo> _servers = [];

    public ServersPage(IMofuShell shell) : base(shell, "服务器")
    {
        _list = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Percent(45),
            Height = Dim.Fill(3),
            Title = " 服务器 ",
            BorderStyle = LineStyle.Single,
        };
        _list.SetSource(new ObservableCollection<string>());
        _list.ValueChanged += (_, _) => UpdateDetail();
        _list.Accepted += (_, _) => ManageSelected();

        _detail = new TextView
        {
            X = Pos.Percent(45) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = true,
        };

        Button create = new() { Text = "创建", X = 1, Y = Pos.AnchorEnd(2) };
        Button toggle = new() { Text = "启动/停止", X = 11, Y = Pos.AnchorEnd(2) };
        Button console = new() { Text = "控制台", X = 27, Y = Pos.AnchorEnd(2) };
        Button manage = new() { Text = "管理", X = 40, Y = Pos.AnchorEnd(2) };
        Button refresh = new() { Text = "刷新", X = 51, Y = Pos.AnchorEnd(2) };

        create.Accepted += (_, _) => CreateServer();
        toggle.Accepted += (_, _) => ToggleServer();
        console.Accepted += (_, _) => OpenConsole();
        manage.Accepted += (_, _) => ManageSelected();
        refresh.Accepted += (_, _) => Refresh();

        Add(_list, _detail, create, toggle, console, manage, refresh);
    }

    public override void Refresh()
    {
        Shell.SetStatus("正在读取服务器列表…");
        Task.Run(() =>
        {
            List<ServerInfo> servers = ServerManage.ListServerInfos().ToList();
            Ui.Post(() =>
            {
                _servers = servers;
                ObservableCollection<string> display = new();
                foreach (ServerInfo server in servers)
                {
                    string status = StatusText(server.Status);
                    display.Add($"{StatusGlyph(server.Status)} {server.Name} · {CoreText(server.Core)} {server.McVersion} · {status}");
                }

                _list.SetSource(display);
                if (servers.Count > 0)
                {
                    int index = servers.FindIndex(server => server.Id == Shell.Settings.SelectedInstance);
                    _list.SelectedItem = index >= 0 ? index : 0;
                }
                else
                {
                    _list.SelectedItem = null;
                }

                UpdateDetail();
                Shell.SetStatus($"服务器 {servers.Count} 个 · 运行中 {servers.Count(server => server.Status == ServerManage.StatusRunning)} 个");
            });
        });
    }

    private ServerInfo? Selected =>
        _list.SelectedItem is int index && index >= 0 && index < _servers.Count ? _servers[index] : null;

    private static string StatusText(string status) => status switch
    {
        ServerManage.StatusStarting => "启动中",
        ServerManage.StatusRunning => "运行中",
        ServerManage.StatusStopping => "停止中",
        _ => "已停止",
    };

    private static string StatusGlyph(string status) => status switch
    {
        ServerManage.StatusRunning => "●",
        ServerManage.StatusStarting or ServerManage.StatusStopping => "◐",
        _ => "○",
    };

    private static string CoreText(string core) => core switch
    {
        ServerManage.CorePaper => "Paper",
        ServerManage.CoreFabric => "Fabric",
        ServerManage.CoreNeoForge => "NeoForge",
        _ => "Vanilla",
    };

    private void UpdateDetail()
    {
        ServerInfo? server = Selected;
        if (server is null)
        {
            _detail.Text = "还没有服务器。\n\n按「创建」选择核心与版本，TUI 会下载服务端并生成启动脚本。";
            return;
        }

        ServerConfig? config = ServerManage.LoadServerConfig(server.Id);
        _detail.Text = string.Join(Environment.NewLine, new[]
        {
            server.Name,
            new string('─', Math.Min(40, Math.Max(10, server.Name.Length * 2))),
            string.Empty,
            $"ID          {server.Id}",
            $"核心        {CoreText(server.Core)} {server.CoreVersion}",
            $"Minecraft   {server.McVersion}",
            $"端口        {server.Port}",
            $"状态        {StatusText(server.Status)}" + (server.Status == ServerManage.StatusRunning ? $" · 在线 {server.Players}/{server.MaxPlayers}" : string.Empty),
            $"内存        {(config?.MemoryMb > 0 ? config.MemoryMb + " MB" : "继承默认")}",
            $"Java        {(string.IsNullOrWhiteSpace(config?.JavaPath) ? "自动检测" : config.JavaPath)}",
            $"目录        {ServerManage.ServerDirectory(server.Id)}",
            string.Empty,
            "Enter 打开管理菜单；控制台按钮可实时查看日志并执行指令。",
        });
    }

    private void ToggleServer()
    {
        ServerInfo? server = Selected;
        if (server is null)
        {
            return;
        }

        if (ServerManage.IsRunning(server.Id))
        {
            if (!Ui.Confirm("停止服务器", $"确定停止 {server.Name} 吗？"))
            {
                return;
            }

            _ = StopAsync(server.Id);
            return;
        }

        _ = StartAsync(server.Id);
    }

    private async Task StartAsync(string id)
    {
        bool ok = await Ui.RunTaskAsync("启动服务器", "正在启动 " + id + "…", async (progress, token) =>
        {
            await EnsureJavaAsync(id, progress, token).ConfigureAwait(false);
            await ServerManage.StartAsync(id, token).ConfigureAwait(false);
            progress.Report("进程已创建，等待服务器就绪…");

            long cursor = 0;
            DateTime deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                ServerSnapshot snapshot = ServerManage.Poll(id, cursor);
                cursor = snapshot.NextCursor;
                foreach (string line in snapshot.LogLines.TakeLast(20))
                {
                    progress.Report(line);
                }

                if (snapshot.Status == ServerManage.StatusRunning)
                {
                    progress.Report("服务器已进入运行状态。");
                    return;
                }

                if (snapshot.Status == ServerManage.StatusStopped && ServerManage.GetPid(id) == 0)
                {
                    throw new InvalidOperationException("服务器进程已退出，请查看控制台日志。");
                }

                await Task.Delay(500, token).ConfigureAwait(false);
            }

            throw new TimeoutException("等待服务器就绪超时（3 分钟）。");
        }).ConfigureAwait(false);

        if (ok)
        {
            Refresh();
            Shell.SetStatus("服务器已启动：" + id);
        }
    }

    private async Task StopAsync(string id)
    {
        bool ok = await Ui.RunTaskAsync("停止服务器", "正在停止 " + id + "…", async (progress, token) =>
        {
            await ServerManage.StopAsync(id).ConfigureAwait(false);
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && ServerManage.IsRunning(id))
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(500, token).ConfigureAwait(false);
            }

            if (ServerManage.IsRunning(id))
            {
                progress.Report("正常停止超时，正在强制结束进程…");
                await ServerManage.StopAsync(id, true).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (ok)
        {
            Refresh();
            Shell.SetStatus("服务器已停止：" + id);
        }
    }

    private static async Task EnsureJavaAsync(string id, IProgress<string> progress, CancellationToken token)
    {
        ServerConfig? config = ServerManage.LoadServerConfig(id);
        if (config is null)
        {
            throw new InvalidOperationException("服务器配置不存在：" + id);
        }

        if (!string.IsNullOrWhiteSpace(config.JavaPath) && File.Exists(config.JavaPath))
        {
            return;
        }

        progress.Report("未指定 Java，正在自动查找…");
        string? java = await JavaPathSearch.FindJavaExecutableAsync(
            YamlRead.JavaExecutable,
            ServerManage.RequiredJavaMajorVersion(config.McVersion),
            JavaDownload.GetRuntimeDirectory(YamlRead.GameDirectory),
            token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(java))
        {
            throw new InvalidOperationException("没有找到可用的 Java 运行时。请到「设置 → 自动下载 Java 运行时」安装。");
        }

        ServerManage.SetJavaPath(id, java);
        progress.Report("使用 Java：" + java);
    }

    private void CreateServer()
    {
        Dictionary<string, string>? form = Ui.Form("创建服务器",
            ("name", "服务器名称", "Mofu Server", false),
            ("port", "端口", "25565", false),
            ("players", "最大玩家数", "10", false),
            ("memory", "最大内存（MB）", "2048", false));
        if (form is null)
        {
            return;
        }

        if (!int.TryParse(form["port"], out int port) || port < 1 || port > 65535)
        {
            Ui.Error("创建服务器", "端口必须是 1–65535 的数字。");
            return;
        }

        if (!int.TryParse(form["players"], out int maxPlayers) || maxPlayers < 1)
        {
            Ui.Error("创建服务器", "最大玩家数必须大于 0。");
            return;
        }

        if (!int.TryParse(form["memory"], out int memory) || memory < 512)
        {
            Ui.Error("创建服务器", "最大内存至少 512 MB。");
            return;
        }

        Task.Run(async () =>
        {
            IReadOnlyList<MinecraftVersion> versions = await MinecraftDownload.GetVersionsAsync().ConfigureAwait(false);
            List<string> releases = versions.Where(version => version.Type == "release")
                .Take(40).Select(version => version.Id).ToList();
            Ui.Post(() =>
            {
                string? mc = Ui.Select("选择 Minecraft 版本", releases);
                if (mc is null)
                {
                    return;
                }

                string? coreName = Ui.Select("选择服务端核心", ["Vanilla 原版", "Paper 插件端", "Fabric 模组端", "NeoForge 模组端"]);
                if (coreName is null)
                {
                    return;
                }

                string core = coreName.StartsWith("Paper", StringComparison.Ordinal) ? ServerManage.CorePaper
                    : coreName.StartsWith("Fabric", StringComparison.Ordinal) ? ServerManage.CoreFabric
                    : coreName.StartsWith("NeoForge", StringComparison.Ordinal) ? ServerManage.CoreNeoForge
                    : ServerManage.CoreVanilla;
                _ = CreateServerAsync(form["name"], port, maxPlayers, memory, mc, core);
            });
        });
    }

    private async Task CreateServerAsync(string name, int port, int maxPlayers, int memory,
        string mcVersion, string core)
    {
        (bool ok, string? id) = await Ui.RunTaskAsync(
            "创建服务器",
            "正在准备下载服务端…",
            async (progress, token) =>
            {
                string java = string.Empty;
                string? found = await JavaPathSearch.FindJavaExecutableAsync(
                    YamlRead.JavaExecutable,
                    ServerManage.RequiredJavaMajorVersion(mcVersion),
                    JavaDownload.GetRuntimeDirectory(YamlRead.GameDirectory),
                    token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    java = found;
                    progress.Report("使用 Java：" + found);
                }
                else
                {
                    progress.Report("警告：未找到可用 Java，创建后需到设置中安装/选择 Java。");
                }

                string created = await ServerManage.CreateServerAsync(new CreateOptions
                {
                    Name = name,
                    Core = core,
                    McVersion = mcVersion,
                    Port = port,
                    MaxPlayers = maxPlayers,
                    JavaPath = java,
                    AcceptEula = true,
                }, async (directory, config, ct) =>
                {
                    await ServerDownload.InstallAsync(new ServerDownload.InstallOptions
                    {
                        Core = CoreKind(core),
                        MinecraftVersion = mcVersion,
                        ServerDirectory = directory,
                        AcceptEula = true,
                        Port = config.Port,
                        MaxPlayers = config.MaxPlayers,
                        Motd = config.Name,
                    }, new Progress<ServerDownload.InstallProgress>(item =>
                        progress.Report($"{item.Detail} · {item.Percentage:0.#}%")), ct).ConfigureAwait(false);
                }, token).ConfigureAwait(false);

                ServerManage.SaveLaunchOptions(created, new LaunchOptions
                {
                    MemoryMb = memory,
                    MemoryMinMb = Math.Max(512, memory / 2),
                    ExtraJavaArgs = [],
                });
                return created;
            }).ConfigureAwait(false);

        if (ok && id is not null)
        {
            Refresh();
            Shell.SetStatus("服务器创建完成：" + id);
            Ui.Info("创建完成", $"服务器已创建：{name}\nID：{id}\n端口：{port}\n\n可在「服务器」页启动并打开控制台。");
        }
    }

    private static ServerDownload.CoreKind CoreKind(string core) => core switch
    {
        ServerManage.CorePaper => ServerDownload.CoreKind.Paper,
        ServerManage.CoreFabric => ServerDownload.CoreKind.Fabric,
        ServerManage.CoreNeoForge => ServerDownload.CoreKind.NeoForge,
        _ => ServerDownload.CoreKind.Vanilla,
    };

    private void OpenConsole()
    {
        ServerInfo? server = Selected;
        if (server is null)
        {
            return;
        }

        ShowConsole(server.Id);
    }

    private void ShowConsole(string id)
    {
        Dialog dialog = new()
        {
            Title = "控制台 · " + id,
            Width = Dim.Percent(92),
            Height = Dim.Percent(85),
        };
        TextView log = new()
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(5),
            ReadOnly = true,
            WordWrap = false,
        };
        TextField command = new()
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(13),
        };
        Label status = new() { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1) };
        Button send = new() { Text = "发送", X = Pos.AnchorEnd(11), Y = Pos.AnchorEnd(3) };
        Button close = new() { Text = "关闭", X = Pos.AnchorEnd(11), Y = Pos.AnchorEnd(1) };

        List<string> lines = [];
        void Append(string text)
        {
            lines.Add(text);
            if (lines.Count > 1500)
            {
                lines.RemoveRange(0, lines.Count - 1200);
            }

            log.Text = string.Join(Environment.NewLine, lines);
            log.MoveEnd();
        }

        ServerSnapshot initial = ServerManage.Poll(id, 0);
        foreach (string line in initial.LogLines)
        {
            lines.Add(line);
        }

        log.Text = string.Join(Environment.NewLine, lines);
        log.MoveEnd();
        status.Text = $"状态：{StatusText(initial.Status)}";

        Action<string, string> handler = (serverId, line) =>
        {
            if (serverId == id)
            {
                Ui.Post(() => Append(line));
            }
        };
        ServerManage.ConsoleLine += handler;

        object? timer = Application.AddTimeout(TimeSpan.FromSeconds(2), () =>
        {
            ServerSnapshot snapshot = ServerManage.Poll(id, 0);
            status.Text = $"状态：{StatusText(snapshot.Status)} · 在线 {snapshot.Players}";
            return true;
        });

        void SendCommand()
        {
            string text = command.Text?.ToString()?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                return;
            }

            command.Text = string.Empty;
            Append("> " + text);
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServerManage.SendCommandAsync(id, text).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Ui.Post(() => Append("[错误] " + exception.Message));
                }
            });
        }

        send.Accepted += (_, _) => SendCommand();
        command.KeyDown += (_, key) =>
        {
            if (key.KeyCode == Terminal.Gui.Drivers.KeyCode.Enter)
            {
                key.Handled = true;
                SendCommand();
            }
        };
        close.Accepted += (_, _) => Application.RequestStop(dialog);

        dialog.Add(log, command, status, send, close);
        Ui.EnableEsc(dialog);
        command.SetFocus();
        try
        {
            Application.Run(dialog);
        }
        finally
        {
            ServerManage.ConsoleLine -= handler;
            if (timer is not null)
            {
                Application.RemoveTimeout(timer);
            }
        }
    }

    private void ManageSelected()
    {
        ServerInfo? server = Selected;
        if (server is null)
        {
            return;
        }

        string? action = Ui.Select("管理 · " + server.Name,
        [
            "控制台",
            "属性编辑",
            "玩家 / 白名单 / OP / 封禁",
            "文件管理",
            "备份与还原",
            "内容管理（插件 / 模组）",
            "导出服务器包",
            "导入服务器包",
            "打开服务器目录",
            "启动参数（内存 / Java）",
            "删除服务器",
        ]);

        switch (action)
        {
            case "控制台":
                ShowConsole(server.Id);
                break;
            case "属性编辑":
                EditProperties(server.Id);
                break;
            case "玩家 / 白名单 / OP / 封禁":
                ManagePlayers(server.Id);
                break;
            case "文件管理":
                ManageFiles(server.Id);
                break;
            case "备份与还原":
                ManageBackups(server.Id);
                break;
            case "内容管理（插件 / 模组）":
                ManageContent(server.Id);
                break;
            case "导出服务器包":
                ExportPackage(server.Id);
                break;
            case "导入服务器包":
                ImportPackage();
                break;
            case "打开服务器目录":
                OpenFolder(ServerManage.ServerDirectory(server.Id));
                break;
            case "启动参数（内存 / Java）":
                EditLaunchOptions(server.Id);
                break;
            case "删除服务器":
                DeleteServer(server);
                break;
        }
    }

    private void EditProperties(string id)
    {
        while (true)
        {
            List<ServerProperty> properties = ServerProperties.Get(id).ToList();
            List<string> options = ["◀ 返回"];
            options.AddRange(properties.Select(property => $"{property.Key} = {property.Value}"));
            string? picked = Ui.Select("服务器属性（" + properties.Count + " 项）", options);
            if (picked is null || picked.StartsWith("◀", StringComparison.Ordinal))
            {
                return;
            }

            ServerProperty? property = properties.FirstOrDefault(item => $"{item.Key} = {item.Value}" == picked);
            if (property is null)
            {
                continue;
            }

            string? value = Ui.Prompt("编辑 " + property.Key, property.Key, property.Value);
            if (value is null)
            {
                continue;
            }

            try
            {
                ServerProperties.Set(id, [new ServerProperty { Key = property.Key, Value = value }]);
            }
            catch (Exception exception)
            {
                Ui.Error("保存属性失败", exception.Message);
            }
        }
    }

    private void ManagePlayers(string id)
    {
        while (true)
        {
            ServerPlayerSummary summary = ServerPlayers.Get(id);
            string? action = Ui.Select(
                $"玩家 · 在线 {summary.Online.Count} · 白名单 {summary.Whitelist.Count} · OP {summary.Ops.Count} · 封禁 {summary.Banned.Count}",
                ["查看概要", "添加白名单", "移除白名单", "添加 OP", "移除 OP", "添加封禁", "移除封禁",
                 summary.WhitelistEnabled ? "关闭白名单" : "开启白名单", "刷新", "返回"]);
            if (action is null || action == "返回")
            {
                return;
            }

            try
            {
                switch (action)
                {
                    case "查看概要":
                        Ui.Info("玩家概要", string.Join(Environment.NewLine, new[]
                        {
                            "在线：" + (summary.Online.Count == 0 ? "无" : string.Join(", ", summary.Online)),
                            "白名单：" + (summary.Whitelist.Count == 0 ? "无" : string.Join(", ", summary.Whitelist.Select(entry => entry.Name))),
                            "OP：" + (summary.Ops.Count == 0 ? "无" : string.Join(", ", summary.Ops.Select(entry => $"{entry.Name}(Lv{entry.Level})"))),
                            "封禁：" + (summary.Banned.Count == 0 ? "无" : string.Join(", ", summary.Banned.Select(entry => entry.Name))),
                        }));
                        break;
                    case "添加白名单":
                    {
                        string? name = Ui.Prompt("添加白名单", "玩家名");
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            List<WhitelistEntry> entries = [.. summary.Whitelist];
                            entries.Add(new WhitelistEntry { Name = name.Trim() });
                            ServerPlayers.SaveWhitelist(id, entries);
                        }
                        break;
                    }
                    case "移除白名单":
                    {
                        if (summary.Whitelist.Count == 0)
                        {
                            Ui.Info("移除白名单", "白名单为空。");
                            break;
                        }
                        string? picked = Ui.Select("移除白名单", summary.Whitelist.Select(entry => entry.Name).ToList());
                        if (picked is not null)
                        {
                            ServerPlayers.SaveWhitelist(id, summary.Whitelist.Where(entry => entry.Name != picked));
                        }
                        break;
                    }
                    case "添加 OP":
                    {
                        string? name = Ui.Prompt("添加 OP", "玩家名");
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            List<OpEntry> entries = [.. summary.Ops];
                            entries.Add(new OpEntry { Name = name.Trim(), Level = 4, BypassesPlayerLimit = true });
                            ServerPlayers.SaveOps(id, entries);
                        }
                        break;
                    }
                    case "移除 OP":
                    {
                        if (summary.Ops.Count == 0)
                        {
                            Ui.Info("移除 OP", "OP 列表为空。");
                            break;
                        }
                        string? picked = Ui.Select("移除 OP", summary.Ops.Select(entry => entry.Name).ToList());
                        if (picked is not null)
                        {
                            ServerPlayers.SaveOps(id, summary.Ops.Where(entry => entry.Name != picked));
                        }
                        break;
                    }
                    case "添加封禁":
                    {
                        string? name = Ui.Prompt("添加封禁", "玩家名");
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            List<BannedPlayerEntry> entries = [.. summary.Banned];
                            entries.Add(new BannedPlayerEntry
                            {
                                Name = name.Trim(),
                                Created = DateTimeOffset.UtcNow.ToString("O"),
                                Source = "Mofumofu TUI",
                                Expires = "forever",
                                Reason = "Banned by Mofumofu",
                            });
                            ServerPlayers.SaveBanned(id, entries);
                        }
                        break;
                    }
                    case "移除封禁":
                    {
                        if (summary.Banned.Count == 0)
                        {
                            Ui.Info("移除封禁", "封禁列表为空。");
                            break;
                        }
                        string? picked = Ui.Select("移除封禁", summary.Banned.Select(entry => entry.Name).ToList());
                        if (picked is not null)
                        {
                            ServerPlayers.SaveBanned(id, summary.Banned.Where(entry => entry.Name != picked));
                        }
                        break;
                    }
                    case "开启白名单":
                        ServerPlayers.SetWhitelistEnabled(id, true);
                        break;
                    case "关闭白名单":
                        ServerPlayers.SetWhitelistEnabled(id, false);
                        break;
                }
            }
            catch (Exception exception)
            {
                Ui.Error("玩家管理失败", exception.Message);
            }
        }
    }

    private void ManageFiles(string id)
    {
        string current = string.Empty;
        while (true)
        {
            IReadOnlyList<ServerFileEntry> entries = ServerFiles.List(id, current);
            List<string> options = ["◀ 返回上级", "＋ 新建文件", "＋ 新建文件夹"];
            foreach (ServerFileEntry fileEntry in entries.OrderByDescending(item => item.IsDir).ThenBy(item => item.Name))
            {
                options.Add((fileEntry.IsDir ? "[目录] " : "[文件] ") + fileEntry.Name);
            }

            string? picked = Ui.Select($"文件 · /{current}", options);
            if (picked is null)
            {
                return;
            }

            if (picked == "◀ 返回上级")
            {
                if (current.Length == 0)
                {
                    return;
                }
                current = Path.GetDirectoryName(current)?.Replace('\\', '/') ?? string.Empty;
                continue;
            }

            if (picked.StartsWith("＋ 新建文件", StringComparison.Ordinal))
            {
                string? name = Ui.Prompt("新建文件", "相对路径（例如 config/test.txt）", current.Length == 0 ? string.Empty : current + "/");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        ServerFiles.CreateTextFile(id, name);
                        current = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? current;
                    }
                    catch (Exception exception)
                    {
                        Ui.Error("新建文件失败", exception.Message);
                    }
                }
                continue;
            }

            if (picked.StartsWith("＋ 新建文件夹", StringComparison.Ordinal))
            {
                string? name = Ui.Prompt("新建文件夹", "目录名", current.Length == 0 ? string.Empty : current + "/");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        ServerFiles.CreateDirectory(id, name);
                    }
                    catch (Exception exception)
                    {
                        Ui.Error("新建文件夹失败", exception.Message);
                    }
                }
                continue;
            }

            string entryName = picked[5..];
            string relative = string.IsNullOrEmpty(current) ? entryName : current + "/" + entryName;
            ServerFileEntry? entry = entries.FirstOrDefault(item => item.Name == entryName);
            if (entry is null)
            {
                continue;
            }

            if (entry.IsDir)
            {
                current = relative;
                continue;
            }

            string? action = Ui.Select("文件操作 · " + entryName, ["编辑文本", "删除", "返回"]);
            if (action == "编辑文本")
            {
                try
                {
                    string content = ServerFiles.ReadText(id, relative);
                    string? edited = EditText(entryName, content);
                    if (edited is not null)
                    {
                        ServerFiles.WriteText(id, relative, edited);
                    }
                }
                catch (Exception exception)
                {
                    Ui.Error("编辑文件失败", exception.Message);
                }
            }
            else if (action == "删除")
            {
                if (Ui.Confirm("删除文件", "确定删除 " + relative + " 吗？"))
                {
                    try
                    {
                        ServerFiles.Delete(id, relative);
                    }
                    catch (Exception exception)
                    {
                        Ui.Error("删除文件失败", exception.Message);
                    }
                }
            }
        }
    }

    private static string? EditText(string title, string content)
    {
        string? result = null;
        Dialog dialog = new()
        {
            Title = "编辑 · " + title,
            Width = Dim.Percent(88),
            Height = Dim.Percent(85),
        };
        TextView editor = new()
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            Text = content,
            WordWrap = false,
        };
        Button save = new() { Text = "保存", X = Pos.Percent(35), Y = Pos.AnchorEnd(1) };
        Button cancel = new() { Text = "取消", X = Pos.Percent(55), Y = Pos.AnchorEnd(1) };
        save.Accepted += (_, _) =>
        {
            result = editor.Text?.ToString() ?? string.Empty;
            Application.RequestStop(dialog);
        };
        cancel.Accepted += (_, _) => Application.RequestStop(dialog);
        dialog.Add(editor, save, cancel);
        Ui.EnableEsc(dialog);
        editor.SetFocus();
        Application.Run(dialog);
        return result;
    }

    private void ManageBackups(string id)
    {
        while (true)
        {
            IReadOnlyList<ServerBackupInfo> backups = ServerBackup.List(id);
            BackupSettings settings = ServerBackup.CurrentSettings();
            string? action = Ui.Select(
                $"备份 · {backups.Count} 份 · 自动备份{(settings.Enabled ? "开启" : "关闭")}",
                ["创建备份", .. backups.Select(item =>
                    $"{item.Name} · {(item.Hot ? "热备" : "冷备")} · {Ui.FormatBytes(item.SizeBytes)} · {DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAt).LocalDateTime:yyyy-MM-dd HH:mm}"),
                 "还原备份", "删除备份", "清理旧备份", "备份设置", "返回"]);
            if (action is null || action == "返回")
            {
                return;
            }

            try
            {
                switch (action)
                {
                    case "创建备份":
                        _ = CreateBackupAsync(id);
                        break;
                    case "还原备份":
                    {
                        if (backups.Count == 0)
                        {
                            Ui.Info("还原备份", "没有可用备份。");
                            break;
                        }
                        string? picked = Ui.Select("选择要还原的备份", backups.Select(item => item.Name).ToList());
                        if (picked is not null && Ui.Confirm("还原备份", $"确定用 {picked} 覆盖当前服务器数据吗？"))
                        {
                            _ = RestoreBackupAsync(id, picked);
                        }
                        break;
                    }
                    case "删除备份":
                    {
                        if (backups.Count == 0)
                        {
                            Ui.Info("删除备份", "没有可删除的备份。");
                            break;
                        }
                        string? picked = Ui.Select("删除备份", backups.Select(item => item.Name).ToList());
                        if (picked is not null && Ui.Confirm("删除备份", "确定删除 " + picked + " 吗？"))
                        {
                            ServerBackup.Delete(id, picked);
                        }
                        break;
                    }
                    case "清理旧备份":
                        if (Ui.Confirm("清理旧备份", "将按当前备份策略删除超出保留数量/天数的备份，继续？"))
                        {
                            ServerBackup.Prune(id);
                        }
                        break;
                    case "备份设置":
                        EditBackupSettings();
                        break;
                }
            }
            catch (Exception exception)
            {
                Ui.Error("备份操作失败", exception.Message);
            }
        }
    }

    private async Task CreateBackupAsync(string id)
    {
        await Ui.RunTaskAsync("创建备份", "正在压缩服务器数据…", async (progress, token) =>
        {
            ServerBackupInfo info = await ServerBackup.CreateAsync(id, token).ConfigureAwait(false);
            progress.Report($"完成：{info.Name}（{Ui.FormatBytes(info.SizeBytes)}）");
        }).ConfigureAwait(false);
    }

    private async Task RestoreBackupAsync(string id, string name)
    {
        await Ui.RunTaskAsync("还原备份", "正在还原 " + name + "…", async (progress, token) =>
        {
            await ServerBackup.RestoreAsync(id, name, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private void EditBackupSettings()
    {
        BackupSettings settings = ServerBackup.CurrentSettings();
        Dictionary<string, string>? values = Ui.Form("备份设置",
            ("enabled", "自动备份（true / false）", settings.Enabled ? "true" : "false", false),
            ("interval", "间隔（小时）", settings.IntervalHours.ToString(), false),
            ("keep", "保留份数", settings.KeepCount.ToString(), false),
            ("days", "保留天数（0 = 不限）", settings.KeepDays.ToString(), false));
        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values["interval"], out int interval) ||
            !int.TryParse(values["keep"], out int keep) ||
            !int.TryParse(values["days"], out int days))
        {
            Ui.Error("备份设置", "请输入有效数字。");
            return;
        }

        settings.Enabled = values["enabled"].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        settings.IntervalHours = Math.Max(1, interval);
        settings.KeepCount = Math.Max(1, keep);
        settings.KeepDays = Math.Max(0, days);
        ServerBackup.SaveSettings(settings);
    }

    private void ManageContent(string id)
    {
        ServerConfig? config = ServerManage.LoadServerConfig(id);
        if (config is null)
        {
            return;
        }

        string kind;
        try
        {
            kind = ServerContent.KindForCore(config.Core);
        }
        catch (Exception exception)
        {
            Ui.Info("内容管理", exception.Message);
            return;
        }

        while (true)
        {
            IReadOnlyList<ServerContentEntry> entries = ServerContent.List(id, kind);
            List<string> options = [$"＋ 从 URL 安装 {kind}", "◀ 返回"];
            options.AddRange(entries.Select(entry => $"[{(entry.Enabled ? "启用" : "停用")}] {entry.FileName} · {Ui.FormatBytes(entry.SizeBytes)}"));
            string? picked = Ui.Select($"{kind} · {entries.Count} 个文件", options);
            if (picked is null || picked.StartsWith("◀", StringComparison.Ordinal))
            {
                return;
            }

            if (picked.StartsWith("＋", StringComparison.Ordinal))
            {
                string? url = Ui.Prompt("从 URL 安装", "直接下载 .jar 地址");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                string? fileName = Ui.Prompt("文件名", "保存为（例如 example.jar）", Path.GetFileName(new Uri(url).LocalPath));
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                _ = InstallContentAsync(id, kind, url, fileName);
                continue;
            }

            ServerContentEntry? entry = entries.FirstOrDefault(item =>
                $"[{(item.Enabled ? "启用" : "停用")}] {item.FileName} · {Ui.FormatBytes(item.SizeBytes)}" == picked);
            if (entry is null)
            {
                continue;
            }

            string? action = Ui.Select("内容操作 · " + entry.FileName,
                [entry.Enabled ? "停用" : "启用", "删除", "返回"]);
            try
            {
                if (action == "停用" || action == "启用")
                {
                    ServerContent.SetEnabled(id, kind, entry.FileName, action == "启用");
                }
                else if (action == "删除" && Ui.Confirm("删除内容", "确定删除 " + entry.FileName + " 吗？"))
                {
                    ServerContent.Delete(id, kind, entry.FileName);
                }
            }
            catch (Exception exception)
            {
                Ui.Error("内容操作失败", exception.Message);
            }
        }
    }

    private async Task InstallContentAsync(string id, string kind, string url, string fileName)
    {
        await Ui.RunTaskAsync("安装 " + fileName, "正在下载…", async (progress, token) =>
        {
            string saved = await ServerContent.InstallFromUrlAsync(id, kind, url, fileName, token).ConfigureAwait(false);
            progress.Report("已保存：" + saved);
        }).ConfigureAwait(false);
    }

    private void ExportPackage(string id)
    {
        string? path = Ui.Prompt("导出服务器包", ".mofuser 文件路径", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), id + ".mofuser"));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = Ui.RunTaskAsync("导出服务器包", "正在打包…", async (progress, token) =>
        {
            await ServerPackage.ExportAsync(id, path,
                new Progress<ServerTransferProgress>(item =>
                    progress.Report($"{item.Done * 100.0 / Math.Max(1, item.Total):0.#}% · {item.Phase}")),
                token).ConfigureAwait(false);
        });
    }

    private void ImportPackage()
    {
        string? path = Ui.Prompt("导入服务器包", ".mofuser 文件路径");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        _ = ImportPackageAsync(path);
    }

    private async Task ImportPackageAsync(string path)
    {
        (bool ok, string? id) = await Ui.RunTaskAsync("导入服务器包", "正在解包…", async (progress, token) =>
        {
            string newId = await ServerPackage.ImportAsync(path,
                new Progress<ServerTransferProgress>(item =>
                    progress.Report($"{item.Done * 100.0 / Math.Max(1, item.Total):0.#}% · {item.Phase}")),
                token).ConfigureAwait(false);
            return newId;
        }).ConfigureAwait(false);

        if (ok && id is not null)
        {
            Refresh();
            Ui.Info("导入完成", "服务器已导入：" + id);
        }
    }

    private void EditLaunchOptions(string id)
    {
        ServerConfig? config = ServerManage.LoadServerConfig(id);
        if (config is null)
        {
            return;
        }

        Dictionary<string, string>? values = Ui.Form("启动参数",
            ("min", "最小内存（MB）", config.MemoryMinMb.ToString(), false),
            ("max", "最大内存（MB）", config.MemoryMb.ToString(), false),
            ("java", "Java 路径（留空自动检测）", config.JavaPath, false));
        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values["min"], out int minimum) || !int.TryParse(values["max"], out int maximum))
        {
            Ui.Error("启动参数", "内存必须是数字。");
            return;
        }

        try
        {
            ServerManage.SaveLaunchOptions(id, new LaunchOptions
            {
                MemoryMb = maximum,
                MemoryMinMb = minimum,
                ExtraJavaArgs = config.ExtraJavaArgs,
            });
            ServerManage.SetJavaPath(id, values["java"]);
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("保存启动参数失败", exception.Message);
        }
    }

    private void DeleteServer(ServerInfo server)
    {
        if (ServerManage.IsRunning(server.Id))
        {
            Ui.Info("删除服务器", "服务器正在运行，请先停止。");
            return;
        }

        if (!Ui.Confirm("删除服务器", $"确定删除 {server.Name} 吗？\n\n目录：{ServerManage.ServerDirectory(server.Id)}\n此操作不可撤销。"))
        {
            return;
        }

        try
        {
            ServerManage.DeleteServer(server.Id);
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("删除服务器失败", exception.Message);
        }
    }

    private static void OpenFolder(string path)
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
