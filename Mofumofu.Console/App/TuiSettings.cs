using System.Text.Json;
using System.Text.Json.Serialization;
using MofuCore.Config;
using MofuCore.Download.Minecraft;

namespace Mofumofu.Console;

/// <summary>TUI 自己的轻量设置，仅保存界面状态与启动偏好，其余复用 MofuCore 配置。</summary>
public sealed class TuiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    [JsonPropertyName("selectedPage")] public string SelectedPage { get; set; } = "home";
    [JsonPropertyName("selectedInstance")] public string SelectedInstance { get; set; } = string.Empty;
    [JsonPropertyName("selectedAccount")] public string SelectedAccount { get; set; } = string.Empty;
    [JsonPropertyName("minimumMemoryMb")] public int MinimumMemoryMb { get; set; } = 1024;
    [JsonPropertyName("maximumMemoryMb")] public int MaximumMemoryMb { get; set; } = 4096;
    [JsonPropertyName("downloadSource")] public string DownloadSource { get; set; } = "BMCL";
    [JsonPropertyName("terracottaBinary")] public string TerracottaBinary { get; set; } = string.Empty;
    [JsonPropertyName("modrinthType")] public string ModrinthType { get; set; } = "mod";
    [JsonPropertyName("verifyBeforeLaunch")] public bool VerifyBeforeLaunch { get; set; }

    [JsonIgnore]
    public static string FilePath => Path.Combine(YamlRead.ConfigDirectory, "tui.json");

    public static TuiSettings Load()
    {
        TuiSettings settings = new();
        try
        {
            if (File.Exists(FilePath))
            {
                settings = JsonSerializer.Deserialize<TuiSettings>(File.ReadAllText(FilePath), JsonOptions) ?? settings;
            }
        }
        catch (Exception)
        {
            // 设置损坏时回退默认值，不阻塞启动
        }

        // 首次运行与 GUI 的全局内存设置对齐
        try
        {
            GlobalLaunchSettings launch = YamlRead.LoadGlobalLaunchSettings();
            if (settings.MaximumMemoryMb <= 0 || settings.MaximumMemoryMb == 4096)
            {
                settings.MaximumMemoryMb = launch.MaximumMemoryMb;
                settings.MinimumMemoryMb = launch.MinimumMemoryMb;
            }
        }
        catch (Exception)
        {
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 设置保存失败不影响使用
        }
    }

    public DownloadSource ResolveDownloadSource()
    {
        return string.Equals(DownloadSource, "Official", StringComparison.OrdinalIgnoreCase)
            ? DownloadSources.Official
            : DownloadSources.Bmcl;
    }

    public void ApplyDownloadSource()
    {
        DownloadSourceProvider.SetActive(ResolveDownloadSource());
    }
}
