using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Music;

/// <summary>
/// 网易云音乐公开接口（搜索 / 歌曲地址 / 歌词 / 登录）。
/// 与开源客户端一样走 music.163.com 的 /api 接口，带 os=pc 的 Cookie。
/// </summary>
public sealed class NetEaseMusicClient
{
    private const string BaseUrl = "https://music.163.com";
    private const string DefaultCookie = "os=pc; appver=2.9.7";

    private readonly HttpClient _http;
    private string _cookie;

    public NetEaseMusicClient(string? cookie = null)
    {
        _cookie = NormalizeCookie(cookie);
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", BaseUrl);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    public bool IsLoggedIn => _cookie.Contains("MUSIC_U=", StringComparison.Ordinal);

    public string Cookie => _cookie;

    public string Nickname { get; set; } = string.Empty;

    /// <summary>当前登录用户的网易云 ID，漫游推荐需要它找「我喜欢的音乐」歌单。</summary>
    public long UserId { get; set; }

    private long? _likedPlaylistId;

    private static string NormalizeCookie(string? cookie)
    {
        string value = (cookie ?? string.Empty).Trim();
        if (value.Length == 0) return DefaultCookie;

        if (!value.Contains('='))
        {
            return $"MUSIC_U={value}; {DefaultCookie}";
        }

        // WebView2 里取到的是浏览器完整 Cookie，补上接口需要的客户端标记
        if (!value.Contains("os=", StringComparison.OrdinalIgnoreCase))
        {
            value += "; os=pc";
        }

        if (!value.Contains("appver=", StringComparison.OrdinalIgnoreCase))
        {
            value += "; appver=2.9.7";
        }

        return value;
    }

    /// <summary>搜索歌曲。</summary>
    public async Task<IReadOnlyList<MusicItem>> SearchAsync(
        string keyword,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/search/get/web",
            new Dictionary<string, string>
            {
                ["csrf_token"] = string.Empty,
                ["s"] = keyword,
                ["type"] = "1",
                ["offset"] = "0",
                ["total"] = "true",
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            }, null, cancellationToken).ConfigureAwait(false);

        List<MusicItem> items = [];
        if (!document.RootElement.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("songs", out JsonElement songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            int code = GetInt(document.RootElement, "code");
            if (code != 200)
            {
                throw new InvalidOperationException($"网易云搜索失败（code={code}）");
            }

            return items;
        }

        foreach (JsonElement song in songs.EnumerateArray())
        {
            MusicItem? item = ParseSong(song);
            if (item is not null) items.Add(item);
        }

        return items;
    }

    /// <summary>私人漫游：基于种子歌曲和「我喜欢的音乐」歌单做智能推荐。</summary>
    public async Task<IReadOnlyList<MusicItem>> GetRoamSongsAsync(
        long seedSongId,
        CancellationToken cancellationToken = default)
    {
        long pid = await GetLikedPlaylistIdAsync(cancellationToken).ConfigureAwait(false);
        if (pid <= 0)
        {
            throw new InvalidOperationException("没有找到「我喜欢的音乐」歌单");
        }

        long songId = seedSongId;
        if (songId <= 0)
        {
            songId = await GetPlaylistFirstSongIdAsync(pid, cancellationToken).ConfigureAwait(false);
        }

        if (songId <= 0)
        {
            throw new InvalidOperationException("没有可用的漫游种子歌曲，先收藏或播放几首试试");
        }

        using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/playmode/intelligence/list",
            new Dictionary<string, string>
            {
                ["songId"] = songId.ToString(CultureInfo.InvariantCulture),
                ["pid"] = pid.ToString(CultureInfo.InvariantCulture),
                ["type"] = "fromToOne",
            }, null, cancellationToken).ConfigureAwait(false);

        List<MusicItem> items = [];
        if (document.RootElement.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in data.EnumerateArray())
            {
                // 返回结构是 data[].songInfo（老版本是 data[].song）
                JsonElement song = entry.ValueKind == JsonValueKind.Object
                    ? entry.TryGetProperty("songInfo", out JsonElement info) ? info
                        : entry.TryGetProperty("song", out JsonElement songElement) ? songElement
                        : entry
                    : entry;
                MusicItem? item = ParseSong(song);
                if (item is not null) items.Add(item);
            }
        }

