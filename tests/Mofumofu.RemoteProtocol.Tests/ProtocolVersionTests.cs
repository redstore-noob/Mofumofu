using Mofumofu.RemoteProtocol;
using Xunit;

namespace Mofumofu.RemoteProtocol.Tests;

public sealed class ProtocolVersionTests
{
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("1.99")]
    public void SameMajorVersion_IsCompatible(string version)
    {
        Assert.True(ProtocolVersion.IsCompatible(version));
        Assert.Null(ProtocolVersion.GetCompatibilityError(version));
        ProtocolVersion.EnsureCompatible(version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("v1.0")]
    [InlineData("1.-1")]
    [InlineData(" 1.0")]
    public void MalformedVersion_ReturnsExplicitInvalidVersionError(string? version)
    {
        ApiError error = Assert.IsType<ApiError>(ProtocolVersion.GetCompatibilityError(version));

        Assert.Equal(ProtocolErrors.InvalidApiVersion, error.Code);
        Assert.Equal(ProtocolConstants.ApiVersion, error.Details!["supportedVersion"]);
        Assert.Contains("major.minor", error.Message, StringComparison.Ordinal);
        Assert.False(ProtocolVersion.IsCompatible(version));
    }

    [Theory]
    [InlineData("0.9", "0")]
    [InlineData("2.0", "2")]
    [InlineData("10.0", "10")]
    public void DifferentMajorVersion_ReturnsExplicitCompatibilityError(string version, string expectedMajor)
    {
        ApiError error = Assert.IsType<ApiError>(ProtocolVersion.GetCompatibilityError(version));

        Assert.Equal(ProtocolErrors.UnsupportedApiVersion, error.Code);
        Assert.Equal(expectedMajor, error.Details!["requestedMajor"]);
        Assert.Equal("1", error.Details["supportedMajor"]);
        Assert.Contains("not compatible", error.Message, StringComparison.Ordinal);
        Assert.Contains("1.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCompatible_ThrowsExceptionCarryingApiError()
    {
        ProtocolVersionException exception = Assert.Throws<ProtocolVersionException>(
            () => ProtocolVersion.EnsureCompatible("2.0"));

        Assert.Equal(ProtocolErrors.UnsupportedApiVersion, exception.Error.Code);
        Assert.Equal(exception.Error.Message, exception.Message);
    }
}
