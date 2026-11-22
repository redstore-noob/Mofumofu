using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Operations;
using Mofumofu.ServerAgent.Security;
using Xunit;

namespace Mofumofu.ServerAgent.Tests;

public sealed class ConfirmationServiceTests
{
    private const string InstanceId = "survival";

    private readonly ManualClock _clock = new();
    private readonly ConfirmationService _service;

    public ConfirmationServiceTests()
    {
        _service = new ConfirmationService(_clock);
    }

    [Fact]
    public async Task ExecuteWithinLifetime_SucceedsExactlyOnce()
    {
        string id = Prepare(ProtocolScopes.Files, Token("token-a"));
        OperationInfo operation = await _service.ExecuteAsync(InstanceId, Token("token-a"), id, default);
        Assert.Equal("op_test", operation.Id);

        ApiException replay = await Assert.ThrowsAsync<ApiException>(() =>
            _service.ExecuteAsync(InstanceId, Token("token-a"), id, default));
        Assert.Equal(ProtocolErrors.ConfirmationAlreadyUsed, replay.Code);
        Assert.Equal(410, replay.StatusCode);
    }

    [Fact]
    public async Task ExecuteAfterSixtySeconds_ReturnsExpired()
    {
        string id = Prepare(ProtocolScopes.Files, Token("token-a"));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(ProtocolConstants.ConfirmationLifetimeSeconds + 1);

        ApiException expired = await Assert.ThrowsAsync<ApiException>(() =>
            _service.ExecuteAsync(InstanceId, Token("token-a"), id, default));
        Assert.Equal(ProtocolErrors.ConfirmationExpired, expired.Code);
        Assert.Equal(410, expired.StatusCode);
    }

    [Fact]
    public async Task ExecuteWithDifferentTokenOrInstance_IsForbidden()
    {
        string id = Prepare(ProtocolScopes.Files, Token("token-a"));

        ApiException otherToken = await Assert.ThrowsAsync<ApiException>(() =>
            _service.ExecuteAsync(InstanceId, Token("token-b"), id, default));
        Assert.Equal(ProtocolErrors.Forbidden, otherToken.Code);

        ApiException otherInstance = await Assert.ThrowsAsync<ApiException>(() =>
            _service.ExecuteAsync("other", Token("token-a"), id, default));
        Assert.Equal(ProtocolErrors.Forbidden, otherInstance.Code);

        OperationInfo operation = await _service.ExecuteAsync(InstanceId, Token("token-a"), id, default);
        Assert.Equal("op_test", operation.Id);
    }

    [Fact]
    public async Task ExecuteAfterScopeRevocation_IsRejected()
    {
        string id = Prepare(ProtocolScopes.Files, Token("token-a"));
        AuthenticationContext withoutScope = new("token-a", new HashSet<string> { ProtocolScopes.Read }, false,
            "Bearer token-a", 1);

        ApiException revoked = await Assert.ThrowsAsync<ApiException>(() =>
            _service.ExecuteAsync(InstanceId, withoutScope, id, default));
        Assert.Equal(ProtocolErrors.InsufficientScope, revoked.Code);
    }

    [Fact]
    public async Task ConcurrentExecute_ConsumesConfirmationOnce()
    {
        string id = Prepare(ProtocolScopes.Files, Token("token-a"));
        Task<OperationInfo> first = Task.Run(() => _service.ExecuteAsync(InstanceId, Token("token-a"), id, default));
        Task<OperationInfo> second = Task.Run(() => _service.ExecuteAsync(InstanceId, Token("token-a"), id, default));

        int successes = 0;
        int conflicts = 0;
        foreach (Task<OperationInfo> task in new[] { first, second })
        {
            try
            {
                await task;
                successes++;
            }
            catch (ApiException error)
            {
                Assert.Equal(ProtocolErrors.ConfirmationAlreadyUsed, error.Code);
                conflicts++;
            }
        }

        Assert.Equal(1, successes);
        Assert.Equal(1, conflicts);
    }

    private string Prepare(string scope, AuthenticationContext authentication) =>
        _service.Prepare(InstanceId, authentication, scope, ProtocolConfirmationActions.FileDelete,
            "Delete config/settings.txt", new Dictionary<string, string> { ["path"] = "config/settings.txt" },
            "r_revision", _ => Task.FromResult(new OperationInfo { Id = "op_test" })).ConfirmationId;

    private static AuthenticationContext Token(string tokenId) => new(tokenId,
        new HashSet<string> { ProtocolScopes.Files }, false, "Bearer " + tokenId, 1);
}
