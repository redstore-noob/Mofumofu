using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MofuCore.Config;
using MofuCore.Tools;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// Rewind 快照横向时间轴弹窗：旧 → 新（最右为最新），节点按真实时间间隔自适应留白，
/// 卡片上下交错挂载；每天起点有竖向虚线 + 日期标签；选中节点高亮并展开操作区。
/// 数据来自 MofuCore 的 <see cref="RewindSystem"/>；找不到游戏目录时显示空态。
/// </summary>
public static class RewindDialogContent
{
    private sealed record SnapshotEntry(
        string Id,
        DateTime CreatedAt,
        string Label,
        string ReasonLabel,
        string ReasonColor,
        string Size,
        string Color,
        bool IsSafetySnapshot,
        bool IsReal);

    private const double DialogWidth = 980;
    private const double TimelineHeight = 320;
    private const double CardAreaHeight = 140;
    private const double AxisRowHeight = 40;
    private const double DayMarkerWidth = 64;
    private const double MinimumSlotWidth = 150;
    private const double MaximumSlotWidth = 280;
    private const double MinimumCardWidth = 126;

    private static readonly int[] IntervalMinutes = [1, 5, 15, 30];

    private static readonly (string Color, string Key, string Fallback)[] ColorChoices =
    [
        ("", "Rewind.Color.None", "无标记"),
        ("primary", "Rewind.Color.Primary", "主题色"),
        ("warning", "Rewind.Color.Warning", "琥珀"),
        ("success", "Rewind.Color.Success", "绿色"),
        ("danger", "Rewind.Color.Danger", "红色"),
    ];

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
    private static IBrush SuccessSoftBrush => ThemeTokens.Brush("NekoSuccessSoftBrush");
    private static IBrush InfoBrush => ThemeTokens.Brush("NekoInfoBrush");
    private static IBrush InfoSoftBrush => ThemeTokens.Brush("NekoInfoSoftBrush");

    private static IBrush DotBrush(string color) => color switch
    {
        "warning" => WarningBrush,
        "success" => SuccessBrush,
        "danger" => DangerBrush,
        "primary" => AccentBrush,
        "secondary" => MutedBrush,
        _ => DimBrush,
    };

    private static (IBrush Background, IBrush Foreground) ReasonColors(string reasonColor) => reasonColor switch
    {
        "warning" => (WarningSoftBrush, WarningBrush),
        "success" => (SuccessSoftBrush, SuccessBrush),
        "info" => (InfoSoftBrush, InfoBrush),
        _ => (AccentSoftBrush, AccentBrush),
    };

