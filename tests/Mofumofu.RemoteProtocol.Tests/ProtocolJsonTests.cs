using Mofumofu.RemoteProtocol;
using Xunit;

namespace Mofumofu.RemoteProtocol.Tests;

public sealed class ProtocolJsonTests
{
    [Fact]
    public void HandshakeSerialization_MatchesGoldenJson_AndKeepsUnknownCapabilities()
    {
        HandshakeResponse response = new()
        {
            InstanceId = "survival",
            ServerName = "Survival",
            DeploymentMode = ProtocolDeploymentModes.Agent,
            ServerImplementationVersion = "agent-1.0.0",
            Capabilities = [ProtocolCapabilities.Read, ProtocolCapabilities.Console, "future.capability"],
            Scopes = [ProtocolScopes.Read],
            ServerTime = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero)
        };

        const string expected = """{"apiVersion":"1.0","instanceId":"survival","serverName":"Survival","deploymentMode":"agent","serverImplementationVersion":"agent-1.0.0","capabilities":["read","console","future.capability"],"scopes":["read"],"serverTime":"2026-11-22T08:00:00+00:00","webSocketPath":"/ws"}""";
        string json = ProtocolJson.Serialize(response);

        Assert.Equal(expected, json);
        HandshakeResponse roundTrip = ProtocolJson.Deserialize<HandshakeResponse>(json);
        Assert.Contains("future.capability", roundTrip.Capabilities);
        Assert.Equal("1.0", roundTrip.ApiVersion);
    }

    [Fact]
    public void WebSocketSerialization_MatchesGoldenJson_AndRoundTripsConcreteFrame()
    {
        WebSocketFrame frame = new ResetFrame
        {
            Cursor = "cursor-18",
            SentAt = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero),
            Reason = "cache_overflow"
        };

        const string expected = """{"type":"reset","apiVersion":"1.0","cursor":"cursor-18","sentAt":"2026-11-22T08:00:00+00:00","reason":"cache_overflow","requiresSnapshot":true}""";
        string json = ProtocolJson.Serialize(frame);

        Assert.Equal(expected, json);
        ResetFrame roundTrip = Assert.IsType<ResetFrame>(ProtocolJson.Deserialize<WebSocketFrame>(json));
        Assert.Equal("cache_overflow", roundTrip.Reason);
        Assert.True(roundTrip.RequiresSnapshot);
    }

    [Fact]
    public void ApiErrorSerialization_MatchesGoldenJson_AndOmitsNullOptionals()
    {
        ApiError error = new()
        {
            Code = ProtocolErrors.RevisionConflict,
            Message = "The resource revision does not match.",
            Details = new Dictionary<string, string> { ["path"] = "server.properties" },
            RequestId = "req-1"
        };

        const string expected = """{"apiVersion":"1.0","code":"revision_conflict","message":"The resource revision does not match.","details":{"path":"server.properties"},"requestId":"req-1"}""";
        Assert.Equal(expected, ProtocolJson.Serialize(error));
    }

    [Fact]
    public void PropertiesPatch_PreservesDictionaryKeysAndNullRemoval()
    {
        ServerPropertiesPatchRequest request = new()
        {
            ExpectedRevision = "r1",
            Changes = new Dictionary<string, string?>
            {
                ["max-players"] = "20",
                ["View-Distance"] = null
            }
        };

        const string expected = """{"apiVersion":"1.0","expectedRevision":"r1","changes":{"max-players":"20","View-Distance":null}}""";
        Assert.Equal(expected, ProtocolJson.Serialize(request));
        Assert.IsAssignableFrom<ConfirmationPrepareRequest>(request);
    }

    [Fact]
    public void FileCreateAndRenameSerialization_MatchesGoldenJson()
    {
        FileCreateRequest create = new() { Path = "config/new.txt", IsDirectory = false };
        FileRenameRequest rename = new() { Path = "config/old.txt", NewName = "new.txt", ExpectedRevision = "r7" };
        FileMutationResponse mutation = new()
        {
            Path = "config/new.txt",
            Revision = "r8",
            IsDirectory = false,
            ModifiedAt = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero)
        };

        Assert.Equal("""{"apiVersion":"1.0","path":"config/new.txt","isDirectory":false}""", ProtocolJson.Serialize(create));
        Assert.Equal("""{"apiVersion":"1.0","path":"config/old.txt","newName":"new.txt","expectedRevision":"r7"}""", ProtocolJson.Serialize(rename));
        Assert.Equal("""{"apiVersion":"1.0","path":"config/new.txt","revision":"r8","isDirectory":false,"modifiedAt":"2026-11-22T08:00:00+00:00"}""", ProtocolJson.Serialize(mutation));
    }

    [Fact]
    public void BackupDeleteSerialization_IsPrepareRequestWithBackupId()
    {
        BackupDeleteRequest request = new() { BackupId = "bkp-42" };

        Assert.Equal("""{"apiVersion":"1.0","backupId":"bkp-42"}""", ProtocolJson.Serialize(request));
        Assert.IsAssignableFrom<ConfirmationPrepareRequest>(request);
    }

    [Fact]
    public void ControlStart_IsAlsoPrepareRequest()
    {
        ControlRequest request = new() { Action = ProtocolControlActions.Start };

        Assert.Equal("""{"apiVersion":"1.0","action":"start"}""", ProtocolJson.Serialize(request));
        Assert.IsAssignableFrom<ConfirmationPrepareRequest>(request);
    }
}
