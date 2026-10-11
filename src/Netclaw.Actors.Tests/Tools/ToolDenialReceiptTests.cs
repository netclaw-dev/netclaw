// -----------------------------------------------------------------------
// <copyright file="ToolDenialReceiptTests.cs" company="Petabridge, LLC">
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
/// Only an authorization denial is a denial. A file tool that meets an operating system permission error
/// reports AccessDenied too, but that is an ordinary tool failure and must not mark a reminder run.
/// </summary>
public sealed class ToolDenialReceiptTests
{
    private static ToolExecutionContext NewContext()
        => TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions { Audience = TrustAudience.Personal });

    [Fact]
    public void A_path_policy_denial_is_an_authorization_denial()
    {
        var context = NewContext();

        context.Invocation.PathAccessFailure("denied", PathAccessPolicy.PathAccessFailure.AccessDenied);

        Assert.IsType<ToolInvocationReceipt.AuthorizationDenied>(context.Receipt);
    }

    [Fact]
    public void An_operating_system_permission_error_is_not()
    {
        var context = NewContext();

        context.Invocation.AccessDenied("Error: Permission denied: /root/secret");

        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, context.Receipt?.Category);
        Assert.IsNotType<ToolInvocationReceipt.AuthorizationDenied>(context.Receipt);
    }
}
