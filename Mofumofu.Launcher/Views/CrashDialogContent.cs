using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using MofuCore.Log;
using MofuCore.Tools;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Agent;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

public sealed record CrashRescueInfo(string TargetDirectory, RewindSnapshot Snapshot);

public static class CrashDialogContent
{
    private const double DialogWidth = 760;
    private const double DialogMaxHeight = 560;

    private static IBrush TextBrush => ThemeTokens.Brush("NekoTextBrush");
    private static IBrush MutedBrush => ThemeTokens.Brush("NekoMutedBrush");
    private static IBrush DimBrush => ThemeTokens.Brush("NekoDimBrush");
    private static IBrush BorderBrush => ThemeTokens.Brush("NekoBorderBrush");
    private static IBrush AccentBrush => ThemeTokens.Brush("NekoAccentBrush");
    private static IBrush AccentSoftBrush => ThemeTokens.Brush("NekoAccentSoftBrush");
    private static IBrush FieldSoftBrush => ThemeTokens.Brush("NekoFieldSoftBrush");
    private static IBrush DangerBrush => ThemeTokens.Brush("NekoDangerBrush");
    private static IBrush WarningBrush => ThemeTokens.Brush("NekoWarningBrush");
    private static IBrush WarningSoftBrush => ThemeTokens.Brush("NekoWarningSoftBrush");
    private static IBrush SuccessBrush => ThemeTokens.Brush("NekoSuccessBrush");
    private static IBrush InfoBrush => ThemeTokens.Brush("NekoInfoBrush");

    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");

