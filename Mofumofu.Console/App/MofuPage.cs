using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console;

/// <summary>页面与主窗口之间的最小契约。</summary>
public interface IMofuShell
{
    TuiSettings Settings { get; }
    AccountService Accounts { get; }

    void SetStatus(string text);
    void ShowPage(string key);
}

/// <summary>所有 TUI 页面的基类。</summary>
public abstract class MofuPage : FrameView
{
    protected MofuPage(IMofuShell shell, string title)
    {
        Shell = shell;
        Title = title;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
    }

    protected IMofuShell Shell { get; }

    public abstract void Refresh();

    public virtual void OnShown() => Refresh();

    public virtual void OnHidden()
    {
    }
}
