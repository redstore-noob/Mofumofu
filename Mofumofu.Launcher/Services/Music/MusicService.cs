using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services.Music;

/// <summary>
/// 音乐播放的全局状态：播放器、网易云客户端、设置与当前队列。
/// 挂在单例上，音乐页销毁 / 重建、桌面小组件都共用同一份播放状态，
/// 离开音乐页播放不会中断。
/// </summary>
public sealed class MusicService
{
    public static MusicService Instance { get; } = new();

    private MusicService()
    {
        Player = new MusicPlayer { Volume = Settings.Volume };
        Player.Failed += message => Status?.Invoke(L.T("Pages.Music.PlayFailed", "播放失败：{0}", message));
        Player.PlaybackEnded += OnPlaybackEnded;

        Client = new NetEaseMusicClient(Settings.NetEaseCookie);
        if (!string.IsNullOrWhiteSpace(Settings.NetEaseNickname)) Client.Nickname = Settings.NetEaseNickname;
    }

    public MusicSettings Settings { get; } = MusicStore.Load();

    public MusicPlayer Player { get; }

    public NetEaseMusicClient Client { get; private set; }

    public MusicItem? Current { get; private set; }

    /// <summary>播放队列：音乐页切换列表时同步进来，连播/切歌按它走。</summary>
    public IReadOnlyList<MusicItem> Queue { get; set; } = [];

    public bool IsPlaying => Player.IsPlaying;

    public bool HasTrack => Player.HasTrack;

    /// <summary>当前曲目或播放状态变化（切歌 / 播放暂停 / 队列连播）。</summary>
    public event Action? StateChanged;

    /// <summary>需要弹提示的消息（解析地址失败之类），只有音乐页订阅。</summary>
    public event Action<string>? Status;

    public event Action<TimeSpan, TimeSpan>? PositionChanged
    {
        add => Player.PositionChanged += value;
        remove => Player.PositionChanged -= value;
    }

    public void SetClient(NetEaseMusicClient client)
    {
        Client = client;
        StateChanged?.Invoke();
    }

    public void SetQueue(IReadOnlyList<MusicItem> queue) => Queue = queue;

    public void Save() => MusicStore.Save(Settings);

    /// <summary>播放一首歌；网易云歌曲会先解析直链（带缓存下载）。</summary>
    public async Task<bool> PlayAsync(MusicItem item)
    {
        try
        {
            if (item.IsCloud && string.IsNullOrWhiteSpace(item.RemoteUrl))
            {
                if (!long.TryParse(item.Id.Replace("netease:", string.Empty), out long songId))
                {
                    Status?.Invoke(L.T("Pages.Music.PlayFailed", "播放失败：{0}", "歌曲 ID 无效"));
                    return false;
                }

                Status?.Invoke(L.T("Pages.Music.Resolving", "正在获取「{0}」的播放地址…", item.Title));
                string? url = await Client.GetSongUrlAsync(songId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    Status?.Invoke(L.T("Pages.Music.NoUrl",
                        "「{0}」没有可用的播放地址（可能需要 VIP 或已下架）", item.Title));
                    return false;
                }

                item.RemoteUrl = url;
            }

            string source = item.IsCloud ? item.RemoteUrl : item.LocalPath;
            if (string.IsNullOrWhiteSpace(source))
            {
                Status?.Invoke(L.T("Pages.Music.PlayFailed", "播放失败：{0}", "没有音频地址"));
                return false;
            }

            bool played = await Player.PlayAsync(source, item.IsCloud).ConfigureAwait(false);
            if (!played) return false;

            Current = item;

            if (item.Duration <= TimeSpan.Zero && Player.Duration > TimeSpan.Zero)
            {
                item.Duration = Player.Duration;
                if (item.IsFavorite) UpsertFavoriteDetail(item);
            }

            if (item.IsCloud && long.TryParse(item.Id.Replace("netease:", string.Empty), out long playedSongId))
            {
                Settings.LastNetEaseSongId = playedSongId;
                Save();
            }

            StateChanged?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            Status?.Invoke(L.T("Pages.Music.PlayFailed", "播放失败：{0}", exception.Message));
            return false;
        }
    }

    public void Toggle()
    {
        if (!HasTrack) return;
        Player.Toggle();
        StateChanged?.Invoke();
    }

    public void Next() => Skip(1);

    public void Previous() => Skip(-1);

    private void Skip(int delta)
    {
        if (Queue.Count == 0) return;

        int index = Current is null ? -1 : Queue.ToList().FindIndex(item => item.Id == Current.Id);
        index = index < 0 ? 0 : (index + delta + Queue.Count) % Queue.Count;
        _ = PlayAsync(Queue[index]);
    }

    private void OnPlaybackEnded()
    {
        if (Settings.Loop && Current is not null)
        {
            Player.Seek(TimeSpan.Zero);
            Player.Resume();
            return;
        }

        Skip(1);
    }

    private void UpsertFavoriteDetail(MusicItem item)
    {
        Settings.FavoriteDetails.RemoveAll(detail => detail.Id == item.Id);
        Settings.FavoriteDetails.Add(MusicFavoriteEntry.From(item));
        Save();
    }
}
