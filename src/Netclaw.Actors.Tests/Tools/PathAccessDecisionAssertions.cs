// -----------------------------------------------------------------------
// <copyright file="PathAccessDecisionAssertions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

internal static class PathAccessDecisionAssertions
{
    public static void AssertAllowed(
        PathAccessPolicy.PathAccessDecision decision,
        string expectedCanonicalPath)
    {
        var allowed = Assert.IsType<PathAccessPolicy.PathAccessDecision.Allowed>(decision);
        Assert.Equal(Path.GetFullPath(expectedCanonicalPath), allowed.CanonicalPath);
    }

    public static void AssertDenied(
        PathAccessPolicy.PathAccessDecision decision,
        string? expectedDiagnosticPath,
        PathAccessPolicy.PathAccessFailure expectedFailure = PathAccessPolicy.PathAccessFailure.AccessDenied)
    {
        var denied = Assert.IsType<PathAccessPolicy.PathAccessDecision.Denied>(decision);
        Assert.Equal(expectedDiagnosticPath, denied.DiagnosticPath);
        Assert.NotEmpty(denied.Error);
        Assert.Equal(expectedFailure, denied.Failure);
    }
}
