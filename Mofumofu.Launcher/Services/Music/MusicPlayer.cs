using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using NAudio.Wave;

namespace Mofumofu.Launcher.Services.Music;

public sealed class MusicPlayer : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    // 单个在线音频的体积上限，防止恶意/异常响应把缓存文件无限写盘。
    private const int MaxAudioBytes = 256 * 1024 * 1024;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private bool _disposed;
    private bool _stopping;

    public MusicPlayer()
    {
        _timer.Tick += (_, _) => RaiseProgress();
    }

    public event Action<TimeSpan, TimeSpan>? PositionChanged;

    public event Action? PlaybackEnded;

    public event Action<string>? Failed;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public bool HasTrack => _reader is not null;

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    private int _volume = 72;

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            if (_output is not null) _output.Volume = _volume / 100f;
        }
    }

    public string? CurrentPath { get; private set; }

    public async Task<bool> PlayAsync(string source, bool remote, CancellationToken cancellationToken = default)
    {
        try
        {
            // Stop 也要在 UI 线程执行：调用方可能已经用 ConfigureAwait(false) 切到线程池，
            // 直接在这里碰 _timer / _output / _reader 会和 UI 线程产生竞态。
            await Dispatcher.UIThread.InvokeAsync(() => { Stop(); });

            string path = remote
                ? await CacheRemoteAsync(source, cancellationToken).ConfigureAwait(false)
                : source;

            if (!File.Exists(path))
            {
                Failed?.Invoke("音频文件不存在");
                return false;
            }

            // 缓存下载用了 ConfigureAwait(false)，之后已在线程池线程；播放器字段、
            // WaveOutEvent 与 Timer 统一回到 UI 线程重建，避免跨线程竞态。
            return await Dispatcher.UIThread.InvokeAsync<bool>(() => StartPlayback(path));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception.Message);
            return false;
        }
    }

    private bool StartPlayback(string path)
    {
        WaveStream reader;
        try
        {
            reader = new MediaFoundationReader(path);
        }
        catch (Exception)
        {
            reader = new AudioFileReader(path);
        }

        _reader = reader;
        CurrentPath = path;
        _output = new WaveOutEvent { DesiredLatency = 180 };
        _output.Init(_reader);
        _output.Volume = _volume / 100f;
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Play();
        _timer.Start();
        RaiseProgress();
        return true;
    }

    public void Pause()
    {
        if (_output is null || _output.PlaybackState != PlaybackState.Playing) return;
        _output.Pause();
        _timer.Stop();
        RaiseProgress();
    }

    public void Resume()
    {
        if (_output is null || _output.PlaybackState == PlaybackState.Playing) return;
        _output.Play();
        _timer.Start();
    }

    public void Toggle()
    {
        if (IsPlaying) Pause();
        else Resume();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null) return;
        TimeSpan target = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > _reader.TotalTime ? _reader.TotalTime : position;
        _reader.CurrentTime = target;
        RaiseProgress();
    }

    public void Stop()
    {
        _timer.Stop();
        WaveOutEvent? output = _output;
        WaveStream? reader = _reader;
        _output = null;
        _reader = null;
        _stopping = true;
        try
        {
            if (output is not null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                output.Stop();
                output.Dispose();
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _stopping = false;
        }

        try
        {
            reader?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;

        Dispatcher.UIThread.Post(() =>
        {
            _timer.Stop();
            if (e.Exception is not null)
            {
                Failed?.Invoke(e.Exception.Message);
                return;
            }

            PlaybackEnded?.Invoke();
        });
    }

    private void RaiseProgress()
    {
        if (_reader is null) return;
        PositionChanged?.Invoke(_reader.CurrentTime, _reader.TotalTime);
    }

    private static async Task<string> CacheRemoteAsync(string url, CancellationToken cancellationToken)
    {
        if (!MusicUrlPolicy.TryNormalize(url, out string safeUrl))
        {
            throw new InvalidOperationException("音频地址不在允许的网易云 CDN 域内");
        }

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mofumofu.Launcher",
            "music-cache");
        Directory.CreateDirectory(directory);

        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(safeUrl));
        string path = Path.Combine(directory, Convert.ToHexString(hash).ToLowerInvariant() + ".audio");

        if (File.Exists(path) && new FileInfo(path).Length > 1024)
        {
            return path;
        }

        // 外部响应加体积与长度校验：拒绝超大/伪造长度的音频，避免无上限写盘。
        using var request = new HttpRequestMessage(HttpMethod.Get, safeUrl);
        using HttpResponseMessage response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (!MusicUrlPolicy.IsAllowedAudioContentType(response.Content.Headers.ContentType?.MediaType))
            throw new InvalidOperationException("音频地址返回了非音频内容");
        if (response.Content.Headers.ContentLength is long declared && declared > MaxAudioBytes)
            throw new InvalidOperationException("音频文件过大");

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaxAudioBytes) throw new InvalidOperationException("音频文件过大");
            buffer.Write(chunk, 0, read);
        }

        byte[] data = buffer.ToArray();
        if (data.Length < 1024)
        {
            throw new InvalidOperationException("下载到的音频为空");
        }
        // 再按文件头校验，避免把文本错误页当成音频缓存。
        if (LooksLikeText(data))
        {
            throw new InvalidOperationException("音频地址返回了非音频内容");
        }

        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, data, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
        return path;
    }

    private static bool LooksLikeText(byte[] data)
    {
        int probe = Math.Min(data.Length, 64);
        for (int i = 0; i < probe; i++)
        {
            byte b = data[i];
            if (b == 0) return false;
            if (b < 0x20 && b != (byte)'\r' && b != (byte)'\n' && b != (byte)'\t') return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Stop();
    }
}
