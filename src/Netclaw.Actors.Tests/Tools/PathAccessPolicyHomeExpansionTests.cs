// -----------------------------------------------------------------------
// <copyright file="PathAccessPolicyHomeExpansionTests.cs" company="Petabridge, LLC">
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
/// Regression coverage for tilde / $HOME expansion in configured filesystem
/// roots. Without expansion, <see cref="PathAccessPolicy"/> rejects
/// access to real files under the user's home because <c>Path.GetFullPath</c>
/// treats <c>~</c> as a literal subdirectory of CWD.
/// </summary>
public sealed class PathAccessPolicyHomeExpansionTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly NetclawPaths _paths;

    public PathAccessPolicyHomeExpansionTests()
    {
        _sessionDir = Path.Combine(_dir.Path, "sessions", "test-session");
        Directory.CreateDirectory(_sessionDir);
        _paths = new NetclawPaths(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("~/repositories")]
    [InlineData("$HOME/repositories")]
    [InlineData("${HOME}/repositories")]
    public void Personal_roots_expand_shell_home_tokens(string configured)
    {
        var toolConfig = BuildPersonalWriteRootsConfig(configured);
        var policy = new PathAccessPolicy(toolConfig, _paths, new ToolPathPolicy([]));
        var context = CreateContext(TrustAudience.Personal);

        var roots = policy.GetTrustedRoots(context, PathAccessPolicy.FileOperation.Write);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = PathUtility.Normalize(Path.Combine(home, "repositories"));

        Assert.Contains(roots, r => r.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Personal_write_to_home_relative_root_is_allowed()
    {
        var toolConfig = BuildPersonalWriteRootsConfig("~/repositories");
        var policy = new PathAccessPolicy(toolConfig, _paths, new ToolPathPolicy([]));
        var context = CreateContext(TrustAudience.Personal);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = Path.Combine(home, "repositories", "any-project", "RELEASE_NOTES.md");

        var decision = policy.Evaluate(target, context, PathAccessPolicy.FileOperation.Write);

        AssertAllowed(decision, target);
    }

    [Fact]
    public void Personal_write_outside_home_relative_root_is_rejected()
    {
        var toolConfig = BuildPersonalWriteRootsConfig("~/repositories");
        var policy = new PathAccessPolicy(toolConfig, _paths, new ToolPathPolicy([]));
        var context = CreateContext(TrustAudience.Personal);

        var unrelated = Path.Combine(Path.GetTempPath(), "outside-the-root.txt");

        var decision = policy.Evaluate(unrelated, context, PathAccessPolicy.FileOperation.Write);

        AssertDenied(decision, Path.GetFullPath(unrelated));
    }

    private static ToolConfig BuildPersonalWriteRootsConfig(string configuredRoot)
    {
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.WriteFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots
        };
        toolConfig.AudienceProfiles.Personal.WriteFiles.Roots.Add(configuredRoot);
        return toolConfig;
    }

    private ToolInvocationContext CreateContext(TrustAudience audience)
        => TestToolExecutionContext.CreateBound("personal/test-session", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            ChannelType = "signalr"
        }).Invocation;
}
