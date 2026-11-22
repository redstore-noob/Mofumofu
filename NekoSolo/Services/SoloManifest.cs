using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoSolo.Services;

/// <summary>
/// NekoSolo 安装包清单（manifest.json，格式见 NekoSolo/FORMAT.md）。
/// 字段名与上一代导出器保持一致，v1/v2/v3 共用同一份读取。
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

    /// <summary>决定安装完成后是否还需要补 Java。</summary>
    [JsonIgnore] public bool BundledJava => HasJava;

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

        // 清单值会用于安装路径、zip 条目名、solo.json 与换行分隔的 .nekosolo-installed，
        // 所以反序列化后逐字段校验，拒绝路径分隔符、..、控制字符（含换行，防 marker 注入）与超长值，
        // 避免目录穿越/注入（fail-closed）。
        manifest.PackId = ValidateIdentifier(manifest.PackId, nameof(SoloManifest.PackId));
        manifest.PackName = ValidateIdentifier(manifest.PackName, nameof(SoloManifest.PackName));
        manifest.PackVersion = ValidateIdentifier(manifest.PackVersion, nameof(SoloManifest.PackVersion));
        manifest.Author = ValidateIdentifier(manifest.Author, nameof(SoloManifest.Author));
        manifest.McVersion = ValidateIdentifier(manifest.McVersion, nameof(SoloManifest.McVersion));
        manifest.LoaderName = ValidateIdentifier(manifest.LoaderName, nameof(SoloManifest.LoaderName));
        manifest.LoaderVersion = ValidateIdentifier(manifest.LoaderVersion, nameof(SoloManifest.LoaderVersion));
        manifest.VersionId = ValidateIdentifier(manifest.VersionId, nameof(SoloManifest.VersionId));
        manifest.IconPath = ValidateZipPath(manifest.IconPath, nameof(SoloManifest.IconPath));
        manifest.UpdateLink = ValidateText(manifest.UpdateLink, nameof(SoloManifest.UpdateLink), allowNewlines: false);
        manifest.Description = ValidateText(manifest.Description, nameof(SoloManifest.Description), allowNewlines: true);
        return manifest;
    }

    private const int MaxIdentifierLength = 256;
    private const int MaxTextLength = 4096;

    /// <summary>会拼进安装路径、文件名或 marker 行，校验最严格。</summary>
    private static string ValidateIdentifier(string? value, string field)
    {
        string text = value ?? string.Empty;
        if (text.Length > MaxIdentifierLength) throw Invalid(field, "长度超过上限");
        foreach (char ch in text)
        {
            if (ch is '/' or '\\' || ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
                throw Invalid(field, "含路径分隔符");
            if (char.IsControl(ch)) throw Invalid(field, "含控制字符");
        }

        if (text.Contains("..", StringComparison.Ordinal)) throw Invalid(field, "含 .. 路径片段");
        return text;
    }

    /// <summary>IconPath 是 zip 内的条目名：允许 / 子路径，但拒绝 .. 段、反斜杠、绝对路径与控制字符。</summary>
    private static string ValidateZipPath(string? value, string field)
    {
        string text = value ?? string.Empty;
        if (text.Length > MaxIdentifierLength) throw Invalid(field, "长度超过上限");
        if (text.Contains('\\')) throw Invalid(field, "含反斜杠");
        if (text.StartsWith('/')) throw Invalid(field, "是绝对路径");
        foreach (string part in text.Split('/'))
        {
            if (part is ".." or ".") throw Invalid(field, "含 .. 路径片段");
        }

        foreach (char ch in text)
        {
            if (char.IsControl(ch)) throw Invalid(field, "含控制字符");
        }

        return text;
    }

    /// <summary>URL / 描述等自由文本：允许常见标点（含 https://），仍拒绝控制字符与超长值。</summary>
    private static string ValidateText(string? value, string field, bool allowNewlines)
    {
        string text = value ?? string.Empty;
        if (text.Length > MaxTextLength) throw Invalid(field, "长度超过上限");
        foreach (char ch in text)
        {
            if (char.IsControl(ch) && !(allowNewlines && ch is '\n' or '\r' or '\t'))
                throw Invalid(field, "含控制字符");
        }

        return text;
    }

    private static InvalidOperationException Invalid(string field, string reason) =>
        new($"manifest.json 字段 {field} 非法：{reason}。");

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
