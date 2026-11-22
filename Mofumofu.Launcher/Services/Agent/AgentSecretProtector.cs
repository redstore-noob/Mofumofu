using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mofumofu.Launcher.Services.Agent;

public static class AgentSecretProtector
{
    private const string AesPrefix = "aesgcm:v1:";
    private const string KeyDpapiPrefix = "dpapi:v1:";
    private const string KeyRawPrefix = "raw:v1:";

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Mofumofu.Launcher/MofuAgent/Secret/v1");

    private static readonly byte[] Aad =
        Encoding.UTF8.GetBytes("Mofumofu.MofuAgent.Secret.v1");

    private static readonly object KeyLock = new();
    private static byte[]? _key;
    private static string? _keyError;

    /// <summary>密文解不开（换账户 / 换机器 / 主密钥丢失 / 配置损坏），不要覆盖原值。</summary>
    public static bool DecryptFailed { get; private set; }

    public static string KeyPath => Path.Combine(KeyDirectory, "secret.key");

    public static string KeyDirectory
    {
        get
        {
            // 测试与便携部署可以覆盖密钥目录，避免测试轮换密钥时碰到真实用户的 Key。
            string? overrideDirectory = Environment.GetEnvironmentVariable("MOFU_AGENT_KEY_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDirectory)) return overrideDirectory;

            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Mofumofu", "MofuAgent");
            }

            if (OperatingSystem.IsMacOS())
            {
                string home = ResolveHome();
                return Path.Combine(home, "Library", "Application Support", "Mofumofu", "MofuAgent");
            }

            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(ResolveHome(), ".local", "share")
                : xdg;
            return Path.Combine(root, "Mofumofu", "MofuAgent");
        }
    }

    public static void ResetFlags()
    {
        DecryptFailed = false;
    }

    public static void ResetKey()
    {
        lock (KeyLock)
        {
            _key = null;
            _keyError = null;

            try
            {
                if (File.Exists(KeyPath)) File.Delete(KeyPath);
                string temp = KeyPath + ".tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (LooksEncrypted(plaintext)) return plaintext;

        byte[] key = GetKeyOrThrow();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Aad);
        }

        byte[] payload = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, payload, NonceSize + TagSize, cipher.Length);

        return AesPrefix + Convert.ToBase64String(payload);
    }

    public static bool LooksEncrypted(string? value) =>
        value is not null && value.StartsWith(AesPrefix, StringComparison.Ordinal);

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value.StartsWith(AesPrefix, StringComparison.Ordinal)
            ? UnprotectAes(value)
            : value;
    }

    public static bool TryUnprotect(string? value, out string plaintext)
    {
        plaintext = Unprotect(value);
        return !LooksEncrypted(value) || !ReferenceEquals(plaintext, value);
    }

    private static string UnprotectAes(string value)
    {
        try
        {
            byte[] key = GetKeyOrThrow();
            byte[] payload = Convert.FromBase64String(value[AesPrefix.Length..]);
            if (payload.Length < NonceSize + TagSize)
                throw new CryptographicException("密文长度不正确。");

            ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
            ReadOnlySpan<byte> tag = payload.AsSpan(NonceSize, TagSize);
            ReadOnlySpan<byte> cipher = payload.AsSpan(NonceSize + TagSize);
            byte[] plain = new byte[cipher.Length];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain, Aad);
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            DecryptFailed = true;
            return value;
        }
    }

    private static byte[] GetKeyOrThrow()
    {
        lock (KeyLock)
        {
            if (_key is not null) return _key;
            if (_keyError is not null) throw new CryptographicException(_keyError);

            try
            {
                Directory.CreateDirectory(KeyDirectory);
                EnsureDirectoryPermissions();

                if (File.Exists(KeyPath))
                {
                    string text = File.ReadAllText(KeyPath).Trim();
                    if (!TryDecodeKey(text, out byte[] existing, out string error))
                    {
                        _keyError = error;
                        throw new CryptographicException(error);
                    }

                    _key = existing;
                    EnsureKeyPermissions();
                    return existing;
                }

                byte[] created = RandomNumberGenerator.GetBytes(KeySize);
                WriteKey(created);
                _key = created;
                return created;
            }
            catch (CryptographicException)
            {
                throw;
            }
            catch (Exception error)
            {
                _keyError = "创建/读取主密钥失败：" + error.Message;
                throw new CryptographicException(_keyError, error);
            }
        }
    }

    private static bool TryDecodeKey(string text, out byte[] key, out string error)
    {
        key = Array.Empty<byte>();
        error = string.Empty;

        try
        {
            if (text.StartsWith(KeyDpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                {
                    error = "主密钥由 Windows DPAPI 保护，当前系统无法解开。";
                    return false;
                }

                byte[] blob = Convert.FromBase64String(text[KeyDpapiPrefix.Length..]);
                byte[] plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                if (plain.Length != KeySize)
                {
                    error = "主密钥长度不正确。";
                    return false;
                }

                key = plain;
                return true;
            }

            if (text.StartsWith(KeyRawPrefix, StringComparison.Ordinal))
            {
                // Windows 上主密钥必须由 DPAPI(CurrentUser) 保护；接受 raw:v1: 等于让
                // 能改写 secret.key 的进程绕过系统保护直接解出全部凭据，这里 fail-closed。
                if (OperatingSystem.IsWindows())
                {
                    error = "Windows 上不接受明文主密钥格式。";
                    return false;
                }

                byte[] plain = Convert.FromBase64String(text[KeyRawPrefix.Length..]);
                if (plain.Length != KeySize)
                {
                    error = "主密钥长度不正确。";
                    return false;
                }

                key = plain;
                return true;
            }

            error = "无法识别的主密钥格式。";
            return false;
        }
        catch (Exception exception)
        {
            error = "读取主密钥失败：" + exception.Message;
            return false;
        }
    }

    private static void WriteKey(byte[] key)
    {
        string content;
        if (OperatingSystem.IsWindows())
        {
            byte[] blob = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
            content = KeyDpapiPrefix + Convert.ToBase64String(blob);
        }
        else
        {
            content = KeyRawPrefix + Convert.ToBase64String(key);
        }

        // 先写临时文件再替换，避免磁盘满/中断把主密钥写成半截。
        string temp = KeyPath + ".tmp";
        File.WriteAllText(temp, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(temp, KeyPath, true);
        EnsureKeyPermissions();
    }

    private static void EnsureDirectoryPermissions()
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(KeyDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
        }
    }

    private static void EnsureKeyPermissions()
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
        }
    }

    private static string ResolveHome()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) return home;

        return Environment.GetEnvironmentVariable("HOME") ?? Environment.CurrentDirectory;
    }
}

/// <summary>
/// 把字符串字段在序列化时加密、反序列化时解密。
/// 只挂在真正需要保护的小字段上（ApiKey / ExtraHeaders），不影响其它设置的可读性。
/// </summary>
public sealed class AgentSecretJsonConverter : JsonConverter<string>
{
    public override string Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return string.Empty;
        if (reader.TokenType != JsonTokenType.String) return string.Empty;
        return AgentSecretProtector.Unprotect(reader.GetString());
    }

    public override void Write(
        Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(AgentSecretProtector.Protect(value));
    }
}
