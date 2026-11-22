using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Files;
using Xunit;

namespace Mofumofu.ServerAgent.Tests;

public sealed class SafeFileServiceTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly SafeFileService _files;

    public SafeFileServiceTests()
    {
        _files = new SafeFileService(_directory.Options);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("config/../outside.txt")]
    [InlineData("config/./settings.txt")]
    [InlineData("config//settings.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/outside.txt")]
    [InlineData("config\\settings.txt")]
    [InlineData("config/settings.txt ")]
    [InlineData("config/settings.txt.")]
    public void NormalizeRelativePath_RejectsTraversalRootedAndAmbiguousPaths(string path)
    {
        ApiException error = Assert.Throws<ApiException>(() => _files.NormalizeRelativePath(path));
        Assert.Equal(ProtocolErrors.InvalidPath, error.Code);
    }

    [Fact]
    public async Task CreateWriteAndRename_UseNormalizedPathsAndRevisions()
    {
        SafeFileMutation directory = await _files.CreateAsync("config", isDirectory: true);
        Assert.True(directory.IsDirectory);
        Assert.StartsWith("d_", directory.Revision, StringComparison.Ordinal);

        SafeFileMutation created = await _files.CreateAsync("config/old.txt", isDirectory: false);
        SafeFileContent written = await _files.WriteAsync(created.Path, "enabled=true\n", created.Revision);
        Assert.Equal("config/old.txt", written.Path);
        Assert.NotEqual(created.Revision, written.Revision);

        ApiException stale = await Assert.ThrowsAsync<ApiException>(() =>
            _files.WriteAsync(created.Path, "overwritten", created.Revision));
        Assert.Equal(ProtocolErrors.RevisionConflict, stale.Code);
        Assert.Equal("enabled=true\n", (await _files.ReadAsync(created.Path)).Content);

        SafeFileMutation renamed = await _files.RenameAsync(created.Path, "new.txt", written.Revision);
        Assert.Equal("config/new.txt", renamed.Path);
        Assert.False(renamed.IsDirectory);
        Assert.Equal("enabled=true\n", (await _files.ReadAsync(renamed.Path)).Content);
        Assert.False(File.Exists(Path.Combine(_directory.Instance, "config", "old.txt")));

        ApiException crossDirectory = await Assert.ThrowsAsync<ApiException>(() =>
            _files.RenameAsync(renamed.Path, "other/escape.txt", renamed.Revision));
        Assert.Equal(ProtocolErrors.InvalidPath, crossDirectory.Code);
    }

    [Fact]
    public async Task TextLimitAndRevisionConflict_DoNotLeavePartialWrites()
    {
        string path = Path.Combine(_directory.Instance, "large.txt");
        await File.WriteAllTextAsync(path, new string('x', AgentOptions.MaxTextBytes + 1));
        ApiException read = await Assert.ThrowsAsync<ApiException>(() => _files.ReadAsync("large.txt"));
        Assert.Equal(ProtocolErrors.PayloadTooLarge, read.Code);

        SafeFileMutation created = await _files.CreateAsync("settings.txt", isDirectory: false);
        string oversized = new('a', AgentOptions.MaxTextBytes + 1);
        ApiException write = await Assert.ThrowsAsync<ApiException>(() =>
            _files.WriteAsync(created.Path, oversized, created.Revision));
        Assert.Equal(ProtocolErrors.PayloadTooLarge, write.Code);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(Path.Combine(_directory.Instance, "settings.txt")));
        Assert.Empty(Directory.EnumerateFiles(_directory.Instance, ".mofu-*.tmp"));
    }

    [Fact]
    public async Task DirectoryRevision_TracksNestedChanges_AndDeleteIsRecursive()
    {
        await _files.CreateAsync("config", isDirectory: true);
        await _files.CreateAsync("config/nested", isDirectory: true);
        SafeFileMutation child = await _files.CreateAsync("config/nested/settings.txt", isDirectory: false);
        child = await WriteMutation(child, "one");
        SafeFileMutation before = await _files.GetItemAsync("config");

        child = await WriteMutation(child, "two");
        SafeFileMutation after = await _files.GetItemAsync("config");
        Assert.NotEqual(before.Revision, after.Revision);

        ApiException stale = await Assert.ThrowsAsync<ApiException>(() =>
            _files.DeleteAsync("config", before.Revision));
        Assert.Equal(ProtocolErrors.RevisionConflict, stale.Code);
        Assert.True(Directory.Exists(Path.Combine(_directory.Instance, "config")));

        await _files.DeleteAsync("config", after.Revision);
        Assert.False(Directory.Exists(Path.Combine(_directory.Instance, "config")));
    }

    [Fact]
    public async Task DirectoryRename_UsesDirectoryRevisionAndStaysInSameParent()
    {
        await _files.CreateAsync("config", isDirectory: true);
        await _files.CreateAsync("config/old", isDirectory: true);
        SafeFileMutation child = await _files.CreateAsync("config/old/settings.txt", isDirectory: false);
        await WriteMutation(child, "value");
        SafeFileMutation current = await _files.GetItemAsync("config/old");

        SafeFileMutation renamed = await _files.RenameAsync(current.Path, "new", current.Revision);

        Assert.Equal("config/new", renamed.Path);
        Assert.True(renamed.IsDirectory);
        Assert.True(File.Exists(Path.Combine(_directory.Instance, "config", "new", "settings.txt")));
        Assert.False(Directory.Exists(Path.Combine(_directory.Instance, "config", "old")));
    }

    [Fact]
    public async Task ReparsePointSegments_AreRejectedBeforeReadingOrCreating()
    {
        string outside = Path.Combine(_directory.Root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
        string link = Path.Combine(_directory.Instance, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException
            or IOException)
        {
            // Windows without Developer Mode returns ERROR_PRIVILEGE_NOT_HELD as IOException.
            Assert.Skip("This account cannot create a symbolic link for the reparse-point test.");
        }

        ApiException create = await Assert.ThrowsAsync<ApiException>(() =>
            _files.CreateAsync("linked/escape.txt", isDirectory: false));
        Assert.Equal(ProtocolErrors.InvalidPath, create.Code);
        ApiException read = await Assert.ThrowsAsync<ApiException>(() => _files.ReadAsync("linked/secret.txt"));
        Assert.Equal(ProtocolErrors.InvalidPath, read.Code);
        ApiException revision = await Assert.ThrowsAsync<ApiException>(() => _files.GetItemAsync("linked"));
        Assert.Equal(ProtocolErrors.InvalidPath, revision.Code);
        Assert.False(File.Exists(Path.Combine(outside, "escape.txt")));
    }

    private async Task<SafeFileMutation> WriteMutation(SafeFileMutation file, string content)
    {
        SafeFileContent result = await _files.WriteAsync(file.Path, content, file.Revision);
        return new SafeFileMutation(result.Path, result.Revision, false, result.ModifiedAt);
    }

    public void Dispose() => _directory.Dispose();
}
