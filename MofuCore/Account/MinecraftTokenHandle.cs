using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using MofuCore.Config;
namespace MofuCore.Account;

public class MinecraftTokenHandle
{
    private const int StreamCopyBufferSize = 1024;

    public const string DefaultClientId = "427f0a7c-9edd-40ba-a0ec-f189f8328418";
    public const string ClientIdEnvironmentVariable = "MOFUCORE_MSA_CLIENT_ID";
    private const string MicrosoftScope = "XboxLive.signin offline_access";

    private const string DeviceCodeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    private const string XboxLiveAuthenticateUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsAuthorizeUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private const int DeviceCodeTransientRetryLimit = 3;

    private static readonly TimeSpan BrowserLoginTimeout = TimeSpan.FromMinutes(10);

    private const int LoopbackListenRetryLimit = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 发往 Xbox Live 的请求必须保持 PascalCase（RelyingParty/Properties/RpsTicket）。
    /// .NET 的 <c>JsonContent.Create</c> 默认使用 Web 命名策略（camelCase），Xbox Live 会直接返回
    /// HTTP 400 且响应体为空；这里显式不套用命名策略，与 NekoLauncher 的 Go 实现保持一致。
    /// </summary>
    private static readonly JsonSerializerOptions RequestJsonOptions = new() { PropertyNamingPolicy = null };

    private readonly HttpClient _http;
    private readonly MinecraftTokenCrypto _crypto;
    private readonly AccountStore _store;

