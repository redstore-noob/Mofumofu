using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mofumofu.Launcher.Models;

public sealed record InstanceInfo(
    string Id,
    string Name,
    string Version,
    string Loader,
    string Icon,
    string LastPlayed,
    string Playtime,
    string JavaPath,
    int MemoryMb,
    int ModCount,
    int WorldCount,
    bool Selected = false);

public sealed record GameVersion(string Id, string Type, string ReleaseDate, bool Installed, string Icon);

public sealed record ResourceItem(
    string Name,
    string Author,
    string Description,
    string Downloads,
    string Likes,
    string Category,
    string Icon)
{
    public string IconKey => Icon;
}

public sealed record ServerConsoleLine(string Time, string Level, string Text);

public sealed record WorldInfo(
    string Name, string Version, string LastPlayed, string IconGlyph, string FilePath = "")
{
    public string IconKey => IconGlyph;
}

public sealed record OnlineProvider(
    string Id,
    string Name,
    string Summary,
    string Homepage,
    bool Ready,
    bool NeedsMod,
    string HostNote,
    string JoinNote);

public sealed record CreatorTool(string Id, string Title, string Description, string IconGlyph, string AccentHex)
{
    public string IconKey => IconGlyph;
}

public sealed record LaunchLogLine(string Time, string Text);

public sealed record WidgetInfo(string Id, string Name, string Description, string IconGlyph)
{
    public string IconKey => IconGlyph;
}

public sealed record SettingsEntry(string Key, string Label, string Value, string Hint);
