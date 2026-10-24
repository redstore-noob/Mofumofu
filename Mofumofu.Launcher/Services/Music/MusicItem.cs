using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Mofumofu.Launcher.Services.Music;

/// <summary>音乐条目：本地文件或网易云歌曲。</summary>
public sealed class MusicItem : INotifyPropertyChanged
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string Album { get; init; } = string.Empty;

    /// <summary>网易云专辑封面地址（本地歌曲为空，封面从文件内嵌提取）。</summary>
    public string CoverUrl { get; set; } = string.Empty;

    /// <summary>local / netease。</summary>
    public string Source { get; init; } = "local";

    /// <summary>本地文件绝对路径（网易云为空）。</summary>
    public string LocalPath { get; init; } = string.Empty;

    /// <summary>在线播放地址（本地为空）。</summary>
    public string RemoteUrl { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; } = TimeSpan.Zero;

    public string DurationDisplay => Duration > TimeSpan.Zero
        ? Duration.TotalHours >= 1
            ? $"{(int)Duration.TotalHours}:{Duration.Minutes:00}:{Duration.Seconds:00}"
            : $"{Duration.Minutes}:{Duration.Seconds:00}"
        : "--:--";

    public string FileInfo
    {
        get
        {
            if (Source == "netease")
            {
                return string.IsNullOrWhiteSpace(Album) ? "网易云音乐" : Album;
            }

            try
            {
                if (!File.Exists(LocalPath)) return "本地音乐";
                var size = new FileInfo(LocalPath).Length;
                string format = Path.GetExtension(LocalPath).TrimStart('.').ToLowerInvariant();
                return $"{format} · {FormatSize(size)}";
            }
            catch (Exception)
            {
                return "本地音乐";
            }
        }
    }

    public string DisplayTitle => Title;

    public bool IsCloud => Source == "netease";

    private bool _isFavorite;

    /// <summary>是否收藏；列表里的爱心按钮通过双向绑定读写它。</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        }
    }

    private Bitmap? _coverImage;
    private bool _coverLoading;

    /// <summary>
    /// 封面位图，列表与正在播放面板直接绑定它。
    /// 首次访问触发异步加载，加载完成后发 PropertyChanged 刷新绑定。
    /// </summary>
    public Bitmap? CoverImage
    {
        get
        {
            EnsureCover();
            return _coverImage;
        }
    }

    private void EnsureCover()
    {
        if (_coverLoading) return;
        _coverLoading = true;
        _ = LoadCoverAsync();
    }

    private async Task LoadCoverAsync()
    {
        Bitmap? bitmap = await CoverCache.GetAsync(this).ConfigureAwait(false);
        _coverImage = bitmap;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
