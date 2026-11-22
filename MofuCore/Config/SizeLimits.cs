namespace MofuCore.Config;

// Package* 与 StreamCopyBufferBytes 被 NekoexInstaller、ServerNetworkGuard、CurseForgeBundleInstall 直接引用，口径需与 Bundle* 保持一致。
public static class SizeLimits
{
    public const long SegmentedDownloadMinimumBytes = 8L * 1024 * 1024;

    public const long LogFileMaximumBytes = 8L * 1024 * 1024;

    public const long ServerListPacketMaximumBytes = 8L * 1024 * 1024;

    public const long RewindMinimumBytes = 64L * 1024 * 1024;

    public const long BundleEntryMaximumBytes = 2L * 1024 * 1024 * 1024;

    public const long BundleExtractedMaximumBytes = 8L * 1024 * 1024 * 1024;

    public const long BundleIndexMaximumBytes = 8L * 1024 * 1024;

    public const long IconDownloadMaximumBytes = 16L * 1024 * 1024;

    public const long ScreenshotMaximumBytes = 64L * 1024 * 1024;

    public const long SkinTextureMaximumBytes = 4L * 1024 * 1024;

    public const int StreamCopyBufferBytes = 81920;

    public const long PackageManifestMaximumBytes = 1L * 1024 * 1024;

    public const long PackageArchiveMaximumBytes = 4L * 1024 * 1024 * 1024;

    public const long PackageEntryMaximumBytes = 2L * 1024 * 1024 * 1024;

    public const long PackageExtractedMaximumBytes = 8L * 1024 * 1024 * 1024;
}
