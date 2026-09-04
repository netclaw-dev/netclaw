// -----------------------------------------------------------------------
// <copyright file="PublicAudienceFileAccessPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using static Netclaw.Actors.Tests.Tools.PathAccessDecisionAssertions;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Verifies that <see cref="PathAccessPolicy"/> enforces audience-dependent
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
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var publicContext = CreateContext(TrustAudience.Public);

        var roots = policy.GetTrustedRoots(publicContext, PathAccessPolicy.FileOperation.Read);

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
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var context = CreateContext(audience);

        var roots = policy.GetTrustedRoots(context, PathAccessPolicy.FileOperation.Read);

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
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var publicContext = CreateContext(TrustAudience.Public);

        var roots = policy.GetTrustedRoots(publicContext, PathAccessPolicy.FileOperation.Read);

        Assert.Contains(roots, r =>
            r.Equals(Normalize(_sessionDir), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Public_audience_denied_path_error_does_not_leak_root_paths()
    {
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var publicContext = CreateContext(TrustAudience.Public);

        // Try to read a file outside the session directory
        var outsidePath = Path.Combine(_paths.SkillsDirectory, "secret-skill", "SKILL.md");
        var decision = policy.Evaluate(
            outsidePath,
            publicContext,
            PathAccessPolicy.FileOperation.Read);

        AssertDenied(decision, Path.GetFullPath(outsidePath));
        // Error must mention "Public" audience but should contain only session-scoped
        // roots (the session dir), not global infrastructure paths
        Assert.Contains("Public", decision.Error);
        Assert.DoesNotContain(_sessionDir, decision.Error);
        Assert.DoesNotContain("configured roots", decision.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_paths.SkillsDirectory, decision.Error);
        Assert.DoesNotContain(_paths.IdentityDirectory, decision.Error);
        Assert.DoesNotContain(_paths.WorkspacesDirectory, decision.Error);
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

        var policy = new PathAccessPolicy(toolConfig, _paths, new ToolPathPolicy([]));
        var publicContext = CreateContext(TrustAudience.Public);

        var path = Path.Combine(Path.GetTempPath(), "netclaw-public-denied.txt");
        var decision = policy.Evaluate(
            path,
            publicContext,
            PathAccessPolicy.FileOperation.Write);

        AssertDenied(decision, Path.GetFullPath(path));
        Assert.Contains("Public", decision.Error);
        Assert.Contains("does not allow", decision.Error);
        // Ensure no internal paths leak
        Assert.DoesNotContain(_paths.BasePath, decision.Error);
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
