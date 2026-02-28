using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class AttachFileToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AttachFileTool _tool = new();

    public AttachFileToolTests()
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
    public async Task Valid_file_within_session_directory_succeeds()
    {
        var filePath = Path.Combine(_tempDir, "report.png");
        await File.WriteAllBytesAsync(filePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var context = new ToolExecutionContext("test-session", _tempDir);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("File attached", result);
        Assert.Contains("report.png", result);
        Assert.Contains("image/png", result);
    }

    [Fact]
    public async Task Path_traversal_attempt_is_rejected()
    {
        // Create a file outside the session directory
        var outsidePath = Path.Combine(Path.GetTempPath(), $"netclaw-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outsidePath, "sensitive data");

        try
        {
            var context = new ToolExecutionContext("test-session", _tempDir);
            var args = new Dictionary<string, object?>
            {
                ["Path"] = outsidePath
            };

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Error", result);
            Assert.Contains("session directory", result);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task Dotdot_traversal_is_rejected()
    {
        var context = new ToolExecutionContext("test-session", _tempDir);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = Path.Combine(_tempDir, "..", "..", "etc", "passwd")
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("session directory", result);
    }

    [Fact]
    public async Task Missing_file_returns_error()
    {
        var filePath = Path.Combine(_tempDir, "nonexistent.png");
        var context = new ToolExecutionContext("test-session", _tempDir);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_path_returns_error()
    {
        var context = new ToolExecutionContext("test-session", _tempDir);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = ""
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task No_session_directory_returns_error()
    {
        var context = new ToolExecutionContext("test-session", null);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = "/tmp/anything.png"
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("session directory", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Display_name_is_used_when_provided()
    {
        var filePath = Path.Combine(_tempDir, "abc123.png");
        await File.WriteAllBytesAsync(filePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var context = new ToolExecutionContext("test-session", _tempDir);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath,
            ["DisplayName"] = "My Custom Report.png"
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("File attached", result);
        // FilenameSanitizer will clean the display name
        Assert.Contains("My Custom Report.png", result);
    }
}
