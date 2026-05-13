// -----------------------------------------------------------------------
// <copyright file="ScopedFileAccessPolicyHomeExpansionTests.cs" company="Petabridge, LLC">
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

/// <summary>
/// Regression coverage for tilde / $HOME expansion in configured filesystem
/// roots. Without expansion, <see cref="ScopedFileAccessPolicy"/> rejects
/// access to real files under the user's home because <c>Path.GetFullPath</c>
/// treats <c>~</c> as a literal subdirectory of CWD.
/// </summary>
public sealed class ScopedFileAccessPolicyHomeExpansionTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _sessionDir;
    private readonly NetclawPaths _paths;

    public ScopedFileAccessPolicyHomeExpansionTests()
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
        var policy = new ScopedFileAccessPolicy(toolConfig, _paths);
        var context = CreateContext(TrustAudience.Personal);

        var roots = policy.GetRootsForContext(context, ScopedFileAccessPolicy.AccessKind.Write);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = PathUtility.Normalize(Path.Combine(home, "repositories"));

        Assert.Contains(roots, r => r.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Personal_write_to_home_relative_root_is_allowed()
    {
        var toolConfig = BuildPersonalWriteRootsConfig("~/repositories");
        var policy = new ScopedFileAccessPolicy(toolConfig, _paths);
        var context = CreateContext(TrustAudience.Personal);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = Path.Combine(home, "repositories", "any-project", "RELEASE_NOTES.md");

        var allowed = policy.TryResolveWritePath(target, context, out _, out var error);

        Assert.True(allowed, error);
    }

    [Fact]
    public void Personal_write_outside_home_relative_root_is_rejected()
    {
        var toolConfig = BuildPersonalWriteRootsConfig("~/repositories");
        var policy = new ScopedFileAccessPolicy(toolConfig, _paths);
        var context = CreateContext(TrustAudience.Personal);

        var unrelated = Path.Combine(Path.GetTempPath(), "outside-the-root.txt");

        var allowed = policy.TryResolveWritePath(unrelated, context, out _, out _);

        Assert.False(allowed);
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

    private ToolExecutionContext CreateContext(TrustAudience audience)
        => new("personal/test-session", _sessionDir)
        {
            Audience = audience.ToWireValue(),
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            ChannelType = "signalr"
        };
}
