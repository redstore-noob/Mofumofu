using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Mofumofu.Launcher.Services;

/// <summary>
/// 账号皮肤大头（32×32）：从本地皮肤档案缓存（<c>skins/profiles/&lt;name&gt;.json</c>）里的
/// skin data URL 取贴图，合成 8×8 基础头部 + 40,8 帽子层后放大为 32×32。
/// 结果走内存 + 磁盘缓存；失败返回 <c>null</c>（调用方回退首字母占位）。
/// </summary>
public static class SkinHeadService
{
    private const int HeadPixels = 8;
    private const int OutputPixels = 32;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Bitmap> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string SkinRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher", "skins");

    /// <summary>账号皮肤档案缓存路径（与皮肤解析服务共用同一份缓存文件）。</summary>
    internal static string ProfileCachePath(string accountName) =>
        Path.Combine(SkinRoot, "profiles", Sanitize(accountName) + ".json");

    /// <summary>大头磁盘缓存路径。</summary>
    internal static string HeadCachePath(string accountName) =>
        Path.Combine(SkinRoot, "heads", Sanitize(accountName) + ".png");

    /// <summary>
    /// 取账号大头（32×32）。读不到皮肤档案缓存时返回 <c>null</c>；可在任意线程调用。
    /// 返回的 <see cref="Bitmap"/> 由服务内部缓存复用，调用方<b>不要</b> Dispose。
    /// </summary>
    public static async Task<Bitmap?> LoadHeadAsync(
        string accountName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;

        var key = accountName.Trim();
        lock (Gate)
        {
            if (MemoryCache.TryGetValue(key, out var cached)) return cached;
        }

        var profilePath = ProfileCachePath(key);
        if (!File.Exists(profilePath)) return null;

        var headPath = HeadCachePath(key);

        Bitmap? head;
        try
        {
            head = IsHeadFresh(headPath, profilePath)
                ? await Task.Run(() => TryLoadBitmap(headPath), cancellationToken).ConfigureAwait(false)
                : null;

            if (head is null)
            {
                var skinPng = await Task.Run(() => ReadSkinPng(profilePath), cancellationToken)
                    .ConfigureAwait(false);
                if (skinPng is null) return null;

                head = await ComposeAsync(skinPng, cancellationToken).ConfigureAwait(false);
                if (head is null) return null;

                await Task.Run(() => TryWriteBitmap(head!, headPath), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        lock (Gate)
        {
            MemoryCache[key] = head;
        }

        return head;
    }

    /// <summary>皮肤变化后清掉内存与磁盘大头缓存，下次调用重新合成。</summary>
    public static void Invalidate(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return;

        var key = accountName.Trim();
        lock (Gate)
        {
            MemoryCache.Remove(key);
        }

        try
        {
            var headPath = HeadCachePath(key);
            if (File.Exists(headPath)) File.Delete(headPath);
        }
        catch (Exception)
        {
            // 删除缓存失败不影响使用
        }
    }

    // ---------------- 合成 ----------------

    private static async Task<Bitmap?> ComposeAsync(byte[] skinPng, CancellationToken cancellationToken)
    {
        try
        {
            return ComposeHead(skinPng);
        }
        catch (Exception)
        {
            // 个别渲染后端不允许后台线程离屏渲染时，退回 UI 线程再试一次
        }

        if (Dispatcher.UIThread.CheckAccess()) return null;

        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => ComposeHead(skinPng));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 用一张皮肤 PNG 合成 32×32 头像（基础头部 + 帽子层，支持双层皮肤）。
    /// 无缓存，供账号列表等一次性展示使用；渲染需在 UI 线程调用。
    /// </summary>
    public static Bitmap? ComposeHead(byte[] skinPng)
    {
        using var stream = new MemoryStream(skinPng);
        using var skin = new Bitmap(stream);
        var size = skin.PixelSize;
        if (size.Width < 16 || size.Height < 16) return null;

        var target = new RenderTargetBitmap(
            new PixelSize(OutputPixels, OutputPixels), new Vector(96, 96));

        using (var context = target.CreateDrawingContext())
        {
            var options = new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None };
            using (context.PushRenderOptions(options))
            {
                // 基础头部
                context.DrawImage(
                    new CroppedBitmap(skin, new PixelRect(8, 8, HeadPixels, HeadPixels)),
                    new Rect(0, 0, OutputPixels, OutputPixels));

                // 帽子层（64×64 贴图才有；透明像素叠加不影响基础层）
                if (size.Width >= 48 && size.Height >= 16)
                {
                    context.DrawImage(
                        new CroppedBitmap(skin, new PixelRect(40, 8, HeadPixels, HeadPixels)),
                        new Rect(0, 0, OutputPixels, OutputPixels));
                }
            }
        }

        return target;
    }

    // ---------------- 缓存 IO ----------------

    private static bool IsHeadFresh(string headPath, string profilePath)
    {
        try
        {
            if (!File.Exists(headPath)) return false;

            return File.GetLastWriteTimeUtc(headPath) >= File.GetLastWriteTimeUtc(profilePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Bitmap? TryLoadBitmap(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryWriteBitmap(Bitmap bitmap, string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            bitmap.Save(temp, PngBitmapEncoderOptions.Default);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // 磁盘缓存失败不影响使用
        }
    }

    private static byte[]? ReadSkinPng(string profilePath)
    {
        try
        {
            using var stream = File.OpenRead(profilePath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Skin", out var skin)) return null;
            if (skin.ValueKind != JsonValueKind.String) return null;

            var dataUrl = skin.GetString();
            if (string.IsNullOrWhiteSpace(dataUrl)) return null;

            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;

            var payload = dataUrl[(comma + 1)..];
            if (payload.Length == 0) return null;

            return Convert.FromBase64String(payload);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "account";

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        var result = builder.ToString().Trim('_');

        return result.Length == 0 ? "account" : result;
    }
}
