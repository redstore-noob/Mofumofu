using Mofumofu.RemoteProtocol;
using Xunit;

namespace Mofumofu.RemoteProtocol.Tests;

public sealed class ProtocolConstantsTests
{
    [Fact]
    public void ProtocolBaseAndConfirmation_MatchApprovedContract()
    {
        Assert.Equal("/v1/servers/{instanceId}", ProtocolConstants.ServerBasePath);
        Assert.Equal("/v1/servers/survival%20east", ProtocolConstants.GetServerBasePath("survival east"));
        Assert.Equal("1.0", ProtocolConstants.ApiVersion);
        Assert.Equal(60, ProtocolConstants.ConfirmationLifetimeSeconds);
        Assert.Equal(60, new ConfirmationPrepareResponse().ExpiresInSeconds);
    }

    [Fact]
    public void ScopesAndCapabilities_ContainRequiredConstants()
    {
        Assert.Equal(
            ["read", "players", "files", "backup", "control", "command"],
            ProtocolScopes.All);

        Assert.Contains(ProtocolCapabilities.Console, ProtocolCapabilities.Known);
        Assert.Contains(ProtocolCapabilities.Events, ProtocolCapabilities.Known);
        Assert.Contains(ProtocolCapabilities.Properties, ProtocolCapabilities.Known);
        Assert.Contains(ProtocolCapabilities.Command, ProtocolCapabilities.Known);
        Assert.Contains(ProtocolCapabilities.Tokens, ProtocolCapabilities.Known);
    }

    [Fact]
    public void ConfirmationActions_CoverEveryPrepareEndpoint()
    {
        Assert.Equal("control.start", ProtocolConfirmationActions.ControlStart);
        Assert.Equal("control.stop", ProtocolConfirmationActions.ControlStop);
        Assert.Equal("control.restart", ProtocolConfirmationActions.ControlRestart);
        Assert.Equal("control.kill", ProtocolConfirmationActions.ControlKill);
        Assert.Equal("backup.restore", ProtocolConfirmationActions.BackupRestore);
        Assert.Equal("backup.delete", ProtocolConfirmationActions.BackupDelete);
        Assert.Equal("file.delete", ProtocolConfirmationActions.FileDelete);
        Assert.Equal("properties.patch", ProtocolConfirmationActions.PropertiesPatch);
    }

    [Fact]
    public void EmptyInstanceId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ProtocolConstants.GetServerBasePath(" "));
    }
}
