using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MofuCore.Log;
using Mofumofu.ExtensionAPI.UI;
using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.Launcher.Services;

/// <summary>
/// 页面注入宿主：把插件注册的内容包在内置页面外层。
/// 页面本体占中间一行，插件内容按 Banner / Overlay / Footer 三个位置插入。
/// 页面切换或语言切换重建页面时，宿主会重新包一次，插件拿到的永远是新建的控件。
/// </summary>
public sealed class PageComposer : IPageContentHost
{
    /// <summary>注册到全部页面时使用的 pageId。</summary>
    public const string AnyPage = "*";

    /// <summary>宿主使用的单例，插件通过 IPluginContext 拿到同一个实例。</summary>
    public static PageComposer Current { get; } = new();

    private sealed class Entry
    {
        public required string PageId { get; init; }
        public required PageSlot Slot { get; init; }
        public required Func<Control> Factory { get; init; }
        public required int Order { get; init; }
        public required int Sequence { get; init; }
    }

    private sealed class Subscription(PageComposer owner, Entry entry) : IDisposable
    {
        private Entry? _entry = entry;

        public void Dispose()
        {
            if (_entry is null) return;

            owner._entries.Remove(_entry);
            _entry = null;
            Changed?.Invoke();
        }
    }

    private readonly List<Entry> _entries = [];
    private int _sequence;

    /// <summary>注册内容发生变化（新增或取消）时触发，宿主据此重建当前页面。</summary>
    public static event Action? Changed;

    private PageComposer()
    {
    }

    public IDisposable Add(string pageId, PageSlot slot, Func<Control> factory, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(factory);

        var entry = new Entry
        {
            PageId = pageId,
            Slot = slot,
            Factory = factory,
            Order = order,
            Sequence = _sequence++,
        };

        _entries.Add(entry);
        Changed?.Invoke();
        return new Subscription(this, entry);
    }

    /// <summary>
    /// 把页面包进注入宿主。每次进入页面时调用，返回新的外层控件。
    /// </summary>
    public Control Wrap(string pageId, Control page)
    {
        var host = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        Grid.SetRow(page, 1);
        host.Children.Add(page);

        StackPanel banner = BuildStack(pageId, PageSlot.Banner);
        if (banner.Children.Count > 0)
        {
            banner.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(banner, 0);
            host.Children.Add(banner);
        }

        StackPanel footer = BuildStack(pageId, PageSlot.Footer);
        if (footer.Children.Count > 0)
        {
            footer.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(footer, 2);
            host.Children.Add(footer);
        }

        var overlay = new Panel();
        foreach (Entry entry in Match(pageId, PageSlot.Overlay))
        {
            if (Create(entry) is { } content) overlay.Children.Add(content);
        }

        if (overlay.Children.Count > 0)
        {
            Grid.SetRow(overlay, 0);
            Grid.SetRowSpan(overlay, 3);
            host.Children.Add(overlay);
        }

        return host;
    }

    private StackPanel BuildStack(string pageId, PageSlot slot)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (Entry entry in Match(pageId, slot))
        {
            if (Create(entry) is { } content) panel.Children.Add(content);
        }

        return panel;
    }

    private List<Entry> Match(string pageId, PageSlot slot) =>
        _entries
            .Where(entry => entry.Slot == slot &&
                            (entry.PageId == AnyPage ||
                             string.Equals(entry.PageId, pageId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Sequence)
            .ToList();

    /// <summary>插件内容由外部代码创建，单个插件抛异常不能拖垮页面。</summary>
    private static Control? Create(Entry entry)
    {
        try
        {
            return entry.Factory();
        }
        catch (Exception exception)
        {
            LogWrite.Write("ERROR", $"插件内容创建失败（{entry.PageId}/{entry.Slot}）：{exception}");
            return null;
        }
    }
}
