using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class FileWriteToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileWriteTool _tool = new();

    public FileWriteToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Write_new_file_creates_it()
    {
        var filePath = Path.Combine(_tempDir, "new.txt");
        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath,
            ["Content"] = "hello world"
        };

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Contains("bytes", result);
        Assert.Equal("hello world", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task Overwrite_existing_file()
    {
        var filePath = Path.Combine(_tempDir, "existing.txt");
        await File.WriteAllTextAsync(filePath, "old content");

        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath,
            ["Content"] = "new content"
        };

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Equal("new content", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task Creates_parent_directories()
    {
        var filePath = Path.Combine(_tempDir, "sub", "dir", "deep.txt");
        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath,
            ["Content"] = "deep content"
        };

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Successfully wrote", result);
        Assert.Equal("deep content", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task Missing_path_returns_error()
    {
        var args = new Dictionary<string, object?> { ["Content"] = "hello" };
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Path", result);
        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_content_returns_error()
    {
        var args = new Dictionary<string, object?> { ["Path"] = "/tmp/test.txt" };
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Content", result);
        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_arguments_returns_error()
    {
        var result = await _tool.ExecuteAsync(null, CancellationToken.None);
        Assert.Contains("No arguments provided", result);
    }
}
