using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoSolo.Services;

/// <summary>
/// NekoSolo 安装包清单（manifest.json，格式见 NekoLauncher/NekoSolo/FORMAT.md）。
/// 字段名与上一代导出器保持一致，v1/v2/v3 都按同一份读取。
/// </summary>
public sealed class SoloManifest
{
    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("packId")] public string PackId { get; set; } = string.Empty;
    [JsonPropertyName("packName")] public string PackName { get; set; } = string.Empty;
    [JsonPropertyName("packVersion")] public string PackVersion { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("mcVersion")] public string McVersion { get; set; } = string.Empty;
    [JsonPropertyName("loaderName")] public string LoaderName { get; set; } = string.Empty;
    [JsonPropertyName("loaderVersion")] public string LoaderVersion { get; set; } = string.Empty;
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = string.Empty;
    [JsonPropertyName("simpleMode")] public bool SimpleMode { get; set; }
    [JsonPropertyName("hasJava")] public bool HasJava { get; set; }
    [JsonPropertyName("iconPath")] public string IconPath { get; set; } = string.Empty;
    [JsonPropertyName("silent")] public bool Silent { get; set; }
    [JsonPropertyName("updateLink")] public string UpdateLink { get; set; } = string.Empty;

    /// <summary>载荷里是否有随包 JRE（决定安装完成后是否还需要补 Java）。</summary>
    [JsonIgnore] public bool BundledJava => HasJava;

    /// <summary>加载器展示名：Forge 47.2.0 / Fabric 0.15.11 …</summary>
    [JsonIgnore]
    public string LoaderDisplay => string.IsNullOrWhiteSpace(LoaderName)
        ? "Vanilla"
        : string.IsNullOrWhiteSpace(LoaderVersion) ? LoaderName : $"{LoaderName} {LoaderVersion}";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(PackName)
        ? (string.IsNullOrWhiteSpace(PackId) ? "NekoSolo" : PackId)
        : PackName;

    [JsonIgnore] public string VersionDisplay => string.IsNullOrWhiteSpace(PackVersion) ? "1.0.0" : PackVersion;

    [JsonIgnore]
    public string McDisplay => string.IsNullOrWhiteSpace(McVersion) ? "—" : McVersion;

    [JsonIgnore]
    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author) ? SoloStrings.T("Welcome.Unknown") : Author;

    public static SoloManifest FromJson(string json)
    {
        SoloManifest? manifest = JsonSerializer.Deserialize<SoloManifest>(json, JsonOptions);
        if (manifest is null) throw new InvalidOperationException("manifest.json 解析结果为空。");
        return manifest;
    }

    public static async Task<SoloManifest> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return FromJson(json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
