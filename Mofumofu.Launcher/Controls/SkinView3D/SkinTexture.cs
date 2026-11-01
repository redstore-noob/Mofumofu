using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Mofumofu.Launcher.Controls.SkinView3D;

internal sealed class SkinTextureData
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }
}

internal static class SkinTextureLoader
{
    private static readonly HttpClient Http = new();

    public static async Task<byte[]> ReadBytesAsync(string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("贴图地址不能为空。", nameof(source));

        string value = source.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = value.IndexOf(',');
            if (comma < 0) throw new FormatException("无效的 data URL。");
            return Convert.FromBase64String(value[(comma + 1)..]);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await Http.GetByteArrayAsync(uri, cancellationToken);
        }

        return await File.ReadAllBytesAsync(value, cancellationToken);
    }

    public static SkinTextureData DecodeSkin(byte[] png, out bool legacy)
    {
        SkinTextureData decoded = Decode(png);
        if (decoded.Width != 64 || decoded.Height is not (32 or 64))
            throw new InvalidDataException("Minecraft 皮肤必须是 64×64 或旧版 64×32 PNG。");

        legacy = decoded.Height == 32;
        return legacy ? ConvertLegacySkin(decoded) : decoded;
    }

    public static SkinTextureData DecodeCape(byte[] png)
    {
        SkinTextureData decoded = Decode(png);
        if (decoded.Width != decoded.Height * 2)
            throw new InvalidDataException("披风贴图必须使用 2:1 尺寸，例如 64×32。");
        return decoded;
    }

    public static SkinTextureData DecodeNameTag(Bitmap bitmap) => DecodeBitmap(bitmap);

    public static bool DetectSlim(SkinTextureData skin)
    {
        if (skin.Width != 64 || skin.Height != 64) return false;

        // slim 手臂比 classic 窄一格，这些 UV 区域在标准 slim 皮肤中保持透明。
        return IsTransparent(skin, 50, 16, 2, 4) &&
               IsTransparent(skin, 54, 20, 2, 12) &&
               IsTransparent(skin, 42, 48, 2, 4) &&
               IsTransparent(skin, 46, 52, 2, 12);
    }

    private static SkinTextureData Decode(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        using var bitmap = new Bitmap(stream);
        return DecodeBitmap(bitmap);
    }

    private static SkinTextureData DecodeBitmap(Bitmap bitmap)
    {
        using var converted = new WriteableBitmap(
            bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var framebuffer = converted.Lock())
            bitmap.CopyPixels(framebuffer);
        using var pixels = converted.Lock();
        return ReadFramebuffer(pixels);
    }

    private static SkinTextureData ReadFramebuffer(ILockedFramebuffer framebuffer)
    {
        int width = framebuffer.Size.Width;
        int height = framebuffer.Size.Height;
        int bytesPerPixel = framebuffer.Format.BitsPerPixel / 8;
        if (bytesPerPixel != 4)
            throw new NotSupportedException($"暂不支持 {framebuffer.Format} 像素格式。");

        byte[] result = new byte[width * height * 4];
        byte[] row = new byte[framebuffer.RowBytes];
        bool bgra = framebuffer.Format == PixelFormat.Bgra8888;
        bool rgba = framebuffer.Format == PixelFormat.Rgba8888;
        if (!bgra && !rgba)
            throw new NotSupportedException($"暂不支持 {framebuffer.Format} 像素格式。");

        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(framebuffer.Address + y * framebuffer.RowBytes, row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                int source = x * 4;
                int target = (y * width + x) * 4;
                result[target] = bgra ? row[source + 2] : row[source];
                result[target + 1] = row[source + 1];
                result[target + 2] = bgra ? row[source] : row[source + 2];
                result[target + 3] = row[source + 3];
            }
        }

        return new SkinTextureData { Width = width, Height = height, Rgba = result };
    }

    private static bool IsTransparent(SkinTextureData skin, int x, int y, int width, int height)
    {
        for (int py = y; py < y + height; py++)
        for (int px = x; px < x + width; px++)
            if (skin.Rgba[(py * skin.Width + px) * 4 + 3] != 0)
                return false;
        return true;
    }

    private static SkinTextureData ConvertLegacySkin(SkinTextureData legacy)
    {
        byte[] rgba = new byte[64 * 64 * 4];
        Array.Copy(legacy.Rgba, 0, rgba, 0, 64 * 32 * 4);
        var modern = new SkinTextureData { Width = 64, Height = 64, Rgba = rgba };

        // 旧格式没有左手/左腿，将右侧六个面水平镜像到新版对应 UV。
        MirrorBox(legacy, modern, 0, 16, 4, 12, 4, 16, 48);
        MirrorBox(legacy, modern, 40, 16, 4, 12, 4, 32, 48);
        return modern;
    }

    private static void MirrorBox(
        SkinTextureData source, SkinTextureData target,
        int oldU, int oldV, int width, int height, int depth,
        int newU, int newV)
    {
        CopyMirrored(source, target, oldU + depth, oldV, width, depth,
            newU + depth, newV);
        CopyMirrored(source, target, oldU + depth + width, oldV, width, depth,
            newU + depth + width, newV);
        CopyMirrored(source, target, oldU, oldV + depth, depth, height,
            newU + depth + width, newV + depth);
        CopyMirrored(source, target, oldU + depth, oldV + depth, width, height,
            newU + depth, newV + depth);
        CopyMirrored(source, target, oldU + depth + width, oldV + depth, depth, height,
            newU, newV + depth);
        CopyMirrored(source, target, oldU + depth + width + depth, oldV + depth, width, height,
            newU + depth + width + depth, newV + depth);
    }

    private static void CopyMirrored(
        SkinTextureData source, SkinTextureData target,
        int sourceX, int sourceY, int width, int height,
        int targetX, int targetY)
    {
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int from = ((sourceY + y) * source.Width + sourceX + x) * 4;
            int to = ((targetY + y) * target.Width + targetX + width - 1 - x) * 4;
            Buffer.BlockCopy(source.Rgba, from, target.Rgba, to, 4);
        }
    }
}
