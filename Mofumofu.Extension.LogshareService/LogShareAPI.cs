using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mofumofu.Extension.LogshareService;

/// <summary>
/// LogShare 开放接口封装，与 MofuMofu 启动器无直接关系；使用时须遵守 LogShare 的 EULA。
/// </summary>
public class LogShareAPI
{
    public const string DefaultBaseUrl = "https://api.logshare.cn";

    /// <summary>LogShare 接入规范建议写明启动器与版本。</summary>
    public const string DefaultSource = "Mofumofu/0.1.0";

    /// <summary>服务端限制：10MiB。</summary>
    public const long MaximumContentBytes = 10 * 1024 * 1024;

    /// <summary>服务端限制：50000 行。</summary>
    public const int MaximumLines = 50000;

    /// <summary>服务端保留 7 天。</summary>
    public static readonly TimeSpan StorageTime = TimeSpan.FromDays(7);

    public const string EulaNotice =
        "上传即表示同意 LogShare 的服务协议与隐私政策：日志内容会公开，保留 7 天后自动删除，且请勿包含密码、令牌等敏感信息。";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Http = new()
    {
        Timeout = RequestTimeout,
        // fail-closed：避免 ReadAsStringAsync 无上限读入导致 OOM。
        MaxResponseContentBufferSize = MaximumContentBytes + 1024 * 1024,
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _baseUrl;

    /// <summary>baseUrl 传空串表示未配置，所有请求都会优雅失败。</summary>
    public LogShareAPI(string baseUrl = DefaultBaseUrl)
    {
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    public string BaseUrl => _baseUrl;

    public bool IsConfigured => _baseUrl.Length > 0;

    /// <summary>
    /// token 是删除凭据，需自行持久化，丢失后无法删除。
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

        if (!IsWithinLimits(content))
        {
            return LogShareUploadResult.Fail(
                Encoding.UTF8.GetByteCount(content) > MaximumContentBytes
                    ? "日志内容超过 10MiB，无法上传。"
                    : $"日志超过 {MaximumLines} 行，无法上传。");
        }

        Dictionary<string, object> payload = new()
        {
            ["content"] = content,
            ["source"] = string.IsNullOrWhiteSpace(source) ? DefaultSource : source.Trim(),
        };
        if (files is { Count: > 0 })
        {
            List<Dictionary<string, string>> attachments = [];
            foreach (KeyValuePair<string, string> pair in files)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                // 附加文件同样受上限约束（fail-closed，复用同一校验）。
                if (!IsWithinLimits(pair.Value))
                {
                    return LogShareUploadResult.Fail("附加文件内容超过限制，无法上传。");
                }
                attachments.Add(new Dictionary<string, string>
                {
                    ["name"] = pair.Key,
                    ["content"] = pair.Value,
                });
            }
            payload["files"] = attachments;
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

        string mainContent = files.First().Value;
        Dictionary<string, string> attachments = files.Skip(1).ToDictionary(pair => pair.Key, pair => pair.Value);
        return UploadAsync(mainContent, source, attachments, cancellationToken);
    }

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

    /// <summary>需要上传时返回的 token；ids 支持逗号分隔多个。</summary>
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
            // 逗号必须保留，否则整个 ids 做 EscapeDataString 会把逗号编码成 %2C，服务端只会看到一个 ID。
            var escapedIds = new List<string>();
            foreach (string id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                escapedIds.Add(Uri.EscapeDataString(id.Trim()));
            }
            if (escapedIds.Count == 0)
            {
                return LogShareDeleteResult.Fail("日志 ID 不能为空。");
            }

            using HttpRequestMessage request = new(HttpMethod.Delete, UrlOf("v1/log/" + string.Join(",", escapedIds)));
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

    /// <summary>不落盘，只做服务端分析；失败时返回 null。</summary>
    public async Task<LogShareInsights?> AnalyseAsync(string content, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // 与 UploadAsync 复用同一内容上限校验，避免分析接口被超大 / 超行数日志拖垮。
        if (!IsWithinLimits(content))
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

        JsonDocument document = JsonDocument.Parse(text);
        // 先校验根元素类型：数组/字符串/null 等顶层 JSON 会让后续 TryGetProperty 抛
        // InvalidOperationException，而上层只过滤 HttpRequestException/JsonException/IOException。
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException($"服务端返回了非对象的 JSON 根元素（HTTP {(int)response.StatusCode}）。");
        }

        return document;
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

        if (root.ValueKind != JsonValueKind.Object) return insights;
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
        if (parent.ValueKind != JsonValueKind.Object) return messages;
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
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("success", out JsonElement success)
            && success.ValueKind == JsonValueKind.True;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object) return string.Empty;
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
        if (root.ValueKind != JsonValueKind.Object) return null;
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
        if (root.ValueKind != JsonValueKind.Object) return null;
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
        if (root.ValueKind != JsonValueKind.Object) return result;
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

    /// <summary>字节数与行数都必须在服务端限制内。</summary>
    private static bool IsWithinLimits(string content) =>
        Encoding.UTF8.GetByteCount(content) <= MaximumContentBytes
        && CountLines(content) <= MaximumLines;

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
/// token 是删除凭据，丢失后无法删除。
/// </summary>
public class LogShareUploadResult
{
    public bool Success { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string RawUrl { get; set; } = string.Empty;

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

public class LogShareLog
{
    public string Id { get; set; } = string.Empty;

    public long Size { get; set; }

    public long Lines { get; set; }

    public DateTimeOffset? Created { get; set; }

    public DateTimeOffset? Expires { get; set; }

    /// <summary>服务端元数据，原样 JSON 文本。</summary>
    public string Metadata { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string RawUrl { get; set; } = string.Empty;

    public List<LogShareFile> Files { get; set; } = [];
}

public class LogShareFile
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }
}

public class LogShareLimits
{
    public long MaxLength { get; set; }

    public long MaxLines { get; set; }

    public long StorageTime { get; set; }
}

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

public class LogShareMessage
{
    public string Message { get; set; } = string.Empty;

    public int Counter { get; set; }

    public List<string> Solutions { get; set; } = [];
}
