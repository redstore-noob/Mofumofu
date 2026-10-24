using System.ComponentModel;
using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Music;
using SukiUI.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 音乐播放器：本地文件夹扫描 + 网易云搜索/登录，底部播放条走真实音频输出。
/// </summary>
public partial class MusicView : UserControl
{
    private enum MusicSource
    {
        Local,
        Cloud,
        Favorites,
        Roam,
    }

    private readonly MusicService _service = MusicService.Instance;

    /// <summary>播放器与网易云客户端都在全局服务上，页面只是它的一层 UI。</summary>
    private MusicPlayer Player => _service.Player;

    private NetEaseMusicClient _client => _service.Client;

    private MusicSettings _settings => _service.Settings;

    private readonly DispatcherTimer _cloudDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private List<MusicItem> _local = [];
    private List<MusicItem> _cloud = [];
    private List<MusicItem> _roam = [];
    private List<MusicItem> _cloudLiked = [];
    private bool _cloudLikedLoaded;
    private bool _cloudLikedLoading;
    private List<MusicItem> _view = [];
    private MusicItem? _current;
    private MusicSource _source = MusicSource.Local;
    private CancellationTokenSource? _cloudCts;
    private bool _ready;
    private bool _suppressSelection;
    private bool _suppressSort;
    private bool _suppressSearch;
    private bool _updatingSlider;
    private bool _savePending;

    public MusicView()
    {
        InitializeComponent();

        VolumeSlider.Value = _settings.Volume;
        VolumeText.Text = _settings.Volume + "%";
        LoopButton.Classes.Set("active", _settings.Loop);

        _cloudDebounce.Tick += async (_, _) =>
        {
            _cloudDebounce.Stop();
            await SearchCloudAsync(SearchBox.Text ?? string.Empty);
        };
        _saveTimer.Tick += (_, _) => FlushSettings();

        AttachedToVisualTree += (_, _) =>
        {
            UiAnimations.Reset(TrackListCard);
            UiAnimations.FadeSlideIn(TrackListCard, 0.995, 8, 190);

            // 播放状态在服务上，页面重新挂回时把 UI 同步到最新
            _service.Status += OnServiceStatus;
            _service.StateChanged += OnServiceStateChanged;
            UpdateNowPlayingUi();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            // 离开音乐页不停止播放，后台继续放，桌面小组件也能控制
            _service.Status -= OnServiceStatus;
            _service.StateChanged -= OnServiceStateChanged;
            CancelCloud();
            _cloudDebounce.Stop();
            FlushSettings();
        };

        _ready = true;
        UpdateSourceUi();
        RefreshView();
        _ = LoadLocalAsync();
    }

    private void OnServiceStatus(string message) => Shell.Toast(message);

    private void OnServiceStateChanged()
    {
        if (_service.Current is not null) _current = _service.Current;
        UpdateNowPlayingUi();
        SyncSelection(_current);
    }

    // ==================== 来源 / 列表 ====================

    private void OnBackClick(object? sender, RoutedEventArgs e) => Shell.Go("home");

    private void OnSearchNavClick(object? sender, RoutedEventArgs e)
    {
        TextBox box = UseThreePane ? SearchBox : ListSearch;
        box.Focus();
    }

    private void OnRailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;

        _source = tag switch
        {
            "cloud" => MusicSource.Cloud,
            "favorites" => MusicSource.Favorites,
            "roam" => MusicSource.Roam,
            _ => MusicSource.Local,
        };

        UpdateSourceUi();
        RefreshView();

