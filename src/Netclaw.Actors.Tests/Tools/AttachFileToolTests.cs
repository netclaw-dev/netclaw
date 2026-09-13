// -----------------------------------------------------------------------
// <copyright file="AttachFileToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class AttachFileToolTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly AttachFileTool _tool = new(new ToolConfig(), new NetclawPaths(), new ToolPathPolicy([]));

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Valid_file_within_session_directory_succeeds()
    {
        var filePath = Path.Combine(_dir.Path, "report.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var context = TestToolExecutionContext.CreateBound("test-session", _dir.Path, TrustAudience.Personal);
        var args = ToolInput.Create("Path", filePath);

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("File attached", result);
        Assert.Contains("report.png", result);
        Assert.Contains("image/png", result);
    }

    [Fact]
    public async Task Missing_session_is_invalid_and_does_not_suggest_project_declaration()
    {
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal
        });

        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Path", "report.png"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("invalid_context", result, StringComparison.Ordinal);
        Assert.DoesNotContain("set_working_directory", result, StringComparison.Ordinal);
        Assert.Equal(ToolInvocationOutcomeCategory.InvalidInput, context.Receipt?.Category);
        Assert.IsNotType<ToolInvocationReceipt.Correction>(context.Receipt);
    }

    [Fact]
    public async Task Path_traversal_attempt_is_rejected()
    {
        // Default Personal file policy is unrestricted only for interactive
        // sessions. Unattended runs remain confined to trusted roots.
        var outsidePath = Path.Combine(Path.GetTempPath(), $"netclaw-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outsidePath, "sensitive data", TestContext.Current.CancellationToken);

        try
        {
            var context = TestToolExecutionContext.CreateBound("reminder/test-session", _dir.Path, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false),
                ChannelType = "reminder"
            });
            var args = ToolInput.Create("Path", outsidePath);

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Error", result);
            Assert.Contains("trusted roots", result);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task Dotdot_traversal_is_rejected()
    {
        // Unattended Personal: dotdot escape is denied outside trusted roots (#1724).
        var context = TestToolExecutionContext.CreateBound("reminder/test-session", _dir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false),
            ChannelType = "reminder"
        });
        var args = ToolInput.Create("Path", Path.Combine(_dir.Path, "..", "..", "etc", "passwd"));

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("trusted roots", result);
    }

    [Fact]
    public async Task Missing_file_returns_error()
    {
        var filePath = Path.Combine(_dir.Path, "nonexistent.png");
        var context = TestToolExecutionContext.CreateBound("test-session", _dir.Path, TrustAudience.Personal);
        var args = ToolInput.Create("Path", filePath);

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_path_returns_error()
    {
        var context = TestToolExecutionContext.CreateBound("test-session", _dir.Path, TrustAudience.Personal);
        var args = ToolInput.Create("Path", "");

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task No_session_directory_returns_error()
    {
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal
        });
        var args = ToolInput.Create("Path", "/tmp/anything.png");

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("No session directory", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Display_name_is_used_when_provided()
    {
        var filePath = Path.Combine(_dir.Path, "abc123.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var context = TestToolExecutionContext.CreateBound("test-session", _dir.Path, TrustAudience.Personal);
        var args = ToolInput.Create("Path", filePath, "DisplayName", "My Custom Report.png");

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

        var context = TestToolExecutionContext.CreateBound("test-session", _dir.Path, TrustAudience.Personal);
        var args = ToolInput.Create("Path", filePath);

        await _tool.ExecuteAsync(args, context, CancellationToken.None);

        var attachment = Assert.Single(context.FileAttachments);
        Assert.Equal(filePath, attachment.FilePath);
        Assert.Equal("chart.png", attachment.FileName);
        Assert.Equal("image/png", attachment.MimeType.Value);
    }

    [Fact]
    public async Task Failed_attach_does_not_populate_file_attachments()
    {
        var context = TestToolExecutionContext.CreateBound("test-session", _dir.Path, TrustAudience.Personal);
        var args = ToolInput.Create("Path", Path.Combine(_dir.Path, "nonexistent.png"));

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Empty(context.FileAttachments);
    }

    [Fact]
    public async Task Prefix_collision_path_is_rejected()
    {
        // Autonomous Personal: a sibling directory sharing the session dir's
        // name prefix is outside the zone and denied (#1724).
        var outsideDir = _dir.Path + "-outside";
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "sensitive", TestContext.Current.CancellationToken);

        var context = TestToolExecutionContext.CreateBound("reminder/test-session", _dir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false),
            ChannelType = "reminder"
        });
        var args = ToolInput.Create("Path", outsideFile);

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("trusted roots", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.FileAttachments);
    }

    [Fact]
    public async Task Symlink_to_outside_file_is_rejected()
    {
        // Unattended Personal: a link in the session directory that resolves
        // outside is denied by path access policy (#1724).
        var outsideFile = Path.Combine(Path.GetTempPath(), $"netclaw-outside-{Guid.NewGuid():N}.txt");
        var symlinkPath = Path.Combine(_dir.Path, "linked.txt");

        await File.WriteAllTextAsync(outsideFile, "sensitive data", TestContext.Current.CancellationToken);

        try
        {
            File.CreateSymbolicLink(symlinkPath, outsideFile);

            var context = TestToolExecutionContext.CreateBound("reminder/test-session", _dir.Path, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false),
                ChannelType = "reminder"
            });
            var args = ToolInput.Create("Path", symlinkPath);

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            // The shared path decision rejects linked paths outright (#1724).
            Assert.Contains("Error", result);
            Assert.Contains("links inside trusted roots", result, StringComparison.OrdinalIgnoreCase);
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

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", currentSessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        });
        var args = ToolInput.Create("Path", sourcePath, "DisplayName", "Copied Report.png");

        var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("File attached", result);
        Assert.Contains("copied into current session", result, StringComparison.OrdinalIgnoreCase);

        var attachment = Assert.Single(context.FileAttachments);
        Assert.StartsWith(Path.Combine(currentSessionDir, "attachments"), attachment.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(attachment.FilePath));
        Assert.Equal("Copied Report.png", attachment.FileName);
        Assert.Equal("image/png", attachment.MimeType.Value);
    }

    [Theory]
    [InlineData("attachments")]
    [InlineData("workspace")]
    [InlineData("ancestor")]
    [InlineData("protected")]
    public async Task AuthorityRegression_AttachmentDestination_denies_before_copy(string boundary)
    {
        var paths = new NetclawPaths(_dir.Path);
        var envelope = Path.Combine(paths.SessionsDirectory, "current");
        var session = Path.Combine(envelope, "workspace");
        var outside = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(paths.SessionsDirectory);
        var source = Path.Combine(_dir.Path, "source.txt");
        await File.WriteAllTextAsync(source, "source marker", TestContext.Current.CancellationToken);

        if (boundary == "ancestor")
            Directory.CreateSymbolicLink(envelope, outside);
        else
            Directory.CreateDirectory(envelope);
        if (boundary == "workspace")
            Directory.CreateSymbolicLink(session, outside);
        else
            Directory.CreateDirectory(session);
        var attachments = Path.Combine(session, "attachments");
        if (boundary == "attachments")
            Directory.CreateSymbolicLink(attachments, outside);

        var tool = new AttachFileTool(new ToolConfig(), paths, new ToolPathPolicy(
            boundary == "protected" ? [attachments] : [], [], []));
        var before = Directory.GetFiles(outside, "*", SearchOption.AllDirectories);
        var context = TestToolExecutionContext.CreateBound("current", session, TrustAudience.Personal);
        await tool.ExecuteAsync(ToolInput.Create("Path", source), context, TestContext.Current.CancellationToken);

        Assert.Equal(before, Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
        Assert.Empty(context.FileAttachments);
        Assert.Equal("source marker", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        if (boundary != "attachments")
            Assert.False(Directory.Exists(attachments));
    }

    [Fact]
    public async Task AuthorityRegression_AttachmentDestination_copy_preserves_collision_and_direct_access()
    {
        var source = Path.Combine(_dir.Path, "report.txt");
        var session = Path.Combine(_dir.Path, "session");
        var attachments = Path.Combine(session, "attachments");
        Directory.CreateDirectory(attachments);
        await File.WriteAllTextAsync(source, "new report", TestContext.Current.CancellationToken);
        var previous = Path.Combine(attachments, "report.txt");
        await File.WriteAllTextAsync(previous, "previous report", TestContext.Current.CancellationToken);
        var context = TestToolExecutionContext.CreateBound("current", session, TrustAudience.Personal);

        await _tool.ExecuteAsync(ToolInput.Create("Path", source, "DisplayName", "Report.txt"), context,
            TestContext.Current.CancellationToken);

        var attachment = Assert.Single(context.FileAttachments);
        Assert.Equal(Path.Combine(attachments, "report-1.txt"), attachment.FilePath);
        Assert.Equal("Report.txt", attachment.FileName);
        Assert.Equal("text/plain", attachment.MimeType.Value);
        Assert.Equal("new report", await File.ReadAllTextAsync(attachment.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal("previous report", await File.ReadAllTextAsync(previous, TestContext.Current.CancellationToken));
        var direct = TestToolExecutionContext.CreateBound("current", session, TrustAudience.Personal);
        await _tool.ExecuteAsync(ToolInput.Create("Path", attachment.FilePath), direct, TestContext.Current.CancellationToken);
        Assert.Equal(attachment.FilePath, Assert.Single(direct.FileAttachments).FilePath);
        Assert.Equal(2, Directory.GetFiles(attachments).Length);
    }

    [Theory]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Team)]
    public async Task AuthorityRegression_AttachmentDestination_attach_permission_does_not_require_general_writes(TrustAudience audience)
    {
        var source = Path.Combine(_dir.Path, "report.txt");
        await File.WriteAllTextAsync(source, "report", TestContext.Current.CancellationToken);
        var session = Path.Combine(_dir.Path, "session");
        var config = new ToolConfig();
        var profile = audience == TrustAudience.Public ? config.AudienceProfiles.Public : config.AudienceProfiles.Team;
        profile.AttachFiles.Roots.Add(source);
        profile.WriteFiles.Mode = ToolFilesystemMode.None;
        var tool = new AttachFileTool(config, new NetclawPaths(_dir.Path), new ToolPathPolicy([]));
        var context = TestToolExecutionContext.CreateBound("current", session, audience);

        await tool.ExecuteAsync(ToolInput.Create("Path", source), context, TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationOutcomeCategory.Success, context.Receipt?.Category);
        var copy = Assert.Single(context.FileAttachments).FilePath;
        Assert.Equal("report", await File.ReadAllTextAsync(copy, TestContext.Current.CancellationToken));
        var write = TestToolExecutionContext.CreateBound("current", session, audience);
        await new FileWriteTool(config, new NetclawPaths(_dir.Path), new ToolPathPolicy([])).ExecuteAsync(
            ToolInput.Create("Path", copy, "Content", "replacement"), write, TestContext.Current.CancellationToken);
        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, write.Receipt?.Category);
        Assert.Equal("report", await File.ReadAllTextAsync(copy, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("protected-suffix")]
    [InlineData("file-link")]
    [InlineData("dangling-link")]
    public async Task ReviewRegression_Attachment_checks_each_destination_file(string boundary)
    {
        var source = Path.Combine(_dir.Path, "report.txt");
        var session = Path.Combine(_dir.Path, "session");
        var attachments = Path.Combine(session, "attachments");
        var outside = Path.Combine(_dir.Path, "outside.txt");
        Directory.CreateDirectory(attachments);
        await File.WriteAllTextAsync(source, "source marker", TestContext.Current.CancellationToken);
        var destination = Path.Combine(attachments, "report.txt");
        var suffix = Path.Combine(attachments, "report-1.txt");
        if (boundary == "protected-suffix")
            await File.WriteAllTextAsync(destination, "previous marker", TestContext.Current.CancellationToken);
        else
        {
            if (boundary == "file-link")
                await File.WriteAllTextAsync(outside, "outside marker", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(destination, outside);
        }
        var protectedPaths = new ToolPathPolicy(boundary == "protected-suffix" ? [suffix] : []);
        var tool = new AttachFileTool(new ToolConfig(), new NetclawPaths(_dir.Path), protectedPaths);
        var context = TestToolExecutionContext.CreateBound("probe", session, TrustAudience.Personal);

        var result = await tool.ExecuteAsync(ToolInput.Create("Path", source), context, TestContext.Current.CancellationToken);

        Assert.StartsWith("Error:", result);
        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
        Assert.Empty(context.FileAttachments);
        Assert.False(File.Exists(suffix));
        Assert.Equal("source marker", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        if (boundary == "protected-suffix")
            Assert.Equal("previous marker", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        else if (boundary == "file-link")
            Assert.Equal("outside marker", await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        else
            Assert.False(File.Exists(outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewRegression_Invalid_generated_destination_returns_a_denial(bool missingDirectory)
    {
        var output = Path.Combine(_dir.Path, "output");
        var policy = new PathAccessPolicy(new ToolConfig(), new NetclawPaths(_dir.Path), new ToolPathPolicy([]));
        var result = policy.EvaluateGeneratedDestination(missingDirectory ? Path.Combine(output, "file.txt") : "invalid\0path",
            missingDirectory ? string.Empty : output);

        var denied = Assert.IsType<PathAccessPolicy.PathAccessDecision.Denied>(result);
        Assert.Equal(PathAccessPolicy.PathAccessFailure.InvalidInput, denied.Failure);
        Assert.StartsWith("Error:", denied.Error);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Public_context_cannot_attach_file_outside_session_directory()
    {
        var outsidePath = Path.Combine(_dir.Path, "outside.txt");
        await File.WriteAllTextAsync(outsidePath, "secret", TestContext.Current.CancellationToken);

        var sessionDir = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(sessionDir);

        var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            ChannelType = "slack"
        });

        var args = ToolInput.Create("Path", outsidePath);

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

            var context = TestToolExecutionContext.CreateBound("reminder/thread-1", currentSessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false),
                ChannelType = "reminder"
            });
            var args = ToolInput.Create("Path", symlinkPath);

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
