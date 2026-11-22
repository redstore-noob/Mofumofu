using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent.Probe;

public sealed record ProbeResponse(HttpStatusCode StatusCode, string Content)
{
    public T Deserialize<T>() => JsonSerializer.Deserialize<T>(Content, ProtocolJson.Options)
        ?? throw new JsonException("Probe returned a null JSON value.");
}

public sealed class ProbeUnavailableException : Exception
{
    public ProbeUnavailableException(Exception innerException)
        : base("The Probe is unavailable.", innerException)
    {
    }
}

public interface IProbeGateway
{
    Task<ProbeResponse> SendAsync(HttpMethod method, string path, string authorization,
        object? body = null, string? query = null, CancellationToken cancellationToken = default);
}

public sealed class HttpProbeGateway : IProbeGateway, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _baseAddress;

    public HttpProbeGateway(AgentOptions options)
    {
        string baseText = options.ProbeBaseUrl.TrimEnd('/') + "/";
        _baseAddress = new Uri(baseText, UriKind.Absolute);
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
            DefaultRequestVersion = HttpVersion.Version11,
        };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ProbeResponse> SendAsync(HttpMethod method, string path, string authorization,
        object? body = null, string? query = null, CancellationToken cancellationToken = default)
    {
        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("A fixed absolute Probe path is required.", nameof(path));
        }

        Uri target = new(_baseAddress, path.TrimStart('/') + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query));
        using HttpRequestMessage request = new(method, target);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (body is not null)
        {
            request.Content = new StringContent(ProtocolJson.Serialize(body), Encoding.UTF8, "application/json");
        }

        try
        {
            using HttpResponseMessage response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.Content.Headers.ContentLength > AgentOptions.MaxRequestBodyBytes)
            {
                throw new InvalidDataException("Probe response exceeded the size limit.");
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (Encoding.UTF8.GetByteCount(content) > AgentOptions.MaxRequestBodyBytes)
            {
                throw new InvalidDataException("Probe response exceeded the size limit.");
            }

            return new ProbeResponse(response.StatusCode, content);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
            or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ProbeUnavailableException(error);
        }
    }

    public void Dispose() => _client.Dispose();
}
