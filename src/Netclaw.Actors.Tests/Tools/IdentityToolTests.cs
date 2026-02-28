using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class IdentityToolTests : IDisposable
{
    private readonly string _identityRoot;

    public IdentityToolTests()
    {
        _identityRoot = Path.Combine(Path.GetTempPath(), $"netclaw-identity-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_identityRoot);
        Directory.CreateDirectory(Path.Combine(_identityRoot, "soul"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_identityRoot))
            Directory.Delete(_identityRoot, recursive: true);
    }

    // ── IdentityReadTool ──

    [Fact]
    public async Task Read_existing_file()
    {
        File.WriteAllText(Path.Combine(_identityRoot, "SOUL.md"), "test content");
        var tool = new IdentityReadTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "SOUL.md" },
            CancellationToken.None);

        Assert.Equal("test content", result);
    }

    [Fact]
    public async Task Read_file_in_subdirectory()
    {
        File.WriteAllText(Path.Combine(_identityRoot, "soul", "traits.md"), "brave");
        var tool = new IdentityReadTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "soul/traits.md" },
            CancellationToken.None);

        Assert.Equal("brave", result);
    }

    [Fact]
    public async Task Read_nonexistent_returns_error()
    {
        var tool = new IdentityReadTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "MISSING.md" },
            CancellationToken.None);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Read_rejects_path_traversal_dotdot()
    {
        var tool = new IdentityReadTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "../../../etc/passwd" },
            CancellationToken.None);

        Assert.Contains("Path traversal is not allowed", result);
    }

    [Fact]
    public async Task Read_rejects_absolute_path()
    {
        var tool = new IdentityReadTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "/etc/passwd" },
            CancellationToken.None);

        Assert.Contains("Path traversal is not allowed", result);
    }

    // ── IdentityWriteTool ──

    [Fact]
    public async Task Write_creates_new_file()
    {
        var tool = new IdentityWriteTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "SOUL.md", ["Content"] = "new soul" },
            CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Equal("new soul", File.ReadAllText(Path.Combine(_identityRoot, "SOUL.md")));
    }

    [Fact]
    public async Task Write_creates_parent_directories()
    {
        var tool = new IdentityWriteTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "agents/rules.md", ["Content"] = "rule 1" },
            CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Equal("rule 1", File.ReadAllText(Path.Combine(_identityRoot, "agents", "rules.md")));
    }

    [Fact]
    public async Task Write_rejects_path_traversal()
    {
        var tool = new IdentityWriteTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "../../evil.md", ["Content"] = "hacked" },
            CancellationToken.None);

        Assert.Contains("Path traversal is not allowed", result);
    }

    [Fact]
    public async Task Write_rejects_oversized_content()
    {
        var tool = new IdentityWriteTool(_identityRoot);
        var bigContent = new string('x', IdentityWriteTool.MaxFileSizeBytes + 1);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "big.md", ["Content"] = bigContent },
            CancellationToken.None);

        Assert.Contains("exceeds maximum file size", result);
        Assert.False(File.Exists(Path.Combine(_identityRoot, "big.md")));
    }

    [Fact]
    public async Task Write_overwrites_existing_file()
    {
        File.WriteAllText(Path.Combine(_identityRoot, "SOUL.md"), "old");
        var tool = new IdentityWriteTool(_identityRoot);

        await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "SOUL.md", ["Content"] = "new" },
            CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_identityRoot, "SOUL.md")));
    }

    // ── IdentityListTool ──

    [Fact]
    public async Task List_root_shows_files_and_directories()
    {
        File.WriteAllText(Path.Combine(_identityRoot, "SOUL.md"), "soul");
        File.WriteAllText(Path.Combine(_identityRoot, "AGENTS.md"), "agents");
        var tool = new IdentityListTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { },
            CancellationToken.None);

        Assert.Contains("soul/", result);  // subdirectory
        Assert.Contains("SOUL.md", result);
        Assert.Contains("AGENTS.md", result);
    }

    [Fact]
    public async Task List_subdirectory()
    {
        File.WriteAllText(Path.Combine(_identityRoot, "soul", "traits.md"), "content");
        var tool = new IdentityListTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "soul" },
            CancellationToken.None);

        Assert.Contains("traits.md", result);
    }

    [Fact]
    public async Task List_rejects_path_traversal()
    {
        var tool = new IdentityListTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "../.." },
            CancellationToken.None);

        Assert.Contains("Path traversal is not allowed", result);
    }

    [Fact]
    public async Task List_nonexistent_directory_returns_error()
    {
        var tool = new IdentityListTool(_identityRoot);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "nonexistent" },
            CancellationToken.None);

        Assert.Contains("Directory not found", result);
    }

    // ── Round-trip ──

    [Fact]
    public async Task Write_then_read_then_list_roundtrip()
    {
        var writeTool = new IdentityWriteTool(_identityRoot);
        var readTool = new IdentityReadTool(_identityRoot);
        var listTool = new IdentityListTool(_identityRoot);

        await writeTool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "SOUL.md", ["Content"] = "roundtrip content" },
            CancellationToken.None);

        var readResult = await readTool.ExecuteAsync(
            new Dictionary<string, object?> { ["Path"] = "SOUL.md" },
            CancellationToken.None);
        Assert.Equal("roundtrip content", readResult);

        var listResult = await listTool.ExecuteAsync(
            new Dictionary<string, object?> { },
            CancellationToken.None);
        Assert.Contains("SOUL.md", listResult);
    }
}