        if (_source == MusicSource.Cloud && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _ = SearchCloudAsync(SearchBox.Text ?? string.Empty);
        }
        else if (_source == MusicSource.Roam)
        {
            _ = LoadRoamAsync();
        }
        else if (_source == MusicSource.Favorites)
        {
            _ = LoadCloudLikedAsync();
        }
    }

    /// <summary>三段式布局只给本地「首页」用，云 / 收藏 / 漫游走传统单列列表。</summary>
    private bool UseThreePane => _source == MusicSource.Local;

    private void UpdateSourceUi()
    {
        RailLocal.Classes.Set("active", _source == MusicSource.Local);
        RailCloud.Classes.Set("active", _source == MusicSource.Cloud);
        RailFavorites.Classes.Set("active", _source == MusicSource.Favorites);
        RailRoam.Classes.Set("active", _source == MusicSource.Roam);

        NowPane.IsVisible = UseThreePane;
        QueuePane.IsVisible = UseThreePane;
        ListPane.IsVisible = !UseThreePane;

        LoginButton.IsVisible = _source == MusicSource.Cloud || _source == MusicSource.Roam;
        LoginButtonText.Text = _client.IsLoggedIn
            ? string.IsNullOrWhiteSpace(_client.Nickname)
                ? L.T("Pages.Music.LoggedIn", "已登录")
                : _client.Nickname
            : L.T("Pages.Music.Login", "登录网易云");

        ListIcon.Kind = _source switch
        {
            MusicSource.Cloud => "IconGlobe",
            MusicSource.Favorites => "IconFluentHeartFilled",
            MusicSource.Roam => "IconFluentSparkle",
            _ => "IconFluentMusic",
        };
        ListTitleText.Text = _source switch
        {
            MusicSource.Cloud => L.T("Pages.Music.Source.Cloud", "网易云音乐"),
            MusicSource.Favorites => L.T("Pages.Music.Nav.Liked", "我喜欢"),
            MusicSource.Roam => L.T("Pages.Music.Roam.Title", "私人漫游"),
            _ => L.T("Pages.Music.Source.Local", "本地音乐"),
        };

        string placeholder = _source switch
        {
            MusicSource.Cloud => L.T("Pages.Music.SearchCloud", "搜索网易云歌曲 / 歌手"),
            MusicSource.Favorites => L.T("Pages.Music.SearchFavorites", "搜索收藏"),
            MusicSource.Roam => L.T("Pages.Music.SearchRoam", "搜索漫游歌曲"),
            _ => L.T("Pages.Music.SearchLocal", "搜索本地歌曲"),
        };
        _suppressSearch = true;
        SearchBox.PlaceholderText = placeholder;
        ListSearch.PlaceholderText = placeholder;
        _suppressSearch = false;

        Header.Subtitle = _source switch
        {
            MusicSource.Cloud => _client.IsLoggedIn
                ? L.T("Pages.Music.Subtitle.CloudLogged", "网易云 · {0}", _client.Nickname)
                : L.T("Pages.Music.Subtitle.Cloud", "网易云音乐 · 未登录"),
            MusicSource.Favorites => L.T("Pages.Music.Subtitle.Favorites", "{0} 首收藏",
                _settings.Favorites.Count + _cloudLiked.Count(detail => !_settings.Favorites.Contains(detail.Id))),
            MusicSource.Roam => L.T("Pages.Music.Subtitle.Roam", "私人漫游 · {0} 首", _roam.Count),
            _ => L.T("Pages.Music.Subtitle.Local", "{0} 首本地歌曲", _local.Count),
        };
    }

    private void RefreshView()
    {
        string keyword = (SearchBox.Text ?? string.Empty).Trim();
        IEnumerable<MusicItem> source = _source switch
        {
            MusicSource.Cloud => _cloud,
            MusicSource.Favorites => BuildFavoritesView(),
            MusicSource.Roam => _roam,
            _ => _local,
        };

        if (keyword.Length > 0 && _source != MusicSource.Cloud)
        {
            source = source.Where(item =>
                item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.Album.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (SortCombo.SelectedIndex == 1)
        {
            source = source.OrderBy(item => item.Duration);
        }
        else
        {
            source = source.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase);
        }

        _view = source.ToList();
        // 连播 / 上一首 / 下一首按当前列表走
        _service.SetQueue(_view);
        _suppressSelection = true;
        TrackList.ItemsSource = _view;
        ListTracks.ItemsSource = _view;
        MusicItem? selection = _current is null
            ? null
            : _view.FirstOrDefault(item => item.Id == _current.Id);
        TrackList.SelectedItem = selection;
        ListTracks.SelectedItem = selection;
        _suppressSelection = false;

        string count = L.T("Pages.Music.TrackCount", "{0} 首歌曲", _view.Count);
        TrackCountText.Text = count;
        ListCountText.Text = count;
        string empty = _source switch
        {
            MusicSource.Cloud => string.IsNullOrWhiteSpace(keyword)
                ? L.T("Pages.Music.Empty.Cloud", "输入关键词搜索网易云音乐。")
                : L.T("Pages.Music.Empty.Search", "没有匹配的歌曲，换个关键词试试。"),
            MusicSource.Favorites => _client.IsLoggedIn && !_cloudLikedLoaded
                ? L.T("Pages.Music.Empty.FavoritesSyncing", "正在同步网易云「我喜欢」…")
                : L.T("Pages.Music.Empty.Favorites", "还没有收藏的歌曲，点播放条上的爱心收藏当前歌曲。"),
            MusicSource.Roam => L.T("Pages.Music.Empty.Roam", "暂无漫游歌曲，点击侧栏「漫游」重新获取。"),
            _ => _local.Count == 0
                ? L.T("Pages.Music.Empty.Local", "还没有本地歌曲，点「选择文件夹」添加音乐目录。")
                : L.T("Pages.Music.Empty.Search", "没有匹配的歌曲，换个关键词试试。"),
        };
        EmptyHint.IsVisible = _view.Count == 0;
        EmptyHint.Text = empty;
        ListEmpty.IsVisible = _view.Count == 0;
        ListEmpty.Text = empty;

        UpdateSourceUi();
    }

    private async void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppressSearch) return;
        SyncSearchText(SearchBox);
        await ApplySearchChangedAsync();
    }

    private async void OnListSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppressSearch) return;
        SyncSearchText(ListSearch);
        await ApplySearchChangedAsync();
    }

    /// <summary>两个搜索框内容互相同步，然后走统一的搜索逻辑。</summary>
    private void SyncSearchText(TextBox edited)
    {
        _suppressSearch = true;
        SearchBox.Text = edited.Text;
        ListSearch.Text = edited.Text;
        _suppressSearch = false;
    }

    private async Task ApplySearchChangedAsync()
    {
        if (_source == MusicSource.Cloud)
        {
            _cloudDebounce.Stop();
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                CancelCloud();
                _cloud = [];
                RefreshView();
                return;
            }

            _cloudDebounce.Start();
            return;
        }

        RefreshView();
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressSort) return;

        // 两个排序下拉互相同步
        _suppressSort = true;
        SortCombo.SelectedIndex = ListSort.SelectedIndex;
        ListSort.SelectedIndex = SortCombo.SelectedIndex;
        _suppressSort = false;

        RefreshView();
    }

    private async Task SearchCloudAsync(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;

        CancelCloud();
        _cloudCts = new CancellationTokenSource();
        CancellationToken token = _cloudCts.Token;
        try
        {
            IReadOnlyList<MusicItem> items = await _client.SearchAsync(keyword.Trim(), 40, token);
            if (token.IsCancellationRequested) return;

            _cloud = items.ToList();
            MarkFavorites(_cloud);
            RefreshView();
        }
        catch (OperationCanceledException)
        {
            // 新的搜索
        }
        catch (Exception exception)
        {
            _cloud = [];
            RefreshView();
            Shell.Toast(L.T("Pages.Music.SearchFailed", "网易云搜索失败：{0}", exception.Message));
        }
    }

    private void CancelCloud()
    {
        CancellationTokenSource? cts = _cloudCts;
        _cloudCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }
    }

    /// <summary>私人漫游：基于最近播放的网易云歌曲拉取智能推荐。</summary>
    private async Task LoadRoamAsync()
    {
        if (!_client.IsLoggedIn)
        {
            Shell.Toast(L.T("Pages.Music.Roam.NeedLogin", "漫游需要先登录网易云"));
            return;
        }

        try
        {
            IReadOnlyList<MusicItem> items = await _client.GetRoamSongsAsync(_settings.LastNetEaseSongId);
            _roam = items.ToList();
            MarkFavorites(_roam);
            RefreshView();
            Shell.Toast(L.T("Pages.Music.Roam.Loaded", "已获取漫游推荐：{0} 首", _roam.Count));
        }
        catch (Exception exception)
        {
            _roam = [];
            RefreshView();
            Shell.Toast(L.T("Pages.Music.RoamFailed", "获取漫游歌曲失败：{0}", exception.Message));
        }
    }

    /// <summary>
    /// 同步网易云云端「我喜欢的音乐」歌单；登录后进收藏页会拉一次，
    /// 之后再进不重复拉（重进侧栏想强制刷新就等下次会话）。
    /// </summary>
    private async Task LoadCloudLikedAsync()
    {
        if (_cloudLikedLoaded || _cloudLikedLoading || !_client.IsLoggedIn) return;

        _cloudLikedLoading = true;
        try
        {
            IReadOnlyList<MusicItem> items = await _client.GetLikedSongsAsync();
            _cloudLiked = items.ToList();
            _cloudLikedLoaded = true;
            foreach (MusicItem item in _cloudLiked)
            {
                item.IsFavorite = true; // 云端歌单里就是喜欢的
                item.PropertyChanged += OnCloudLikedItemChanged;
            }
            if (_source == MusicSource.Favorites) RefreshView();
            Shell.Toast(L.T("Pages.Music.CloudLiked.Loaded", "已同步网易云「我喜欢」：{0} 首", _cloudLiked.Count));
        }
        catch (Exception exception)
        {
            Shell.Toast(L.T("Pages.Music.CloudLiked.Failed", "同步网易云「我喜欢」失败：{0}", exception.Message));
        }
        finally
        {
            _cloudLikedLoading = false;
        }
    }

    /// <summary>云端喜欢的歌曲爱心变化时，把喜欢/取消喜欢写回网易云。</summary>
    private void OnCloudLikedItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MusicItem.IsFavorite) ||
            sender is not MusicItem { IsCloud: true } item ||
            !_client.IsLoggedIn)
        {
            return;
        }

        _ = SyncCloudLikeAsync(item, item.IsFavorite);
    }

    private async Task SyncCloudLikeAsync(MusicItem item, bool like)
    {
        if (!long.TryParse(item.Id.Replace("netease:", string.Empty), out long songId)) return;

        try
        {
            bool ok = await _client.LikeSongAsync(songId, like);
            if (!ok)
            {
                Shell.Toast(L.T("Pages.Music.CloudLike.Failed", "同步喜欢状态到网易云失败"));
            }
        }
        catch (Exception exception)
        {
            Shell.Toast(L.T("Pages.Music.CloudLike.Failed", "同步喜欢状态到网易云失败：{0}", exception.Message));
        }
    }

    /// <summary>按收藏表标记条目上的爱心状态。</summary>
    private void MarkFavorites(IEnumerable<MusicItem> items)
    {
        foreach (MusicItem item in items.Where(item => !_cloudLiked.Contains(item)))
        {
            item.IsFavorite = _settings.Favorites.Contains(item.Id);
        }
    }

    /// <summary>
    /// 「我喜欢」视图：优先用内存里现成的条目（带搜索结果 / 扫描结果），
    /// 缺的从持久化的 FavoriteDetails 重建——云歌曲重启后搜索缓存为空也能显示。
    /// </summary>
    private List<MusicItem> BuildFavoritesView()
    {
        List<MusicItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        // 云端「我喜欢的音乐」整单都算喜欢，放最前（封面 / 元数据最全）
        foreach (MusicItem item in _cloudLiked)
        {
            if (seen.Add(item.Id)) items.Add(item);
        }

        foreach (MusicItem item in _local.Concat(_cloud).Concat(_roam))
        {
            if (_settings.Favorites.Contains(item.Id) && seen.Add(item.Id))
            {
                items.Add(item);
            }
        }

        foreach (MusicFavoriteEntry detail in _settings.FavoriteDetails)
        {
            if (_settings.Favorites.Contains(detail.Id) && seen.Add(detail.Id))
            {
                items.Add(detail.ToItem());
            }
        }

        return items;
    }

    private void UpsertFavoriteDetail(MusicItem item)
    {
        _settings.FavoriteDetails.RemoveAll(detail => detail.Id == item.Id);
        _settings.FavoriteDetails.Add(MusicFavoriteEntry.From(item));
    }

    private void RemoveFavoriteDetail(string id)
    {
        _settings.FavoriteDetails.RemoveAll(detail => detail.Id == id);
    }

    // ==================== 本地库 ====================

    private async Task LoadLocalAsync()
    {
        try
        {
            _local = await LocalMusicLibrary.ScanAsync(_settings.Folders);
        }
        catch (Exception exception)
        {
            Shell.Toast(L.T("Pages.Music.ScanFailed", "扫描本地音乐失败：{0}", exception.Message));
            _local = [];
        }

        if (_current is not null)
        {
            MusicItem? again = _local.FirstOrDefault(item => item.Id == _current.Id);
            if (again is not null) _current = again;
        }

        MarkFavorites(_local);
        RefreshView();
    }

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            Shell.Toast(L.T("Pages.Music.NoPicker", "当前窗口不支持选择文件夹"));
            return;
        }

        try
        {
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L.T("Pages.Music.PickFolderTitle", "选择音乐文件夹"),
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;

            string? path = folders[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

            if (!_settings.Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _settings.Folders.Add(path);
                FlushSettings();
            }

            await LoadLocalAsync();
            Shell.Toast(L.T("Pages.Music.FolderAdded", "已添加音乐目录：{0}", path));
        }
        catch (Exception exception)
        {
            Shell.Toast(L.T("Pages.Music.FolderPickFailed", "选择文件夹失败：{0}", exception.Message));
        }
    }

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        await LoadLocalAsync();
        Shell.Toast(L.T("Pages.Music.RescanToast", "已重新扫描曲库：{0} 首", _local.Count));

        if (_source == MusicSource.Cloud && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            await SearchCloudAsync(SearchBox.Text ?? string.Empty);
        }
    }

    // ==================== 网易云登录 ====================

    private void OnCloudLoginClick(object? sender, RoutedEventArgs e)
    {
        var cookie = new TextBox
        {
            PlaceholderText = L.T("Pages.Music.CookieHint", "或粘贴浏览器里的 MUSIC_U / 整段 Cookie"),
            Width = 320,
            AcceptsReturn = true,
            Height = 64,
            TextWrapping = TextWrapping.Wrap,
        };

        var browserButton = new Button
        {
            Content = L.T("Pages.Music.LoginBrowser", "使用内置浏览器登录"),
            Classes = { "primary" },
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cookieButton = new Button
        {
            Content = L.T("Pages.Music.LoginCookie", "用 Cookie 登录"),
            Classes = { "ghost" },
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var logoutButton = new Button
        {
            Content = L.T("Pages.Music.Logout", "退出登录"),
            Classes = { "ghost" },
            Height = 34,
            Padding = new Thickness(16, 0),
            IsVisible = _client.IsLoggedIn,
        };
        var cancelButton = new Button
        {
            Content = L.T("Common.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 34,
            Padding = new Thickness(16, 0),
        };

        var content = new StackPanel
        {
            Spacing = 10,
            Width = 340,
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Pages.Music.LoginTitle", "连接网易云音乐"),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = L.T("Pages.Music.LoginHint",
                        "在启动器内置浏览器里完成手机号 / 邮箱 / 扫码登录，登录状态只会保存在本机。"),
                    Classes = { "caption" },
                    TextWrapping = TextWrapping.Wrap,
                },
                browserButton,
                new Border { Height = 1, Background = ThemeTokens.Brush("NekoBorderBrush") },
                new TextBlock
                {
                    Text = L.T("Pages.Music.CookieLabel", "已有 Cookie？直接粘贴"),
                    Classes = { "caption" },
                },
                cookie,
                cookieButton,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { logoutButton, cancelButton },
                },
            },
        };

        cancelButton.Click += (_, _) => CloseActiveDialog(cancelButton);
        logoutButton.Click += (_, _) =>
        {
            _settings.NetEaseCookie = string.Empty;
            _settings.NetEaseNickname = string.Empty;
            FlushSettings();
            _service.SetClient(new NetEaseMusicClient());
            _cloud = [];
            _cloudLiked = [];
            _cloudLikedLoaded = false;
            CloseActiveDialog(logoutButton);
            RefreshView();
            Shell.Toast(L.T("Pages.Music.LoggedOut", "已退出网易云账号"));
        };

        browserButton.Click += async (_, _) =>
        {
            CloseActiveDialog(browserButton);
            await LoginWithBrowserAsync();
        };

        cookieButton.Click += async (_, _) =>
        {
            string value = (cookie.Text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                Shell.Toast(L.T("Pages.Music.CookieEmpty", "请粘贴 MUSIC_U 或 Cookie"));
                return;
            }

            cookieButton.IsEnabled = false;
            try
            {
                var client = new NetEaseMusicClient(value);
                if (await client.LoginWithCookieAsync(value))
                {
                    await FinishCloudLoginAsync(client);
                }
                else
                {
                    Shell.Toast(L.T("Pages.Music.CookieInvalid", "Cookie 无效或已过期"));
                }
            }
            catch (Exception exception)
            {
                Shell.Toast(L.T("Pages.Music.LoginFailedDetail", "登录失败：{0}", exception.Message));
            }
            finally
            {
                cookieButton.IsEnabled = true;
            }
        };

        Shell.Dialog(content, L.T("Pages.Music.Login", "登录网易云"));
    }

    /// <summary>
    /// 用内置 WebView2 打开网易云登录页，登录成功后从 CookieManager 读取 MUSIC_U。
    /// 比手机号密码接口稳：不会被网易的风险控制/验证码拦截，也不需要用户手动复制 Cookie。
    /// </summary>
    private async Task LoginWithBrowserAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new LoginBrowserWindow
        {
            Title = L.T("Pages.Music.LoginTitle", "连接网易云音乐"),
            ReadyStatusText = L.T("Pages.Music.LoginBrowserReady",
                "请登录网易云音乐，登录成功后会自动返回启动器"),
        };

        try
        {
            if (owner is not null) window.Show(owner);
            else window.Show();

            await window.NavigateAsync("https://music.163.com/login?targetUrl=%2F");

            string? cookie = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            while (!window.IsClosed && !timeout.IsCancellationRequested)
            {
                cookie = await window.ReadCookieHeaderAsync(
                    "https://music.163.com", "MUSIC_U", timeout.Token);
                if (!string.IsNullOrWhiteSpace(cookie)) break;

                await Task.Delay(1200, timeout.Token);
            }

            if (string.IsNullOrWhiteSpace(cookie))
            {
                Shell.Toast(window.IsClosed
                    ? L.T("Pages.Music.LoginCancelled", "已取消网易云登录")
                    : L.T("Pages.Music.LoginTimeout", "登录超时，请重新打开登录窗口"));
                return;
            }

            var client = new NetEaseMusicClient(cookie);
            if (!await client.LoginWithCookieAsync(cookie))
            {
                Shell.Toast(L.T("Pages.Music.CookieInvalid", "Cookie 无效或已过期"));
                return;
            }

            await FinishCloudLoginAsync(client);
        }
        catch (OperationCanceledException)
        {
            Shell.Toast(L.T("Pages.Music.LoginTimeout", "登录超时，请重新打开登录窗口"));
        }
        catch (Exception exception)
        {
            Shell.Toast(window.IsClosed
                ? L.T("Pages.Music.LoginCancelled", "已取消网易云登录")
                : L.T("Pages.Music.LoginFailedDetail", "登录失败：{0}", exception.Message));
        }
        finally
        {
            if (!window.IsClosed)
            {
                window.CloseByHost = true;
                window.Close();
            }
        }
    }

    private async Task FinishCloudLoginAsync(NetEaseMusicClient client)
    {
        _service.SetClient(client);
        _settings.NetEaseCookie = client.Cookie;
        _settings.NetEaseNickname = client.Nickname;
        FlushSettings();
        _cloudLikedLoaded = false;
        _cloudLiked = [];
        _ = LoadCloudLikedAsync();
        RefreshView();
        await SearchCloudAsync(SearchBox.Text ?? string.Empty);
        Shell.Toast(L.T("Pages.Music.LoginDone", "已登录网易云：{0}", client.Nickname));
    }

    private static void CloseActiveDialog(object? source)
    {
        if (source is Visual visual)
        {
            SukiDialogHost? host = visual.FindAncestorOfType<SukiDialogHost>(true);
            host?.Manager?.DismissDialog();
        }
    }

    // ==================== 播放 ====================

    private async void OnTrackSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressSelection) return;
        if ((sender as ListBox)?.SelectedItem is not MusicItem item) return;

        await PlayItemAsync(item);
    }

    /// <summary>交给全局服务播放，成功后把正在播放区刷成这首歌。</summary>
    private async Task PlayItemAsync(MusicItem item)
    {
        bool played = await _service.PlayAsync(item);
        if (!played) return;

        _current = item;
        UpdateNowPlayingUi();

        if (item.Duration <= TimeSpan.Zero && Player.Duration > TimeSpan.Zero)
        {
            item.Duration = Player.Duration;
        }
    }

    /// <summary>正在播放区：标题 / 元信息 / 封面 / 播放键 / 红心，全部对着服务的当前曲目。</summary>
    private void UpdateNowPlayingUi()
    {
        MusicItem? item = _service.Current ?? _current;
        if (item is not null) _current = item;

        if (item is null)
        {
            SetPlayingIcon(false);
            return;
        }

        NowTitle.Text = item.Title;
        NowMeta.Text = string.IsNullOrWhiteSpace(item.Artist)
            ? item.FileInfo
            : $"{item.Artist} · {item.FileInfo}";
        SetPlayingIcon(_service.IsPlaying);
        UpdateFavoriteUi();
        NowCoverHost.DataContext = item;
    }

    private void SetPlayingIcon(bool playing)
    {
        PlayIcon.Kind = playing ? "IconPause" : "IconPlay";
        ToolTip.SetTip(PlayButton, playing
            ? L.T("Pages.Music.Pause", "暂停")
            : L.T("Pages.Music.Play", "播放"));
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (!_service.HasTrack)
        {
            MusicItem? first = _current ?? _view.FirstOrDefault();
            if (first is not null) _ = PlayItemAsync(first);
            return;
        }

        _service.Toggle();
        SetPlayingIcon(_service.IsPlaying);
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        Player.Stop();
        SetPlayingIcon(false);
        _updatingSlider = true;
        ProgressSlider.Value = 0;
        _updatingSlider = false;
        ElapsedText.Text = "0:00";
        TotalText.Text = "--:--";
        Shell.Toast(L.T("Pages.Music.StopToast", "已停止播放"));
    }

    private void OnPrevClick(object? sender, RoutedEventArgs e) => _service.Previous();
    private void OnNextClick(object? sender, RoutedEventArgs e) => _service.Next();

    /// <summary>两个列表同步高亮同一首歌，不触发播放。</summary>
    private void SyncSelection(MusicItem? item)
    {
        _suppressSelection = true;
        TrackList.SelectedItem = item;
        ListTracks.SelectedItem = item;
        _suppressSelection = false;
    }

    private void OnRewind10Click(object? sender, RoutedEventArgs e) =>
        Player.Seek(Player.Position - TimeSpan.FromSeconds(10));

    private void OnForward10Click(object? sender, RoutedEventArgs e) =>
        Player.Seek(Player.Position + TimeSpan.FromSeconds(10));

    // ==================== 进度 / 音量 / 收藏 ====================

    private void OnPlayerProgress(TimeSpan position, TimeSpan duration)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPlayerProgress(position, duration));
            return;
        }

        if (duration > TimeSpan.Zero)
        {
            _updatingSlider = true;
            ProgressSlider.Maximum = duration.TotalSeconds;
            ProgressSlider.Value = Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds);
            _updatingSlider = false;
            TotalText.Text = FormatTime(duration);
        }

        ElapsedText.Text = FormatTime(position);
        SetPlayingIcon(Player.IsPlaying);
    }

    private void OnProgressChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider) return;
        Player.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (VolumeText is null) return;
        VolumeText.Text = $"{(int)e.NewValue}%";
        Player.Volume = (int)e.NewValue;
        _settings.Volume = (int)e.NewValue;
        QueueSave();
    }

    private void OnLoopClick(object? sender, RoutedEventArgs e)
    {
        _settings.Loop = !_settings.Loop;
        LoopButton.Classes.Set("active", _settings.Loop);
        QueueSave();
        Shell.Toast(_settings.Loop
            ? L.T("Pages.Music.LoopOnToast", "已开启单曲循环")
            : L.T("Pages.Music.LoopOffToast", "已关闭单曲循环"));
    }

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        ToggleFavorite(_current);
    }

    /// <summary>列表行里的爱心点击：走 ToggleButton 双向绑定已改过 item.IsFavorite。</summary>
    private void OnItemFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: MusicItem item }) return;

        // 绑定已经翻转过 IsFavorite，这里只按最终状态落盘
        bool favorited = item.IsFavorite;
        if (favorited)
        {
            if (!_settings.Favorites.Contains(item.Id)) _settings.Favorites.Add(item.Id);
            UpsertFavoriteDetail(item);
        }
        else
        {
            _settings.Favorites.Remove(item.Id);
            RemoveFavoriteDetail(item.Id);
        }

        FlushSettings();
        UpdateFavoriteUi();
        UpdateSourceUi();

        if (_source == MusicSource.Favorites && !favorited) RefreshView();

        Shell.Toast(favorited
            ? L.T("Pages.Music.FavoriteToast", "已收藏「{0}」（{1} 首）", item.Title, _settings.Favorites.Count)
            : L.T("Pages.Music.UnfavoriteToast", "已取消收藏「{0}」", item.Title));
    }

    private void ToggleFavorite(MusicItem item)
    {
        string id = item.Id;
        bool removed = _settings.Favorites.Remove(id);
        if (!removed)
        {
            _settings.Favorites.Add(id);
            UpsertFavoriteDetail(item);
        }
        else
        {
            RemoveFavoriteDetail(id);
        }

        item.IsFavorite = !removed;
        FlushSettings();
        UpdateFavoriteUi();
        UpdateSourceUi();

        if (_source == MusicSource.Favorites && removed) RefreshView();

        Shell.Toast(removed
            ? L.T("Pages.Music.UnfavoriteToast", "已取消收藏「{0}」", item.Title)
            : L.T("Pages.Music.FavoriteToast", "已收藏「{0}」（{1} 首）", item.Title, _settings.Favorites.Count));
    }

    /// <summary>正在播放面板上的红心与列表爱心保持一致。</summary>
    private void UpdateFavoriteUi()
    {
        MusicItem? item = _service.Current ?? _current;
        bool favorite = item is not null && _settings.Favorites.Contains(item.Id);
        if (item is not null) item.IsFavorite = favorite;
        FavHeartIcon.Kind = favorite ? "IconFluentHeartFilled" : "IconFluentHeart";
        FavButton.Classes.Set("active", favorite);
    }

    private void QueueSave()
    {
        _savePending = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSettings()
    {
        _saveTimer.Stop();
        _savePending = false;
        _service.Save();
    }
}
