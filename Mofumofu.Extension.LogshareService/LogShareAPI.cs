using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mofumofu.Extension.LogshareService;

/*
 * 这个文件内的API基于LogShare API，与MofuMofu启动器没有直接关系。
 * 使用时请注意LogShare的Eula。
 */
public class LogShareAPI
{
    /// <summary>LogShare API 默认地址。</summary>
    public const string DefaultBaseUrl = "https://api.logshare.cn";

    /// <summary>上传来源标识（LogShare 接入规范建议写明启动器与版本）。</summary>
    public const string DefaultSource = "Mofumofu/0.1.0";

    /// <summary>单条日志最大长度（10MiB，服务端限制）。</summary>
    public const long MaximumContentBytes = 10 * 1024 * 1024;

    /// <summary>单条日志最大行数。</summary>
    public const int MaximumLines = 50000;

    /// <summary>日志默认保留时间（7 天）。</summary>
    public static readonly TimeSpan StorageTime = TimeSpan.FromDays(7);

    /// <summary>上传前必须让用户确认的 EULA 说明：日志会公开并保留 7 天。</summary>
    public const string EulaNotice =
        "上传即表示同意 LogShare 的服务协议与隐私政策：日志内容会公开，保留 7 天后自动删除，且请勿包含密码、令牌等敏感信息。";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Http = new() { Timeout = RequestTimeout };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseUrl;

    /// <param name="baseUrl">LogShare API 地址；传空串表示未配置，所有请求都会优雅失败。</param>
    public LogShareAPI(string baseUrl = DefaultBaseUrl)
    {
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    public string BaseUrl => _baseUrl;

    /// <summary>是否配置了可用的 API 地址。</summary>
    public bool IsConfigured => _baseUrl.Length > 0;

    /// <summary>
    /// 上传一段日志内容，返回分享链接与删除凭据（token 请自行持久化，丢失后无法删除）。
    /// 未配置、内容为空、超出限制或请求失败时返回 Success=false，不抛异常。
    /// </summary>
    public async Task<LogShareUploadResult> UploadAsync(
        string content,
        string source = DefaultSource,
        IReadOnlyDictionary<string, string>? files = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return LogShareUploadResult.Fail("未配置 LogShare 服务地址。");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return LogShareUploadResult.Fail("日志内容不能为空。");
        }

        if (Encoding.UTF8.GetByteCount(content) > MaximumContentBytes)
        {
            return LogShareUploadResult.Fail("日志内容超过 10MiB，无法上传。");
        }

        if (CountLines(content) > MaximumLines)
        {
            return LogShareUploadResult.Fail($"日志超过 {MaximumLines} 行，无法上传。");
        }

        Dictionary<string, object> payload = new()
        {
            ["content"] = content,
            ["source"] = string.IsNullOrWhiteSpace(source) ? DefaultSource : source.Trim(),
        };
        if (files is { Count: > 0 })
        {
            payload["files"] = files
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair => new Dictionary<string, string>
                {
                    ["name"] = pair.Key,
                    ["content"] = pair.Value,
                })
                .ToList();
        }

