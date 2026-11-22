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

public partial class MusicView : UserControl
{
    private enum MusicSource
    {
        Local,
        Cloud,
        Favorites,
        Roam,
        Playlists,
    }

    private readonly MusicService _service = MusicService.Instance;

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
    private MusicPlaylist? _activePlaylist;
    private bool _suppressPlaylistCombo;
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

            _service.Status += OnServiceStatus;
            _service.StateChanged += OnServiceStateChanged;
            UpdateNowPlayingUi();
        };
        DetachedFromVisualTree += (_, _) =>
        {
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
            "playlists" => MusicSource.Playlists,
            _ => MusicSource.Local,
        };

        if (_source == MusicSource.Playlists) RefreshPlaylistCombo();

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

    private bool UseThreePane => _source == MusicSource.Local;

    private void UpdateSourceUi()
    {
        RailLocal.Classes.Set("active", _source == MusicSource.Local);
        RailCloud.Classes.Set("active", _source == MusicSource.Cloud);
        RailFavorites.Classes.Set("active", _source == MusicSource.Favorites);
        RailRoam.Classes.Set("active", _source == MusicSource.Roam);
        RailPlaylists.Classes.Set("active", _source == MusicSource.Playlists);

        NowPane.IsVisible = UseThreePane;
        QueuePane.IsVisible = UseThreePane;
        ListPane.IsVisible = !UseThreePane;
        PlaylistBar.IsVisible = _source == MusicSource.Playlists;

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
            MusicSource.Playlists => _activePlaylist?.Name ?? L.T("Pages.Music.Nav.Playlists", "歌单"),
            _ => L.T("Pages.Music.Source.Local", "本地音乐"),
        };

        string placeholder = _source switch
        {
            MusicSource.Cloud => L.T("Pages.Music.SearchCloud", "搜索网易云歌曲 / 歌手"),
            MusicSource.Favorites => L.T("Pages.Music.SearchFavorites", "搜索收藏"),
            MusicSource.Roam => L.T("Pages.Music.SearchRoam", "搜索漫游歌曲"),
            MusicSource.Playlists => L.T("Pages.Music.SearchPlaylists", "搜索歌单中的歌曲"),
            _ => L.T("Pages.Music.SearchLocal", "搜索本地歌曲"),
        };
        _suppressSearch = true;
        SearchBox.PlaceholderText = placeholder;
        ListSearch.PlaceholderText = placeholder;
        _suppressSearch = false;

        if (_source == MusicSource.Playlists)
        {
            var selected = ListTracks.SelectedItem as MusicItem ?? _service.Current;
            bool inPlaylist = _activePlaylist is not null && selected is not null &&
                              _activePlaylist.Tracks.Any(entry => entry.Id == selected.Id);
            PlaylistAddButton.Content = inPlaylist
                ? L.T("Pages.Music.Playlists.Remove", "移出歌单")
                : L.T("Pages.Music.Playlists.Add", "加入歌曲");
        }
    }

    private void RefreshView()
    {
        string keyword = (SearchBox.Text ?? string.Empty).Trim();
        IEnumerable<MusicItem> source = _source switch
        {
            MusicSource.Cloud => _cloud,
            MusicSource.Favorites => BuildFavoritesView(),
            MusicSource.Roam => _roam,
            MusicSource.Playlists => _activePlaylist?.Tracks.Select(entry => entry.ToItem())
                                     ?? Enumerable.Empty<MusicItem>(),
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
            MusicSource.Playlists => _activePlaylist is null
                ? L.T("Pages.Music.Playlists.Empty", "还没有歌单，输入名称后点「新建」。")
                : L.T("Pages.Music.Playlists.EmptyTracks", "这个歌单还没有歌曲，选中歌曲后点「加入歌曲」。"),
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
        }
    }

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
                item.IsFavorite = true;
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

    private void MarkFavorites(IEnumerable<MusicItem> items)
    {
        foreach (MusicItem item in items.Where(item => !_cloudLiked.Contains(item)))
        {
            item.IsFavorite = _settings.Favorites.Contains(item.Id);
        }
    }

    private List<MusicItem> BuildFavoritesView()
    {
        List<MusicItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

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

    private void OnCloudLoginClick(object? sender, RoutedEventArgs e)
    {
        var cookie = new TextBox
        {
            PlaceholderText = L.T("Pages.Music.CookieHint", "或粘贴浏览器里的 MUSIC_U / 整段 Cookie"),
            AcceptsReturn = true,
            Height = 76,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        cookie.Classes.Add("field");

        var browserButton = MofuDialog.Primary(L.T("Pages.Music.LoginBrowser", "使用浏览器扫码登录"), () => { });
        browserButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        var cookieButton = MofuDialog.Secondary(L.T("Pages.Music.LoginCookie", "用 Cookie 登录"), () => { });
        cookieButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        var logoutButton = MofuDialog.Secondary(L.T("Pages.Music.Logout", "退出登录"), () => { });
        logoutButton.IsVisible = _client.IsLoggedIn;

        Button cancelButton = null!;
        cancelButton = MofuDialog.Secondary(L.T("Common.Cancel", "取消"), () => CloseActiveDialog(cancelButton));

        var content = MofuDialog.Stack(width: 340, spacing: 10);
        content.Children.Add(MofuDialog.Message(L.T("Pages.Music.LoginHint",
            "在系统浏览器中打开网易云扫码页，用网易云 App 扫码确认后会自动完成登录；也可以直接粘贴 Cookie。")));
        content.Children.Add(browserButton);
        content.Children.Add(new Border { Height = 1, Background = ThemeTokens.Brush("NekoBorderBrush") });
        content.Children.Add(MofuDialog.Field(L.T("Pages.Music.CookieLabel", "已有 Cookie？直接粘贴"), cookie));
        content.Children.Add(cookieButton);
        content.Children.Add(MofuDialog.Footer(logoutButton, cancelButton));

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
            await LoginWithQrCodeAsync();
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

        Shell.Dialog(content, L.T("Pages.Music.Login", "登录网易云"), sender as Control);
    }

    private async Task LoginWithQrCodeAsync()
    {
        try
        {
            var probe = new NetEaseMusicClient();
            string? key = await probe.CreateQrLoginKeyAsync();
            if (string.IsNullOrWhiteSpace(key))
            {
                Shell.Toast(L.T("Pages.Music.QrKeyFailed", "获取网易云二维码失败，请重试"));
                return;
            }

            string pageUrl = "https://music.163.com/login?codekey=" + Uri.EscapeDataString(key);
            var loginWindow = LoginBrowserWindow.Show(pageUrl, null,
                () => OpenLinkBrowser.TryOpen(pageUrl, out _));
            if (loginWindow is null && !OpenLinkBrowser.TryOpen(pageUrl, out string error))
            {
                Shell.Toast(L.T("Pages.Music.QrOpenFailed", "无法打开系统浏览器：{0}", error));
                return;
            }

            Shell.Toast(L.T("Pages.Music.LoginBrowserReady", "请用网易云 App 扫描浏览器中的二维码"));

            bool scanned = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (true)
            {
                await Task.Delay(2000, timeout.Token);
                (int code, string cookie) = await probe.CheckQrLoginAsync(key, timeout.Token);

                switch (code)
                {
                    case 800:
                        Shell.Toast(L.T("Pages.Music.QrExpired", "二维码已过期，请重新登录"));
                        return;

                    case 802 when !scanned:
                        scanned = true;
                        Shell.Toast(L.T("Pages.Music.QrScanned", "已扫码，请在手机上确认登录"));
                        break;

                    case 803:
                        if (string.IsNullOrWhiteSpace(cookie))
                        {
                            Shell.Toast(L.T("Pages.Music.QrNoCookie", "登录成功但未获取到 Cookie，请改用 Cookie 登录"));
                            return;
                        }

                        var client = new NetEaseMusicClient(cookie);
                        if (await client.LoginWithCookieAsync(cookie))
                        {
                            await FinishCloudLoginAsync(client);
                        }
                        else
                        {
                            Shell.Toast(L.T("Pages.Music.CookieInvalid", "Cookie 无效或已过期"));
                        }

                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Shell.Toast(L.T("Pages.Music.LoginTimeout", "扫码登录超时，请重试"));
        }
        catch (Exception exception)
        {
            Shell.Toast(L.T("Pages.Music.LoginFailedDetail", "登录失败：{0}", exception.Message));
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
            MofuDialogHost? host = visual.FindAncestorOfType<MofuDialogHost>(true);
            host?.Manager?.DismissDialog();
        }
    }

    private async void OnTrackSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressSelection) return;
        if ((sender as ListBox)?.SelectedItem is not MusicItem item) return;

        await PlayItemAsync(item);
    }

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

    private void OnItemFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: MusicItem item }) return;

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

    private void UpdateFavoriteUi()
    {
        MusicItem? item = _service.Current ?? _current;
        bool favorite = item is not null && _settings.Favorites.Contains(item.Id);
        if (item is not null) item.IsFavorite = favorite;
        FavHeartIcon.Kind = favorite ? "IconFluentHeartFilled" : "IconFluentHeart";
        FavButton.Classes.Set("active", favorite);
    }

    private void RefreshPlaylistCombo()
    {
        _suppressPlaylistCombo = true;
        PlaylistCombo.Items.Clear();
        foreach (var playlist in _settings.Playlists)
        {
            PlaylistCombo.Items.Add(new ComboBoxItem { Content = playlist.Name, Tag = playlist.Id });
        }

        _activePlaylist ??= _settings.Playlists.FirstOrDefault();
        PlaylistCombo.SelectedItem = _activePlaylist is null
            ? null
            : PlaylistCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _activePlaylist.Id, StringComparison.Ordinal));
        PlaylistNameBox.Text = _activePlaylist?.Name ?? string.Empty;
        _suppressPlaylistCombo = false;
    }

    private void OnPlaylistComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressPlaylistCombo) return;
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;

        _activePlaylist = _settings.Playlists.FirstOrDefault(p => p.Id == id);
        PlaylistNameBox.Text = _activePlaylist?.Name ?? string.Empty;
        RefreshView();
    }

    private void OnPlaylistNewClick(object? sender, RoutedEventArgs e)
    {
        string name = (PlaylistNameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Pages.Music.Playlists.NeedName", "请先输入歌单名称"));
            return;
        }
        if (_settings.Playlists.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Shell.Toast(L.T("Pages.Music.Playlists.Duplicate", "已有同名歌单"));
            return;
        }

        var playlist = new MusicPlaylist { Name = name };
        _settings.Playlists.Add(playlist);
        _activePlaylist = playlist;
        FlushSettings();
        RefreshPlaylistCombo();
        RefreshView();
        Shell.Toast(L.T("Pages.Music.Playlists.Created", "已创建歌单「{0}」", name));
    }

    private void OnPlaylistRenameClick(object? sender, RoutedEventArgs e)
    {
        if (_activePlaylist is null)
        {
            Shell.Toast(L.T("Pages.Music.Playlists.NeedSelect", "请先选择或新建歌单"));
            return;
        }
        string name = (PlaylistNameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Pages.Music.Playlists.NeedName", "请先输入歌单名称"));
            return;
        }
        if (_settings.Playlists.Any(p => p != _activePlaylist &&
                                         string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Shell.Toast(L.T("Pages.Music.Playlists.Duplicate", "已有同名歌单"));
            return;
        }

        _activePlaylist.Name = name;
        FlushSettings();
        RefreshPlaylistCombo();
        RefreshView();
        Shell.Toast(L.T("Pages.Music.Playlists.Renamed", "已重命名为「{0}」", name));
    }

    private void OnPlaylistDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_activePlaylist is null)
        {
            Shell.Toast(L.T("Pages.Music.Playlists.NeedSelect", "请先选择或新建歌单"));
            return;
        }

        string name = _activePlaylist.Name;
        _settings.Playlists.Remove(_activePlaylist);
        _activePlaylist = null;
        FlushSettings();
        RefreshPlaylistCombo();
        RefreshView();
        Shell.Toast(L.T("Pages.Music.Playlists.Deleted", "已删除歌单「{0}」", name));
    }

    private void OnPlaylistAddClick(object? sender, RoutedEventArgs e)
    {
        _activePlaylist ??= _settings.Playlists.FirstOrDefault();
        if (_activePlaylist is null)
        {
            Shell.Toast(L.T("Pages.Music.Playlists.NeedSelect", "请先选择或新建歌单"));
            return;
        }
        MusicItem? item = ListTracks.SelectedItem as MusicItem ?? _service.Current ?? _current;
        if (item is null)
        {
            Shell.Toast(L.T("Pages.Music.Playlists.NeedTrack", "请先选择一首歌曲"));
            return;
        }

        var existing = _activePlaylist.Tracks.FirstOrDefault(entry => entry.Id == item.Id);
        if (existing is not null)
        {
            _activePlaylist.Tracks.Remove(existing);
            FlushSettings();
            RefreshView();
            Shell.Toast(L.T("Pages.Music.Playlists.Removed", "已从歌单移除「{0}」", item.Title));
            return;
        }

        _activePlaylist.Tracks.Add(MusicFavoriteEntry.From(item));
        FlushSettings();
        RefreshView();
        Shell.Toast(L.T("Pages.Music.Playlists.Added", "已加入歌单「{0}」", item.Title));
    }

    private void OnItemAddToPlaylistClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MusicItem item }) return;

        _activePlaylist ??= _settings.Playlists.FirstOrDefault();
        if (_activePlaylist is null)
        {
            Shell.Toast(L.T("Pages.Music.Playlists.NeedSelect", "请先选择或新建歌单"));
            return;
        }

        var existing = _activePlaylist.Tracks.FirstOrDefault(entry => entry.Id == item.Id);
        if (existing is not null)
        {
            _activePlaylist.Tracks.Remove(existing);
            FlushSettings();
            RefreshView();
            Shell.Toast(L.T("Pages.Music.Playlists.Removed", "已从歌单移除「{0}」", item.Title));
            return;
        }

        _activePlaylist.Tracks.Add(MusicFavoriteEntry.From(item));
        FlushSettings();
        RefreshView();
        Shell.Toast(L.T("Pages.Music.Playlists.Added", "已加入歌单「{0}」", item.Title));
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
