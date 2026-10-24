using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NekoSolo.Services;

namespace NekoSolo;

/// <summary>
/// NekoSolo 安装器主窗口：欢迎 / 安装进度 / 完成 三态 + 关于页。
/// 所有文案由 <see cref="SoloStrings"/> 提供（中英跟随系统语言）。
/// </summary>
public partial class MainWindow : Window
{
    private SoloManifest _manifest = new();
    private SoloPayloadInfo _payload;
    private bool _hasPayload;
    private SoloReleaseInfo? _releaseInfo;
    private string? _downloadedPackagePath;
    private SoloInstallController? _controller;
    private CancellationTokenSource? _installCts;
    private readonly StringBuilder _log = new();
    private readonly List<Border> _sidebarDots = [];
    private readonly List<TextBlock> _sidebarTitles = [];
    private readonly List<Border> _progressDots = [];
    private readonly List<TextBlock> _progressTitles = [];
    private readonly List<TextBlock> _progressDetails = [];
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _installClock = new();
    private int _logLines;
    private bool _showAbout;

    public MainWindow()
    {
        InitializeComponent();
        ApplyStrings();
        BuildStepRows();

        _elapsedTimer.Tick += (_, _) =>
        {
            if (_installClock.IsRunning)
            {
                ElapsedText.Text = SoloStrings.F("Progress.Elapsed", FormatDuration(_installClock.Elapsed));
            }
        };

        Loaded += OnLoaded;
    }

    // ---------------- 初始化 ----------------

    private void ApplyStrings()
    {
        Title = SoloStrings.T("App.Title");
        PoweredByText.Text = SoloStrings.T("App.PoweredBy");
        VersionText.Text = SoloStrings.F("App.Version", ShortVersion());

        WelcomeDesc.Text = SoloStrings.T("Welcome.Desc");
        InfoMcLabel.Text = SoloStrings.T("Welcome.McVersion");
        InfoLoaderLabel.Text = SoloStrings.T("Welcome.Loader");
        InfoVersionLabel.Text = SoloStrings.T("Welcome.PackVersion");
        InfoAuthorLabel.Text = SoloStrings.T("Welcome.Author");
        TargetLabel.Text = SoloStrings.T("Welcome.Target");
        BrowseButton.Content = SoloStrings.T("Welcome.Browse");
        TargetHint.Text = SoloStrings.T("Welcome.TargetTip");
        ShortcutCheck.Content = SoloStrings.T("Welcome.OptShortcut");
        LaunchCheck.Content = SoloStrings.T("Welcome.OptLaunch");
        PortableCheck.Content = SoloStrings.T("Welcome.OptPortable");
        StartButton.Content = SoloStrings.T("Welcome.Start");
        ReleaseButton.Content = SoloStrings.T("Welcome.FromRelease");
        CancelWelcomeButton.Content = SoloStrings.T("Welcome.Cancel");

        ProgressTitle.Text = SoloStrings.T("Progress.Title");
        LogHeader.Text = SoloStrings.T("Progress.Log");
        CancelInstallButton.Content = SoloStrings.T("Progress.Cancel");

        DoneFilesLabel.Text = SoloStrings.T("Done.Files");
        DoneSizeLabel.Text = SoloStrings.T("Done.Size");
        DoneElapsedLabel.Text = SoloStrings.T("Done.Elapsed");
        DoneLaunchButton.Content = SoloStrings.T("Done.Launch");
        DoneOpenButton.Content = SoloStrings.T("Done.OpenFolder");
        DoneRetryButton.Content = SoloStrings.T("Done.Retry");
        DoneCloseButton.Content = SoloStrings.T("Done.Close");

        AboutLinkButton.Content = SoloStrings.T("Nav.About");
        AboutTitle.Text = SoloStrings.T("About.Title");
        AboutIntro.Text = SoloStrings.T("About.Intro");
        AboutCreatorHeader.Text = SoloStrings.T("About.Creator");
        AboutFormatHeader.Text = SoloStrings.T("About.Format");
        AboutFormatValue.Text = SoloStrings.T("About.FormatValue");
        AboutBackButton.Content = SoloStrings.T("About.Back");
        AboutOpenButton.Content = SoloStrings.T("About.OpenFolder");
        AboutLogButton.Content = SoloStrings.T("About.ExportLog");
        AboutConfigButton.Content = SoloStrings.T("About.ViewConfig");
        AboutCopyButton.Content = SoloStrings.T("About.Copy");
        ContentsTitle.Text = SoloStrings.T("Welcome.Contents");
    }

