using Mofumofu.RemoteProtocol;
using Xunit;

namespace Mofumofu.RemoteProtocol.Tests;

public sealed class TokenUtilitiesTests
{
    [Fact]
    public void GenerateToken_Returns32RandomBytesAsLowercaseHex()
    {
        string first = TokenUtilities.GenerateToken();
        string second = TokenUtilities.GenerateToken();

        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.True(TokenUtilities.IsValidToken(first));
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("Mofumofu-猫", "9a56d9c3ec12d35ae9eb65615c1aabe40deb8b729ec15ee0b399678c68e96cd4")]
    public void HashToken_MatchesSha256Utf8Vectors(string value, string expected)
    {
        Assert.Equal(expected, TokenUtilities.HashToken(value));
    }

    [Fact]
    public void FixedTimeEquals_UsesContentEquality()
    {
        byte[] left = [1, 2, 3, 4];
        byte[] same = [1, 2, 3, 4];
        byte[] different = [1, 2, 3, 5];

        Assert.True(TokenUtilities.FixedTimeEquals(left, same));
        Assert.False(TokenUtilities.FixedTimeEquals(left, different));
        Assert.True(TokenUtilities.FixedTimeEquals("abc", "abc"));
        Assert.False(TokenUtilities.FixedTimeEquals("abc", "abd"));
        Assert.False(TokenUtilities.FixedTimeEquals("abc", "abcd"));
        Assert.False(TokenUtilities.FixedTimeEquals(null, "abc"));
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidToken_Enforces64CharacterLowercaseHex(string? token, bool expected)
    {
        Assert.Equal(expected, TokenUtilities.IsValidToken(token));
    }

    [Theory]
    [InlineData("Steve", true)]
    [InlineData("Alex_2026", true)]
    [InlineData("abcdefghijklmnop", true)]
    [InlineData("abcdefghijklmnopq", false)]
    [InlineData("bad-name", false)]
    [InlineData("bad name", false)]
    [InlineData("玩家", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidPlayerName_EnforcesMinecraftNameRules(string? playerName, bool expected)
    {
        Assert.Equal(expected, TokenUtilities.IsValidPlayerName(playerName));
    }

    [Fact]
    public void IsValidCommand_EnforcesLengthAndSingleConsoleCommand()
    {
        Assert.True(TokenUtilities.IsValidCommand("say hello"));
        Assert.True(TokenUtilities.IsValidCommand("/op Steve"));
        Assert.True(TokenUtilities.IsValidCommand(new string('a', 256)));

        Assert.False(TokenUtilities.IsValidCommand(new string('a', 257)));
        Assert.False(TokenUtilities.IsValidCommand("say hello\nop Steve"));
        Assert.False(TokenUtilities.IsValidCommand("say hello\rop Steve"));
        Assert.False(TokenUtilities.IsValidCommand("say\0hello"));
        Assert.False(TokenUtilities.IsValidCommand(" \t "));
        Assert.False(TokenUtilities.IsValidCommand(null));
    }
}
