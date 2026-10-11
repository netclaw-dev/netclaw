// -----------------------------------------------------------------------
// <copyright file="PathAccessPolicyMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class PathAccessPolicyMutationTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        "netclaw-mutation-tests",
        Guid.NewGuid().ToString("N"));
    private readonly NetclawPaths _paths;
    private readonly SessionStoragePaths _storage;
    private readonly PathAccessPolicy _policy;

    public PathAccessPolicyMutationTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")));
        _policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
    }

    [Fact]
    public void Team_roots_exclude_shared_session_directories()
    {
        var roots = _policy.GetTrustedRoots(
            CreateContext(TrustAudience.Team),
            PathAccessPolicy.FileOperation.Read);

        Assert.DoesNotContain(_paths.SessionsDirectory, roots);
        Assert.DoesNotContain(_paths.SessionLogsDirectory, roots);
    }

    [Fact]
    public void Personal_roots_include_shared_session_directories()
    {
        var roots = _policy.GetTrustedRoots(
            CreateContext(TrustAudience.Personal),
            PathAccessPolicy.FileOperation.Read);

        Assert.Contains(_paths.SessionsDirectory, roots);
        Assert.Contains(_paths.SessionLogsDirectory, roots);
    }

    // A reviewed phrase may read each path that the audience may read,
    // attended or not (D2). A protected path and a relative path never qualify.
    [Theory]
    [InlineData(true, "outside", true)]
    [InlineData(false, "outside", true)]
    [InlineData(true, "protected", false)]
    [InlineData(false, "protected", false)]
    [InlineData(true, "relative", false)]
    [InlineData(false, "skills", true)]
    public void Reviewed_shell_path_uses_the_read_authority_of_the_audience(
        bool interactive,
        string target,
        bool allowed)
    {
        var outside = Path.Combine(_basePath, "outside");
        var protectedDirectory = Path.Combine(_basePath, "protected");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(protectedDirectory);
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([protectedDirectory]));
        var context = CreateContext(TrustAudience.Personal, interactive);
        var path = target switch
        {
            "protected" => protectedDirectory,
            "relative" => "outside",
            "skills" => _paths.SkillsDirectory,
            _ => outside
        };
        var style = OperatingSystem.IsWindows() ? ShellPathStyle.Windows : ShellPathStyle.Posix;

        var decision = policy.EvaluateReviewedShellPath(path, context, style);

        Assert.Equal(allowed, decision is PathAccessPolicy.PathAccessDecision.Allowed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    private ToolInvocationContext CreateContext(TrustAudience audience, bool interactive = false) =>
        new(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound("signalr/current", _storage),
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = interactive
                    ? new InteractiveApprovalCapability.Available(new OperatorBridge())
                    : new InteractiveApprovalCapability.Unavailable()
            },
            ToolExecutionTimeout.Default);

    // The path decision never asks the bridge. It only needs an interactive run.
    private sealed class OperatorBridge : IParentApprovalBridge;
}
