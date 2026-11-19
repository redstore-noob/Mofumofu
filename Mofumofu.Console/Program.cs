using MofuCore.Config;
using MofuCore.Download.Minecraft;
using MofuCore.Server;
using Mofumofu.Console;
using Terminal.Gui.App;

YamlRead.Initialize();
YamlRead.EnsureGameDirectory();

// TUI 与 GUI 共用配置，但服务器目录固定到数据根下，避免随启动目录漂移。
ServerManage.ServerRootDirectory = Path.Combine(YamlRead.DataRoot, "Mofumofu", "mc-servers");
Directory.CreateDirectory(ServerManage.ServerRootDirectory);

TuiSettings settings = TuiSettings.Load();
DownloadSourceProvider.SetActive(settings.ResolveDownloadSource());

Application.Run<MofuMainWindow>();
