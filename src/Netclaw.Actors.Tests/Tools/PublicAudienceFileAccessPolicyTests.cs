// -----------------------------------------------------------------------
// <copyright file="PublicAudienceFileAccessPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Verifies that <see cref="ScopedFileAccessPolicy"/> enforces audience-dependent
/// file root resolution. Public audience sessions must NOT receive global read roots
/// (skills, identity, workspaces) — they are confined to their session directory.
/// </summary>
public sealed class PublicAudienceFileAccessPolicyTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly NetclawPaths _paths;

    public PublicAudienceFileAccessPolicyTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "sessions", "test-session");
        Directory.CreateDirectory(_sessionDir);
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public void Public_audience_read_roots_exclude_global_roots()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var publicContext = CreateContext(TrustAudience.Public);

        var roots = policy.GetRootsForContext(publicContext, ScopedFileAccessPolicy.AccessKind.Read);

        // Public should only get session directory — no skills, identity, or workspaces
        Assert.DoesNotContain(roots, r =>
            r.Equals(Normalize(_paths.SkillsDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(roots, r =>
            r.Equals(Normalize(_paths.IdentityDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(roots, r =>
            r.Equals(Normalize(_paths.WorkspacesDirectory), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Team_and_Personal_audience_read_roots_include_global_roots(TrustAudience audience)
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var context = CreateContext(audience);

        var roots = policy.GetRootsForContext(context, ScopedFileAccessPolicy.AccessKind.Read);

        // Team and Personal should include global read roots
        Assert.Contains(roots, r =>
            r.Equals(Normalize(_paths.SkillsDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r =>
            r.Equals(Normalize(_paths.IdentityDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r =>
            r.Equals(Normalize(_paths.WorkspacesDirectory), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Public_audience_read_roots_include_session_directory()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var publicContext = CreateContext(TrustAudience.Public);

        var roots = policy.GetRootsForContext(publicContext, ScopedFileAccessPolicy.AccessKind.Read);

        Assert.Contains(roots, r =>
            r.Equals(Normalize(_sessionDir), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Public_audience_denied_path_error_does_not_leak_root_paths()
    {
        var policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        var publicContext = CreateContext(TrustAudience.Public);

        // Try to read a file outside the session directory
        var outsidePath = Path.Combine(_paths.SkillsDirectory, "secret-skill", "SKILL.md");
        var allowed = policy.TryResolveReadPath(outsidePath, publicContext, out _, out var error);

        Assert.False(allowed);
        // Error must mention "Public" audience but should contain only session-scoped
        // roots (the session dir), not global infrastructure paths
        Assert.Contains("Public", error);
        Assert.DoesNotContain(_sessionDir, error);
        Assert.DoesNotContain("configured roots", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_paths.SkillsDirectory, error);
        Assert.DoesNotContain(_paths.IdentityDirectory, error);
        Assert.DoesNotContain(_paths.WorkspacesDirectory, error);
    }

    [Fact]
    public void Public_audience_filesystem_mode_none_error_is_sanitized()
    {
        // Create a ToolConfig with Public write mode = None (default)
        var toolConfig = new ToolConfig();
        // Default Public profile has WriteFiles = Roots with session_dir, not None.
        // Create one with None explicitly:
        toolConfig.AudienceProfiles.Public.WriteFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.None
        };

        var policy = new ScopedFileAccessPolicy(toolConfig, _paths);
        var publicContext = CreateContext(TrustAudience.Public);

        var allowed = policy.TryResolveWritePath("/some/path", publicContext, out _, out var error);

        Assert.False(allowed);
        Assert.Contains("Public", error);
        Assert.Contains("does not allow", error);
        // Ensure no internal paths leak
        Assert.DoesNotContain(_paths.BasePath, error);
    }

    private ToolInvocationContext CreateContext(TrustAudience audience)
        => TestToolExecutionContext.CreateBound("test/session-1", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            ChannelType = audience == TrustAudience.Personal ? "signalr" : "slack"
        }).Invocation;

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
