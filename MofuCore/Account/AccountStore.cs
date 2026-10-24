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
        Vault vault = LoadVault();
        vault.Microsoft.RemoveAll(item => string.Equals(item.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));
        vault.Microsoft.Insert(0, account.Clone());
        SaveVault(vault);
    }

    /// <summary>读取全部已保存的正版账号。</summary>
    public IReadOnlyList<MicrosoftAccount> LoadMicrosoftAccounts()
    {
        return LoadVault().Microsoft;
    }

    /// <summary>删除一个正版账号（按 UUID）。</summary>
    public bool RemoveMicrosoftAccount(string uuid)
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

    /// <summary>保存/更新一个皮肤站账号（按 UUID + API 根去重）。</summary>
    public void SaveAuthlibCredential(AuthlibCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Vault vault = LoadVault();
        vault.Authlib.RemoveAll(item =>
            string.Equals(item.ProfileUuid, credential.ProfileUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ApiRoot.TrimEnd('/'), credential.ApiRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        vault.Authlib.Insert(0, credential.Clone());
        SaveVault(vault);
    }

    /// <summary>读取全部已保存的皮肤站凭据。</summary>
    public IReadOnlyList<AuthlibCredential> LoadAuthlibCredentials()
    {
        return LoadVault().Authlib;
    }

    /// <summary>保存一个离线账号名（按名称去重，新账号排在最前）。</summary>
    public void SaveOfflineAccount(string username)
    {
        string name = (username ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("离线账号名不能为空。", nameof(username));
        }

        Vault vault = LoadVault();
        if (vault.Offline.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        vault.Offline.Insert(0, name);
        SaveVault(vault);
    }

    /// <summary>读取全部离线账号名。</summary>
    public IReadOnlyList<string> LoadOfflineAccounts()
    {
        return LoadVault().Offline;
    }

    /// <summary>删除一个离线账号名。</summary>
    public bool RemoveOfflineAccount(string username)
    {
        string name = (username ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        Vault vault = LoadVault();
        int removed = vault.Offline.RemoveAll(item =>
            string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SaveVault(vault);
        }

        return removed > 0;
    }

    /// <summary>把旧版 LocalSettings 里的离线账号一次性导入保险库。</summary>
    public void ImportOfflineAccounts(IEnumerable<string> usernames)
    {
        ArgumentNullException.ThrowIfNull(usernames);
        Vault vault = LoadVault();
        bool changed = false;
        foreach (string raw in usernames)
        {
            string name = (raw ?? string.Empty).Trim();
            if (name.Length == 0 || vault.Offline.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            vault.Offline.Add(name);
            changed = true;
        }

        if (changed)
        {
            SaveVault(vault);
        }
    }

    /// <summary>本安装固定的 Yggdrasil clientToken（首次调用时生成并落盘）。</summary>
    public string GetOrCreateClientToken()
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

    private Vault LoadVault()
    {
        string path = VaultPath();
        try
        {
            if (!File.Exists(path))
            {
                return new Vault();
            }

            string stored = File.ReadAllText(path, Encoding.UTF8);
            string json = _crypto.Decrypt(stored);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Vault();
            }

            return JsonSerializer.Deserialize<Vault>(json, JsonOptions) ?? new Vault();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Vault();
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
                // 加密不可用时宁可明文也不能丢账号
                stored = json;
            }

            string temporaryPath = path + ".tmp";
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
