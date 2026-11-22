using System.Security.Cryptography;
using System.Text;

namespace Mofumofu.RemoteProtocol;

public static class TokenUtilities
{
    public static string GenerateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(ProtocolConstants.TokenRandomBytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        return FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);

    public static bool IsValidToken(string? token)
    {
        if (token is null || token.Length != ProtocolConstants.TokenHexLength)
        {
            return false;
        }

        foreach (char character in token)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidPlayerName(string? playerName)
    {
        if (string.IsNullOrEmpty(playerName) || playerName.Length > ProtocolConstants.MaxPlayerNameLength)
        {
            return false;
        }

        foreach (char character in playerName)
        {
            if (!((character >= '0' && character <= '9')
                || (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > ProtocolConstants.MaxCommandLength)
        {
            return false;
        }

        foreach (char character in command)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}