    /// <param name="targetName">目标名（实例 id 或世界名），用于解析与展示。</param>
    /// <param name="onRestored">回滚完成后的回调。</param>
    /// <param name="explicitDirectory">
    /// 显式目标目录（如实例隔离目录下的 saves/世界）。提供时优先于按名字解析，
    /// 因为 FindTargetDirectory 只扫全局 saves，找不到版本隔离实例的存档。
    /// </param>
    public static Control Build(string targetName, Action? onRestored = null, string? explicitDirectory = null)
    {
        targetName = string.IsNullOrWhiteSpace(targetName) ? L.T("Rewind.Target.Default", "当前实例") : targetName.Trim();
        (string Directory, RewindKind Kind)? target =
            !string.IsNullOrWhiteSpace(explicitDirectory) && Directory.Exists(explicitDirectory)
                ? (explicitDirectory, RewindKind.Save)
                : FindTargetDirectory(targetName);
        bool isReal = target is not null;

        List<SnapshotEntry> entries = [];
        string? selectedId = entries.Count > 0 ? entries[^1].Id : null;
        string hintText = target is null
            ? (string.IsNullOrWhiteSpace(SafeGameDirectory())
                ? L.T("Rewind.Mock.NoGameDirectory", "未找到游戏目录")
                : L.T("Rewind.Mock.NoInstanceDirectory", "未找到 {0} 的游戏目录", targetName))
            : string.Empty;
        string statusText = isReal ? target!.Value.Directory : hintText;

        RewindScheduler? scheduler = null;
        bool initializingAuto = true;
        bool scrollToLatestPending = true;

        var timeline = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = TimelineHeight,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var timelineScroll = new ScrollViewer
        {
            Height = TimelineHeight + 26,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = timeline,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var status = new TextBlock
        {
            Text = statusText,
            FontSize = 10.5,
            Foreground = isReal ? DimBrush : WarningBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        var noteInput = new TextBox
        {
            PlaceholderText = L.T("Rewind.Note.Placeholder", "快照备注（可选）"),
            Height = 32,
            MinWidth = 220,
            Background = FieldSoftBrush,
            BorderBrush = BorderBrush,
            CornerRadius = new CornerRadius(8),
            FontSize = 12,
        };

        void ScrollToLatest()
        {
            void Apply()
            {
                double maximum = Math.Max(0, timelineScroll.Extent.Width - timelineScroll.Viewport.Width);
                timelineScroll.Offset = new Vector(maximum, 0);
            }

            Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
            Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
        }

        void Render()
        {
            timeline.Children.Clear();
            if (entries.Count == 0)
            {
                timeline.HorizontalAlignment = HorizontalAlignment.Center;
                timelineScroll.HorizontalContentAlignment = HorizontalAlignment.Center;
                timeline.Children.Add(new TextBlock
                {
                    Text = L.T("Rewind.Empty", "暂无快照"),
                    Margin = new Thickness(0, 130, 0, 0),
                    Foreground = MutedBrush,
                    FontSize = 12.5,
                });
                return;
            }

            bool single = entries.Count == 1;
            timeline.HorizontalAlignment = single ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            timelineScroll.HorizontalContentAlignment = single ? HorizontalAlignment.Center : HorizontalAlignment.Left;

            Dictionary<DateTime, int> dayCounts = new();
            foreach (SnapshotEntry entry in entries)
            {
                dayCounts[entry.CreatedAt.Date] = dayCounts.TryGetValue(entry.CreatedAt.Date, out int count) ? count + 1 : 1;
            }

            DateTime? previousDate = null;
            DateTime previousTime = entries[0].CreatedAt;
            for (int index = 0; index < entries.Count; index++)
            {
                SnapshotEntry entry = entries[index];
                bool newDay = previousDate is null || previousDate.Value.Date != entry.CreatedAt.Date;
                if (newDay)
                {
                    timeline.Children.Add(BuildDayMarker(entry.CreatedAt, dayCounts[entry.CreatedAt.Date]));
                    previousTime = entry.CreatedAt;
                }

                double slotWidth = index == 0 ? MinimumSlotWidth : SlotWidth(previousTime, entry.CreatedAt);
                bool isLatest = index == entries.Count - 1;
                timeline.Children.Add(BuildSlot(entry, slotWidth, isTop: index % 2 == 0, isLatest, entry.Id == selectedId));

                previousTime = entry.CreatedAt;
                previousDate = entry.CreatedAt;
            }

            if (scrollToLatestPending)
            {
                scrollToLatestPending = false;
                ScrollToLatest();
            }
        }

        void SelectEntry(SnapshotEntry entry)
        {
            selectedId = entry.Id;
            Render();
        }

        async Task ReloadAsync()
        {
            if (target is null)
            {
                Dispatcher.UIThread.Post(Render);
                return;
            }

            try
            {
                RewindTimeline snapshotTimeline = await RewindSystem.GetTimelineAsync(target.Value.Directory);
                Dispatcher.UIThread.Post(() =>
                {
                    entries = ToEntries(snapshotTimeline);
                    if (selectedId is null || entries.All(entry => entry.Id != selectedId))
                    {
                        selectedId = entries.Count > 0 ? entries[^1].Id : null;
                    }

                    status.Text = StatusTextForReal(target.Value.Directory, snapshotTimeline);
                    Render();
                    ScrollToLatest();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Shell.Toast(L.T("Rewind.Error.Load", "读取快照失败：{0}", ex.Message)));
            }
        }

        async Task CreateSnapshotAsync()
        {
            string label = (noteInput.Text ?? string.Empty).Trim();
            try
            {
                if (target is null)
                {
                    Shell.Toast(hintText);
                    return;
                }

                await RewindSystem.CreateSnapshotAsync(
                    target.Value.Directory,
                    target.Value.Kind,
                    label.Length > 0 ? label : null);
                noteInput.Text = string.Empty;
                selectedId = null;
                await ReloadAsync();
                Shell.Toast(L.T("Rewind.Toast.Created", "快照已创建"));
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Rewind.Error.Create", "创建快照失败：{0}", ex.Message));
            }
        }

        async Task RollbackAsync(SnapshotEntry entry)
        {
            try
            {
                if (!entry.IsReal || target is null)
                {
                    Shell.Toast(L.T("Rewind.Toast.RolledBackMock", "已回滚到 {0} 的快照", entry.CreatedAt.ToString("HH:mm")));
                    onRestored?.Invoke();
                    return;
                }

                RewindRollbackResult result = await RewindSystem.RollbackAsync(
                    target.Value.Directory, entry.Id, target.Value.Kind);
                selectedId = null;
                await ReloadAsync();
                onRestored?.Invoke();
                Shell.Toast(L.T("Rewind.Toast.RolledBack", "已回滚到 {0} 的快照（已自动创建安全快照 {1}）", entry.CreatedAt.ToString("HH:mm"), result.SafetySnapshot.Id));
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Rewind.Error.Rollback", "回滚失败：{0}", ex.Message));
            }
        }

        async Task DeleteSnapshotAsync(SnapshotEntry entry)
        {
            try
            {
                if (!entry.IsReal || target is null)
                {
                    entries = entries.Where(item => !string.Equals(item.Id, entry.Id, StringComparison.Ordinal)).ToList();
                    if (string.Equals(selectedId, entry.Id, StringComparison.Ordinal))
                    {
                        selectedId = entries.Count > 0 ? entries[^1].Id : null;
                    }

                    Render();
                    Shell.Toast(L.T("Rewind.Toast.DeletedMock", "快照已删除"));
                    return;
                }

                await RewindSystem.DeleteSnapshotAsync(target.Value.Directory, entry.Id);
                if (string.Equals(selectedId, entry.Id, StringComparison.Ordinal))
                {
                    selectedId = null;
                }

                await ReloadAsync();
                Shell.Toast(L.T("Rewind.Toast.Deleted", "快照已删除"));
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Rewind.Error.Delete", "删除快照失败：{0}", ex.Message));
            }
        }

        async Task SetColorAsync(SnapshotEntry entry, string color)
        {
            try
            {
                if (!entry.IsReal || target is null)
                {
                    string normalized = NormalizeColor(color);
                    entries = entries
                        .Select(item => string.Equals(item.Id, entry.Id, StringComparison.Ordinal) ? item with { Color = normalized } : item)
                        .ToList();
                    Render();
                    Shell.Toast(L.T("Rewind.Toast.ColorUpdatedMock", "已更新标记颜色"));
                    return;
                }

                await RewindSystem.SetSnapshotColorAsync(target.Value.Directory, entry.Id, color);
                await ReloadAsync();
                Shell.Toast(string.IsNullOrEmpty(color)
                    ? L.T("Rewind.Toast.ColorCleared", "已清除标记颜色")
                    : L.T("Rewind.Toast.ColorUpdated", "已更新标记颜色"));
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Rewind.Error.Color", "标记颜色失败：{0}", ex.Message));
            }
        }

        void BindConfirm(Button button, object? idleContent, object confirmContent, string hint, Func<Task> action)
        {
            bool armed = false;
            int generation = 0;

            button.Click += async (_, _) =>
            {
                if (!armed)
                {
                    armed = true;
                    int currentGeneration = ++generation;
                    button.Content = confirmContent;
                    Shell.Toast(hint);
                    _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                    {
                        if (armed && generation == currentGeneration)
                        {
                            armed = false;
                            button.Content = idleContent;
                        }
                    }));
                    return;
                }

                armed = false;
                generation++;
                button.Content = idleContent;
                button.IsEnabled = false;
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    Shell.Toast(L.T("Rewind.Error.Action", "操作失败：{0}", ex.Message));
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
        }

        MenuFlyout BuildColorFlyout(SnapshotEntry entry)
        {
            MenuFlyout flyout = new();
            foreach ((string color, string key, string fallback) in ColorChoices)
            {
                MenuItem item = new() { Header = L.T(key, fallback) };
                item.Click += (_, _) => _ = SetColorAsync(entry, color);
                flyout.Items.Add(item);
            }

            return flyout;
        }

        Control BuildDayMarker(DateTime date, int count)
        {
            var marker = new Canvas { Width = DayMarkerWidth, Height = TimelineHeight };

            var dashes = new StackPanel { Spacing = 6, Width = 1 };
            for (int index = 0; index < 26; index++)
            {
                dashes.Children.Add(new Border { Width = 1, Height = 6, Background = BorderBrush });
            }

            Canvas.SetLeft(dashes, 15);
            Canvas.SetTop(dashes, 6);
            marker.Children.Add(dashes);

            var axis = new Border { Width = DayMarkerWidth, Height = 1, Background = BorderBrush };
            Canvas.SetLeft(axis, 0);
            Canvas.SetTop(axis, TimelineHeight / 2);
            marker.Children.Add(axis);

            var countBadge = new Border
            {
                Background = AccentSoftBrush,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1),
                Child = new TextBlock
                {
                    Text = L.T("Rewind.Day.Count", "{0} 份", count),
                    FontSize = 9,
                    Foreground = AccentBrush,
                },
            };
            Canvas.SetLeft(countBadge, -18);
            Canvas.SetTop(countBadge, CardAreaHeight - 32);
            marker.Children.Add(countBadge);

            var dateLabel = new TextBlock
            {
                Text = FormatDayLabel(date, DateTime.Today),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = MutedBrush,
            };
            Canvas.SetLeft(dateLabel, 4);
            Canvas.SetTop(dateLabel, CardAreaHeight + AxisRowHeight - 16);
            marker.Children.Add(dateLabel);

            return marker;
        }

        Control BuildSlot(SnapshotEntry entry, double width, bool isTop, bool isLatest, bool isSelected)
        {
            var slot = new Grid
            {
                Width = width,
                Height = TimelineHeight,
                RowDefinitions = new RowDefinitions($"{CardAreaHeight},{AxisRowHeight},{CardAreaHeight}"),
            };

            slot.Children.Add(new Border
            {
                Height = 1,
                Background = BorderBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            Control node = BuildNode(entry, isSelected);
            Grid.SetRow(node, 1);
            slot.Children.Add(node);

            Control card = BuildCard(entry, width, isLatest, isSelected);
            Grid.SetRow(card, isTop ? 0 : 2);
            card.VerticalAlignment = isTop ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            card.Margin = isTop ? new Thickness(0, 0, 0, 10) : new Thickness(0, 10, 0, 0);
            slot.Children.Add(card);

            return slot;
        }

        Control BuildNode(SnapshotEntry entry, bool isSelected)
        {
            var inner = new Grid { Width = 24, Height = 24 };
            if (isSelected)
            {
                inner.Children.Add(new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    BorderBrush = AccentBrush,
                    BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            double dotSize = isSelected ? 12 : 10;
            inner.Children.Add(new Border
            {
                Width = dotSize,
                Height = dotSize,
                CornerRadius = new CornerRadius(dotSize / 2),
                Background = DotBrush(entry.Color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var node = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Background = ThemeTokens.WithAlpha("NekoMutedBrush", 0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = inner,
            };
            ToolTip.SetTip(node, $"{entry.CreatedAt:yyyy-MM-dd HH:mm:ss} · {entry.ReasonLabel}");
            node.Click += (_, _) => SelectEntry(entry);
            return node;
        }

        Control BuildCard(SnapshotEntry entry, double slotWidth, bool isLatest, bool isSelected)
        {
            double cardWidth = Math.Max(MinimumCardWidth, slotWidth - 20);

            var colorDot = new Button
            {
                Width = 14,
                Height = 14,
                Padding = new Thickness(0),
                Background = ThemeTokens.WithAlpha("NekoMutedBrush", 0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = DotBrush(entry.Color),
                },
                Flyout = BuildColorFlyout(entry),
            };
            ToolTip.SetTip(colorDot, L.T("Rewind.Color.Tooltip", "点击标记颜色"));

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    colorDot,
                    new TextBlock
                    {
                        Text = entry.CreatedAt.ToString("HH:mm"),
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = TextBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };

            (IBrush reasonBackground, IBrush reasonForeground) = ReasonColors(entry.ReasonColor);
            header.Children.Add(new Border
            {
                Background = reasonBackground,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = entry.ReasonLabel,
                    FontSize = 9,
                    Foreground = reasonForeground,
                },
            });

            if (isLatest)
            {
                header.Children.Add(new Border
                {
                    Background = AccentSoftBrush,
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(5, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = L.T("Rewind.Latest", "最新"),
                        FontSize = 9,
                        Foreground = AccentBrush,
                    },
                });
            }

            var content = new StackPanel { Spacing = 2, Children = { header } };

            if (!string.IsNullOrWhiteSpace(entry.Label))
            {
                content.Children.Add(new TextBlock
                {
                    Text = entry.Label,
                    FontSize = 11,
                    Foreground = TextBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = $"{entry.Size} · {entry.CreatedAt:MM-dd}",
                FontSize = 9.5,
                Foreground = DimBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (isSelected)
            {
                var rollback = new Button
                {
                    Content = L.T("Rewind.Action.Rollback", "回滚"),
                    Classes = { "ghost" },
                    Height = 24,
                    Padding = new Thickness(8, 0),
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                string rollbackIdle = L.T("Rewind.Action.Rollback", "回滚");
                BindConfirm(
                    rollback,
                    rollbackIdle,
                    L.T("Rewind.Confirm.Rollback", "确认回滚?"),
                    L.T("Rewind.Confirm.RollbackHint", "再次点击确认回滚（会先自动创建安全快照）"),
                    () => RollbackAsync(entry));

                var removeIdle = new TextBlock
                {
                    Text = "✕",
                    FontSize = 11,
                    Foreground = DangerBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var remove = new Button
                {
                    Classes = { "ghost" },
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    Content = removeIdle,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                BindConfirm(remove, removeIdle, new TextBlock
                {
                    Text = L.T("Rewind.Confirm.Delete", "确定?"),
                    FontSize = 9.5,
                    Foreground = DangerBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }, L.T("Rewind.Confirm.DeleteHint", "再次点击删除该快照"), () => DeleteSnapshotAsync(entry));

                content.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(0, 3, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { rollback, remove },
                });
            }

            var card = new Border
            {
                Width = cardWidth,
                Background = isSelected ? AccentSoftBrush : FieldSoftBrush,
                BorderBrush = isSelected ? AccentBrush : BorderBrush,
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6),
                Child = content,
            };
            card.PointerPressed += (_, e) =>
            {
                if (e.Source is Control source && source.FindAncestorOfType<Button>(true) is not null)
                {
                    return;
                }

                SelectEntry(entry);
                e.Handled = true;
            };
            return card;
        }

        var createButton = new Button
        {
            Content = L.T("Rewind.Action.Create", "新建快照"),
            Classes = { "primary" },
            Height = 32,
            Padding = new Thickness(14, 0),
            FontSize = 12,
        };
        createButton.Click += async (_, _) =>
        {
            createButton.IsEnabled = false;
            try
            {
                await CreateSnapshotAsync();
            }
            finally
            {
                createButton.IsEnabled = true;
            }
        };

        var refreshButton = new Button
        {
            Classes = { "icon" },
            Width = 32,
            Height = 32,
            Content = new IconParkIcon
            {
                Kind = "IconRefresh",
                Width = 13,
                Height = 13,
            },
        };
        refreshButton.Click += async (_, _) =>
        {
            try
            {
                scrollToLatestPending = true;
                await ReloadAsync();
                Shell.Toast(L.T("Rewind.Toast.Refreshed", "快照列表已刷新"));
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Rewind.Error.Refresh", "刷新失败：{0}", ex.Message));
            }
        };
        ToolTip.SetTip(refreshButton, L.T("Rewind.Action.Refresh", "刷新"));

        var createRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        createRow.Children.Add(noteInput);
        Grid.SetColumn(createButton, 1);
        createButton.Margin = new Thickness(8, 0, 0, 0);
        createRow.Children.Add(createButton);
        Grid.SetColumn(refreshButton, 2);
        refreshButton.Margin = new Thickness(8, 0, 0, 0);
        createRow.Children.Add(refreshButton);

        var autoSwitch = new ToggleSwitch
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = isReal,
        };
        var interval = new ComboBox
        {
            Width = 122,
            Height = 32,
            ItemsSource = IntervalMinutes.Select(IntervalLabel).ToArray(),
            SelectedIndex = 1,
            FontSize = 11.5,
            IsEnabled = isReal,
        };

        int SelectedIntervalMinutes() =>
            IntervalMinutes[Math.Clamp(interval.SelectedIndex, 0, IntervalMinutes.Length - 1)];

        var autoRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Rewind.Auto.Label", "定时创建快照"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = TextBrush,
                },
                autoSwitch,
                new TextBlock
                {
                    Text = L.T("Rewind.Auto.Every", "每"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = DimBrush,
                },
                interval,
            },
        };

        void StartScheduler()
        {
            if (target is null)
            {
                return;
            }

            int seconds = SelectedIntervalMinutes() * 60;
            scheduler?.Dispose();
            scheduler = RewindSystem.StartScheduler(target.Value.Directory, TimeSpan.FromSeconds(seconds), target.Value.Kind);
            scheduler.SnapshotCreated += snapshot =>
                Dispatcher.UIThread.Post(() =>
                {
                    Shell.Toast(L.T("Rewind.Toast.AutoCreated", "已自动创建快照 {0}", snapshot.CreatedAt.ToString("HH:mm")));
                    scrollToLatestPending = true;
                    _ = ReloadAsync();
                });
        }

        void SaveAutoSettings(bool enabled)
        {
            if (target is null)
            {
                return;
            }

            RewindSettings settings = RewindSystem.LoadSettings();
            settings.AutoSnapshotEnabled = enabled;
            settings.AutoSnapshotIntervalSeconds = SelectedIntervalMinutes() * 60;
            RewindSystem.SaveSettings(settings);
        }

        autoSwitch.IsCheckedChanged += (_, _) =>
        {
            if (initializingAuto)
            {
                return;
            }

            bool enabled = autoSwitch.IsChecked == true;
            if (target is null)
            {
                initializingAuto = true;
                autoSwitch.IsChecked = false;
                initializingAuto = false;
                Shell.Toast(L.T("Rewind.Toast.MockNoGameDirectory", "未找到游戏目录"));
                return;
            }

            try
            {
                SaveAutoSettings(enabled);
                if (enabled)
                {
                    StartScheduler();
                    Shell.Toast(L.T("Rewind.Toast.SchedulerOn", "已开启定时快照（每 {0} 分钟）", SelectedIntervalMinutes()));
                }
                else
                {
                    scheduler?.Dispose();
                    scheduler = null;
                    Shell.Toast(L.T("Rewind.Toast.SchedulerOff", "已关闭定时快照"));
                }
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Rewind.Error.Scheduler", "定时快照设置失败：{0}", ex.Message));
            }
        };

        interval.SelectionChanged += (_, _) =>
        {
            if (initializingAuto || target is null)
            {
                return;
            }

            try
            {
                SaveAutoSettings(autoSwitch.IsChecked == true);
                if (autoSwitch.IsChecked == true)
                {
                    StartScheduler();
                }
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Rewind.Error.Scheduler", "定时快照设置失败：{0}", ex.Message));
            }
        };

        if (target is not null)
        {
            try
            {
                RewindSettings settings = RewindSystem.LoadSettings();
                initializingAuto = true;
                autoSwitch.IsChecked = settings.AutoSnapshotEnabled;
                interval.SelectedIndex = ClosestIntervalIndex(settings.AutoSnapshotIntervalSeconds);
                initializingAuto = false;
                if (settings.AutoSnapshotEnabled)
                {
                    StartScheduler();
                }
            }
            catch (Exception ex)
            {
                initializingAuto = false;
                Shell.Toast(L.T("Rewind.Error.Settings", "读取定时快照设置失败：{0}", ex.Message));
            }
        }

        var root = new StackPanel
        {
            Width = DialogWidth,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Rewind.Title", "实例快照时间线 · {0}", targetName),
                    FontSize = 12,
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2),
                },
                status,
                createRow,
                autoRow,
                new Border { Height = 1, Background = BorderBrush, Margin = new Thickness(0, 2) },
                timelineScroll,
                new TextBlock
                {
                    Text = L.T("Rewind.Footer", "横向时间轴：旧 → 新（最右为最新）。每次快照只保存与上一份不同的文件，回滚前会自动创建安全快照。"),
                    FontSize = 10.5,
                    Foreground = DimBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                },
            },
        };

        Render();
        if (target is not null)
        {
            _ = ReloadAsync();
        }

        // SukiUI 弹窗在离屏/部分主题下是半透明材质，横向时间轴留白多会透出下层页面，
        // 这里给内容补一层不透明底板，保证时间轴区域干净可读。
        return new Border
        {
            Background = ThemeTokens.Brush("NekoDialogSolidBrush"),
            CornerRadius = new CornerRadius(12),
            // 用负外边距把底板铺到 SukiDialog 的内边距上，避免边缘透出下层页面
            Margin = new Thickness(-18, -14, -18, -14),
            Padding = new Thickness(20, 16, 20, 14),
            Child = root,
        };
    }

    private static double SlotWidth(DateTime previous, DateTime current)
    {
        // 间隔越宽留白越多（对数压缩），并限制上限避免稀疏
        double hours = Math.Max(0, (current - previous).TotalHours);
        double width = MinimumSlotWidth + Math.Min(MaximumSlotWidth - MinimumSlotWidth, Math.Log(1 + hours, 2) * 22);
        return Math.Clamp(width, MinimumSlotWidth, MaximumSlotWidth);
    }

    private static string SafeGameDirectory()
    {
        try
        {
            return YamlRead.GameDirectory;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static (string Directory, RewindKind Kind)? FindTargetDirectory(string targetName)
    {
        try
        {
            string gameDirectory = YamlRead.GameDirectory;
            if (string.IsNullOrWhiteSpace(gameDirectory) || string.IsNullOrWhiteSpace(targetName))
            {
                return null;
            }

            List<(string Directory, RewindKind Kind)> candidates = [];
            string versionsRoot = Path.Combine(gameDirectory, "versions");
            if (Directory.Exists(versionsRoot))
            {
                candidates.Add((Path.Combine(versionsRoot, targetName), RewindKind.Instance));
                TryAddExactMatch(versionsRoot, targetName, RewindKind.Instance, candidates);
            }

            string savesRoot = Path.Combine(gameDirectory, "saves");
            if (Directory.Exists(savesRoot))
            {
                candidates.Add((Path.Combine(savesRoot, targetName), RewindKind.Save));
                TryAddExactMatch(savesRoot, targetName, RewindKind.Save, candidates);
            }

            foreach ((string directory, RewindKind kind) in candidates)
            {
                if (Directory.Exists(directory))
                {
                    return (directory, kind);
                }
            }
        }
        catch
        {
            // 游戏目录不可读时按没有实例处理，界面显示空态
        }

        return null;
    }

    private static void TryAddExactMatch(
        string root,
        string targetName,
        RewindKind kind,
        List<(string Directory, RewindKind Kind)> candidates)
    {
        try
        {
            string? match = Directory.GetDirectories(root)
                .FirstOrDefault(directory => string.Equals(Path.GetFileName(directory), targetName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                candidates.Add((match, kind));
            }
        }
        catch
        {
            // 目录不可读时忽略
        }
    }

    private static List<SnapshotEntry> ToEntries(RewindTimeline timeline)
    {
        return timeline.Days
            .SelectMany(day => day.Snapshots)
            .OrderBy(snapshot => snapshot.CreatedAt)
            .Select(snapshot => new SnapshotEntry(
                snapshot.Id,
                snapshot.CreatedAt.LocalDateTime,
                snapshot.Label,
                ReasonLabel(snapshot.Reason),
                ReasonColor(snapshot.Reason),
                snapshot.TotalSizeDisplay,
                snapshot.Color,
                snapshot.IsSafetySnapshot,
                true))
            .ToList();
    }

    private static string ReasonLabel(string? reason) => reason switch
    {
        RewindReasons.BeforeRollback => L.T("Rewind.Reason.Safety", "安全快照"),
        RewindReasons.BeforeLaunch => L.T("Rewind.Reason.BeforeLaunch", "启动前"),
        RewindReasons.Scheduled => L.T("Rewind.Reason.Scheduled", "定时"),
        _ => L.T("Rewind.Reason.Manual", "手动"),
    };

    private static string ReasonColor(string? reason) => reason switch
    {
        RewindReasons.BeforeRollback => "warning",
        RewindReasons.BeforeLaunch => "info",
        RewindReasons.Scheduled => "success",
        _ => "primary",
    };

    /// <summary>
    /// 日期分组标签：不直接用 <see cref="RewindTimeline"/> 里的中文 Label，
    /// 而是按当前语言重新格式化（中文 今天/昨天/X 月 X 日；英文 Today/Yesterday/MMM d）。
    /// </summary>
    private static string FormatDayLabel(DateTime value, DateTime today)
    {
        if (value.Date == today) return L.T("Rewind.Day.Today", "今天");
        if (value.Date == today.AddDays(-1)) return L.T("Rewind.Day.Yesterday", "昨天");

        string monthName = L.IsChinese
            ? value.Month.ToString(CultureInfo.InvariantCulture)
            : value.ToString("MMM", CultureInfo.GetCultureInfo("en-US"));
        return value.Year == today.Year
            ? L.T("Rewind.Day.ThisYear", "{0} 月 {1} 日", monthName, value.Day)
            : L.T("Rewind.Day.OtherYear", "{2} 年 {0} 月 {1} 日", monthName, value.Day, value.Year);
    }

    private static string IntervalLabel(int minutes) => L.T("Rewind.Interval.Minutes", "{0} 分钟", minutes);

    private static string NormalizeColor(string? color)
    {
        string value = (color ?? string.Empty).Trim().ToLowerInvariant();
        return value is "" or "primary" or "warning" or "success" or "danger" or "secondary" ? value : string.Empty;
    }

    private static int ClosestIntervalIndex(int seconds)
    {
        int minutes = Math.Max(1, seconds / 60);
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < IntervalMinutes.Length; index++)
        {
            int distance = Math.Abs(IntervalMinutes[index] - minutes);
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static string StatusTextForReal(string directory, RewindTimeline timeline)
    {
        return L.T(
            "Rewind.Status.Summary",
            "{0} · 共 {1} 份快照 · 占用 {2}",
            directory,
            timeline.SnapshotCount,
            MemoryRead.FormatBytes(timeline.TotalSize));
    }
}
