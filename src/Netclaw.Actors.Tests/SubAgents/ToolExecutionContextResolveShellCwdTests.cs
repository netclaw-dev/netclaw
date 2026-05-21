// -----------------------------------------------------------------------
// <copyright file="ToolExecutionContextResolveShellCwdTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Locks in the <see cref="ToolExecutionContext.ResolveShellCwd"/> fallback
/// order, including the <see cref="ToolExecutionContext.InheritedCwd"/>
/// last-resort branch added so a sub-agent's parent-cwd snapshot is visible
/// to the approval gate when neither <c>ProjectDirectory</c> nor
/// <c>SessionDirectory</c> is available on the child.
/// </summary>
public class ToolExecutionContextResolveShellCwdTests
{
    [Fact]
    public void Explicit_arg_wins_over_all_other_sources()
    {
        var context = new ToolExecutionContext("sess", "/tmp/sess")
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = "/home/user/repos/foo",
            InheritedCwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/explicit/arg", context.ResolveShellCwd("/explicit/arg"));
    }

    [Fact]
    public void ProjectDirectory_wins_over_session_directory_and_inherited_cwd()
    {
        var context = new ToolExecutionContext("sess", "/tmp/sess")
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = "/home/user/repos/foo",
            InheritedCwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/home/user/repos/foo", context.ResolveShellCwd(null));
    }

    [Fact]
    public void SessionDirectory_wins_over_inherited_cwd()
    {
        var context = new ToolExecutionContext("sess", "/tmp/sess")
        {
            Audience = TrustAudience.Personal,
            InheritedCwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/tmp/sess", context.ResolveShellCwd(null));
    }

    [Fact]
    public void Inherited_cwd_is_last_resort_fallback_before_null()
    {
        var context = new ToolExecutionContext("sess", sessionDirectory: null)
        {
            Audience = TrustAudience.Personal,
            InheritedCwd = "/home/user/repos/inherited",
        };

        Assert.Equal("/home/user/repos/inherited", context.ResolveShellCwd(null));
    }

    [Fact]
    public void Cwd_output_field_does_not_feed_resolve()
    {
        // Cwd is the per-call resolved output the approval gate writes; it
        // must not feed back into ResolveShellCwd or a stale value could
        // shadow a later ProjectDirectory/SessionDirectory change.
        var context = new ToolExecutionContext("sess", sessionDirectory: null)
        {
            Audience = TrustAudience.Personal,
            Cwd = "/stale/cwd",
        };

        Assert.Null(context.ResolveShellCwd(null));
    }

    [Fact]
    public void Returns_null_when_no_source_is_available()
    {
        var context = new ToolExecutionContext("sess", sessionDirectory: null)
        {
            Audience = TrustAudience.Personal,
        };

        Assert.Null(context.ResolveShellCwd(null));
    }
}
