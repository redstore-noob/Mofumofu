using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Plugins;
using SukiUI.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 插件页：左侧插件列表（真实运行状态），右侧详情三标签（概览 / 权限 / 设置）。
/// 数据源是 PluginManager 运行时；装卸/开关/权限全部即时生效。
/// </summary>
public partial class PluginsView : UserControl
{
    private string _selectedId = string.Empty;
    private string _search = string.Empty;

    public PluginsView()
    {
        InitializeComponent();
        PluginManager.PluginsChanged += OnPluginsChanged;
        DetachedFromVisualTree += (_, _) => PluginManager.PluginsChanged -= OnPluginsChanged;

        // 视图可能先于 PluginManager.Initialize 出现：先画一版再等事件刷新
        RenderList();
        UpdateInfoPanel();

        AttachedToVisualTree += (_, _) =>
        {
            UiAnimations.Reset(PluginListCard);
            UiAnimations.FadeSlideIn(PluginListCard, 0.995, 8, 190);
        };
    }

    /// <summary>插件目录：{DataRoot}/Mofumofu/plugins。</summary>
    public static string PluginDirectory => PluginManager.PluginsRoot;

    private void OnPluginsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(OnPluginsChanged);
            return;
        }

        RenderList();
        UpdateInfoPanel();
    }

    // ------------------------------------------------------------------
    // 列表
    // ------------------------------------------------------------------

    private void RenderList()
    {
        PluginList.Children.Clear();
        string query = _search.Trim();
        List<PluginRuntime> filtered = PluginManager.All
            .Where(runtime => query.Length == 0 ||
                              $"{runtime.Manifest.Name} {runtime.Manifest.Id} {runtime.Manifest.Author}"
                                  .Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        PluginCountText.Text = L.T("Pages.Plugins.Count", "{0} 个插件", filtered.Count);
        EmptyText.IsVisible = filtered.Count == 0;
        EmptyText.Text = PluginManager.All.Count == 0
            ? L.T("Pages.Plugins.Empty", "插件目录里还没有插件。")
            : L.T("Pages.Plugins.EmptySearch", "没有匹配的插件。");

        for (int index = 0; index < filtered.Count; index++)
        {
            if (index > 0)
            {
                PluginList.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(10, 2),
                    Background = ThemeTokens.Brush("NekoBorderBrush"),
                });
            }

            PluginList.Children.Add(BuildRow(filtered[index]));
        }
    }

    private Control BuildRow(PluginRuntime runtime)
    {
        Control icon = BuildPluginIcon(runtime.Manifest, 38, 10);

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(new TextBlock
        {
            Text = runtime.Manifest.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        });
        nameRow.Children.Add(BuildStateBadge(runtime, fontSize: 10.5));

        string meta = string.Join(" · ", new[] { runtime.Manifest.Version, runtime.Manifest.Author }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (meta.Length > 0)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = meta,
                Classes = { "caption" },
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(12, 10),
        };
        row.Children.Add(icon);
        Grid.SetColumn(nameRow, 1);
        nameRow.Margin = new Thickness(12, 0, 12, 0);
        row.Children.Add(nameRow);

        bool selected = string.Equals(runtime.Manifest.Id, _selectedId, StringComparison.OrdinalIgnoreCase);
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = selected ? ThemeTokens.Brush("NekoFieldSoftBrush") : Brushes.Transparent,
            Child = row,
        };
        border.Cursor = new Cursor(StandardCursorType.Hand);
        border.Tapped += (_, _) => SelectPlugin(runtime.Manifest.Id);
        return border;
    }

    /// <summary>插件图标：icon.png 优先，回退内置拼图图标。</summary>
    private Control BuildPluginIcon(PluginManifest manifest, double size, double radius)
    {
        Border host = new()
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (manifest.HasIcon && LoadIconBitmap(manifest.IconPath) is { } bitmap)
        {
            host.Child = new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            host.Child = new IconParkIcon
            {
                Kind = "IconFluentPuzzle",
                Width = size * 0.47,
                Height = size * 0.47,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return host;
    }

    private Bitmap? LoadIconBitmap(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>状态徽章：运行中绿 / 已停用灰 / 异常红 / 不兼容橙。</summary>
    private Control BuildStateBadge(PluginRuntime runtime, double fontSize)
    {
        (string key, string fallback, string brush) = runtime.State switch
        {
            PluginRuntimeState.Loaded => ("Stat.Enabled", "已启用", "NekoSuccessBrush"),
            PluginRuntimeState.Disabled => ("Stat.Disabled", "已停用", "NekoMutedBrush"),
            PluginRuntimeState.Refused => ("Stat.Refused", "拒绝加载", "NekoWarningBrush"),
            PluginRuntimeState.Error => ("Stat.Error", "异常", "NekoDangerBrush"),
            PluginRuntimeState.Incompatible => ("Stat.Incompatible", "不兼容", "NekoWarningBrush"),
            PluginRuntimeState.PendingConsent => ("Stat.PendingConsent", "待授权", "NekoWarningBrush"),
            _ => ("Stat.Broken", "无法加载", "NekoDangerBrush"),
        };

        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeTokens.Brush(brush),
            Child = new TextBlock
            {
                Text = L.T($"Pages.Plugins.{key}", fallback),
                FontSize = fontSize,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
            },
        };
    }

    private void SelectPlugin(string pluginId)
    {
        _selectedId = pluginId;
        RenderList();
        UpdateInfoPanel();
        UiAnimations.Reset(PluginInfoCard);
        UiAnimations.FadeSlideIn(PluginInfoCard, 0.995, 6, 160);
    }

    // ------------------------------------------------------------------
    // 详情
    // ------------------------------------------------------------------

    private void UpdateInfoPanel()
    {
        PluginRuntime? runtime = PluginManager.ById(_selectedId);
        InfoEmpty.IsVisible = runtime is null;
        InfoDetail.IsVisible = runtime is not null;
        if (runtime is null) return;

        PluginManifest manifest = runtime.Manifest;
        InfoName.Text = manifest.Name;

        string meta = string.Join(" · ", new[] { manifest.Version, manifest.Author }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        InfoMeta.Text = meta.Length > 0 ? meta : "—";

        string markdown = manifest.ReadDescriptionMarkdown();
        InfoDescription.Text = markdown.Length > 0
            ? StripMarkdown(markdown)
            : string.IsNullOrWhiteSpace(manifest.Description)
                ? L.T("Pages.Plugins.Info.NoDescription", "这个插件没有提供简介。")
                : manifest.Description;

        InfoId.Text = manifest.Id;
        InfoVersion.Text = manifest.Version.Length > 0 ? manifest.Version : "—";
        InfoAuthor.Text = manifest.Author.Length > 0 ? manifest.Author : "—";
        InfoDirectory.Text = manifest.Directory;
        ToolTip.SetTip(InfoDirectory, manifest.Directory);
        InfoState.Text = StateText(runtime);
        InfoType.Text = runtime.Instance?.ExtensionType.ToString() ?? "—";

        // 图标（icon.png 回退拼图）
        InfoIconHost.Child = manifest.HasIcon && LoadIconBitmap(manifest.IconPath) is { } bitmap
            ? new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
            : new IconParkIcon
            {
                Kind = "IconFluentPuzzle",
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        bool loaded = runtime.State == PluginRuntimeState.Loaded;
        InfoEnableButton.Content = runtime.State == PluginRuntimeState.PendingConsent
            ? L.T("Pages.Plugins.Button.Review", "审查并授权")
            : loaded
                ? L.T("Pages.Plugins.Button.Disable", "停用插件")
                : L.T("Pages.Plugins.Button.Enable", "启用插件");

        RenderPermissions(manifest);
    }

    private static string StateText(PluginRuntime runtime) => runtime.State switch
    {
        PluginRuntimeState.Loaded => L.T("Pages.Plugins.Stat.Enabled", "已启用"),
        PluginRuntimeState.Disabled => L.T("Pages.Plugins.Stat.Disabled", "已停用"),
        PluginRuntimeState.Refused => L.T("Pages.Plugins.Stat.Refused", "拒绝加载"),
        PluginRuntimeState.Error => L.T("Pages.Plugins.Stat.Error", "异常") + (runtime.Error.Length > 0 ? "：" + runtime.Error : ""),
        PluginRuntimeState.Incompatible => L.T("Pages.Plugins.Stat.Incompatible", "不兼容"),
        PluginRuntimeState.PendingConsent => L.T("Pages.Plugins.Stat.PendingConsent", "待授权") + "：" + ConsentReasonText(runtime),
        _ => L.T("Pages.Plugins.Stat.Broken", "无法加载") + (runtime.Error.Length > 0 ? "：" + runtime.Error : ""),
    };

    /// <summary>待授权原因（本地化）：首次安装 / 指纹变化 / 新增权限。</summary>
    private static string ConsentReasonText(PluginRuntime runtime) =>
        PluginConsent.Evaluate(runtime.Manifest, out _) switch
        {
            PluginConsentState.Changed => L.T("Pages.Plugins.Consent.Reason.Changed", "插件文件已更新（指纹变化），需要重新确认。"),
            PluginConsentState.NewPermissions => L.T("Pages.Plugins.Consent.Reason.NewPermissions", "插件新增了权限声明，需要重新确认。"),
            _ => L.T("Pages.Plugins.Consent.Reason.Pending", "这是新安装的插件，需要你同意后才会加载。"),
        };

    /// <summary>极简 Markdown 转纯文本（详情页直接展示 description.md）。</summary>
    private static string StripMarkdown(string markdown) => markdown
        .Replace("```", string.Empty)
        .Replace("**", string.Empty)
        .Replace("*", string.Empty)
        .Replace("`", string.Empty)
        .Replace("#", string.Empty)
        .Replace(">", string.Empty)
        .Trim();

    private void RenderPermissions(PluginManifest manifest)
    {
        InfoPermissionList.Children.Clear();
        if (manifest.Permissions.Count == 0)
        {
            InfoPermissionList.Children.Add(new TextBlock
            {
                Text = L.T("Pages.Plugins.Perm.None", "该插件没有声明任何权限。"),
                Classes = { "caption" },
            });
            return;
        }

        foreach (string permission in manifest.Permissions)
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = PluginPermissions.IsAllowedByUser(manifest.Id, permission),
                Padding = new Thickness(6, 2),
            };
            toggle.Content = new TextBlock { Text = PermissionLabel(permission), FontSize = 12.5 };
            string zone = permission;
            toggle.IsCheckedChanged += (_, _) =>
                PluginPermissions.SetAllowed(manifest.Id, zone, toggle.IsChecked == true);
            InfoPermissionList.Children.Add(toggle);
        }
    }

    /// <summary>权限区展示名：已知权限走语言包，未知权限标注 API 不兼容。</summary>
    private static string PermissionLabel(string permission) =>
        PluginPermissions.IsKnown(permission)
            ? L.T($"Pages.Plugins.Perm.{permission}", permission)
            : L.T("Pages.Plugins.Perm.Unknown", "未知权限（API 不兼容）") + $"（{permission}）";

    // ------------------------------------------------------------------
    // 安装同意（先同意、后加载）
    // ------------------------------------------------------------------

    /// <summary>
    /// 安装同意对话框：插件信息 + extension.dll SHA256 指纹 + 权限清单 + 醒目风险警告。
    /// 同意 = 记录指纹与整包权限并加载；拒绝 = 完全不加载（保持待授权）。
    /// </summary>
    private void ShowConsentDialog(PluginRuntime runtime)
    {
        PluginManifest manifest = runtime.Manifest;
        PluginConsentState consentState = PluginConsent.Evaluate(manifest, out string dllHash);

        var content = new StackPanel { Spacing = 12, Width = 440 };

        // 风险警告：插件与启动器拥有相同系统权限
        var warningRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        warningRow.Children.Add(new IconParkIcon
        {
            Kind = "IconWarning",
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        });
        warningRow.Children.Add(new TextBlock
        {
            Text = L.T("Pages.Plugins.Consent.Warning",
                "插件与启动器拥有相同系统权限，可以读取你的账号数据（含 Microsoft 登录凭据）、修改启动器配置。请确认你信任该插件的作者与来源。"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoDangerBrush"),
        });
        content.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Background = ThemeTokens.Brush("NekoDangerSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoDangerBrush"),
            BorderThickness = new Thickness(1),
            Child = warningRow,
        });

        // 插件信息
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock
        {
            Text = manifest.Name,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        string meta = string.Join(" · ", new[] { manifest.Version, manifest.Author }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (meta.Length > 0)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = meta,
                Classes = { "caption" },
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        content.Children.Add(nameRow);

        // 重谈原因（更新 / 新增权限时提示为什么又弹窗）
        if (consentState != PluginConsentState.Pending)
        {
            content.Children.Add(new TextBlock
            {
                Text = ConsentReasonText(runtime),
                FontSize = 12,
                Foreground = ThemeTokens.Brush("NekoWarningBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // extension.dll 指纹
        content.Children.Add(new TextBlock
        {
            Text = L.T("Pages.Plugins.Consent.Hash", "extension.dll 指纹（SHA256）"),
            Classes = { "caption" },
        });
        content.Children.Add(new TextBlock
        {
            Text = dllHash.Length > 0 ? FormatFingerprint(dllHash) : "—",
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            FontSize = 11.5,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });

        // 权限清单
        content.Children.Add(new TextBlock
        {
            Text = L.T("Pages.Plugins.Consent.Permissions", "该插件声明的权限"),
            Classes = { "caption" },
            Margin = new Thickness(0, 4, 0, 0),
        });
        if (manifest.Permissions.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = L.T("Pages.Plugins.Perm.None", "该插件没有声明任何权限。"),
                FontSize = 12,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            });
        }
        else
        {
            var permList = new StackPanel { Spacing = 3 };
            foreach (string permission in manifest.Permissions)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                row.Children.Add(new IconParkIcon
                {
                    Kind = "IconFluentInfo",
                    Width = 13,
                    Height = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock
                {
                    Text = PermissionLabel(permission),
                    FontSize = 12,
                    Foreground = ThemeTokens.Brush("NekoTextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
                permList.Children.Add(row);
            }
            content.Children.Add(permList);
        }

        content.Children.Add(new TextBlock
        {
            Text = L.T("Pages.Plugins.Consent.PermNote",
                "首次同意即一次性授权以上权限；之后可以在「权限」标签页里逐项关闭。"),
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
        });

        // 按钮：拒绝 = 完全不加载
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var decline = new Button
        {
            Content = L.T("Pages.Plugins.Consent.Decline", "拒绝"),
            Classes = { "ghost" },
            Height = 30,
        };
        decline.Click += (_, _) =>
        {
            DismissDialog(decline);
            Shell.Toast(L.T("Pages.Plugins.Consent.Declined", "已拒绝授权，{0} 不会被加载", manifest.Name));
        };
        var agree = new Button
        {
            Content = L.T("Pages.Plugins.Consent.Agree", "同意并启用"),
            Classes = { "primary" },
            Height = 30,
        };
        agree.Click += (_, _) =>
        {
            DismissDialog(agree);
            PluginConsent.Grant(manifest, dllHash);
            PluginManager.SetEnabled(manifest.Id, true);
            Shell.Toast(L.T("Pages.Plugins.Consent.Granted", "{0} 已授权并启用", manifest.Name));
        };
        buttons.Children.Add(decline);
        buttons.Children.Add(agree);
        content.Children.Add(buttons);

        Shell.Dialog(content, L.T("Pages.Plugins.Consent.Title", "插件授权确认"));
    }

    /// <summary>指纹按 8 位分组，方便肉眼比对。</summary>
    private static string FormatFingerprint(string hash) => string.Join(" ",
        Enumerable.Range(0, (hash.Length + 7) / 8)
            .Select(index => hash.Substring(index * 8, Math.Min(8, hash.Length - index * 8))));

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    private async void OnInstallPluginClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null) return;

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Pages.Plugins.Install.PickTitle", "选择 .nekoex 插件包"),
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("NekoEX 插件包") { Patterns = new[] { "*.nekoex" } },
                },
            });
            if (files.Count == 0) return;

            string id = await NekoexInstaller.InstallAsync(files[0].TryGetLocalPath() ?? files[0].Path.AbsolutePath);
            _selectedId = id;
            UpdateInfoPanel();

            // 首次安装：先弹同意窗，同意后才会真正加载
            if (PluginManager.ById(id) is { State: PluginRuntimeState.PendingConsent } pending)
            {
                Shell.Toast(L.T("Pages.Plugins.Install.NeedConsent", "已安装 {0}，请先确认授权", id));
                ShowConsentDialog(pending);
            }
            else
            {
                Shell.Toast(L.T("Pages.Plugins.Install.Done", "已安装插件 {0}", id));
            }
        }
        catch (Exception error)
        {
            Shell.Toast(L.T("Pages.Plugins.Install.Failed", "安装失败：{0}", error.Message));
        }
    }

    private void OnInfoEnableClick(object? sender, RoutedEventArgs e)
    {
        if (PluginManager.ById(_selectedId) is not { } runtime) return;

        // 待授权插件：先走同意流程，同意前不会加载
        if (runtime.State == PluginRuntimeState.PendingConsent)
        {
            ShowConsentDialog(runtime);
            return;
        }

        bool enable = runtime.State != PluginRuntimeState.Loaded;
        PluginManager.SetEnabled(runtime.Manifest.Id, enable);
        Shell.Toast(enable
            ? L.T("Pages.Plugins.Toast.Enabled", "{0} 已启用", runtime.Manifest.Name)
            : L.T("Pages.Plugins.Toast.Disabled", "{0} 已停用", runtime.Manifest.Name));
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (PluginManager.ById(_selectedId) is not { } runtime) return;
        string pluginId = runtime.Manifest.Id;

        foreach (string repo in LocalSettings.Current.PluginRepos)
        {
            if (PluginStore.NormalizeRepo(repo)?.Split('/')[1] is not { } repoName ||
                string.Equals(repoName, pluginId, StringComparison.OrdinalIgnoreCase) == false)
            {
                continue;
            }

            try
            {
                PluginStoreEntry entry = await PluginStore.FetchLatestAsync(repo);
                if (PluginStore.IsUpToDate(entry))
                {
                    Shell.Toast(L.T("Pages.Plugins.Update.Latest", "已是最新版本 {0}", runtime.Manifest.Version));
                }
                else
                {
                    Shell.Toast(L.T("Pages.Plugins.Update.Available",
                        "有新版本 {0}，到 下载 → 插件 标签页更新", entry.Version));
                }
            }
            catch (Exception error)
            {
                Shell.Toast(L.T("Pages.Plugins.Update.Failed", "检查更新失败：{0}", error.Message));
            }

            return;
        }

        Shell.Toast(L.T("Pages.Plugins.Update.NoRepo", "没有找到托管该插件的仓库，先在 下载 → 插件 添加它的仓库。"));
    }

    private void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        if (PluginManager.ById(_selectedId) is not { } runtime) return;

        var content = new StackPanel { Spacing = 12, Width = 360 };
        content.Children.Add(new TextBlock
        {
            Text = L.T("Pages.Plugins.Uninstall.Confirm", "确定卸载「{0}」吗？插件文件会从磁盘上删除。", runtime.Manifest.Name),
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = L.T("Common.Cancel", "取消"), Classes = { "ghost" }, Height = 30 };
        cancel.Click += (_, _) => DismissDialog(cancel);
        var confirm = new Button { Content = L.T("Common.Ok", "确定"), Classes = { "danger" }, Height = 30 };
        confirm.Click += (_, _) =>
        {
            DismissDialog(confirm);
            if (NekoexInstaller.Uninstall(runtime.Manifest.Id))
            {
                _selectedId = string.Empty;
                PluginManager.Rescan();
                Shell.Toast(L.T("Pages.Plugins.Toast.Uninstalled", "已卸载 {0}", runtime.Manifest.Name));
            }
            else
            {
                Shell.Toast(L.T("Pages.Plugins.Toast.UninstallFailed", "卸载失败，详见插件日志"));
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        Shell.Dialog(content, L.T("Pages.Plugins.Uninstall.Title", "卸载插件"));
    }

    private static void DismissDialog(Control control) =>
        control.FindAncestorOfType<SukiDialogHost>(true)?.Manager?.DismissDialog();

    private void OpenPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Pages.Plugins.OpenFailed", "打开插件目录失败：{0}", ex.Message));
        }
    }

    private void OnInfoOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (PluginManager.ById(_selectedId) is { } runtime) OpenPath(runtime.Manifest.Directory);
    }

    private void OnOpenPluginFolderClick(object? sender, RoutedEventArgs e) => OpenPath(PluginDirectory);

    private void OnOpenStoreClick(object? sender, RoutedEventArgs e)
    {
        Shell.Go("download");
        DownloadView.OpenExtensionTab();
    }

    private void OnReloadPluginsClick(object? sender, RoutedEventArgs e)
    {
        PluginManager.Rescan();
        UiAnimations.Reset(PluginListCard);
        UiAnimations.FadeSlideIn(PluginListCard, 0.995, 5, 150);
        Shell.Toast(L.T("Pages.Plugins.ReloadToast", "已重新扫描插件目录"));
    }

    private void OnPluginSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _search = PluginSearch.Text ?? string.Empty;
        RenderList();
    }
}