        return items;
    }

    /// <summary>云端「我喜欢的音乐」歌单里的歌曲（按收藏顺序，默认前 100 首）。</summary>
    public async Task<IReadOnlyList<MusicItem>> GetLikedSongsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        List<MusicItem> tracks = await GetPlaylistTracksAsync(
            await GetLikedPlaylistIdAsync(cancellationToken).ConfigureAwait(false),
            limit, cancellationToken).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// 拉歌单内歌曲。/api/playlist/detail 在不同 Cookie 下返回结构不一样：
    /// 老结构是 result.tracks（直接带完整歌曲），v6 是 playlist.trackIds（只有 id），
    /// 两种都兼容。
    /// </summary>
    private async Task<List<MusicItem>> GetPlaylistTracksAsync(
        long playlistId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (playlistId <= 0) return [];

        using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/playlist/detail",
            new Dictionary<string, string>
            {
                ["id"] = playlistId.ToString(CultureInfo.InvariantCulture),
                ["n"] = limit.ToString(CultureInfo.InvariantCulture),
            }, null, cancellationToken).ConfigureAwait(false);

        List<MusicItem> items = [];

        // 老结构：result.tracks 是完整歌曲对象
        if (document.RootElement.TryGetProperty("result", out JsonElement result) &&
            result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("tracks", out JsonElement tracks) &&
            tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement song in tracks.EnumerateArray())
            {
                MusicItem? item = ParseSong(song);
                if (item is not null) items.Add(item);
                if (items.Count >= limit) return items;
            }
        }

        // v6 结构：playlist.trackIds 只有 id，需要再查详情
        if (items.Count == 0 &&
            document.RootElement.TryGetProperty("playlist", out JsonElement playlist) &&
            playlist.ValueKind == JsonValueKind.Object)
        {
            List<long> ids = [];
            if (playlist.TryGetProperty("trackIds", out JsonElement trackIds) &&
                trackIds.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in trackIds.EnumerateArray())
                {
                    long id = GetLong(entry, "id");
                    if (id > 0) ids.Add(id);
                    if (ids.Count >= limit) break;
                }
            }

            if (ids.Count > 0)
            {
                items.AddRange(await GetSongsDetailAsync(ids, cancellationToken).ConfigureAwait(false));
            }
        }

        return items;
    }

    /// <summary>批量拉取歌曲详情（含专辑封面）；一次最多 1000 个 id。</summary>
    public async Task<IReadOnlyList<MusicItem>> GetSongsDetailAsync(
        IReadOnlyList<long> songIds,
        CancellationToken cancellationToken = default)
    {
        string ids = "[" + string.Join(",", songIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";
        using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/song/detail",
            new Dictionary<string, string> { ["ids"] = ids, ["c"] = ids },
            null, cancellationToken).ConfigureAwait(false);

        List<MusicItem> items = [];
        if (document.RootElement.TryGetProperty("songs", out JsonElement songs) &&
            songs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement song in songs.EnumerateArray())
            {
                MusicItem? item = ParseSong(song);
                if (item is not null) items.Add(item);
            }
        }

        return items;
    }

    /// <summary>喜欢 / 取消喜欢一首歌（写云端「我喜欢的音乐」歌单）。</summary>
    public async Task<bool> LikeSongAsync(
        long songId,
        bool like,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/song/like",
            new Dictionary<string, string>
            {
                ["songId"] = songId.ToString(CultureInfo.InvariantCulture),
                ["like"] = like ? "true" : "false",
                ["csrf_token"] = string.Empty,
            }, null, cancellationToken).ConfigureAwait(false);

        return GetInt(document.RootElement, "code") == 200;
    }

    /// <summary>当前用户「我喜欢的音乐」歌单 ID，带缓存。</summary>
    private async Task<long> GetLikedPlaylistIdAsync(CancellationToken cancellationToken)
    {
        if (_likedPlaylistId is long cached && cached > 0) return cached;

        if (UserId <= 0)
        {
            using JsonDocument account = await SendAsync(HttpMethod.Get, "/api/nuser/account/get",
                null, null, cancellationToken).ConfigureAwait(false);
            if (account.RootElement.TryGetProperty("profile", out JsonElement profile) &&
                profile.ValueKind == JsonValueKind.Object)
            {
                UserId = GetLong(profile, "userId");
            }
        }

        if (UserId <= 0) return 0;

        using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/user/playlist",
            new Dictionary<string, string>
            {
                ["uid"] = UserId.ToString(CultureInfo.InvariantCulture),
                ["limit"] = "1",
                ["offset"] = "0",
            }, null, cancellationToken).ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("playlist", out JsonElement playlists) &&
            playlists.ValueKind == JsonValueKind.Array &&
            playlists.GetArrayLength() > 0)
        {
            long id = GetLong(playlists[0], "id");
            if (id > 0) _likedPlaylistId = id;
            return id;
        }

        return 0;
    }

    /// <summary>歌单里的第一首歌 ID，作为没有播放历史时的漫游种子。</summary>
    private async Task<long> GetPlaylistFirstSongIdAsync(long playlistId, CancellationToken cancellationToken)
    {
        List<MusicItem> tracks = await GetPlaylistTracksAsync(playlistId, 1, cancellationToken)
            .ConfigureAwait(false);
        if (tracks.Count > 0 &&
            long.TryParse(tracks[0].Id.Replace("netease:", string.Empty), out long id))
        {
            return id;
        }

        return 0;
    }

    private static MusicItem? ParseSong(JsonElement song)
    {
        long id = song.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt64(out long parsed)
            ? parsed
            : 0;
        if (id <= 0) return null;

        string name = GetString(song, "name");
        string artists = string.Join(" / ", ReadNames(song, "artists", "ar"));
        string album = ReadAlbum(song);
        string cover = ReadAlbumPic(song);
        double duration = song.TryGetProperty("duration", out JsonElement durationElement) &&
                          durationElement.TryGetDouble(out double durationMs)
            ? durationMs
            : song.TryGetProperty("dt", out JsonElement dt) && dt.TryGetDouble(out double dtMs) ? dtMs : 0;

        return new MusicItem
        {
            Id = "netease:" + id.ToString(CultureInfo.InvariantCulture),
            Title = name,
            Artist = artists,
            Album = album,
            Source = "netease",
            Duration = TimeSpan.FromMilliseconds(duration),
            CoverUrl = cover,
        };
    }

    /// <summary>获取歌曲播放地址；VIP / 无版权时返回 null。</summary>
    public async Task<string?> GetSongUrlAsync(long songId, CancellationToken cancellationToken = default)
    {
        foreach (int bitrate in new[] { 320000, 192000, 128000 })
        {
            using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/song/enhance/player/url",
                new Dictionary<string, string>
                {
                    ["ids"] = "[" + songId.ToString(CultureInfo.InvariantCulture) + "]",
                    ["br"] = bitrate.ToString(CultureInfo.InvariantCulture),
                }, null, cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                continue;
            }

            string? url = GetString(data[0], "url");
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        return null;
    }

    /// <summary>获取歌词（LRC 文本），没有时返回 null。</summary>
    public async Task<string?> GetLyricAsync(long songId, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/song/lyric",
            new Dictionary<string, string>
            {
                ["id"] = songId.ToString(CultureInfo.InvariantCulture),
                ["lv"] = "-1",
                ["kv"] = "-1",
                ["tv"] = "-1",
            }, null, cancellationToken).ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("lrc", out JsonElement lrc) &&
            GetString(lrc, "lyric") is { Length: > 0 } lyric)
        {
            return lyric;
        }

        return null;
    }

    /// <summary>用浏览器里复制的 MUSIC_U / 整段 Cookie 登录。</summary>
    public async Task<bool> LoginWithCookieAsync(string cookie, CancellationToken cancellationToken = default)
    {
        _cookie = NormalizeCookie(cookie);
        try
        {
            using JsonDocument document = await SendAsync(HttpMethod.Get, "/api/nuser/account/get",
                null, null, cancellationToken).ConfigureAwait(false);

            if (GetInt(document.RootElement, "code") == 200 &&
                document.RootElement.TryGetProperty("profile", out JsonElement profile) &&
                profile.ValueKind == JsonValueKind.Object)
            {
                Nickname = ReadObjectName(document.RootElement, "profile");
                UserId = GetLong(profile, "userId");
                _likedPlaylistId = null;
                return true;
            }
        }
        catch (Exception)
        {
            // 无效 Cookie
        }

        _cookie = DefaultCookie;
        Nickname = string.Empty;
        return false;
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string path,
        Dictionary<string, string>? query,
        Dictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        string url = BaseUrl + path;
        if (query is { Count: > 0 })
        {
            url += "?" + string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        using var request = new HttpRequestMessage(method, url);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        request.Headers.TryAddWithoutValidation("Cookie", _cookie);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"网易云接口请求失败（HTTP {(int)response.StatusCode}）");
        }

        return JsonDocument.Parse(body);
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : 0;

    private static long GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt64(out long parsed)
            ? parsed
            : 0;

    private static string ReadObjectName(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return GetString(value, "nickname") is { Length: > 0 } nickname
            ? nickname
            : GetString(value, "name");
    }

    private static string ReadAlbum(JsonElement song)
    {
        if (song.TryGetProperty("album", out JsonElement album) && album.ValueKind == JsonValueKind.Object)
        {
            return GetString(album, "name");
        }

        if (song.TryGetProperty("al", out JsonElement al) && al.ValueKind == JsonValueKind.Object)
        {
            return GetString(al, "name");
        }

        return string.Empty;
    }

    /// <summary>专辑封面：不同接口用 album.picUrl / al.picUrl / img1v1Url，取第一个像 URL 的。</summary>
    private static string ReadAlbumPic(JsonElement song)
    {
        foreach (string name in new[] { "album", "al" })
        {
            if (!song.TryGetProperty(name, out JsonElement album) || album.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (string field in new[] { "picUrl", "pic", "img1v1Url" })
            {
                string pic = GetString(album, field);
                if (pic.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return pic;
            }
        }

        return string.Empty;
    }

    private static List<string> ReadNames(JsonElement element, string name, string legacyName)
    {
        List<string> names = [];
        string key = element.TryGetProperty(name, out _) ? name : legacyName;
        if (!element.TryGetProperty(key, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            string value = GetString(item, "name");
            if (value.Length > 0) names.Add(value);
        }

        return names;
    }

}