        try
        {
            using HttpResponseMessage response = await PostJsonAsync("v1/log", payload, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;

            if (!IsSuccess(root))
            {
                return LogShareUploadResult.Fail(
                    ReadString(root, "error") is { Length: > 0 } error ? error : $"上传失败（HTTP {(int)response.StatusCode}）。",
                    ReadInt(root, "code") ?? (int)response.StatusCode);
            }

            return new LogShareUploadResult
            {
                Success = true,
                Id = ReadString(root, "id"),
                Url = ReadString(root, "url"),
                RawUrl = ReadString(root, "raw"),
                Token = ReadString(root, "token"),
                Message = ReadString(root, "message"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LogShareUploadResult.Fail("上传日志超时。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return LogShareUploadResult.Fail("上传日志失败：" + e.Message);
        }
    }

    /// <summary>
    /// 多文件打包上传（主日志 + 崩溃报告等），返回分享链接与删除凭据。
    /// </summary>
    public Task<LogShareUploadResult> UploadFilesAsync(
        IReadOnlyDictionary<string, string> files,
        string source = DefaultSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return Task.FromResult(LogShareUploadResult.Fail("没有可上传的文件。"));
        }

        // 第一个文件作为主日志（content），其余作为附加文件
        string mainContent = files.First().Value;
        Dictionary<string, string> attachments = files.Skip(1).ToDictionary(pair => pair.Key, pair => pair.Value);
        return UploadAsync(mainContent, source, attachments, cancellationToken);
    }

    /// <summary>
    /// 上传指定日志文件。
    /// </summary>
    public async Task<LogShareUploadResult> UploadLogFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return LogShareUploadResult.Fail("日志文件路径不能为空。");
        }

        try
        {
            string fullPath = Path.GetFullPath(filePath.Trim());
            if (!File.Exists(fullPath))
            {
                return LogShareUploadResult.Fail("日志文件不存在：" + fullPath);
            }

            string content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return await UploadAsync(content, DefaultSource, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LogShareUploadResult.Fail("读取日志文件超时。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LogShareUploadResult.Fail("读取日志文件失败：" + e.Message);
        }
    }

    /// <summary>
    /// 删除已上传的日志（需要上传时返回的 token），支持多个 ID 用逗号分隔。
    /// </summary>
    public async Task<LogShareDeleteResult> DeleteAsync(
        string ids,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return LogShareDeleteResult.Fail("未配置 LogShare 服务地址。");
        }

        if (string.IsNullOrWhiteSpace(ids))
        {
            return LogShareDeleteResult.Fail("日志 ID 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return LogShareDeleteResult.Fail("缺少删除凭据 token。");
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Delete, UrlOf("v1/log/" + Uri.EscapeDataString(ids.Trim())));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;

            if (!IsSuccess(root))
            {
                return LogShareDeleteResult.Fail(
                    ReadString(root, "error") is { Length: > 0 } error ? error : $"删除失败（HTTP {(int)response.StatusCode}）。",
                    ReadInt(root, "code") ?? (int)response.StatusCode);
            }

            return new LogShareDeleteResult
            {
                Success = true,
                Deleted = ReadStringArray(root, "deleted"),
                Failed = ReadStringArray(root, "failed"),
                DeletedCount = ReadInt(root, "deletedCount") ?? 0,
                FailedCount = ReadInt(root, "failedCount") ?? 0,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LogShareDeleteResult.Fail("删除日志超时。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return LogShareDeleteResult.Fail("删除日志失败：" + e.Message);
        }
    }

    /// <summary>
    /// 获取已上传日志的原文；日志不存在时返回 null。
    /// </summary>
    public async Task<string?> GetRawAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync(UrlOf("v1/raw/" + Uri.EscapeDataString(id.Trim())), cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 获取已上传日志的元信息（大小、行数、保留时间、附加文件列表）；日志不存在时返回 null。
    /// </summary>
    public async Task<LogShareLog?> GetLogAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync(UrlOf("v1/log/" + Uri.EscapeDataString(id.Trim())), cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!IsSuccess(root))
            {
                return null;
            }

            List<LogShareFile> files = [];
            if (root.TryGetProperty("files", out JsonElement filesElement) && filesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in filesElement.EnumerateArray())
                {
                    files.Add(new LogShareFile
                    {
                        Name = ReadString(item, "name"),
                        Size = ReadLong(item, "size") ?? 0,
                    });
                }
            }

            return new LogShareLog
            {
                Id = ReadString(root, "id"),
                Size = ReadLong(root, "size") ?? 0,
                Lines = ReadLong(root, "lines") ?? 0,
                Created = ReadDateTimeFromUnix(root, "created"),
                Expires = ReadDateTimeFromUnix(root, "expires"),
                Metadata = ReadString(root, "metadata"),
                Source = ReadString(root, "source"),
                RawUrl = ReadString(root, "raw"),
                Files = files,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 获取服务端的日志存储限制；失败时返回 null。
    /// </summary>
    public async Task<LogShareLimits?> GetLimitsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(UrlOf("v1/limits"), cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            return new LogShareLimits
            {
                MaxLength = ReadLong(root, "maxLength") ?? MaximumContentBytes,
                MaxLines = ReadLong(root, "maxLines") ?? MaximumLines,
                StorageTime = ReadLong(root, "storageTime") ?? (long)StorageTime.TotalSeconds,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 提交日志内容做本地分析（不落盘），返回检测到的问题与信息；失败时返回 null。
    /// </summary>
    public async Task<LogShareInsights?> AnalyseAsync(string content, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using StringContent body = new(content, Encoding.UTF8, "text/plain");
            using HttpResponseMessage response = await Http
                .PostAsync(UrlOf("v1/analyse"), body, cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseInsights(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 获取已存储日志的分析洞察；失败时返回 null。
    /// </summary>
    public async Task<LogShareInsights?> GetInsightsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync(UrlOf("v1/insights/" + Uri.EscapeDataString(id.Trim())), cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseInsights(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 检查 LogShare 服务是否可用（请求 API 根端点）。
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(_baseUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return IsSuccess(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return false;
        }
    }

    private string UrlOf(string endpoint)
    {
        return _baseUrl + "/" + endpoint;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string endpoint,
        object payload,
        CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(payload, JsonOptions);
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return await Http.PostAsync(UrlOf(endpoint), content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException($"服务端返回空响应（HTTP {(int)response.StatusCode}）。");
        }

        return JsonDocument.Parse(text);
    }

    private static LogShareInsights ParseInsights(JsonElement root)
    {
        LogShareInsights insights = new()
        {
            Id = ReadString(root, "id"),
            Name = ReadString(root, "name"),
            Type = ReadString(root, "type"),
            Version = ReadString(root, "version"),
            Title = ReadString(root, "title"),
        };

        if (!root.TryGetProperty("analysis", out JsonElement analysis) || analysis.ValueKind != JsonValueKind.Object)
        {
            return insights;
        }

        insights.Problems = ReadMessages(analysis, "problems");
        insights.Information = ReadMessages(analysis, "information");
        return insights;
    }

    private static List<LogShareMessage> ReadMessages(JsonElement parent, string propertyName)
    {
        List<LogShareMessage> messages = [];
        if (!parent.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            LogShareMessage message = new()
            {
                Message = ReadString(item, "message"),
                Counter = ReadInt(item, "counter") ?? 0,
                Solutions = ReadStringArray(item, "solutions"),
            };
            messages.Add(message);
        }

        return messages;
    }

    private static bool IsSuccess(JsonElement root)
    {
        return root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.True;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.ToString(),
        };
    }

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
            _ => null,
        };
    }

    private static long? ReadLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out long parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadDateTimeFromUnix(JsonElement root, string propertyName)
    {
        long? seconds = ReadLong(root, propertyName);
        return seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        List<string> result = [];
        if (!root.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString() ?? string.Empty);
            }
        }

        return result;
    }

    private static int CountLines(string content)
    {
        int lines = 1;
        foreach (char character in content)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}

/// <summary>
/// 日志上传结果（token 是删除凭据，丢失后无法删除）。
/// </summary>
public class LogShareUploadResult
{
    public bool Success { get; set; }

    /// <summary>日志 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>分享链接，如 https://logshare.cn/abc123。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>原文 API 地址。</summary>
    public string RawUrl { get; set; } = string.Empty;

    /// <summary>删除凭据。</summary>
    public string Token { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public int Code { get; set; }

    public bool HasUrl => Success && Url.Length > 0;

    public static LogShareUploadResult Fail(string error, int code = 0)
    {
        return new LogShareUploadResult { Success = false, Error = error, Code = code };
    }
}

/// <summary>
/// 删除结果。
/// </summary>
public class LogShareDeleteResult
{
    public bool Success { get; set; }

    public List<string> Deleted { get; set; } = [];

    public List<string> Failed { get; set; } = [];

    public int DeletedCount { get; set; }

    public int FailedCount { get; set; }

    public string Error { get; set; } = string.Empty;

    public int Code { get; set; }

    public static LogShareDeleteResult Fail(string error, int code = 0)
    {
        return new LogShareDeleteResult { Success = false, Error = error, Code = code };
    }
}

/// <summary>
/// 已上传日志的元信息。
/// </summary>
public class LogShareLog
{
    public string Id { get; set; } = string.Empty;

    /// <summary>字节数。</summary>
    public long Size { get; set; }

    public long Lines { get; set; }

    public DateTimeOffset? Created { get; set; }

    public DateTimeOffset? Expires { get; set; }

    /// <summary>服务端元数据（原样 JSON 文本）。</summary>
    public string Metadata { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string RawUrl { get; set; } = string.Empty;

    /// <summary>附加文件列表。</summary>
    public List<LogShareFile> Files { get; set; } = [];
}

/// <summary>
/// 附加文件。
/// </summary>
public class LogShareFile
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }
}

/// <summary>
/// 服务端日志存储限制。
/// </summary>
public class LogShareLimits
{
    public long MaxLength { get; set; }

    public long MaxLines { get; set; }

    /// <summary>保留时间（秒）。</summary>
    public long StorageTime { get; set; }
}

/// <summary>
/// 日志分析洞察。
/// </summary>
public class LogShareInsights
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<LogShareMessage> Problems { get; set; } = [];

    public List<LogShareMessage> Information { get; set; } = [];
}

/// <summary>
/// 分析结果中的一条问题 / 信息。
/// </summary>
public class LogShareMessage
{
    public string Message { get; set; } = string.Empty;

    public int Counter { get; set; }

    public List<string> Solutions { get; set; } = [];
}
