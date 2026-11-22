using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mofumofu.Launcher.Services.Servers;
using Mofumofu.RemoteProtocol;
using Xunit;

namespace Mofumofu.Launcher.Tests;

public sealed class RemoteApiClientTests
{
    [Fact]
    public void Pairing_link_uses_explicit_api_and_ignores_capability_hints()
    {
        string host = Uri.EscapeDataString("https://mc.example.com");
        string token = string.Concat(Enumerable.Repeat("ab", 32));
        RemotePairingRequest pairing = RemotePairingService.Parse(
            $"mofumofu://pair?host={host}&token={token}&instance=survival&api=1.0&caps=control");

        Assert.Equal("https://mc.example.com", pairing.BaseUrl);
        Assert.Equal("survival", pairing.InstanceId);
        Assert.Equal(token, pairing.RootToken);
        Assert.Throws<ArgumentException>(() => new RemoteApiClient("http://mc.example.com", "survival"));
    }

    [Fact]
    public async Task Control_uses_server_side_prepare_and_execute()
    {
        using var handler = new FakeHandler(
            Response(new ConfirmationPrepareResponse
            {
                ConfirmationId = "cfm-1",
                Action = ProtocolConfirmationActions.ControlStart,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60),
            }),
            Response(new ConfirmationExecuteResponse
            {
                Operation = new OperationInfo { Id = "op-1", State = ProtocolOperationStates.Succeeded },
            }));
        using var client = new RemoteApiClient("https://mc.example.com", "survival", handler);

        ConfirmationExecuteResponse result = await client.ControlAsync(ProtocolControlActions.Start, "secret-token");

        Assert.Equal("op-1", result.Operation.Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/v1/servers/survival/control/prepare", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/v1/servers/survival/confirm/execute", handler.Requests[1].Uri.AbsolutePath);
        Assert.Contains("\"action\":\"start\"", handler.Requests[0].Body);
        Assert.Contains("cfm-1", handler.Requests[1].Body);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer secret-token", request.Authorization));
    }

    [Fact]
    public async Task Handshake_preserves_unknown_capabilities_and_api_errors_keep_codes()
    {
        using var handshakeHandler = new FakeHandler(Response(new HandshakeResponse
        {
            InstanceId = "survival",
            Capabilities = [ProtocolCapabilities.Read, "future.capability"],
        }));
        using var client = new RemoteApiClient("https://mc.example.com", "survival", handshakeHandler);
        HandshakeResponse handshake = await client.HandshakeAsync("token");
        Assert.Contains("future.capability", handshake.Capabilities);

        using var errorHandler = new FakeHandler(Response(new ApiError
        {
            Code = ProtocolErrors.RevisionConflict,
            Message = "revision mismatch",
        }, HttpStatusCode.Conflict));
        using var failingClient = new RemoteApiClient("https://mc.example.com", "survival", errorHandler);
        RemoteApiException error = await Assert.ThrowsAsync<RemoteApiException>(() =>
            failingClient.GetStatusAsync("token"));
        Assert.Equal(ProtocolErrors.RevisionConflict, error.Code);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    private static HttpResponseMessage Response<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(ProtocolJson.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!, request.Headers.Authorization?.ToString() ?? "", body));
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(Uri Uri, string Authorization, string Body);
}