    private void BuildStepRows()
    {
        // 侧栏：小圆点 + 标题；进度页：圆点 + 标题 + 说明
        foreach (InstallStepItem step in new SoloInstallController().Steps)
        {
            var dot = new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(5),
                Background = (Brush)FindResource("NekoBorderBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var title = new TextBlock
            {
                Text = step.Title,
                Style = (Style)FindResource("NekoStepTitle"),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(title);
            SidebarStepHost.Children.Add(row);
            _sidebarDots.Add(dot);
            _sidebarTitles.Add(title);

            var pDot = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1.4),
                BorderBrush = (Brush)FindResource("NekoBorderBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var pTitle = new TextBlock
            {
                Text = step.Title,
                Style = (Style)FindResource("NekoStepTitle"),
                Width = 150,
            };
            var pDetail = new TextBlock
            {
                Style = (Style)FindResource("NekoCaption"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var pRow = new Grid();
            pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pRow.Children.Add(pDot);
            Grid.SetColumn(pTitle, 1);
            pRow.Children.Add(pTitle);
            Grid.SetColumn(pDetail, 2);
            pRow.Children.Add(pDetail);
            ProgressStepHost.Children.Add(pRow);
            _progressDots.Add(pDot);
            _progressTitles.Add(pTitle);
            _progressDetails.Add(pDetail);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadPackage();
        _ = FetchReleaseAsync();
    }

    private void LoadPackage()
    {
        string executable = Environment.ProcessPath ?? string.Empty;
        if (!string.IsNullOrEmpty(executable) && SoloPayload.TryRead(executable, out SoloPayloadInfo info, out _))
        {
            _payload = info;
            _hasPayload = true;
            try
            {
                SoloManifest? fromPayload = SoloPayload.ReadManifest(info);
                if (fromPayload is not null) _manifest = fromPayload;
            }
            catch (Exception ex)
            {
                AppendLog("manifest: " + ex.Message);
            }

            TryLoadIcon(info);
        }
        else
        {
            // 空壳 exe（未追加载荷）：走 GitHub Releases 下载安装包，欢迎页在取到真实清单前只显示通用信息
            _manifest = new SoloManifest();
        }

        PackNameText.Text = _manifest.DisplayName;
        PackAuthorText.Text = _manifest.AuthorDisplay;
        PackVersionChip.Text = "v" + _manifest.VersionDisplay;
        McChip.Text = _manifest.McDisplay;
        LoaderChip.Text = _manifest.LoaderDisplay;
        TitlePackText.Text = " · " + _manifest.DisplayName;
        WelcomeTitle.Text = SoloStrings.F("Welcome.Title", _manifest.DisplayName);
        InfoMcValue.Text = _manifest.McDisplay;
        InfoLoaderValue.Text = _manifest.LoaderDisplay;
        InfoVersionValue.Text = _manifest.VersionDisplay;
        InfoAuthorValue.Text = _manifest.AuthorDisplay;

        AboutPackValue.Text = _manifest.DisplayName;
        AboutVersionValue.Text = _manifest.VersionDisplay;
        AboutAuthorValue.Text = _manifest.AuthorDisplay;
        AboutLoaderValue.Text = $"{_manifest.LoaderDisplay} · MC {_manifest.McDisplay}";

        TargetBox.Text = DefaultTargetPath();
        UpdateSizeText();
        UpdateDiskText();
        TargetBox.TextChanged += (_, _) =>
        {
            UpdateDiskText();
            UpdateSizeText();
        };

        UpdateContents();

        if (!_hasPayload)
        {
            PayloadWarning.Visibility = Visibility.Visible;
            PayloadWarningText.Text = SoloStrings.T("Error.NoPayload");
        }

        InstallMarker? marker = SoloInstallController.ReadMarker(TargetBox.Text ?? string.Empty);
        if (marker is not null && _hasPayload)
        {
            PayloadWarning.Visibility = Visibility.Visible;
            PayloadWarningText.Text = SoloStrings.F("Welcome.Installed", marker.PackId, marker.Version);
        }
    }

    /// <summary>欢迎页展示安装包里真实有哪些文件（而不是一句「体积未知」）。</summary>
    private void UpdateContents()
    {
        if (!_hasPayload && string.IsNullOrWhiteSpace(_downloadedPackagePath))
        {
            ContentsText.Text = SoloStrings.T("Welcome.SizeUnknown");
            return;
        }

        try
        {
            using Stream payload = !string.IsNullOrWhiteSpace(_downloadedPackagePath)
                ? File.OpenRead(_downloadedPackagePath)
                : SoloPayload.OpenPayloadStream(_payload);
            using var archive = new System.IO.Compression.ZipArchive(payload,
                System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);

            int files = 0;
            long total = 0;
            int overrides = 0;
            int minecraft = 0;
            int jre = 0;
            foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                files++;
                total += entry.Length;
                if (entry.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                {
                    overrides++;
                }
                else if (entry.FullName.StartsWith("minecraft/", StringComparison.OrdinalIgnoreCase)) minecraft++;
                else if (entry.FullName.StartsWith("jre/", StringComparison.OrdinalIgnoreCase)) jre++;
            }

            string summary = SoloStrings.F("Welcome.ContentsValue", files, FormatSize(total));
            if (overrides + minecraft + jre > 0)
            {
                summary += $" · files {overrides} · minecraft {minecraft} · jre {jre}";
            }

            if (!string.IsNullOrWhiteSpace(_manifest.Description))
            {
                summary += Environment.NewLine + _manifest.Description;
            }

            ContentsText.Text = summary;
        }
        catch (Exception ex)
        {
            ContentsText.Text = ex.Message;
        }
    }

    private void TryLoadIcon(SoloPayloadInfo info)
    {
        if (string.IsNullOrWhiteSpace(_manifest.IconPath)) return;
        try
        {
            using Stream payload = SoloPayload.OpenPayloadStream(info);
            using var archive = new System.IO.Compression.ZipArchive(payload,
                System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            System.IO.Compression.ZipArchiveEntry? entry = archive.GetEntry(_manifest.IconPath)
                                                           ?? archive.Entries.FirstOrDefault(item =>
                                                               string.Equals(Path.GetFileName(item.FullName),
                                                                   Path.GetFileName(_manifest.IconPath),
                                                                   StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            using Stream stream = entry.Open();
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            PackIconImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            AppendLog("icon: " + ex.Message);
        }
    }

    private string DefaultTargetPath()
    {
        string name = _manifest.DisplayName;
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "NekoSolo";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NekoSolo", name);
    }

    private void UpdateSizeText()
    {
        SizeText.Text = _hasPayload
            ? SoloStrings.F("Welcome.Size", $"{_payload.SizeMegabytes:0.0} MB")
            : SoloStrings.T("Welcome.SizeUnknown");
    }

    private void UpdateDiskText()
    {
        try
        {
            string path = TargetBox.Text?.Trim() ?? string.Empty;
            if (path.Length == 0)
            {
                DiskText.Text = string.Empty;
                return;
            }

            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            if (root.Length == 0)
            {
                DiskText.Text = string.Empty;
                return;
            }

            var drive = new DriveInfo(root);
            DiskText.Text = SoloStrings.F("Welcome.DiskFree", FormatSize(drive.AvailableFreeSpace));
        }
        catch
        {
            DiskText.Text = string.Empty;
        }
    }

    // ---------------- 面板切换 ----------------

    private void ShowPanel(string name)
    {
        WelcomePanel.Visibility = name == "welcome" ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = name == "progress" ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = name == "done" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = name == "about" ? Visibility.Visible : Visibility.Collapsed;
        _showAbout = name == "about";
    }

    private void ShowAbout()
    {
        ShowPanel("about");
    }

    private void OnAboutLinkClick(object sender, RoutedEventArgs e)
    {
        if (_showAbout) ShowPanel("welcome");
        else ShowAbout();
    }

    private void OnAboutBackClick(object sender, RoutedEventArgs e) => ShowPanel("welcome");

    // ---------------- 安装 ----------------

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        string target = TargetBox.Text?.Trim() ?? string.Empty;
        if (target.Length == 0)
        {
            ShowInlineError(SoloStrings.T("Error.Target"));
            return;
        }

        if (!ValidateTarget(target, out string error))
        {
            ShowInlineError(error);
            return;
        }

        await StartInstallAsync();
    }

    /// <summary>安装前的真实校验：路径、写权限、磁盘空间。</summary>
    private bool ValidateTarget(string target, out string error)
    {
        error = string.Empty;
        try
        {
            string full = Path.GetFullPath(target);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            if (root.Length == 0)
            {
                error = SoloStrings.T("Error.Target");
                return false;
            }

            long needed = _hasPayload ? _payload.Length + 32L * 1024 * 1024 : 0;
            if (needed > 0)
            {
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < needed)
                {
                    error = SoloStrings.F("Error.NoSpace", FormatSize(needed),
                        FormatSize(drive.AvailableFreeSpace));
                    return false;
                }
            }

            Directory.CreateDirectory(full);
            return true;
        }
        catch (Exception ex)
        {
            error = SoloStrings.T("Error.Target") + " " + ex.Message;
            return false;
        }
    }

    private void ShowInlineError(string message)
    {
        PayloadWarning.Visibility = Visibility.Visible;
        PayloadWarningText.Text = message;
    }

    private async Task StartInstallAsync()
    {
        string target = string.IsNullOrWhiteSpace(TargetBox.Text)
            ? DefaultTargetPath()
            : TargetBox.Text!.Trim();

        if (!ValidateTarget(target, out string error))
        {
            ShowInlineError(error);
            return;
        }

        if (!_hasPayload && string.IsNullOrWhiteSpace(_downloadedPackagePath))
        {
            if (!await EnsureReleasePackageAsync().ConfigureAwait(true))
            {
                return;
            }
        }

        var options = new InstallOptions(target, ShortcutCheck.IsChecked == true,
            LaunchCheck.IsChecked == true, PortableCheck.IsChecked == true);

        _log.Clear();
        _logLines = 0;
        LogText.Text = string.Empty;
        LogCountText.Text = string.Empty;
        ResetSteps();
        ShowPanel("progress");
        CancelInstallButton.IsEnabled = true;
        CancelInstallButton.Content = SoloStrings.T("Progress.Cancel");

        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        _controller = new SoloInstallController(packagePath: _downloadedPackagePath);
        _controller.StepChanged += OnStepChanged;
        _controller.ProgressChanged += OnProgressChanged;
        _controller.LogAppended += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        _controller.Finished += result => Dispatcher.BeginInvoke(() => OnInstallFinished(result));

        _installClock.Restart();
        _elapsedTimer.Start();
        ElapsedText.Text = SoloStrings.F("Progress.Elapsed", "0.0s");

        await _controller.RunAsync(_manifest, options, _installCts.Token);
    }

    private async void OnFetchReleaseClick(object sender, RoutedEventArgs e) =>
        await FetchReleaseAsync(force: true);

    private async Task FetchReleaseAsync(bool force = false)
    {
        if (_hasPayload) return;
        if (_releaseInfo is not null && !force)
        {
            UpdateReleaseStatus();
            return;
        }

        ReleaseStatus.Text = SoloStrings.T("Welcome.ReleaseChecking");
        ReleaseButton.IsEnabled = false;
        try
        {
            _releaseInfo = await SoloReleaseClient.FetchLatestAsync();
            UpdateReleaseStatus();
        }
        catch (Exception ex)
        {
            ReleaseStatus.Text = SoloStrings.F("Welcome.ReleaseFailed", ex.Message);
        }
        finally
        {
            ReleaseButton.IsEnabled = true;
        }
    }

    private void UpdateReleaseStatus()
    {
        SoloReleaseAsset? asset = SoloReleaseClient.SelectPackageAsset(_releaseInfo);
        if (_releaseInfo is null)
        {
            ReleaseStatus.Text = SoloStrings.F("Welcome.ReleaseNone", SoloReleaseClient.ReleasesPageUrl);
            return;
        }

        ReleaseStatus.Text = asset is null
            ? SoloStrings.F("Welcome.ReleaseNoAsset", _releaseInfo.TagName)
            : SoloStrings.F("Welcome.ReleaseFound", _releaseInfo.TagName, asset.Name,
                FormatSize(asset.Size));
    }

    private async Task<bool> EnsureReleasePackageAsync()
    {
        if (!string.IsNullOrWhiteSpace(_downloadedPackagePath) && File.Exists(_downloadedPackagePath))
        {
            return true;
        }

        if (_releaseInfo is null)
        {
            await FetchReleaseAsync(force: true);
        }

        SoloReleaseAsset? asset = SoloReleaseClient.SelectPackageAsset(_releaseInfo);
        if (asset is null)
        {
            ShowInlineError(SoloStrings.F("Welcome.ReleaseMissing", SoloReleaseClient.ReleasesPageUrl));
            return false;
        }

        string target = Path.Combine(Path.GetTempPath(), "NekoSolo-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            ReleaseStatus.Text = SoloStrings.F("Welcome.ReleaseDownloading", asset.Name);
            await SoloReleaseClient.DownloadAsync(asset, target, progress =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ReleaseStatus.Text = SoloStrings.F("Welcome.ReleaseDownloading",
                        $"{asset.Name} · {progress * 100:0}%");
                });
            });

            _downloadedPackagePath = target;
            using (Stream stream = File.OpenRead(target))
            {
                SoloManifest? manifest = SoloPayload.ReadManifest(stream);
                if (manifest is not null)
                {
                    _manifest = manifest;
                    ApplyManifestToUi();
                }
            }

            ReleaseStatus.Text = SoloStrings.F("Welcome.ReleaseReady", asset.Name);
            return true;
        }
        catch (Exception ex)
        {
            ReleaseStatus.Text = SoloStrings.F("Welcome.ReleaseFailed", ex.Message);
            ShowInlineError(ReleaseStatus.Text);
            return false;
        }
    }

    private void ApplyManifestToUi()
    {
        PackNameText.Text = _manifest.DisplayName;
        PackAuthorText.Text = _manifest.AuthorDisplay;
        PackVersionChip.Text = "v" + _manifest.VersionDisplay;
        McChip.Text = _manifest.McDisplay;
        LoaderChip.Text = _manifest.LoaderDisplay;
        TitlePackText.Text = " 路 " + _manifest.DisplayName;
        WelcomeTitle.Text = SoloStrings.F("Welcome.Title", _manifest.DisplayName);
        InfoMcValue.Text = _manifest.McDisplay;
        InfoLoaderValue.Text = _manifest.LoaderDisplay;
        InfoVersionValue.Text = _manifest.VersionDisplay;
        InfoAuthorValue.Text = _manifest.AuthorDisplay;
        AboutPackValue.Text = _manifest.DisplayName;
        AboutVersionValue.Text = _manifest.VersionDisplay;
        AboutAuthorValue.Text = _manifest.AuthorDisplay;
        AboutLoaderValue.Text = $"{_manifest.LoaderDisplay} 路 MC {_manifest.McDisplay}";
        UpdateContents();
    }

    private void OnStepChanged(InstallStepItem step)
    {
        Dispatcher.BeginInvoke(() =>
        {
            int index = (int)step.Step;
            if (index < 0 || index >= _sidebarDots.Count) return;

            Brush dotBrush = step.Status switch
            {
                StepStatus.Done => (Brush)FindResource("NekoSuccessBrush"),
                StepStatus.Running => (Brush)FindResource("NekoAccentBrush"),
                StepStatus.Failed => (Brush)FindResource("NekoDangerBrush"),
                _ => (Brush)FindResource("NekoBorderBrush"),
            };

            _sidebarDots[index].Background = dotBrush;
            _sidebarTitles[index].Foreground = step.Status == StepStatus.Pending
                ? (Brush)FindResource("NekoDimBrush")
                : (Brush)FindResource("NekoTextBrush");
            _sidebarTitles[index].FontWeight = step.Status == StepStatus.Running
                ? FontWeights.SemiBold
                : FontWeights.Normal;

            Border pDot = _progressDots[index];
            pDot.BorderBrush = dotBrush;
            pDot.Background = step.Status switch
            {
                StepStatus.Done => (Brush)FindResource("NekoSuccessBrush"),
                StepStatus.Running => (Brush)FindResource("NekoAccentSoftBrush"),
                StepStatus.Failed => (Brush)FindResource("NekoDangerBrush"),
                _ => Brushes.Transparent,
            };

            _progressTitles[index].Foreground = step.Status == StepStatus.Pending
                ? (Brush)FindResource("NekoDimBrush")
                : (Brush)FindResource("NekoTextBrush");
            _progressTitles[index].FontWeight = step.Status == StepStatus.Running
                ? FontWeights.SemiBold
                : FontWeights.Normal;

            if (!string.IsNullOrEmpty(step.Detail))
            {
                _progressDetails[index].Text = step.Detail;
                _progressDetails[index].Foreground = (Brush)FindResource("NekoMutedBrush");
            }
            else if (step.Status == StepStatus.Failed)
            {
                _progressDetails[index].Text = SoloStrings.T("Done.Failed");
                _progressDetails[index].Foreground = (Brush)FindResource("NekoDangerBrush");
            }
        });
    }

    private void OnProgressChanged(double value, string stage, string detail)
    {
        Dispatcher.BeginInvoke(() =>
        {
            InstallBar.Value = value;
            PercentText.Text = $"{value * 100:0}%";
            StageText.Text = string.IsNullOrWhiteSpace(detail)
                ? stage
                : SoloStrings.F("Progress.Stage", stage, detail);
        });
    }

    private void ResetSteps()
    {
        for (int i = 0; i < _sidebarDots.Count; i++)
        {
            _sidebarDots[i].Background = (Brush)FindResource("NekoBorderBrush");
            _sidebarTitles[i].Foreground = (Brush)FindResource("NekoDimBrush");
            _sidebarTitles[i].FontWeight = FontWeights.Normal;
            _progressDots[i].BorderBrush = (Brush)FindResource("NekoBorderBrush");
            _progressDots[i].Background = Brushes.Transparent;
            _progressTitles[i].Foreground = (Brush)FindResource("NekoDimBrush");
            _progressTitles[i].FontWeight = FontWeights.Normal;
            _progressDetails[i].Text = string.Empty;
        }

        InstallBar.Value = 0;
        PercentText.Text = "0%";
        StageText.Text = SoloStrings.T("Progress.Preparing");
    }

    private void AppendLog(string line)
    {
        _logLines++;
        _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {line}");
        LogText.Text = _log.ToString();
        LogCountText.Text = SoloStrings.F("Progress.Lines", _logLines);
        LogScroll.ScrollToEnd();
    }

    private void OnCancelInstallClick(object sender, RoutedEventArgs e)
    {
        if (_installCts is null || _installCts.IsCancellationRequested) return;
        CancelInstallButton.IsEnabled = false;
        CancelInstallButton.Content = SoloStrings.T("Progress.Cancelling");
        _installCts.Cancel();
    }

    private void OnInstallFinished(InstallResult result)
    {
        _installClock.Stop();
        _elapsedTimer.Stop();
        CancelInstallButton.IsEnabled = true;
        ShowDone(result);
    }

    private void ShowDone(InstallResult result)
    {
        ShowPanel("done");
        bool success = result.Success;
        bool cancelled = result.Cancelled;

        DoneBadge.Background = (Brush)FindResource(success
            ? "NekoSuccessSoftBrush"
            : cancelled ? "NekoWarningSoftBrush" : "NekoDangerSoftBrush");
        DoneIcon.Stroke = (Brush)FindResource(success
            ? "NekoSuccessBrush"
            : cancelled ? "NekoWarningBrush" : "NekoDangerBrush");
        DoneIcon.Data = Geometry.Parse(success
            ? "M2,12 L9,19 L22,5"
            : cancelled ? "M4,12 L20,12" : "M4,4 L20,20 M20,4 L4,20");

        DoneTitle.Text = SoloStrings.T(success
            ? "Done.Success"
            : cancelled ? "Done.Cancelled" : "Done.Failed");
        DoneDesc.Text = success
            ? SoloStrings.F("Done.SuccessDesc", _manifest.DisplayName)
            : cancelled
                ? SoloStrings.T("Done.CancelledDesc")
                : string.IsNullOrWhiteSpace(result.Error)
                    ? SoloStrings.T("Done.FailedDesc")
                    : result.Error;

        DoneFilesValue.Text = result.FileCount.ToString("N0");
        DoneSizeValue.Text = FormatSize(result.WrittenBytes);
        DoneElapsedValue.Text = FormatDuration(result.Elapsed);

        DoneLaunchButton.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
        DoneRetryButton.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        DoneOpenButton.Visibility = Directory.Exists(TargetBox.Text ?? string.Empty)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => ShowPanel("welcome");

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        string target = TargetBox.Text?.Trim() ?? string.Empty;
        if (!Directory.Exists(target)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    private void OnLaunchGameClick(object sender, RoutedEventArgs e)
    {
        string target = TargetBox.Text?.Trim() ?? string.Empty;
        if (!Directory.Exists(target)) return;

        string? launcher = SoloInstallController.FindLauncherExe(target, Environment.ProcessPath);
        if (launcher is null)
        {
            ShowInlineError(SoloStrings.T("Error.Target"));
            return;
        }

        Process.Start(new ProcessStartInfo(launcher) { WorkingDirectory = target, UseShellExecute = true });
    }

    // ---------------- 关于页动作 ----------------

    private void OnExportLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = SoloStrings.T("About.ExportLog"),
                FileName = "nekosolo-install.log",
                Filter = "Log (*.log)|*.log|Text (*.txt)|*.txt",
            };
            if (dialog.ShowDialog(this) != true) return;

            File.WriteAllText(dialog.FileName, _log.ToString());
            AppendLog(SoloStrings.F("About.LogSaved", dialog.FileName));
        }
        catch (Exception ex)
        {
            AppendLog("log export failed: " + ex.Message);
        }
    }

    private void OnViewConfigClick(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(TargetBox.Text?.Trim() ?? string.Empty, "solo.json");
        if (!File.Exists(path))
        {
            ShowInlineError(SoloStrings.T("Error.Target"));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("open config failed: " + ex.Message);
        }
    }

    private void OnCopyInfoClick(object sender, RoutedEventArgs e)
    {
        string info = string.Join(Environment.NewLine,
            $"NekoSolo {ShortVersion()}",
            $"pack={_manifest.PackId} {_manifest.VersionDisplay}",
            $"mc={_manifest.McVersion} loader={_manifest.LoaderDisplay}",
            $"payload={(_hasPayload ? $"{_payload.Length} bytes" : "none")}",
            $"target={TargetBox.Text}");
        try
        {
            System.Windows.Clipboard.SetText(info);
            AppendLog(SoloStrings.T("About.Copied"));
        }
        catch (Exception ex)
        {
            AppendLog("clipboard failed: " + ex.Message);
        }
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------------- 浏览目录 ----------------

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = SoloStrings.T("Dialog.BrowseTitle"),
            InitialDirectory = Directory.Exists(TargetBox.Text) ? TargetBox.Text : null,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            TargetBox.Text = dialog.FolderName;
        }
    }

    // ---------------- 窗口按钮 ----------------

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 安装中直接关窗会留下半装目录：先取消，再关闭
        if (_installCts is { IsCancellationRequested: false })
        {
            _installCts.Cancel();
        }

        Close();
    }

    // ---------------- 工具 ----------------

    private static string ShortVersion()
    {
        string raw = typeof(MainWindow).Assembly
                         .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? typeof(MainWindow).Assembly.GetName().Version?.ToString()
                     ?? "1.0.0";
        int plus = raw.IndexOf('+');
        return plus > 0 ? raw[..plus] : raw;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : elapsed.TotalMinutes >= 1
                ? $"{elapsed.Minutes}m {elapsed.Seconds}s"
                : $"{elapsed.TotalSeconds:0.0}s";
}
