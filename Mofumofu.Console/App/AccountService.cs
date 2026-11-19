using MofuCore.Account;
using MofuCore.Launch;

namespace Mofumofu.Console;

/// <summary>账号列表中的一项（离线 / 微软 / 皮肤站）。</summary>
public sealed class AccountEntry
{
    public required string Kind { get; init; }
    public required string Key { get; init; }
    public required string Display { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string? OfflineName { get; init; }
    public MicrosoftAccount? Microsoft { get; init; }
    public AuthlibCredential? Authlib { get; init; }

    public override string ToString() => Display;
}

/// <summary>统一账号读取、登录结果落盘与启动账号解析。</summary>
public sealed class AccountService
{
    private readonly MinecraftTokenHandle _handle = new();
    private readonly AccountStore _store;
    private readonly List<AccountEntry> _entries = [];

    public AccountService()
    {
        _store = new AccountStore(_handle.Crypto);
        Reload();
    }

    public MinecraftTokenHandle Handle => _handle;

    public IReadOnlyList<AccountEntry> Entries => _entries;

    public void Reload()
    {
        _entries.Clear();
        foreach (MicrosoftAccount account in _store.LoadMicrosoftAccounts())
        {
            _entries.Add(new AccountEntry
            {
                Kind = "microsoft",
                Key = "microsoft:" + account.Uuid,
                Display = $"正版 · {account.Username}",
                Detail = string.IsNullOrWhiteSpace(account.ExpiresAt.ToString())
                    ? account.Uuid
                    : $"uuid {account.Uuid} · 令牌到期 {account.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                Microsoft = account,
            });
        }

        foreach (AuthlibCredential credential in _store.LoadAuthlibCredentials())
        {
            string server = string.IsNullOrWhiteSpace(credential.ServerName) ? credential.ApiRoot : credential.ServerName;
            _entries.Add(new AccountEntry
            {
                Kind = "authlib",
                Key = "authlib:" + credential.ProfileUuid + "@" + credential.ApiRoot.TrimEnd('/'),
                Display = $"皮肤站 · {credential.ProfileName} @ {server}",
                Detail = $"uuid {credential.ProfileUuid} · {credential.ApiRoot}",
                Authlib = credential,
            });
        }

        foreach (string name in _store.LoadOfflineAccounts())
        {
            _entries.Add(new AccountEntry
            {
                Kind = "offline",
                Key = "offline:" + name,
                Display = "离线 · " + name,
                Detail = "离线账号（仅本地，不验证密码）",
                OfflineName = name,
            });
        }
    }

    public AccountEntry AddOffline(string username)
    {
        OfflineAccount account = OfflineAccount.Create(username);
        _store.SaveOfflineAccount(account.Username);
        Reload();
        return _entries.First(entry => entry.Key == "offline:" + account.Username);
    }

    public void SaveMicrosoft(MicrosoftAccount account)
    {
        _store.SaveMicrosoftAccount(account);
        Reload();
    }

    public void SaveAuthlib(AuthlibCredential credential)
    {
        _store.SaveAuthlibCredential(credential);
        Reload();
    }

    public bool Remove(AccountEntry entry)
    {
        bool removed = entry.Kind switch
        {
            "microsoft" => _store.RemoveMicrosoftAccount(entry.Microsoft!.Uuid),
            "authlib" => RemoveAuthlib(entry.Authlib!),
            "offline" => _store.RemoveOfflineAccount(entry.OfflineName!),
            _ => false,
        };
        Reload();
        return removed;
    }

    private bool RemoveAuthlib(AuthlibCredential credential)
    {
        return _store.RemoveAuthlibCredential(credential.ProfileUuid, credential.ApiRoot);
    }

    /// <summary>把保存的账号解析成启动账号（微软令牌过期时自动刷新）。</summary>
    public async Task<IMinecraftAccount> ResolveAsync(string key, CancellationToken cancellationToken = default)
    {
        AccountEntry entry = _entries.FirstOrDefault(item => item.Key == key)
                             ?? throw new InvalidOperationException("找不到已选账号，请到「账号」页重新选择。");

        switch (entry.Kind)
        {
            case "offline":
                return OfflineAccount.Create(entry.OfflineName!);

            case "microsoft":
            {
                MicrosoftAccount account = entry.Microsoft!;
                if (account.IsExpired)
                {
                    account = await _handle.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
                    _store.SaveMicrosoftAccount(account);
                }

                return MicrosoftLaunchAccount.From(account);
            }

            case "authlib":
            {
                AuthlibCredential credential = entry.Authlib!;
                string token = await _handle.Authlib
                    .ValidateOrRefreshAsync(credential, _store.GetOrCreateClientToken(), cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(token, credential.AccessToken, StringComparison.Ordinal))
                {
                    credential.AccessToken = token;
                    _store.SaveAuthlibCredential(credential);
                }

                return new AuthlibLaunchAccount
                {
                    ProfileName = credential.ProfileName,
                    ProfileUuid = credential.ProfileUuid,
                    AccessToken = token,
                    ApiRoot = credential.ApiRoot,
                };
            }

            default:
                throw new InvalidOperationException("未知账号类型：" + entry.Kind);
        }
    }

    public MicrosoftAccount? FindMicrosoftByUuid(string uuid)
    {
        return _entries.FirstOrDefault(entry => entry.Kind == "microsoft" &&
                                                string.Equals(entry.Microsoft?.Uuid, uuid, StringComparison.OrdinalIgnoreCase))?.Microsoft;
    }
}
