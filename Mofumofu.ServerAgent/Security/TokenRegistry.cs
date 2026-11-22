using System.Text.Json;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent.Security;

public sealed class TokenRegistrySnapshot
{
    public int SchemaVersion { get; init; }
    public string InstanceId { get; init; } = string.Empty;
    public int Generation { get; init; }
    public string LinkSecret { get; init; } = string.Empty;
    public List<TokenRegistryToken> Tokens { get; init; } = [];
}

public sealed class TokenRegistryToken
{
    public string TokenId { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string[] Scopes { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    public bool Revoked { get; init; }
    public bool Root { get; init; }
}

public sealed record AuthenticationContext(
    string TokenId,
    IReadOnlySet<string> Scopes,
    bool IsRoot,
    string Authorization,
    int RegistryGeneration);

public sealed class TokenRegistry
{
    private const int SupportedSchemaVersion = 1;
    private const int MaximumRegistryBytes = AgentOptions.MaxRequestBodyBytes;
    private readonly AgentOptions _options;
    private readonly IClock _clock;

    public TokenRegistry(AgentOptions options, IClock clock)
    {
        _options = options;
        _clock = clock;
    }

    public TokenRegistrySnapshot LoadSnapshot(string instanceId)
    {
        try
        {
            using FileStream stream = new(_options.ProbeRegistryPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumRegistryBytes)
            {
                throw new InvalidDataException("Registry is too large.");
            }

            TokenRegistrySnapshot snapshot = JsonSerializer.Deserialize<TokenRegistrySnapshot>(
                stream, ProtocolJson.Options) ?? throw new InvalidDataException("Registry was null.");
            ValidateSnapshot(snapshot, instanceId);
            return snapshot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, ProtocolErrors.Unauthorized,
                "The authentication registry is unavailable or invalid.");
        }
    }

    public AuthenticationContext Authenticate(string? authorization, string instanceId)
    {
        string token = ParseBearer(authorization);
        TokenRegistrySnapshot snapshot = LoadSnapshot(instanceId);
        string hash = TokenUtilities.HashToken(token);
        TokenRegistryToken? matched = null;
        foreach (TokenRegistryToken candidate in snapshot.Tokens)
        {
            if (TokenUtilities.FixedTimeEquals(hash, candidate.Hash))
            {
                matched = candidate;
            }
        }

        if (matched is null || matched.Revoked ||
            matched.NotAfter is { } notAfter && notAfter <= _clock.UtcNow)
        {
            throw Unauthorized();
        }

        return new AuthenticationContext(matched.TokenId,
            new HashSet<string>(matched.Scopes, StringComparer.Ordinal), matched.Root,
            authorization!, snapshot.Generation);
    }

    private static string ParseBearer(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal) ||
            authorization.Length != prefix.Length + ProtocolConstants.TokenHexLength)
        {
            throw Unauthorized();
        }

        string token = authorization[prefix.Length..];
        if (!TokenUtilities.IsValidToken(token))
        {
            throw Unauthorized();
        }

        return token;
    }

    private static void ValidateSnapshot(TokenRegistrySnapshot snapshot, string instanceId)
    {
        if (snapshot.SchemaVersion != SupportedSchemaVersion || snapshot.Generation < 0 ||
            !string.Equals(snapshot.InstanceId, instanceId, StringComparison.Ordinal) ||
            !Hex.IsAnyCase(snapshot.LinkSecret, 64) || snapshot.Tokens is null)
        {
            throw new InvalidDataException("Registry envelope is invalid.");
        }

        HashSet<string> tokenIds = new(StringComparer.Ordinal);
        HashSet<string> hashes = new(StringComparer.Ordinal);
        foreach (TokenRegistryToken token in snapshot.Tokens)
        {
            if (string.IsNullOrWhiteSpace(token.TokenId) || token.TokenId.Length > 128 ||
                token.TokenId.Any(char.IsControl) || !tokenIds.Add(token.TokenId) ||
                !Hex.IsLower(token.Hash, 64) || !hashes.Add(token.Hash) ||
                token.CreatedAt.Offset != TimeSpan.Zero ||
                token.NotAfter is { } notAfter && notAfter.Offset != TimeSpan.Zero ||
                token.Scopes is null || token.Scopes.Length > ProtocolScopes.All.Count)
            {
                throw new InvalidDataException("Registry token is invalid.");
            }

            HashSet<string> scopes = new(StringComparer.Ordinal);
            foreach (string scope in token.Scopes)
            {
                if (!ProtocolScopes.All.Contains(scope, StringComparer.Ordinal) || !scopes.Add(scope))
                {
                    throw new InvalidDataException("Registry scope is invalid.");
                }
            }
        }
    }

    private static ApiException Unauthorized() =>
        new(StatusCodes.Status401Unauthorized, ProtocolErrors.Unauthorized,
            "A valid, active Bearer token is required.");
}
