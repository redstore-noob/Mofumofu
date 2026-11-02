using System;
using System.IO;
using System.Text.Json;
using MofuCore.Config;

namespace Mofumofu.Launcher.Services;

internal static class SoloInstallation
{
    /// <summary>在加载本地设置前将安装包绑定到随包游戏目录，移动安装目录后仍可使用。</summary>
    public static string? Initialize(string? directory = null)
    {
        string root = directory ?? AppContext.BaseDirectory;
        string config = Path.Combine(root, "solo.json");
        if (!File.Exists(config)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(config));
            YamlRead.DataRoot = root;
            YamlRead.SaveGameDirectory(Path.Combine(root, ".minecraft"));
            string java = Path.Combine(root, ".minecraft", "runtime", "jre", "bin", "java.exe");
            if (File.Exists(java)) YamlRead.SaveJava(java, "bundled");
            if (document.RootElement.TryGetProperty("VersionId", out JsonElement version) &&
                version.GetString() is { Length: > 0 } id && id is not "." and not ".." &&
                id.IndexOfAny(['/', '\\', ':']) < 0 &&
                File.Exists(Path.Combine(root, ".minecraft", "versions", id, id + ".json")))
                return id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("NekoSolo config: " + ex.Message);
        }
        return null;
    }
}