    public MinecraftTokenHandle(
        string? clientId = null,
        HttpClient? httpClient = null,
        MinecraftTokenCrypto? crypto = null,
        string? storageDirectory = null)
    {
        string overrideClientId = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable)?.Trim() ?? string.Empty;
        ClientId = string.IsNullOrWhiteSpace(clientId)
            ? (overrideClientId.Length > 0 ? overrideClientId : DefaultClientId)
            : clientId!.Trim();
        _http = httpClient ?? new HttpClient { Timeout = NetworkDefaults.HttpRequestTimeout };
        _crypto = crypto ?? new MinecraftTokenCrypto(storageDirectory);
        _store = new AccountStore(_crypto);
        Authlib = new AuthlibAccountManage(_http, storageDirectory);
    }

    public string ClientId { get; }

    public MinecraftTokenCrypto Crypto => _crypto;

    public AuthlibAccountManage Authlib { get; }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> form = new()
        {
            ["client_id"] = ClientId,
            ["scope"] = MicrosoftScope,
        };
        (HttpStatusCode statusCode, string body) = await PostFormAsync(DeviceCodeEndpoint, form, cancellationToken)
            .ConfigureAwait(false);
        if ((int)statusCode is < 200 or >= 300)
        {
            throw new MicrosoftAuthenticationException($"请求设备码失败（HTTP {(int)statusCode}）。");
        }

        DeviceCodePayload? payload = Deserialize<DeviceCodePayload>(body);
        if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceCode))
        {
            throw new MicrosoftAuthenticationException("设备码响应格式不正确。");
        }

        int expiresIn = payload.ExpiresIn < 1 ? 1 : payload.ExpiresIn;
        int interval = payload.Interval < 1 ? 1 : payload.Interval;
        return new DeviceCodeInfo
        {
            UserCode = payload.UserCode,
            VerificationUri = payload.VerificationUri,
            DeviceCode = payload.DeviceCode,
            ExpiresIn = TimeSpan.FromSeconds(expiresIn),
            PollIntervalSeconds = interval,
        };
    }

    public async Task<MicrosoftAccount> AuthenticateAsync(
        Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeHandler = null,
        Action<LoginStep>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        DeviceCodeInfo deviceCode = await RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        if (deviceCodeHandler is not null)
        {
            await deviceCodeHandler(deviceCode, cancellationToken).ConfigureAwait(false);
        }

        (string accessToken, string refreshToken) = await PollForTokenAsync(deviceCode, cancellationToken)
            .ConfigureAwait(false);
        onStep?.Invoke(LoginStep.MicrosoftToken);
        return await ExchangeForMinecraftAccountAsync(accessToken, refreshToken, onStep, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<MicrosoftAccount> AuthenticateAsync(
        Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeHandler,
        CancellationToken cancellationToken)
        => AuthenticateAsync(deviceCodeHandler, null, cancellationToken);

    private async Task<(string AccessToken, string RefreshToken)> PollForTokenAsync(
        DeviceCodeInfo deviceCode,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(deviceCode.ExpiresIn);
        int pollIntervalSeconds = deviceCode.PollIntervalSeconds;
        int transientFailures = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), cancellationToken).ConfigureAwait(false);

            Dictionary<string, string> form = new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode.DeviceCode,
            };

            string body;
            try
            {
                (_, body) = await PostFormAsync(TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // 网络抖动（超时/连接重置）不终止登录：用户还在浏览器里输入验证码
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            TokenPayload? token = TryDeserialize<TokenPayload>(body);
            if (token is null)
            {
                if (++transientFailures <= DeviceCodeTransientRetryLimit)
                {
                    continue;
                }

                throw new MicrosoftAuthenticationException("令牌服务返回了无法解析的响应。");
            }

            if (!string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return (token.AccessToken, token.RefreshToken);
            }

            switch (token.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    pollIntervalSeconds += 5;
                    continue;
                case "server_error":
                    if (++transientFailures <= DeviceCodeTransientRetryLimit)
                    {
                        continue;
                    }

                    throw new MicrosoftAuthenticationException(
                        "设备码登录失败：令牌服务持续返回 server_error，请稍后重试。", token.Error);
                case "authorization_declined":
                    throw new MicrosoftAuthenticationException(
                        "你在浏览器中拒绝了授权请求。", MicrosoftAuthErrorCodes.AuthorizationDeclined);
                case "expired_token":
                    throw new MicrosoftAuthenticationException(
                        "设备码已过期，请重新开始登录。", MicrosoftAuthErrorCodes.DeviceCodeExpired);
                case "bad_verification_code":
                    throw new MicrosoftAuthenticationException(
                        "设备码无效，请重新开始登录。", "bad_verification_code");
                default:
                    string description = string.IsNullOrWhiteSpace(token.ErrorDescription)
                        ? (string.IsNullOrWhiteSpace(token.Error) ? "未知错误" : token.Error)
                        : token.ErrorDescription;
                    throw new MicrosoftAuthenticationException(
                        $"设备码登录失败：{Truncate(RedactSecrets(description), 200)}", token.Error ?? string.Empty);
            }
        }

        throw new MicrosoftAuthenticationException(
            "设备码登录超时，请重试。", MicrosoftAuthErrorCodes.DeviceCodeExpired);
    }

    public static Task<MicrosoftAccount> StartBrowserLoginAsync(
        Func<string, Task> openLoginPage,
        IProgress<LoginStep>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        if (openLoginPage is null)
        {
            throw new MicrosoftAuthenticationException("缺少打开登录页的回调，无法开始浏览器登录。");
        }

        MinecraftTokenHandle handle = new();
        return handle.StartBrowserLoginCoreAsync(openLoginPage, onStep, cancellationToken);
    }

    private async Task<MicrosoftAccount> StartBrowserLoginCoreAsync(
        Func<string, Task> openLoginPage,
        IProgress<LoginStep>? onStep,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(BrowserLoginTimeout);
        CancellationToken token = timeoutCts.Token;

        string verifier = CreateRandomUrlSafeString(48);
        string state = CreateRandomUrlSafeString(32);
        LoopbackCallbackServer? server = null;
        try
        {
            server = LoopbackCallbackServer.Start(state);
            string redirectUri = $"http://localhost:{server.Port}/callback";
            string authorizeUrl = BuildAuthorizeUrl(redirectUri, CreatePkceChallenge(verifier), state);

            await openLoginPage(authorizeUrl).ConfigureAwait(false);

            OAuthCallbackResult result = await server.WaitForCallbackAsync(token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.ErrorCode))
            {
                throw CreateOAuthCallbackError(result.ErrorCode, result.ErrorDescription);
            }
            if (!string.Equals(result.State, state, StringComparison.Ordinal))
            {
                throw new MicrosoftAuthenticationException("登录回调校验失败（state 不匹配），请重试。", "state_mismatch");
            }
            if (string.IsNullOrWhiteSpace(result.Code))
            {
                throw new MicrosoftAuthenticationException("登录回调缺少授权码，请重试。", "missing_code");
            }
            onStep?.Report(LoginStep.Authorized);

            (string accessToken, string refreshToken) = await RequestTokenByAuthorizationCodeAsync(
                result.Code, verifier, redirectUri, token).ConfigureAwait(false);
            onStep?.Report(LoginStep.MicrosoftToken);

            MicrosoftAccount account = await ExchangeForMinecraftAccountAsync(
                accessToken, refreshToken, step => onStep?.Report(step), token).ConfigureAwait(false);
            SaveMicrosoftAccount(account);
            return account;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new MicrosoftAuthenticationException("登录超时（10 分钟），请重试。", "browser_login_timeout");
        }
        catch (HttpRequestException e)
        {
            throw new MicrosoftAuthenticationException(
                "登录过程中网络请求失败：" + RedactSecrets(e.Message), string.Empty, e);
        }
        catch (JsonException e)
        {
            throw new MicrosoftAuthenticationException(
                "登录服务返回了无法解析的响应：" + RedactSecrets(e.Message), string.Empty, e);
        }
        finally
        {
            server?.Dispose();
        }
    }

    private async Task<(string AccessToken, string RefreshToken)> RequestTokenByAuthorizationCodeAsync(
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["scope"] = MicrosoftScope,
        };
        (HttpStatusCode statusCode, string body) = await PostFormAsync(TokenEndpoint, form, cancellationToken)
            .ConfigureAwait(false);
        TokenPayload token = Deserialize<TokenPayload>(body);
        if ((int)statusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            string detail = string.IsNullOrWhiteSpace(token.ErrorDescription)
                ? (string.IsNullOrWhiteSpace(token.Error) ? "未知错误" : token.Error)
                : token.ErrorDescription;
            throw new MicrosoftAuthenticationException(
                $"换取 Microsoft 令牌失败（HTTP {(int)statusCode}）：{Truncate(RedactSecrets(detail), 200)}",
                token.Error ?? string.Empty);
        }
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new MicrosoftAuthenticationException("授权服务未返回刷新令牌，请重试登录。");
        }

        return (token.AccessToken, token.RefreshToken);
    }

    private string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
    {
        Dictionary<string, string> query = new()
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = MicrosoftScope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };
        string queryString = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return AuthorizeEndpoint + "?" + queryString;
    }

    private static string CreateRandomUrlSafeString(int byteCount)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string CreatePkceChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static MicrosoftAuthenticationException CreateOAuthCallbackError(string code, string description)
    {
        string message;
        if (string.Equals(code, "access_denied", StringComparison.OrdinalIgnoreCase))
        {
            message = "你在登录页拒绝了授权请求。";
        }
        else if (!string.IsNullOrWhiteSpace(description))
        {
            message = $"微软登录页返回错误：{description}（{code}）";
        }
        else
        {
            message = $"微软登录页返回错误：{code}";
        }

        return new MicrosoftAuthenticationException(RedactSecrets(message), code);
    }

    private sealed class OAuthCallbackResult
    {
        public string Code { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string ErrorCode { get; init; } = string.Empty;
        public string ErrorDescription { get; init; } = string.Empty;
    }

    private sealed class LoopbackCallbackServer : IDisposable
    {
        private const int MaxRequestBytes = 16 * 1024;
        private const int RequestReadTimeoutMs = 15_000;
        private const int DisposeWaitMs = 2_000;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _acceptCts = new();
        private readonly TaskCompletionSource<OAuthCallbackResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string? _expectedState;
        private readonly object _clientTasksLock = new();
        private readonly List<Task> _clientTasks = [];
        private readonly Task _acceptLoopTask;

        private LoopbackCallbackServer(TcpListener listener, string? expectedState)
        {
            _listener = listener;
            _expectedState = expectedState;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoopTask = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public static LoopbackCallbackServer Start() => Start(null);

        public static LoopbackCallbackServer Start(string? expectedState)
        {
            SocketException? lastError = null;
            for (var attempt = 0; attempt < LoopbackListenRetryLimit; attempt++)
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                    return new LoopbackCallbackServer(listener, expectedState);
                }
                catch (SocketException e)
                {
                    lastError = e;
                    try
                    {
                        listener.Stop();
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            throw new MicrosoftAuthenticationException(
                "本地回调端口监听失败，请重试登录。" + (lastError is null ? string.Empty : "（" + lastError.Message + "）"));
        }

        public Task<OAuthCallbackResult> WaitForCallbackAsync(CancellationToken cancellationToken)
            => _result.Task.WaitAsync(cancellationToken);

        private async Task AcceptLoopAsync()
        {
            while (!_acceptCts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_acceptCts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                Task clientTask = Task.Run(() => HandleClientAsync(client));
                lock (_clientTasksLock)
                {
                    _clientTasks.Add(clientTask);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using NetworkStream stream = client.GetStream();
                    using CancellationTokenSource readCts =
                        CancellationTokenSource.CreateLinkedTokenSource(_acceptCts.Token);
                    readCts.CancelAfter(RequestReadTimeoutMs);
                    string request = await ReadRequestHeadAsync(stream, readCts.Token).ConfigureAwait(false);
                    if (!IsLoopbackHostAllowed(request))
                    {
                        await WriteResponseAsync(stream, 400, "Bad Request", BuildPage(
                            "请求被拒绝", "非法的 Host 头。"), readCts.Token).ConfigureAwait(false);
                        return;
                    }
                    string target = ExtractRequestTarget(request);
                    bool isCallback = Uri.TryCreate("http://localhost" + target, UriKind.Absolute, out Uri? uri)
                                      && string.Equals(uri.AbsolutePath, "/callback", StringComparison.Ordinal);
                    if (!isCallback)
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", BuildPage("未找到页面", "Not Found"), readCts.Token)
                            .ConfigureAwait(false);
                        return;
                    }

                    Dictionary<string, string> query = ParseQuery(uri!.Query);
                    OAuthCallbackResult result = new()
                    {
                        Code = GetValue(query, "code"),
                        State = GetValue(query, "state"),
                        ErrorCode = GetValue(query, "error"),
                        ErrorDescription = GetValue(query, "error_description"),
                    };
                    // state 校验前移到服务层：拒绝不符合预期的本地连接，避免其抢先写入回调结果
                    if (_expectedState is not null &&
                        !string.Equals(result.State, _expectedState, StringComparison.Ordinal))
                    {
                        await WriteResponseAsync(stream, 400, "Bad Request", BuildPage(
                            "登录校验失败", "回调 state 校验失败，请重试登录。"), readCts.Token).ConfigureAwait(false);
                        return;
                    }
                    _result.TrySetResult(result);

                    if (string.IsNullOrWhiteSpace(result.ErrorCode))
                    {
                        await WriteResponseAsync(stream, 200, "OK", BuildPage(
                            "登录已完成", "登录已完成，可以关闭此窗口返回启动器。"), readCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        string detail = string.IsNullOrWhiteSpace(result.ErrorDescription)
                            ? result.ErrorCode
                            : result.ErrorCode + "：" + result.ErrorDescription;
                        await WriteResponseAsync(stream, 200, "OK", BuildPage(
                            "登录未完成", "登录未完成：" + detail), readCts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            List<byte> buffer = [];
            byte[] chunk = new byte[StreamCopyBufferSize];
            while (buffer.Count < MaxRequestBytes)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                for (var index = 0; index < read; index++)
                {
                    buffer.Add(chunk[index]);
                }
                int end = IndexOfHeadEnd(buffer);
                if (end >= 0)
                {
                    return Encoding.ASCII.GetString(buffer.ToArray(), 0, end);
                }
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        private static int IndexOfHeadEnd(List<byte> buffer)
        {
            for (var index = 0; index <= buffer.Count - 4; index++)
            {
                if (buffer[index] == '\r' && buffer[index + 1] == '\n' &&
                    buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }

        private static string ExtractRequestTarget(string request)
        {
            string firstLine = request.Split("\r\n", 2, StringSplitOptions.None)[0];
            string[] parts = firstLine.Split(' ');
            return parts.Length >= 2 && parts[1].StartsWith('/') ? parts[1] : "/";
        }

        private static bool IsLoopbackHostAllowed(string request)
        {
            foreach (string line in request.Split("\r\n", StringSplitOptions.None))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 ||
                    !string.Equals(line[..colon].Trim(), "Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string host = line[(colon + 1)..].Trim();
                int portColon = host.LastIndexOf(':');
                string name = portColon > 0 ? host[..portColon] : host;
                // 仅放行指向回环地址的 Host，拒绝 DNS rebinding / 跨站把外部 Host 指到本端口
                return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                       || name.Equals("127.0.0.1", StringComparison.Ordinal)
                       || name.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
            }

            // 无 Host 头：保留兼容，交由 state 校验兜底
            return true;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
            string trimmed = query.StartsWith('?') ? query[1..] : query;
            if (trimmed.Length == 0) return result;
            foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int index = pair.IndexOf('=');
                string key = index < 0 ? pair : pair[..index];
                string value = index < 0 ? string.Empty : pair[(index + 1)..];
                result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }

            return result;
        }

        private static string GetValue(Dictionary<string, string> query, string key)
            => query.TryGetValue(key, out string? value) ? value : string.Empty;

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            string reason,
            string body,
            CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string head =
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n" +
                $"Content-Length: {payload.Length}\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string BuildPage(string title, string message)
        {
            string safeTitle = WebUtility.HtmlEncode(title);
            string safeMessage = WebUtility.HtmlEncode(message);
            return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
                   $"<title>{safeTitle}</title></head>" +
                   "<body style=\"margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
                   "background:#0d1117;color:#e6edf3;font-family:'Segoe UI',system-ui,sans-serif;\">" +
                   "<div style=\"text-align:center;padding:32px 40px;\">" +
                   $"<h1 style=\"margin:0 0 12px;font-size:22px;color:#3fc6c1;\">{safeTitle}</h1>" +
                   $"<p style=\"margin:0;font-size:14px;line-height:1.6;color:#9aa7b8;\">{safeMessage}</p>" +
                   "</div></body></html>";
        }

        public void Dispose()
        {
            try
            {
                _acceptCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }

            // 先等待接收循环与在途请求收尾，再释放 CTS，避免后台任务访问已释放对象
            try
            {
                _acceptLoopTask.Wait(DisposeWaitMs);
                Task[] pending = [];
                lock (_clientTasksLock)
                {
                    pending = _clientTasks.ToArray();
                }

                Task.WaitAll(pending, DisposeWaitMs);
            }
            catch (Exception)
            {
            }

            _acceptCts.Dispose();
        }
    }

    public async Task<MicrosoftAccount> RefreshAsync(MicrosoftAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new MicrosoftAuthenticationException("账号没有可用的刷新令牌，请重新登录。");
        }

        (string accessToken, string refreshToken) = await RequestTokenByRefreshTokenAsync(
            account.RefreshToken, account.RefreshToken, cancellationToken).ConfigureAwait(false);

        try
        {
            return await ExchangeForMinecraftAccountAsync(accessToken, refreshToken, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is MicrosoftAuthenticationException or HttpRequestException or JsonException)
        {
            // 令牌已成功轮换：即使后续交换失败也必须把新凭据交给调用方落盘，否则下次刷新必然失败
            MicrosoftAccount refreshed = account.Clone();
            refreshed.RefreshToken = refreshToken;
            throw new RotatedCredentialsException(
                $"凭据已刷新，但获取 Minecraft 档案失败：{RedactSecrets(e.Message)}", refreshed, e);
        }
    }

    public Task<MicrosoftAccount> ValidateAsync(MicrosoftAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.IsExpired ? RefreshAsync(account, cancellationToken) : Task.FromResult(account.Clone());
    }

    private async Task<(string AccessToken, string RefreshToken)> RequestTokenByRefreshTokenAsync(
        string refreshToken,
        string fallbackRefreshToken,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = MicrosoftScope,
        };
        (HttpStatusCode statusCode, string body) = await PostFormAsync(TokenEndpoint, form, cancellationToken)
            .ConfigureAwait(false);
        TokenPayload? token = Deserialize<TokenPayload>(body);

        if ((int)statusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            string detail = string.IsNullOrWhiteSpace(token.ErrorDescription)
                ? (string.IsNullOrWhiteSpace(token.Error) ? "未知错误" : token.Error)
                : token.ErrorDescription;
            string message = $"刷新令牌失败（HTTP {(int)statusCode}）：{Truncate(RedactSecrets(detail), 200)}";
            if (string.Equals(token.Error?.Trim(), "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                message += "——该账号的登录凭据已失效，请删除该账号后重新登录。";
            }

            throw new MicrosoftAuthenticationException(message, token.Error ?? string.Empty);
        }

        string newRefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? fallbackRefreshToken : token.RefreshToken;
        return (token.AccessToken, newRefreshToken);
    }

    public async Task<MicrosoftAccount> ExchangeForMinecraftAccountAsync(
        string microsoftAccessToken,
        string refreshToken,
        Action<LoginStep>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        (string xblToken, string uhs) = await AuthenticateWithXboxLiveAsync(microsoftAccessToken, cancellationToken)
            .ConfigureAwait(false);
        onStep?.Invoke(LoginStep.XboxLive);

        (string xstsToken, string xstsXuid) = await AuthenticateWithXstsAsync(xblToken, cancellationToken)
            .ConfigureAwait(false);
        onStep?.Invoke(LoginStep.Xsts);

        (string minecraftToken, int expiresInSeconds) = await LoginWithMinecraftAsync(uhs, xstsToken, cancellationToken)
            .ConfigureAwait(false);
        onStep?.Invoke(LoginStep.Minecraft);

        string xuid = string.IsNullOrWhiteSpace(xstsXuid) ? ExtractXuidFromMinecraftToken(minecraftToken) : xstsXuid;
        MinecraftProfilePayload profile = await FetchMinecraftProfileAsync(minecraftToken, cancellationToken)
            .ConfigureAwait(false);

        if (expiresInSeconds < 1)
        {
            expiresInSeconds = 1;
        }

        MicrosoftAccount account = new()
        {
            Username = profile.Name,
            Uuid = CompactUuid(profile.Id),
            AccessToken = minecraftToken,
            RefreshToken = refreshToken,
            XboxUserId = xuid,
            ClientId = ClientId,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
        };
        onStep?.Invoke(LoginStep.Done);
        return account;
    }

    private async Task<(string Token, string Uhs)> AuthenticateWithXboxLiveAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        object payload = new
        {
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + microsoftAccessToken,
            },
        };
        (HttpStatusCode statusCode, string body) = await PostJsonAsync(
            XboxLiveAuthenticateUrl, payload, new Dictionary<string, string> { ["x-xbl-contract-version"] = "1" },
            cancellationToken).ConfigureAwait(false);
        if ((int)statusCode is < 200 or >= 300)
        {
            throw new MicrosoftAuthenticationException($"Xbox Live 认证失败（HTTP {(int)statusCode}）。");
        }

        XboxPayload? parsed = TryDeserialize<XboxPayload>(body);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Token))
        {
            throw new MicrosoftAuthenticationException("Xbox Live 认证响应格式不正确。");
        }

        string uhs = parsed.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs ?? string.Empty;
        if (string.IsNullOrWhiteSpace(uhs))
        {
            throw new MicrosoftAuthenticationException("Xbox Live 认证响应格式不正确。");
        }

        return (parsed.Token, uhs);
    }

    private async Task<(string Token, string Xuid)> AuthenticateWithXstsAsync(
        string xblToken,
        CancellationToken cancellationToken)
    {
        object payload = new
        {
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
        };
        (HttpStatusCode statusCode, string body) = await PostJsonAsync(
            XstsAuthorizeUrl, payload, new Dictionary<string, string> { ["x-xbl-contract-version"] = "1" },
            cancellationToken).ConfigureAwait(false);
        if ((int)statusCode is < 200 or >= 300)
        {
            throw CreateXstsError((int)statusCode, body);
        }

        XboxPayload? parsed = TryDeserialize<XboxPayload>(body);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Token))
        {
            throw new MicrosoftAuthenticationException("XSTS 授权响应格式不正确。");
        }

        string xuid = parsed.DisplayClaims?.Xui?.FirstOrDefault()?.Xid ?? string.Empty;
        return (parsed.Token, xuid);
    }

    private static MicrosoftAuthenticationException CreateXstsError(int statusCode, string body)
    {
        string errorCode = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("XErr", out JsonElement xerr) && xerr.TryGetInt64(out long value))
            {
                errorCode = value.ToString();
            }
        }
        catch (JsonException)
        {
        }

        string message = errorCode switch
        {
            MicrosoftAuthErrorCodes.XboxNoAccount => "该 Microsoft 账号没有 Xbox Live 档案，无法完成 Minecraft 登录。",
            MicrosoftAuthErrorCodes.XboxChildAccount => "该账号是儿童账号，需要家长将其加入家庭组后才能游玩。",
            MicrosoftAuthErrorCodes.XboxConsentRequired => "该账号需要获得家长同意（可能未满 18 岁），请先完成相关流程。",
            MicrosoftAuthErrorCodes.XboxRegionBlocked => "当前国家或地区不支持 Minecraft 服务。",
            MicrosoftAuthErrorCodes.XboxAgeVerification => "该账号需要先完成年龄验证才能游玩。",
            _ => $"XSTS 授权失败（HTTP {statusCode}）。",
        };
        return new MicrosoftAuthenticationException(message, errorCode);
    }

    private async Task<(string AccessToken, int ExpiresIn)> LoginWithMinecraftAsync(
        string uhs,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        object payload = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
        (HttpStatusCode statusCode, string body) = await PostJsonAsync(MinecraftLoginUrl, payload, null, cancellationToken)
            .ConfigureAwait(false);
        if ((int)statusCode is < 200 or >= 300)
        {
            if (statusCode == HttpStatusCode.Forbidden &&
                body.Contains("account_suspended", StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftAuthenticationException(
                    "该账号已被 Minecraft 服务封禁（ACCOUNT_SUSPENDED），无法登录，需联系官方客服申诉。");
            }

            throw new MicrosoftAuthenticationException($"Minecraft 登录失败（HTTP {(int)statusCode}）。");
        }

        MinecraftLoginPayload? parsed = TryDeserialize<MinecraftLoginPayload>(body);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
        {
            throw new MicrosoftAuthenticationException("Minecraft 登录响应格式不正确。");
        }

        return (parsed.AccessToken, parsed.ExpiresIn);
    }

    private async Task<MinecraftProfilePayload> FetchMinecraftProfileAsync(
        string minecraftToken,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, MinecraftProfileUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + minecraftToken);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new MicrosoftAuthenticationException("该 Microsoft 账号尚未购买 Minecraft（Java 版），无法启动游戏。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthenticationException(
                $"获取 Minecraft 档案失败（HTTP {(int)response.StatusCode}）。");
        }

        MinecraftProfilePayload? profile = TryDeserialize<MinecraftProfilePayload>(body);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new MicrosoftAuthenticationException("Minecraft 档案响应格式不正确。");
        }

        return profile;
    }

    public async Task UploadSkinAsync(
        MicrosoftAccount account,
        byte[] pngBytes,
        bool slim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (pngBytes is null || pngBytes.Length == 0)
        {
            throw new MicrosoftAuthenticationException("皮肤文件为空。");
        }

        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            throw new MicrosoftAuthenticationException("账号没有 Minecraft 访问令牌，请重新登录。");
        }

        using MultipartFormDataContent form = new();
        form.Add(new StringContent(slim ? "slim" : "classic"), "variant");
        ByteArrayContent file = new(pngBytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");

        using HttpRequestMessage request = new(HttpMethod.Post,
            "https://api.minecraftservices.com/minecraft/profile/skins")
        {
            Content = form,
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + account.AccessToken);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthenticationException(
                $"上传皮肤失败（HTTP {(int)response.StatusCode}）。");
        }
    }

    public static string ExtractXuidFromMinecraftToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return string.Empty;
        }

        string[] parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - (payload.Length % 4)) % 4, '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("xuid", out JsonElement xuid) ? xuid.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return string.Empty;
        }
    }

    public static string CompactUuid(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid) || uuid.Length == 32)
        {
            return uuid ?? string.Empty;
        }

        return uuid.Replace("-", string.Empty, StringComparison.Ordinal);
    }

    public void SaveMicrosoftAccount(MicrosoftAccount account) => _store.SaveMicrosoftAccount(account);

    public IReadOnlyList<MicrosoftAccount> LoadMicrosoftAccounts() => _store.LoadMicrosoftAccounts();

    public bool RemoveMicrosoftAccount(string uuid) => _store.RemoveMicrosoftAccount(uuid);

    public void SaveAuthlibCredential(AuthlibCredential credential) => _store.SaveAuthlibCredential(credential);

    public IReadOnlyList<AuthlibCredential> LoadAuthlibCredentials() => _store.LoadAuthlibCredentials();

    public void SaveOfflineAccount(string username) => _store.SaveOfflineAccount(username);

    public IReadOnlyList<string> LoadOfflineAccounts() => _store.LoadOfflineAccounts();

    public bool RemoveOfflineAccount(string username) => _store.RemoveOfflineAccount(username);

    public string GetOrCreateClientToken() => _store.GetOrCreateClientToken();

    private async Task<(HttpStatusCode StatusCode, string Body)> PostFormAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(
        string endpoint,
        object payload,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RequestJsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        // Xbox Live 文档要求同时声明可接收 JSON；部分代理/边缘节点缺少 Accept 时会直接 400
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private static T Deserialize<T>(string body) where T : class, new()
    {
        return TryDeserialize<T>(body) ?? new T();
    }

    private static T? TryDeserialize<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    // 统一脱敏：把可能混入响应/异常文本里的令牌字段与 Bearer 凭据替换为占位符，
    // 避免随 Exception.ToString() 写入 %TEMP% 明文日志。
    private static readonly Regex SensitiveValueRegex = new(
        @"(?<prefix>""?(?:access_token|refresh_token|id_token|device_code|code_verifier|token)""?\s*[:=]\s*|Bearer\s+)(?<value>""[^""]*""|[^\s,;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string RedactSecrets(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : SensitiveValueRegex.Replace(value, "${prefix}[REDACTED]");

    private sealed class DeviceCodePayload
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    private sealed class TokenPayload
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    private sealed class XboxPayload
    {
        [JsonPropertyName("Token")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("DisplayClaims")] public XboxDisplayClaims? DisplayClaims { get; set; }
    }

    private sealed class XboxDisplayClaims
    {
        [JsonPropertyName("xui")] public List<XboxUserClaim>? Xui { get; set; }
    }

    private sealed class XboxUserClaim
    {
        [JsonPropertyName("uhs")] public string Uhs { get; set; } = string.Empty;
        [JsonPropertyName("xid")] public string Xid { get; set; } = string.Empty;
    }

    private sealed class MinecraftLoginPayload
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class MinecraftProfilePayload
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

}

public sealed class DeviceCodeInfo
{
    public string UserCode { get; init; } = string.Empty;
    public string VerificationUri { get; init; } = string.Empty;
    public string DeviceCode { get; init; } = string.Empty;
    public TimeSpan ExpiresIn { get; init; }
    public int PollIntervalSeconds { get; init; }

    public string VerificationUriFull => VerificationUri + "?user_code=" + Uri.EscapeDataString(UserCode);
}

public sealed class MicrosoftAccount
{
    [JsonPropertyName("Username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("Uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("AccessToken")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("RefreshToken")] public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("XboxUserId")] public string XboxUserId { get; set; } = string.Empty;
    [JsonPropertyName("ClientId")] public string ClientId { get; set; } = string.Empty;
    [JsonPropertyName("ExpiresAt")] public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>访问令牌是否已过期（含 5 分钟提前量）。</summary>
    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt.AddMinutes(-5);

    public MicrosoftAccount Clone()
    {
        return (MicrosoftAccount)MemberwiseClone();
    }
}

public enum LoginStep
{
    Authorized = 1,

    MicrosoftToken = 2,

    XboxLive = 3,

    Xsts = 4,

    Minecraft = 5,

    Done = 6,
}

public static class MicrosoftAuthErrorCodes
{
    public const string AuthorizationDeclined = "authorization_declined";
    public const string DeviceCodeExpired = "expired_token";
    public const string XboxNoAccount = "2148916233";
    public const string XboxChildAccount = "2148916235";
    public const string XboxConsentRequired = "2148916236";
    public const string XboxRegionBlocked = "2148916237";
    public const string XboxAgeVerification = "2148916238";
}

public sealed class MicrosoftAuthenticationException : Exception
{
    public MicrosoftAuthenticationException(string message, string errorCode = "", Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class RotatedCredentialsException : Exception
{
    public RotatedCredentialsException(string message, MicrosoftAccount refreshedAccount, Exception? inner = null)
        : base(message, inner)
    {
        RefreshedAccount = refreshedAccount;
    }

    /// <summary>轮换后的新凭据：调用方必须立即持久化，否则下次刷新将失败。</summary>
    public MicrosoftAccount RefreshedAccount { get; }
}

// Windows 走 DPAPI（当前用户范围），macOS 把主密钥存进钥匙串，Linux 优先 libsecret（secret-tool），都不可用时退回 0600 权限的密钥文件。
public class MinecraftTokenCrypto
{
    public const string EncryptedPrefix = "mofuenc1:";

    private const string KeychainService = "MofuCore.Token";
    private const string KeychainAccount = "token-master-key";
    private const string KeyFileName = "account.secret.key";
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    // 应用专属附加熵：阻止裸密文被搬到其它程序/环境下直接解开。
    private static readonly byte[] AdditionalEntropy =
    [
        0x4D, 0x6F, 0x66, 0x75, 0x43, 0x6F, 0x72, 0x65, 0x2E,
        0x54, 0x6F, 0x6B, 0x65, 0x6E, 0x2E, 0x76, 0x31,
    ];

    private static readonly object KeyGate = new();
    private static byte[]? _masterKey;

    private readonly string _storageDirectory;

    public MinecraftTokenCrypto(string? storageDirectory = null)
    {
        _storageDirectory = string.IsNullOrWhiteSpace(storageDirectory) ? DefaultStorageDirectory : storageDirectory!;
    }

    public static string DefaultStorageDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mofumofu");

    public string StorageDirectory => _storageDirectory;

    public static bool IsAvailable
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            try
            {
                _ = LoadOrCreateMasterKey(DefaultStorageDirectory);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>失败返回空串。</summary>
    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        try
        {
            return EncryptCore(Encoding.UTF8.GetBytes(plaintext));
        }
        catch
        {
            return string.Empty;
        }
    }

    public string Decrypt(string stored)
    {
        return TryDecrypt(stored, out string plaintext) ? plaintext : string.Empty;
    }

    public bool TryDecrypt(string stored, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrWhiteSpace(stored) ||
            !stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            byte[]? raw = DecryptCore(stored[EncryptedPrefix.Length..]);
            if (raw is null)
            {
                return false;
            }

            plaintext = Encoding.UTF8.GetString(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Encoding.UTF8.GetBytes(EncryptCore(plaintext));
    }

    public byte[]? Decrypt(byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        try
        {
            string text = Encoding.UTF8.GetString(stored);
            if (!text.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            return DecryptCore(text[EncryptedPrefix.Length..]);
        }
        catch
        {
            return null;
        }
    }

    private string EncryptCore(byte[] plaintext)
    {
        byte[] body = OperatingSystem.IsWindows()
            ? ProtectWithDPAPI(plaintext)
            : ProtectWithAes(plaintext);
        return EncryptedPrefix + Convert.ToBase64String(body);
    }

    private byte[]? DecryptCore(string encodedBase64)
    {
        if (string.IsNullOrWhiteSpace(encodedBase64))
        {
            return null;
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(encodedBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        return OperatingSystem.IsWindows()
            ? UnprotectWithDPAPI(raw)
            : UnprotectWithAes(raw);
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] ProtectWithDPAPI(byte[] plaintext)
    {
        DataBlob input = CreateBlob(plaintext);
        DataBlob entropy = CreateBlob(AdditionalEntropy);
        GCHandle inputHandle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(AdditionalEntropy, GCHandleType.Pinned);
        try
        {
            input.Data = inputHandle.AddrOfPinnedObject();
            entropy.Data = entropyHandle.AddrOfPinnedObject();
            if (!CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out DataBlob output))
            {
                throw new CryptographicException("返回 Win32 错误 " + Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            entropyHandle.Free();
            inputHandle.Free();
        }
    }

    private static byte[] UnprotectWithDPAPI(byte[] ciphertext)
    {
        DataBlob input = CreateBlob(ciphertext);
        DataBlob entropy = CreateBlob(AdditionalEntropy);
        GCHandle inputHandle = GCHandle.Alloc(ciphertext, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(AdditionalEntropy, GCHandleType.Pinned);
        try
        {
            input.Data = inputHandle.AddrOfPinnedObject();
            entropy.Data = entropyHandle.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out DataBlob output))
            {
                throw new CryptographicException("返回 Win32 错误 " + Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            entropyHandle.Free();
            inputHandle.Free();
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        return new DataBlob { Size = data.Length, Data = IntPtr.Zero };
    }

    private static byte[] CopyAndFree(DataBlob blob)
    {
        try
        {
            byte[] result = new byte[blob.Size];
            Marshal.Copy(blob.Data, result, 0, blob.Size);
            return result;
        }
        finally
        {
            _ = LocalFree(blob.Data);
        }
    }

    private byte[] ProtectWithAes(byte[] plaintext)
    {
        byte[] key = LoadOrCreateMasterKey(_storageDirectory);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagLength];
        using (AesGcm aes = new(key, TagLength))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalEntropy);
        }

        byte[] output = new byte[NonceLength + TagLength + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceLength);
        Buffer.BlockCopy(tag, 0, output, NonceLength, TagLength);
        Buffer.BlockCopy(ciphertext, 0, output, NonceLength + TagLength, ciphertext.Length);
        return output;
    }

    private byte[]? UnprotectWithAes(byte[] raw)
    {
        if (raw.Length < NonceLength + TagLength)
        {
            return null;
        }

        byte[] key = LoadOrCreateMasterKey(_storageDirectory);
        ReadOnlySpan<byte> nonce = raw.AsSpan(0, NonceLength);
        ReadOnlySpan<byte> tag = raw.AsSpan(NonceLength, TagLength);
        ReadOnlySpan<byte> ciphertext = raw.AsSpan(NonceLength + TagLength);
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using AesGcm aes = new(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalEntropy);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] LoadOrCreateMasterKey(string storageDirectory)
    {
        lock (KeyGate)
        {
            if (_masterKey is not null)
            {
                return _masterKey;
            }

            byte[]? loaded = OperatingSystem.IsMacOS()
                ? ReadKeyFromKeychain()
                : ReadKeyFromSecretService(storageDirectory);
            if (loaded is not null)
            {
                _masterKey = loaded;
                return loaded;
            }

            byte[] generated = RandomNumberGenerator.GetBytes(KeyLength);
            bool stored = OperatingSystem.IsMacOS()
                ? WriteKeyToKeychain(generated)
                : WriteKeyToSecretService(generated);
            if (!stored && !OperatingSystem.IsMacOS())
            {
                // Linux 等没有系统凭据库的环境退回仅当前用户可读（0600）的密钥文件；读取侧按 secret-tool → 密钥文件的顺序取键。
                stored = WriteKeyToFile(generated, storageDirectory);
            }
            if (!stored)
            {
                // fail-closed：所有持久化途径都失败时，不使用未持久化的临时密钥
                throw new CryptographicException("无法持久化主密钥：系统凭据库与密钥文件均不可用。");
            }

            _masterKey = generated;
            return generated;
        }
    }

    private static byte[]? ReadKeyFromSecretService(string storageDirectory)
    {
        string secretTool = RunProcess("secret-tool", ["lookup", "service", KeychainService, "key", KeychainAccount]).Output;
        if (TryDecodeKey(secretTool, out byte[] secret))
        {
            return secret;
        }

        return TryReadKeyFile(Path.Combine(storageDirectory, KeyFileName));
    }

    private static bool WriteKeyToSecretService(byte[] key)
    {
        string encoded = Convert.ToBase64String(key);
        (int exitCode, _) = RunProcess("secret-tool",
            ["store", "--label=MofuCore 账号令牌", "service", KeychainService, "key", KeychainAccount],
            encoded);
        return exitCode == 0;
    }

    private static bool WriteKeyToFile(byte[] key, string storageDirectory)
    {
        string temporaryPath = string.Empty;
        try
        {
            Directory.CreateDirectory(storageDirectory);
            string path = Path.Combine(storageDirectory, KeyFileName);
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            // 创建时即限定 0600，避免密钥短暂以默认权限暴露
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (FileStream stream = new(temporaryPath, options))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false)))
            {
                writer.Write(Convert.ToBase64String(key));
            }
            File.Move(temporaryPath, path, true);
            temporaryPath = string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private static byte[]? ReadKeyFromKeychain()
    {
        (int exitCode, string output) = RunProcess("security",
            ["find-generic-password", "-a", KeychainAccount, "-s", KeychainService, "-w"]);
        if (exitCode != 0)
        {
            return null;
        }

        return TryDecodeKey(output, out byte[] key) ? key : null;
    }

    private static bool WriteKeyToKeychain(byte[] key)
    {
        // 主密钥经标准输入传给 security，避免 -w 明文出现在进程命令行（同机 ps 可读）
        (int exitCode, _) = RunProcess("security",
            ["add-generic-password", "-U", "-a", KeychainAccount, "-s", KeychainService,
                "-l", "MofuCore 账号令牌"],
            Convert.ToBase64String(key));
        return exitCode == 0;
    }

    private static bool TryDecodeKey(string value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(value.Trim());
            if (decoded.Length != KeyLength)
            {
                return false;
            }

            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[]? TryReadKeyFile(string path)
    {
        try
        {
            return File.Exists(path) && TryDecodeKey(File.ReadAllText(path), out byte[] key) ? key : null;
        }
        catch
        {
            return null;
        }
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string[] arguments, string? stdin = null)
    {
        try
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 " + fileName);
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            // 在线程池线程并发排空 stderr、当前线程排空 stdout，避免子进程写满 stderr 管道时死锁；
            // 两端都用同步 ReadToEnd，不引入 SynchronizationContext 下 .GetResult() 的死锁风险
            Task<string> stderrTask = Task.Run(() => process.StandardError.ReadToEnd());
            string output = process.StandardOutput.ReadToEnd();
            _ = stderrTask.GetAwaiter().GetResult();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
