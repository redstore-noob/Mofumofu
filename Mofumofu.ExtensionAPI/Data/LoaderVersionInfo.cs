namespace Mofumofu.ExtensionAPI.Data;

/// <summary>某个游戏版本可用的加载器版本；LoaderVersion 需回传给安装请求。</summary>
public sealed record LoaderVersionInfo(
    ModLoaderType Loader,
    string GameVersion,
    string LoaderVersion,
    bool IsStable);
