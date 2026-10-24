namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 单个实例的信息大全喵
/// </summary>
/// <param name="Id">实例目录名</param>
/// <param name="Name">实例显示名</param>
/// <param name="GameVersion">游戏真实版本号</param>
/// <param name="Loader">Mod Loader名字</param>
/// <param name="LastPlayed">上次游玩时间</param>
/// <param name="TotalPlaytime">累计游戏时长</param>
/// <param name="ModCount">模组数量</param>
/// <param name="WorldCount">存档数量</param>
/// <param name="MemoryMb">分配的内存大小</param>
public record InstanceInfo(
    string Id,
    string Name,
    string GameVersion,
    string Loader,
    DateTime? LastPlayed,
    TimeSpan TotalPlaytime,
    int ModCount,
    int WorldCount,
    int MemoryMb);
