using Mofumofu.ServerAgent;

namespace Mofumofu.ServerAgent.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Instance = Path.Combine(Root, "survival");
        Directory.CreateDirectory(Instance);
        Options = new AgentOptions
        {
            ServerRootDirectory = Root,
            InstanceId = "survival",
            ProbeRegistryPath = Path.Combine(Root, "probe-tokens.json"),
            EventJournalPath = Path.Combine(Root, "events.jsonl"),
            AuditPath = Path.Combine(Root, "audit.jsonl"),
        };
    }

    public string Root { get; }
    public string Instance { get; }
    public AgentOptions Options { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class ManualClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
}
