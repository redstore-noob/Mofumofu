using MofuCore.Server;
using Mofumofu.Launcher.Services.Servers;
using Xunit;

namespace Mofumofu.Launcher.Tests;

public sealed class LocalServerBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = ServerManage.ServerRootDirectory;

    public LocalServerBackendTests()
    {
        ServerManage.ServerRootDirectory = _root;
        string serverDirectory = Path.Combine(_root, "survival");
        Directory.CreateDirectory(serverDirectory);
        File.WriteAllText(Path.Combine(serverDirectory, "server.json"),
            """{"id":"survival","name":"生存服","core":"paper","mcVersion":"1.21.4","port":25565,"maxPlayers":20}""");
        File.WriteAllText(Path.Combine(serverDirectory, "server.properties"), string.Empty);
    }

    [Fact]
    public async Task Description_and_stopped_status_are_mapped()
    {
        using var backend = new LocalServerBackend(new ServerInfo
        {
            Id = "survival",
            Name = "生存服",
            Core = ServerManage.CorePaper,
            McVersion = "1.21.4",
            Port = 25565,
            MaxPlayers = 20,
        });

        ServerDescription description = await backend.GetDescriptionAsync();
        BackendStatus status = await backend.GetStatusAsync(0);

        Assert.Equal("local:survival", backend.BackendKey);
        Assert.Equal("生存服", description.Name);
        Assert.Equal("127.0.0.1:25565", description.Address);
        Assert.Equal(ServerManage.StatusStopped, status.Status);
        Assert.Contains(ServerCapability.Control, backend.Capabilities);
    }

    [Fact]
    public async Task Files_and_properties_keep_existing_local_semantics()
    {
        using var backend = new LocalServerBackend(new ServerInfo { Id = "survival", Name = "生存服" });

        await backend.CreateDirectoryAsync("config");
        await backend.CreateTextFileAsync("config/server.yml");
        await backend.WriteFileAsync("config/server.yml", "enabled: true\n");
        BackendFileContent content = await backend.ReadFileAsync("config/server.yml");
        IReadOnlyList<BackendFile> files = await backend.GetFilesAsync("config");

        Assert.Equal("enabled: true\n", content.Content);
        Assert.False(string.IsNullOrWhiteSpace(content.Revision));
        Assert.Equal("config/server.yml", Assert.Single(files).RelativePath);

        BackendOperation propertyResult = await backend.SetPropertiesAsync(
            [new BackendProperty("motd", "hello"), new BackendProperty("max-players", "12")]);
        BackendProperties properties = await backend.GetPropertiesAsync();

        Assert.False(propertyResult.PendingRestart);
        Assert.Contains(properties.Values, property => property.Key == "motd" && property.Value == "hello");
        Assert.Contains(properties.Values, property => property.Key == "max-players" && property.Value == "12");
    }

    public void Dispose()
    {
        ServerManage.ServerRootDirectory = _previousRoot;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
