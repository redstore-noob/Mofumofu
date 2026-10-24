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
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 游戏崩溃分析弹窗：展示 <see cref="LogAnalysis"/> 的结论（原因 + 证据 + 决策），
/// 支持一键复制纯文本诊断与打开崩溃报告 / hs_err 文件所在位置。
/// 弹窗本身经 <see cref="Shell.Dialog"/> 交给主窗口的对话框宿主显示。
/// </summary>
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
    private static IBrush InfoBrush => ThemeTokens.Brush("NekoInfoBrush");

    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");

    public static Control Build(LogAnalysisResult result, string instanceName)
    {
        ArgumentNullException.ThrowIfNull(result);

        StackPanel panel = new() { Width = DialogWidth, Spacing = 12 };

        panel.Children.Add(BuildMetaLine(result, instanceName));
        panel.Children.Add(BuildSummaryCard(result));
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

        return new ScrollViewer
        {
            Content = panel,
            MaxHeight = DialogMaxHeight,
        };
    }

    /// <summary>元信息一行：实例 · 版本 · 加载器 · Java · 内存 · 退出码。</summary>
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
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

        string reportPath = result.CrashReportPath;
        string hsErrPath = result.HsErrPath;

        Button copyButton = new()
        {
            Content = L.T("Crash.Action.Copy", "复制诊断"),
            Classes = { "primary" },
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
                // 剪贴板不可用（远程会话等）：保持原样
            }
        };
        row.Children.Add(copyButton);

        if (File.Exists(reportPath))
        {
            row.Children.Add(BuildOpenButton(L.T("Crash.Action.OpenReport", "打开崩溃报告"), reportPath));
        }
        else if (File.Exists(hsErrPath))
        {
            row.Children.Add(BuildOpenButton(L.T("Crash.Action.OpenHsErr", "打开 JVM 错误日志"), hsErrPath));
        }

        row.Children.Add(new TextBlock
        {
            Text = L.T("Crash.Hint.Close", "点击弹窗外任意位置关闭"),
            FontSize = 11,
            Foreground = DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        return row;
    }

    private static Button BuildOpenButton(string text, string path)
    {
        Button button = new() { Content = text };
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
                // explorer 拉不起来就静默失败，路径已在弹窗里可见
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
