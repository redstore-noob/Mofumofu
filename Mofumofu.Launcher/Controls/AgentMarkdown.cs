using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 轻量 Markdown 渲染：标题、段落、列表、引用、代码块、表格与常见行内样式。
/// 只依赖 Avalonia 原生控件，样式全部走 XAML 里的 .agent-* 类，跟随主题令牌。
/// 设计目标：流式输出时反复重建也不掉帧，坏语法不抛异常。
/// </summary>
public static class AgentMarkdown
{
    private static readonly Regex InlinePattern = new(
        @"(?<code>`[^`]+`)|(?<bold>\*\*[^*]+\*\*)|(?<italic>\*[^*\n]+\*)|(?<underline>__[^_]+__)|(?<link>\[[^\]]+\]\([^)]+\))",
        RegexOptions.Compiled);

    public static Control Render(string markdown, Action<string>? onLink = null, Action<string>? onCopy = null)
    {
        var root = new StackPanel { Spacing = 9 };
        if (string.IsNullOrEmpty(markdown)) return root;

        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var block = new TextBlock
            {
                Classes = { "agent-body" },
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
            };
            AppendInline(block, string.Join('\n', paragraph), onLink);
            root.Children.Add(block);
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // 代码围栏
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                string language = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[i]);
                    i++;
                }

                root.Children.Add(BuildCodeBlock(language, code.ToString().TrimEnd(), onCopy));
                continue;
            }

            // 表格
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsTableDivider(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<string[]> { SplitRow(trimmed) };
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                {
                    rows.Add(SplitRow(lines[i].Trim()));
                    i++;
                }

                i--;
                root.Children.Add(BuildTable(rows));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (Regex.IsMatch(trimmed, "^#{1,6}\\s"))
            {
                FlushParagraph();
                int level = trimmed.TakeWhile(ch => ch == '#').Count();
                string text = trimmed[level..].Trim();
                var heading = new TextBlock
                {
                    Classes = { level <= 2 ? "agent-h1" : "agent-h2" },
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, level <= 2 ? 6 : 3, 0, 0),
                };
                AppendInline(heading, text, onLink);
                root.Children.Add(heading);
                continue;
            }

            if (Regex.IsMatch(trimmed, "^([-*_])\\1{2,}$"))
            {
                FlushParagraph();
                root.Children.Add(new Border
                {
                    Classes = { "divider" },
                    Margin = new Thickness(0, 4),
                });
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].Trim().StartsWith('>'))
                {
                    quote.AppendLine(lines[i].Trim().TrimStart('>').Trim());
                    i++;
                }

                i--;
                var quoteBlock = new TextBlock
                {
                    Classes = { "agent-quote" },
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                };
                AppendInline(quoteBlock, quote.ToString().TrimEnd(), onLink);
                root.Children.Add(new Border
                {
                    Classes = { "agent-quote-card" },
                    Child = quoteBlock,
                });
                continue;
            }

            if (Regex.IsMatch(trimmed, "^[-*+]\\s"))
            {
                FlushParagraph();
                var items = new List<string>();
                while (i < lines.Length && Regex.IsMatch(lines[i].Trim(), "^[-*+]\\s"))
                {
                    items.Add(Regex.Replace(lines[i].Trim(), "^[-*+]\\s+", string.Empty));
                    i++;
                }

                i--;
                root.Children.Add(BuildList(items, numbered: false, onLink));
                continue;
            }

            if (Regex.IsMatch(trimmed, "^\\d+[.)]\\s"))
            {
                FlushParagraph();
                var items = new List<string>();
                while (i < lines.Length && Regex.IsMatch(lines[i].Trim(), "^\\d+[.)]\\s"))
                {
                    items.Add(Regex.Replace(lines[i].Trim(), "^\\d+[.)]\\s+", string.Empty));
                    i++;
                }

                i--;
                root.Children.Add(BuildList(items, numbered: true, onLink));
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
        return root;
    }

    private static Control BuildCodeBlock(string language, string code, Action<string>? onCopy)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Classes = { "agent-code-lang" },
            Text = string.IsNullOrWhiteSpace(language) ? "code" : language,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (onCopy is not null)
        {
            var copy = new Button
            {
                Classes = { "agent-mini" },
                Content = "复制",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            copy.Click += (_, _) => onCopy(code);
            Grid.SetColumn(copy, 1);
            header.Children.Add(copy);
        }

        var body = new TextBlock
        {
            Classes = { "agent-code" },
            Text = code,
            TextWrapping = TextWrapping.NoWrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(header);
        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        });

        return new Border { Classes = { "agent-code-card" }, Child = content };
    }

    private static Control BuildList(IReadOnlyList<string> items, bool numbered, Action<string>? onLink)
    {
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(2, 0, 0, 2) };
        for (int index = 0; index < items.Count; index++)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("18,*"),
            };

            row.Children.Add(new TextBlock
            {
                Classes = { "agent-body", "agent-marker" },
                Text = numbered ? (index + 1) + "." : "•",
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 6, 0),
            });

            var text = new TextBlock
            {
                Classes = { "agent-body" },
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
            };
            AppendInline(text, items[index], onLink);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static Control BuildTable(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return new Panel();

        int columns = rows.Max(row => row.Length);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", columns))),
        };

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int column = 0; column < columns; column++)
            {
                string text = column < rows[rowIndex].Length ? rows[rowIndex][column] : string.Empty;
                var cell = new TextBlock
                {
                    Classes = { "agent-table-cell" },
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                };
                if (rowIndex == 0) cell.Classes.Add("agent-table-head");
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new Border { Classes = { "agent-table-card" }, Child = grid };
    }

    private static bool IsTableDivider(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.Contains('-') &&
               trimmed.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0;
    }

    private static string[] SplitRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    /// <summary>把一行里的 **粗体** / *斜体* / `代码` / [链接](url) 拆成 Inline。</summary>
    private static void AppendInline(TextBlock target, string text, Action<string>? onLink)
    {
        if (string.IsNullOrEmpty(text)) return;

        int cursor = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            target.Inlines ??= new InlineCollection();
            if (match.Index > cursor)
                target.Inlines.Add(new Run(text[cursor..match.Index]));

            string value = match.Value;
            if (match.Groups["code"].Success)
            {
                target.Inlines.Add(new Run(value.Trim('`'))
                {
                    FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                    Foreground = ThemeTokens.Brush("NekoAccentBrush"),
                });
            }
            else if (match.Groups["bold"].Success)
            {
                target.Inlines.Add(new Run(value.Trim('*')) { FontWeight = FontWeight.SemiBold });
            }
            else if (match.Groups["italic"].Success)
            {
                target.Inlines.Add(new Run(value.Trim('*')) { FontStyle = FontStyle.Italic });
            }
            else if (match.Groups["underline"].Success)
            {
                target.Inlines.Add(new Run(value.Trim('_')) { TextDecorations = TextDecorations.Underline });
            }
            else if (match.Groups["link"].Success)
            {
                int close = value.IndexOf(']');
                string label = value[1..close];
                string url = value[(close + 2)..].TrimEnd(')');
                // Run 不支持指针事件，链接只做视觉标记；URL 一并显示便于复制。
                target.Inlines.Add(new Run(label)
                {
                    Foreground = ThemeTokens.Brush("NekoAccentBrush"),
                    TextDecorations = TextDecorations.Underline,
                });
                if (!string.Equals(label, url, StringComparison.Ordinal))
                    target.Inlines.Add(new Run(" (" + url + ")"));
            }

            cursor = match.Index + match.Length;
        }

        target.Inlines ??= new InlineCollection();
        if (cursor < text.Length) target.Inlines.Add(new Run(text[cursor..]));
    }
}
