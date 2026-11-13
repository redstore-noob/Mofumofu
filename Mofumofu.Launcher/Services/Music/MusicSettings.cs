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

/// <summary>音乐页设置：本地文件夹、音量、网易云登录 Cookie、收藏。</summary>
public sealed class MusicSettings
{
    public List<string> Folders { get; set; } = new();

    public List<string> Favorites { get; set; } = new();

    public int Volume { get; set; } = 72;

    public bool Loop { get; set; }

    /// <summary>网易云 MUSIC_U Cookie（内存中为明文；落盘时由 AgentSecretProtector 字段级加密）。</summary>
    [YamlIgnore]
    public string NetEaseCookie { get; set; } = string.Empty;

    /// <summary>music.yaml 中 NetEaseCookie 的加密表示，复用 Agent 凭据加密并兼容旧明文。</summary>
    [YamlMember(Alias = "NetEaseCookie")]
    public string NetEaseCookieProtected
    {
        get => AgentSecretProtector.Protect(NetEaseCookie);
        set => NetEaseCookie = AgentSecretProtector.Unprotect(value);
    }

    public string NetEaseNickname { get; set; } = string.Empty;

    /// <summary>最近一次播放的网易云歌曲 ID，作为私人漫游的种子。</summary>
    public long LastNetEaseSongId { get; set; }

    /// <summary>收藏歌曲的完整信息；云歌曲重启后搜索缓存为空，靠它重建「我喜欢」列表。</summary>
    public List<MusicFavoriteEntry> FavoriteDetails { get; set; } = new();
}

/// <summary>一条收藏记录的最小元数据。</summary>
public sealed class MusicFavoriteEntry
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    /// <summary>local / netease。</summary>
    public string Source { get; set; } = "netease";

    /// <summary>本地文件路径（云歌曲为空）。</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>专辑封面地址（云歌曲）。</summary>
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

    /// <summary>音乐设置：{DataRoot}/Mofumofu/config/music.yaml（与主设置同目录）。</summary>
    public static string SettingsPath => Path.Combine(
        MofuCore.Config.YamlRead.ConfigDirectory,
        "music.yaml");

    public static MusicSettings Load()
    {
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
                    if (parsed.Volume <= 0) parsed.Volume = 72;
                    // 兼容旧版本明文 Cookie：立刻回写为加密版本，避免凭证继续明文驻留磁盘。
                    if (!string.IsNullOrEmpty(parsed.NetEaseCookie) &&
                        AgentSecretProtector.LegacyPlaintextFound &&
                        !AgentSecretProtector.DecryptFailed)
                    {
                        Save(parsed);
                    }
                    return parsed;
                }
            }
        }
        catch (Exception)
        {
            // 损坏的设置按默认值处理
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
        catch (Exception)
        {
            // 写失败不影响播放
        }
    }
}

/// <summary>本地音乐扫描：只看扩展名，不依赖第三方标签库。</summary>
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
