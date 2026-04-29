// -----------------------------------------------------------------------
// <copyright file="AttachFileToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class AttachFileToolTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly AttachFileTool _tool = new(new ToolConfig());

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Valid_file_within_session_directory_succeeds()
    {
        var filePath = Path.Combine(_dir.Path, "report.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var context = new ToolExecutionContext("test-session", _dir.Path);
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
        await File.WriteAllTextAsync(outsidePath, "sensitive data", TestContext.Current.CancellationToken);

        try
        {
            var context = new ToolExecutionContext("test-session", _dir.Path);
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
        var context = new ToolExecutionContext("test-session", _dir.Path);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = Path.Combine(_dir.Path, "..", "..", "etc", "passwd")
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("session directory", result);
    }

    [Fact]
    public async Task Missing_file_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "nonexistent.png");
        var context = new ToolExecutionContext("test-session", _dir.Path);
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
        var context = new ToolExecutionContext("test-session", _dir.Path);
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
        var filePath = Path.Combine(_dir.Path, "abc123.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var context = new ToolExecutionContext("test-session", _dir.Path);
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

    [Fact]
    public async Task Successful_attach_populates_file_attachments_on_context()
    {
        var filePath = Path.Combine(_dir.Path, "chart.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var context = new ToolExecutionContext("test-session", _dir.Path);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = filePath
        };

        await _tool.ExecuteAsync(args, context, CancellationToken.None);

        var attachment = Assert.Single(context.FileAttachments);
        Assert.Equal(filePath, attachment.FilePath);
        Assert.Equal("chart.png", attachment.FileName);
        Assert.Equal("image/png", attachment.MimeType);
    }

    [Fact]
    public async Task Failed_attach_does_not_populate_file_attachments()
    {
        var context = new ToolExecutionContext("test-session", _dir.Path);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = Path.Combine(_dir.Path, "nonexistent.png")
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Empty(context.FileAttachments);
    }

    [Fact]
    public async Task Prefix_collision_path_is_rejected()
    {
        var outsideDir = _dir.Path + "-outside";
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "sensitive", TestContext.Current.CancellationToken);

        var context = new ToolExecutionContext("test-session", _dir.Path);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = outsideFile
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("session directory", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.FileAttachments);
    }

    [Fact]
    public async Task Symlink_to_outside_file_is_rejected()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), $"netclaw-outside-{Guid.NewGuid():N}.txt");
        var symlinkPath = Path.Combine(_dir.Path, "linked.txt");

        await File.WriteAllTextAsync(outsideFile, "sensitive data", TestContext.Current.CancellationToken);

        try
        {
            File.CreateSymbolicLink(symlinkPath, outsideFile);

            var context = new ToolExecutionContext("test-session", _dir.Path);
            var args = new Dictionary<string, object?>
            {
                ["Path"] = symlinkPath
            };

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Error", result);
            Assert.Contains("session directory", result, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(context.FileAttachments);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        finally
        {
            if (File.Exists(symlinkPath))
                File.Delete(symlinkPath);
            if (File.Exists(outsideFile))
                File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task File_from_sibling_session_directory_is_copied_and_attached()
    {
        var sessionsRoot = Path.Combine(_dir.Path, "sessions");
        var currentSessionDir = Path.Combine(sessionsRoot, "current");
        var siblingSessionDir = Path.Combine(sessionsRoot, "sibling");
        Directory.CreateDirectory(currentSessionDir);
        Directory.CreateDirectory(siblingSessionDir);

        var sourcePath = Path.Combine(siblingSessionDir, "report.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var context = new ToolExecutionContext("signalr/thread-1", currentSessionDir)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };
        var args = new Dictionary<string, object?>
        {
            ["Path"] = sourcePath,
            ["DisplayName"] = "Copied Report.png"
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("File attached", result);
        Assert.Contains("copied into current session", result, StringComparison.OrdinalIgnoreCase);

        var attachment = Assert.Single(context.FileAttachments);
        Assert.StartsWith(Path.Combine(currentSessionDir, "attachments"), attachment.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(attachment.FilePath));
        Assert.Equal("Copied Report.png", attachment.FileName);
        Assert.Equal("image/png", attachment.MimeType);
    }

    [Fact]
    public async Task Public_context_cannot_attach_file_outside_session_directory()
    {
        var outsidePath = Path.Combine(_dir.Path, "outside.txt");
        await File.WriteAllTextAsync(outsidePath, "secret", TestContext.Current.CancellationToken);

        var sessionDir = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(sessionDir);

        var context = new ToolExecutionContext("slack/thread-1", sessionDir)
        {
            Audience = TrustAudience.Public.ToWireValue(),
            Boundary = SecurityPolicyDefaults.PublicBoundary,
            ChannelType = "slack"
        };

        var args = new Dictionary<string, object?>
        {
            ["Path"] = outsidePath
        };

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Public trust context may only access files inside the current session directory", result);
        Assert.Empty(context.FileAttachments);
    }

    [Fact]
    public async Task Symlink_from_sibling_session_to_outside_root_is_rejected()
    {
        var sessionsRoot = Path.Combine(_dir.Path, "sessions");
        var currentSessionDir = Path.Combine(sessionsRoot, "current");
        var siblingSessionDir = Path.Combine(sessionsRoot, "sibling");
        Directory.CreateDirectory(currentSessionDir);
        Directory.CreateDirectory(siblingSessionDir);

        var outsidePath = Path.Combine(_dir.Path, "outside.txt");
        await File.WriteAllTextAsync(outsidePath, "secret", TestContext.Current.CancellationToken);

        var symlinkPath = Path.Combine(siblingSessionDir, "linked.txt");
        try
        {
            File.CreateSymbolicLink(symlinkPath, outsidePath);

            var context = new ToolExecutionContext("signalr/thread-1", currentSessionDir)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr"
            };
            var args = new Dictionary<string, object?>
            {
                ["Path"] = symlinkPath
            };

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Error", result);
            Assert.Contains("session", result, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(context.FileAttachments);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        finally
        {
            if (File.Exists(symlinkPath))
                File.Delete(symlinkPath);
        }
    }
}
