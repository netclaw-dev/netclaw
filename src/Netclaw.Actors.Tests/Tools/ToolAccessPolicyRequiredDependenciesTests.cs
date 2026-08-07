// -----------------------------------------------------------------------
// <copyright file="ToolAccessPolicyRequiredDependenciesTests.cs" company="Petabridge, LLC">
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
/// The shell deny-list (<see cref="ShellCommandPolicy"/>) and the protected-path
/// policy (<see cref="ToolPathPolicy"/>) are required, non-nullable dependencies
/// of <see cref="ToolAccessPolicy"/> — the type system (Nullable enabled,
/// warnings-as-errors) forbids a null at every call site, and the shell gate
/// dereferences them directly, so a stray null fails loudly rather than silently
/// skipping a check. That requirement needs no test.
///
/// What does need a test is that the controls are actually consulted once wired —
/// the enforcement the previous nullable/optional fallbacks silently lost. This
/// is the only coverage of <c>shell_references_protected_path</c> through
/// <see cref="ToolAccessPolicy.AuthorizeInvocation"/>. Hard-deny enforcement is
/// covered separately by <c>ShellApprovalCaseCatalog</c> / the disposition matrix.
/// </summary>
public sealed class ToolAccessPolicyRequiredDependenciesTests
{
    private static ToolConfig ShellConfig()
        => new() { ShellMode = ShellExecutionMode.HostAllowed };

    private static EffectivePolicyDefaults Defaults()
        => new(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false);

    private static ToolExecutionContext PersonalContext()
        => TestToolExecutionContext.CreateBound(
            "signalr/thread-1",
            null,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

    private static INetclawTool ShellTool()
        => new ShellTool(ShellConfig(), new ToolPathPolicy([]), new ShellCommandPolicy());

    [Fact]
    public void Protected_path_control_is_enforced_and_scoped()
    {
        var deniedRoot = Path.Combine(Path.GetTempPath(), "netclaw-protected-root");
        var otherRoot = Path.Combine(Path.GetTempPath(), "netclaw-open-root");
        var policy = new ToolAccessPolicy(
            ShellConfig(),
            Defaults(),
            new ShellCommandPolicy(),
            new ToolPathPolicy([deniedRoot]));

        // A command that touches the protected path is denied — the enforcement
        // a null toolPathPolicy silently lost.
        var deniedDecision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            ToolInput.Create("Command", $"cat {Path.Combine(deniedRoot, "secret.txt")}"));

        Assert.False(deniedDecision.Allowed);
        Assert.Equal("shell_references_protected_path", deniedDecision.DenyReason);

        // A command that touches a path outside the protected set is NOT denied
        // for that reason — proving the policy is actually consulted and scoped,
        // not a blanket deny.
        var otherDecision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            ToolInput.Create("Command", $"cat {Path.Combine(otherRoot, "notes.txt")}"));

        Assert.NotEqual("shell_references_protected_path", otherDecision.DenyReason);
    }
}
