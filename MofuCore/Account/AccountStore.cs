using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MofuCore.Account;

/// <summary>
/// 账号凭据的统一落盘入口：Microsoft / Authlib 账号与 Yggdrasil clientToken
/// 统一加密保存在 <c>accounts.token</c>，登录流程不再直接处理文件。
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // 保险库是「读-改-写」整体，跨实例共享同一文件，必须串行化避免并发互相覆盖
    private static readonly object VaultGate = new();

    private readonly MinecraftTokenCrypto _crypto;

    public AccountStore(MinecraftTokenCrypto crypto)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
    }

    public MinecraftTokenCrypto Crypto => _crypto;

    /// <summary>保存/更新一个正版账号（按 UUID 去重，新的排在最前）。</summary>
    public void SaveMicrosoftAccount(MicrosoftAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (VaultGate)
        {
            Vault vault = LoadVault();
            vault.Microsoft.RemoveAll(item => string.Equals(item.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));
            vault.Microsoft.Insert(0, account.Clone());
            SaveVault(vault);
        }
    }

    /// <summary>读取全部已保存的正版账号。</summary>
    public IReadOnlyList<MicrosoftAccount> LoadMicrosoftAccounts()
    {
        lock (VaultGate) return LoadVault().Microsoft;
    }

    /// <summary>删除一个正版账号（按 UUID）。</summary>
    public bool RemoveMicrosoftAccount(string uuid)
    {
        lock (VaultGate)
        {
            Vault vault = LoadVault();
            int removed = vault.Microsoft.RemoveAll(item =>
                string.Equals(item.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveVault(vault);
            }

            return removed > 0;
        }
    }

    /// <summary>保存/更新一个皮肤站账号（按 UUID + API 根去重）。</summary>
    public void SaveAuthlibCredential(AuthlibCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (VaultGate)
        {
            Vault vault = LoadVault();
            vault.Authlib.RemoveAll(item =>
                string.Equals(item.ProfileUuid, credential.ProfileUuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ApiRoot.TrimEnd('/'), credential.ApiRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            vault.Authlib.Insert(0, credential.Clone());
            SaveVault(vault);
        }
    }

    /// <summary>读取全部已保存的皮肤站凭据。</summary>
    public IReadOnlyList<AuthlibCredential> LoadAuthlibCredentials()
    {
        lock (VaultGate) return LoadVault().Authlib;
    }

    /// <summary>保存一个离线账号名（按名称去重，新账号排在最前）。</summary>
    public void SaveOfflineAccount(string username)
    {
        string name = (username ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("离线账号名不能为空。", nameof(username));
        }

        lock (VaultGate)
        {
            Vault vault = LoadVault();
            if (vault.Offline.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            vault.Offline.Insert(0, name);
            SaveVault(vault);
        }
    }

    /// <summary>读取全部离线账号名。</summary>
    public IReadOnlyList<string> LoadOfflineAccounts()
    {
        lock (VaultGate) return LoadVault().Offline;
    }

    /// <summary>删除一个离线账号名。</summary>
    public bool RemoveOfflineAccount(string username)
    {
        string name = (username ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        lock (VaultGate)
        {
            Vault vault = LoadVault();
            int removed = vault.Offline.RemoveAll(item =>
                string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveVault(vault);
            }

            return removed > 0;
        }
    }

    /// <summary>本安装固定的 Yggdrasil clientToken（首次调用时生成并落盘）。</summary>
    public string GetOrCreateClientToken()
    {
        lock (VaultGate)
        {
            Vault vault = LoadVault();
            if (!string.IsNullOrWhiteSpace(vault.ClientToken))
            {
                return vault.ClientToken;
            }

            vault.ClientToken = Guid.NewGuid().ToString("D");
            SaveVault(vault);
            return vault.ClientToken;
        }
    }

    private Vault LoadVault()
    {
        string path = VaultPath();
        if (!File.Exists(path))
        {
            return new Vault();
        }

        string stored;
        try
        {
            stored = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 文件存在却读不出来：绝不能当空库返回，否则下一次 Save 会覆盖并永久丢账号
            throw new MicrosoftAuthenticationException("账号令牌文件读取失败，已中止以避免覆盖：" + e.Message);
        }

        if (string.IsNullOrWhiteSpace(stored))
        {
            return new Vault();
        }

        if (_crypto.TryDecrypt(stored, out string json))
        {
            return DeserializeVault(json);
        }

        // 解不开：fail-closed，绝不覆盖现有文件
        throw new MicrosoftAuthenticationException("账号令牌文件无法解密或已损坏，已中止以避免覆盖。请检查加密密钥或从备份恢复。");
    }

    private static Vault DeserializeVault(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Vault>(json, JsonOptions) ?? new Vault();
        }
        catch (JsonException e)
        {
            throw new MicrosoftAuthenticationException("账号令牌文件内容损坏，已中止以避免覆盖：" + e.Message);
        }
    }

    private void SaveVault(Vault vault)
    {
        string path = VaultPath();
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(vault, JsonOptions);
            string stored = _crypto.Encrypt(json);
            if (string.IsNullOrEmpty(stored))
            {
                // fail-closed：加密不可用时绝不明文写回（泄密），直接报错保住现有密文库
                throw new MicrosoftAuthenticationException("账号令牌加密失败，已中止写入以避免明文泄露。");
            }

            // 唯一临时名：避免多进程/多线程并发时固定 .tmp 互相覆盖
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, stored, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MicrosoftAuthenticationException("账号令牌写入磁盘失败：" + e.Message);
        }
    }

    private string VaultPath()
    {
        return Path.Combine(_crypto.StorageDirectory, "accounts.token");
    }

    private sealed class Vault
    {
        [JsonPropertyName("Microsoft")] public List<MicrosoftAccount> Microsoft { get; set; } = [];
        [JsonPropertyName("Authlib")] public List<AuthlibCredential> Authlib { get; set; } = [];
        [JsonPropertyName("Offline")] public List<string> Offline { get; set; } = [];
        [JsonPropertyName("ClientToken")] public string ClientToken { get; set; } = string.Empty;
    }
}
