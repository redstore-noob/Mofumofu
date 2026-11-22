using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mofumofu.Launcher.Services.Agent;
using YamlDotNet.Serialization;

namespace Mofumofu.Launcher.Services.Music;

public sealed class MusicSettings
{
    public List<string> Folders { get; set; } = new();

    public List<string> Favorites { get; set; } = new();

    public int Volume { get; set; } = 72;

    public bool Loop { get; set; }

    [YamlIgnore]
    public string NetEaseCookie { get; set; } = string.Empty;

    [YamlMember(Alias = "NetEaseCookie")]
    public string NetEaseCookieProtected
    {
        get => AgentSecretProtector.Protect(NetEaseCookie);
        set => NetEaseCookie = AgentSecretProtector.Unprotect(value);
    }

    public string NetEaseNickname { get; set; } = string.Empty;

    public long LastNetEaseSongId { get; set; }

    public List<MusicFavoriteEntry> FavoriteDetails { get; set; } = new();

    public List<MusicPlaylist> Playlists { get; set; } = new();
}

public sealed class MusicPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<MusicFavoriteEntry> Tracks { get; set; } = new();
}

public sealed class MusicFavoriteEntry
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public string Source { get; set; } = "netease";

    public string LocalPath { get; set; } = string.Empty;

    public string CoverUrl { get; set; } = string.Empty;

    public double DurationMs { get; set; }

    public MusicItem ToItem()
    {
        return new MusicItem
        {
            Id = Id,
            Title = Title,
            Artist = Artist,
            Album = Album,
            Source = Source,
            LocalPath = LocalPath,
            Duration = TimeSpan.FromMilliseconds(DurationMs),
            CoverUrl = CoverUrl,
        };
    }

    public static MusicFavoriteEntry From(MusicItem item)
    {
        return new MusicFavoriteEntry
        {
            Id = item.Id,
            Title = item.Title,
            Artist = item.Artist,
            Album = item.Album,
            Source = item.Source,
            LocalPath = item.LocalPath,
            DurationMs = item.Duration.TotalMilliseconds,
            CoverUrl = item.CoverUrl,
        };
    }
}

public static class MusicStore
{
    private static readonly YamlDotNet.Serialization.ISerializer Serializer =
        new YamlDotNet.Serialization.SerializerBuilder().WithIndentedSequences().Build();

    private static readonly YamlDotNet.Serialization.IDeserializer Deserializer =
        new YamlDotNet.Serialization.DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public static string? LastError { get; private set; }

    private static void LogFailure(string message)
    {
        LastError = message;
        System.Diagnostics.Debug.WriteLine("[music] " + message);
    }

    public static string SettingsPath => Path.Combine(
        MofuCore.Config.YamlRead.ConfigDirectory,
        "music.yaml");

    public static MusicSettings Load()
    {
        LastError = null;
        try
        {
            AgentSecretProtector.ResetFlags();
            if (File.Exists(SettingsPath))
            {
                MusicSettings? parsed = Deserializer.Deserialize<MusicSettings>(
                    File.ReadAllText(SettingsPath));
                if (parsed is not null)
                {
                    parsed.Folders ??= new();
                    parsed.Favorites ??= new();
                    parsed.FavoriteDetails ??= new();
                    parsed.Playlists ??= new();
                    foreach (var playlist in parsed.Playlists)
                    {
                        playlist.Tracks ??= new();
                    }
                    if (parsed.Volume <= 0) parsed.Volume = 72;

                    // Unprotect 解密失败时会原样返回密文，绝不能把它当作 Cookie 使用或回写。
                    // 检查全局标志后回退到未登录态，并记录失败原因（finally 里会清标志，必须先读）。
                    if (AgentSecretProtector.DecryptFailed)
                    {
                        parsed.NetEaseCookie = string.Empty;
                        parsed.NetEaseNickname = string.Empty;
                        LogFailure("网易云 Cookie 解密失败（可能换过账户 / 机器或主密钥丢失），" +
                                   "已回退到未登录态，请重新登录。");
                    }
                    return parsed;
                }
            }
        }
        catch (Exception exception)
        {
            LogFailure("读取音乐设置失败，已改用默认值：" + exception.Message);
        }
        finally
        {
            // 清除与 Agent 子系统共享的全局标志，避免串扰。
            AgentSecretProtector.ResetFlags();
        }

        return new MusicSettings();
    }

    public static void Save(MusicSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, Serializer.Serialize(settings));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception exception)
        {
            LogFailure("保存音乐设置失败：" + exception.Message);
        }
    }
}

public static class LocalMusicLibrary
{
    private static readonly string[] SupportedExtensions =
    [
        ".mp3", ".flac", ".ogg", ".wav", ".m4a", ".aac", ".opus", ".wma",
    ];

    public static Task<List<MusicItem>> ScanAsync(
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            List<MusicItem> items = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    if (!SupportedExtensions.Contains(extension)) continue;
                    if (!seen.Add(file)) continue;

                    items.Add(new MusicItem
                    {
                        Id = "local:" + file.ToLowerInvariant(),
                        Title = Path.GetFileNameWithoutExtension(file),
                        Artist = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty),
                        Album = Path.GetFileName(folder),
                        LocalPath = file,
                        Source = "local",
                    });
                }
            }

            items.Sort((left, right) => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase));
            return items;
        }, cancellationToken);
    }
}
