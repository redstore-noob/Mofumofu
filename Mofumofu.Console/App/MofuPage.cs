using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console;

public interface IMofuShell
{
    TuiSettings Settings { get; }
    AccountService Accounts { get; }

    void SetStatus(string text);
    void ShowPage(string key);
}

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
