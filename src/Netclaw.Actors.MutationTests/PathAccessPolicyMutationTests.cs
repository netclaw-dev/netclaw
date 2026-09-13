// -----------------------------------------------------------------------
// <copyright file="PathAccessPolicyMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
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

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    private ToolInvocationContext CreateContext(TrustAudience audience) =>
        new(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound("signalr/current", _storage),
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
            },
            ToolExecutionTimeout.Default);
}
