namespace Mofumofu.ExtensionAPI.Data;

/// <summary>某个游戏版本可用的加载器版本。</summary>
/// <param name="Loader">加载器类型</param>
/// <param name="GameVersion">适配的游戏版本号</param>
/// <param name="LoaderVersion">加载器版本号（安装请求里回传它）</param>
/// <param name="IsStable">是否稳定版（false = beta/preview）</param>
public sealed record LoaderVersionInfo(
    ModLoaderType Loader,
    string GameVersion,
    string LoaderVersion,
    bool IsStable);
