using System;
using System.IO;
using System.Text.Json;
using MofuCore.Config;
using MofuCore.Log;

namespace Mofumofu.Launcher.Services;

internal static class SoloInstallation
{
    public static string? Initialize(string? directory = null)
    {
        string root = directory ?? AppContext.BaseDirectory;
        string config = Path.Combine(root, "solo.json");
        if (!File.Exists(config)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(config));
            // 先完成全部解析与校验（只读、无副作用），避免异常发生后留下半生效的全局配置。
            string? versionId = null;
            if (document.RootElement.TryGetProperty("VersionId", out JsonElement version) &&
                version.ValueKind == JsonValueKind.String &&
                version.GetString() is { Length: > 0 } id && id is not "." and not ".." &&
                id.IndexOfAny(['/', '\\', ':']) < 0 &&
                File.Exists(Path.Combine(root, ".minecraft", "versions", id, id + ".json")))
                versionId = id;

            // 解析/校验成功后才应用绑定安装目录的全局配置副作用，保持与旧行为一致。
            YamlRead.DataRoot = root;
            YamlRead.SaveGameDirectory(Path.Combine(root, ".minecraft"));
            string java = Path.Combine(root, ".minecraft", "runtime", "jre", "bin", "java.exe");
            if (File.Exists(java)) YamlRead.SaveJava(java, "bundled");
            return versionId;
        }
        catch (Exception ex)
        {
            LogWrite.Write("ERROR", "[SoloInstallation] solo.json 解析/绑定失败，未应用任何全局配置：" + ex.Message);
            return null;
        }
    }
}
