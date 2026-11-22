using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mofumofu.ServerAgent.Events;
using Xunit;

namespace Mofumofu.ServerAgent.Tests;

public sealed class ProbeEventContractTests
{
    [Fact]
    public void CanonicalAndSignature_MatchSharedCrossEndVector()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "probe-event-golden.json")));
        JsonElement root = document.RootElement;

        string linkSecret = root.GetProperty("linkSecret").GetString()!;
        string instanceId = root.GetProperty("instanceId").GetString()!;
        string method = root.GetProperty("method").GetString()!;
        string path = root.GetProperty("path").GetString()!;
        string timestamp = root.GetProperty("timestamp").GetString()!;
        string nonce = root.GetProperty("nonce").GetString()!;
        string body = root.GetProperty("body").GetString()!;
        string expectedSha = root.GetProperty("bodySha256").GetString()!;
        string expectedCanonical = root.GetProperty("canonical").GetString()!;
        string expectedSignature = root.GetProperty("signature").GetString()!;

        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        Assert.Equal(expectedSha, Convert.ToHexStringLower(SHA256.HashData(bodyBytes)));

        string canonical = ProbeEventService.CanonicalForContract(method, path, instanceId, timestamp, nonce,
            bodyBytes);
        Assert.Equal(expectedCanonical, canonical);

        byte[] signature = HMACSHA256.HashData(Convert.FromHexString(linkSecret),
            Encoding.UTF8.GetBytes(canonical));
        Assert.Equal(expectedSignature, Convert.ToHexStringLower(signature));
    }
}
