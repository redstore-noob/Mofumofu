using System.Windows;
using NekoSolo.Services;

namespace NekoSolo;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        SoloStrings.Initialize();

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