    public static Control Build(LogAnalysisResult result, string instanceName, CrashRescueInfo? rescue = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        StackPanel panel = new() { Width = DialogWidth, Spacing = 12 };

        panel.Children.Add(BuildMetaLine(result, instanceName));
        panel.Children.Add(BuildSummaryCard(result));
        if (rescue is not null)
        {
            panel.Children.Add(BuildRescueCard(rescue, instanceName));
        }
        if (result.Findings.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Crash.Section.Findings", "分析结论（{0} 条）", result.Findings.Count),
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = TextBrush,
            });
            foreach (LogAnalysisFinding finding in result.Findings)
            {
                panel.Children.Add(BuildFindingCard(finding));
            }
        }

        if (result.ModIssues.Count > 0)
        {
            panel.Children.Add(BuildModIssuesCard(result));
        }

        panel.Children.Add(BuildButtonsRow(result));

        UiAnimations.Stagger(panel.Children, step: 32, milliseconds: 200, offsetY: 10);

        return new ScrollViewer
        {
            Content = panel,
            MaxHeight = DialogMaxHeight,
        };
    }

    private static Control BuildMetaLine(LogAnalysisResult result, string instanceName)
    {
        System.Collections.Generic.List<string> parts =
        [
            L.T("Crash.Meta.Instance", "实例") + " " + (string.IsNullOrWhiteSpace(instanceName) ? "—" : instanceName),
        ];
        if (result.MinecraftVersion.Length > 0)
        {
            parts.Add(L.T("Crash.Meta.Version", "版本") + " " + result.MinecraftVersion);
        }

        parts.Add(L.T("Crash.Meta.Loader", "加载器") + " " + result.ModLoader);
        if (result.JavaVersion.Length > 0)
        {
            parts.Add("Java " + result.JavaVersion);
        }

        if (result.MemoryMb > 0)
        {
            parts.Add(L.T("Crash.Meta.Memory", "内存") + " " + result.MemoryMb + " MiB");
        }

        if (result.ExitCode is int exitCode)
        {
            parts.Add(L.T("Crash.Meta.ExitCode", "退出码") + " " + exitCode);
        }

        return new TextBlock
        {
            Text = string.Join(" · ", parts),
            FontSize = 11.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static Control BuildSummaryCard(LogAnalysisResult result)
    {
        StackPanel card = new() { Spacing = 6 };
        card.Children.Add(new TextBlock
        {
            Text = result.Summary,
            FontSize = 14.5,
            FontWeight = FontWeight.Bold,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        if (result.CrashDescription.Length > 0)
        {
            card.Children.Add(DimLine(L.T("Crash.Description", "崩溃场景") + "：" + result.CrashDescription));
        }

        if (result.CrashException.Length > 0)
        {
            card.Children.Add(DimLine(L.T("Crash.Exception", "首个异常") + "：" + Truncate(result.CrashException, 200)));
        }

        if (result.CrashReportPath.Length > 0)
        {
            card.Children.Add(DimLine(L.T("Crash.Report.Path", "崩溃报告") + "：" + result.CrashReportPath));
        }

        if (result.HsErrPath.Length > 0)
        {
            card.Children.Add(DimLine("hs_err：" + result.HsErrPath));
        }

        return new Border
        {
            Child = card,
            Background = AccentSoftBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
        };
    }

    private static Control BuildRescueCard(CrashRescueInfo rescue, string instanceName)
    {
        StackPanel card = new() { Spacing = 8 };

        card.Children.Add(new TextBlock
        {
            Text = L.T("Crash.Rescue.Question", "救砖：游戏崩了，要回档到启动前的状态吗？"),
            FontSize = 14.5,
            FontWeight = FontWeight.Bold,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        card.Children.Add(DimLine(L.T("Crash.Rescue.Snapshot", "启动前快照：{0} · {1}",
            rescue.Snapshot.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm:ss"),
            rescue.Snapshot.TotalSizeDisplay)));
        card.Children.Add(DimLine(L.T("Crash.Rescue.Safety",
            "回档会丢失本次启动后的进度；回档前会自动创建安全快照，随时可以反悔。")));

        Button rollback = new()
        {
            Content = L.T("Crash.Rescue.Action.Rollback", "一键救砖（回档到启动前）"),
            Classes = { "primary", "dialog-action" },
        };
        Button pick = new()
        {
            Content = L.T("Crash.Rescue.Action.Pick", "选择其他快照"),
            Classes = { "dialog-secondary", "dialog-action" },
        };

        rollback.Click += async (_, _) =>
        {
            rollback.IsEnabled = false;
            pick.IsEnabled = false;
            object originalContent = rollback.Content!;
            rollback.Content = L.T("Crash.Rescue.Action.Busy", "正在救砖…");
            try
            {
                RewindRollbackResult result = await RewindSystem.RollbackAsync(
                    rescue.TargetDirectory, rescue.Snapshot.Id, RewindKind.Instance);
                Shell.Toast(L.T("Crash.Rescue.Done", "救砖成功：已回档到启动前状态（安全快照 {0}）",
                    result.SafetySnapshot.Id));

                card.Children.Clear();
                card.Children.Add(new TextBlock
                {
                    Text = L.T("Crash.Rescue.DoneTitle", "救砖成功，已回档到启动前状态"),
                    FontSize = 14.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = SuccessBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
                card.Children.Add(DimLine(L.T("Crash.Rescue.DoneHint",
                    "回档前的状态已存为安全快照，随时可以反悔。现在可以重新启动游戏了。")));

                Button relaunch = new()
                {
                    Content = L.T("Crash.Rescue.Action.Relaunch", "重新启动游戏"),
                    Classes = { "primary", "dialog-action" },
                };
                relaunch.Click += (_, _) =>
                {
                    MofuDialog.Close(relaunch);
                    LaunchProgress.Shared.StartAuto();
                };
                card.Children.Add(relaunch);
            }
            catch (Exception e)
            {
                Shell.Toast(L.T("Crash.Rescue.Failed", "救砖失败：{0}", e.Message));
                rollback.Content = originalContent;
                rollback.IsEnabled = true;
                pick.IsEnabled = true;
            }
        };

        pick.Click += (_, _) =>
        {
            Shell.Dialog(
                RewindDialogContent.Build(instanceName, null, rescue.TargetDirectory, RewindKind.Instance),
                "Rewind");
        };

        card.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { rollback, pick },
        });

        return new Border
        {
            Child = card,
            Background = WarningSoftBrush,
            BorderBrush = WarningBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
        };
    }

    private static Control BuildFindingCard(LogAnalysisFinding finding)
    {
        StackPanel card = new() { Spacing = 7 };

        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(BuildSeverityBadge(finding.Severity));
        header.Children.Add(new TextBlock
        {
            Text = finding.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        card.Children.Add(header);

        if (finding.Evidence.Count > 0)
        {
            card.Children.Add(new TextBlock
            {
                Text = string.Join("\n", finding.Evidence),
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = DimBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        for (int index = 0; index < finding.Decisions.Count; index++)
        {
            TextBlock step = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
            step.Inlines.Add(new Run { Text = (index + 1) + ".  ", Foreground = AccentBrush, FontWeight = FontWeight.Bold });
            step.Inlines.Add(new Run { Text = finding.Decisions[index] });
            card.Children.Add(step);
        }

        return new Border
        {
            Child = card,
            Background = FieldSoftBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
        };
    }

    private static Control BuildSeverityBadge(LogAnalysisSeverity severity)
    {
        (IBrush brush, string text) = severity switch
        {
            LogAnalysisSeverity.Critical => (DangerBrush, L.T("Crash.Severity.Critical", "严重")),
            LogAnalysisSeverity.Warning => (WarningBrush, L.T("Crash.Severity.Warning", "可疑")),
            _ => (InfoBrush, L.T("Crash.Severity.Info", "信息")),
        };
        return new Border
        {
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
            },
            Background = brush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 3),
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private static Control BuildModIssuesCard(LogAnalysisResult result)
    {
        StackPanel card = new() { Spacing = 5 };
        card.Children.Add(new TextBlock
        {
            Text = L.T("Crash.Section.ModIssues", "模组依赖 / 冲突明细"),
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = TextBrush,
        });
        foreach (ModDependencyIssue issue in result.ModIssues)
        {
            card.Children.Add(new TextBlock
            {
                Text = "• " + issue,
                FontSize = 12.5,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Child = card,
            Background = FieldSoftBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
        };
    }

    private static Control BuildButtonsRow(LogAnalysisResult result)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };

        string reportPath = result.CrashReportPath;
        string hsErrPath = result.HsErrPath;

        Button copyButton = new()
        {
            Content = L.T("Crash.Action.Copy", "复制诊断"),
            Classes = { "primary", "dialog-action" },
        };
        copyButton.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(copyButton)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(result.BuildPlainText());
                    copyButton.Content = L.T("Crash.Action.Copied", "已复制 ✓");
                }
            }
            catch
            {
            }
        };
        row.Children.Add(copyButton);
        Button agentButton = new()
        {
            Content = L.T("Agent.Task.Crash.Action", "用 MofuAgent 排查"),
            Classes = { "dialog-secondary", "dialog-action" },
        };
        agentButton.Click += (_, _) =>
        {
            if (Shell.OpenAgentTask(LauncherAgentTasks.DiagnoseCrash(result)))
                MofuDialog.Close(agentButton);
        };
        row.Children.Add(agentButton);

        if (File.Exists(reportPath))
        {
            row.Children.Add(BuildOpenButton(L.T("Crash.Action.OpenReport", "打开崩溃报告"), reportPath));
        }
        else if (File.Exists(hsErrPath))
        {
            row.Children.Add(BuildOpenButton(L.T("Crash.Action.OpenHsErr", "打开 JVM 错误日志"), hsErrPath));
        }

        return row;
    }

    private static Button BuildOpenButton(string text, string path)
    {
        Button button = new() { Content = text, Classes = { "dialog-secondary", "dialog-action" } };
        button.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
            }
        };
        return button;
    }

    private static TextBlock DimLine(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = MutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
