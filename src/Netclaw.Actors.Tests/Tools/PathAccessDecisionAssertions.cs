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
        Assert.True(decision.Allowed, decision.Error);
        Assert.Equal(Path.GetFullPath(expectedCanonicalPath), decision.CanonicalPath);
        Assert.Empty(decision.Error);
        Assert.Null(decision.Failure);
    }

    public static void AssertDenied(
        PathAccessPolicy.PathAccessDecision decision,
        string expectedCanonicalPath,
        PathAccessPolicy.PathAccessFailure expectedFailure = PathAccessPolicy.PathAccessFailure.AccessDenied)
    {
        Assert.False(decision.Allowed);
        Assert.Equal(expectedCanonicalPath, decision.CanonicalPath);
        Assert.NotEmpty(decision.Error);
        Assert.Equal(expectedFailure, decision.Failure);
    }
}
