namespace Mofumofu.ExtensionAPI.Data;

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
