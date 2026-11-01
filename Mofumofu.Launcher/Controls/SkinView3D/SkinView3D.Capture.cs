using System;
using System.Diagnostics;
using System.IO;
using Silk.NET.OpenGL;
using SilkPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace Mofumofu.Launcher.Controls.SkinView3D;

/// <summary>
/// 验收用：设置 MOFU_SKIN_CAPTURE=输出 BMP 路径 后，查看器会在第 N 帧
/// （MOFU_SKIN_CAPTURE_FRAME，默认第 4 帧）把当前帧从 OpenGL 帧缓冲读回并写成 24 位 BMP，
/// 用于没有人工目视时的渲染回归。未设置环境变量时整条通路不会进入。
/// </summary>
public partial class SkinView3D
{
    private static readonly string? CapturePath =
        Environment.GetEnvironmentVariable("MOFU_SKIN_CAPTURE");

    private static readonly int CaptureFrame = ReadCaptureFrame();

    private int _renderedFrames;
    private bool _captureDone;

    private static int ReadCaptureFrame() =>
        int.TryParse(Environment.GetEnvironmentVariable("MOFU_SKIN_CAPTURE_FRAME"), out int parsed) && parsed > 0
            ? parsed
            : 4;

    private void TryCapture(GL api, int framebuffer, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(CapturePath) || _captureDone) return;
        if (++_renderedFrames < CaptureFrame) return;
        _captureDone = true;

        byte[] pixels = new byte[width * height * 4];
        api.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)framebuffer);
        api.ReadBuffer(ReadBufferMode.ColorAttachment0);
        api.PixelStore(PixelStoreParameter.PackAlignment, 1);
        api.ReadPixels(0, 0, (uint)width, (uint)height, SilkPixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        try
        {
            WriteBmp(CapturePath!, pixels, width, height);
        }
        catch (Exception error)
        {
            Trace.TraceError("[SkinView3D] 抓帧写盘失败：" + error);
        }
    }

    /// <summary>把 RGBA 像素写成 24 位 BMP。OpenGL 回读的行序已经是自上而下。</summary>
    private static void WriteBmp(string path, byte[] rgba, int width, int height)
    {
        int rowSize = (width * 3 + 3) & ~3;
        int pixelBytes = rowSize * height;
        var header = new byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        WriteInt(header, 2, 54 + pixelBytes);
        WriteInt(header, 10, 54);
        WriteInt(header, 14, 40);
        WriteInt(header, 18, width);
        WriteInt(header, 22, height);
        header[26] = 1;
        header[28] = 24;
        WriteInt(header, 34, pixelBytes);

        var rows = new byte[pixelBytes];
        for (int y = 0; y < height; y++)
        {
            int source = y * width * 4;
            int target = y * rowSize;
            for (int x = 0; x < width; x++)
            {
                int s = source + x * 4;
                int t = target + x * 3;
                rows[t] = rgba[s + 2];
                rows[t + 1] = rgba[s + 1];
                rows[t + 2] = rgba[s];
            }
        }

        using var stream = File.Create(path);
        stream.Write(header);
        stream.Write(rows);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
