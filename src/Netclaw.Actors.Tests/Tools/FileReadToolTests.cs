using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class FileReadToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileReadTool _tool = new(new ToolConfig());

    public FileReadToolTests()
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
    public async Task Read_existing_file_returns_content()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        var args = new Dictionary<string, object?> { ["Path"] = filePath };
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task Read_missing_file_returns_error()
    {
        var filePath = Path.Combine(_tempDir, "nonexistent.txt");
        var args = new Dictionary<string, object?> { ["Path"] = filePath };

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Read_with_offset_and_limit()
    {
        var filePath = Path.Combine(_tempDir, "lines.txt");
        var lines = Enumerable.Range(1, 10).Select(i => $"Line {i}");
        await File.WriteAllLinesAsync(filePath, lines);

        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath,
            ["Offset"] = 3,
            ["Limit"] = 2
        };

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Line 3", result);
        Assert.Contains("Line 4", result);
        Assert.DoesNotContain("Line 2", result);
        Assert.DoesNotContain("Line 5", result);
    }

    [Fact]
    public async Task Large_file_is_truncated()
    {
        var tool = new FileReadTool(new ToolConfig { MaxOutputChars = 100 });
        var filePath = Path.Combine(_tempDir, "large.txt");
        await File.WriteAllTextAsync(filePath, new string('x', 500));

        var args = new Dictionary<string, object?> { ["Path"] = filePath };
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("[output truncated]", result);
    }

    [Fact]
    public async Task Missing_path_returns_error()
    {
        var args = new Dictionary<string, object?>();
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Path", result);
        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_arguments_returns_error()
    {
        var result = await _tool.ExecuteAsync(null, CancellationToken.None);
        Assert.Contains("No arguments provided", result);
    }
}
