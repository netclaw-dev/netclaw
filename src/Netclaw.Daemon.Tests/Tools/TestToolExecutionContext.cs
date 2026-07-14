// -----------------------------------------------------------------------
// <copyright file="TestToolExecutionContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Tools;

internal static class TestToolExecutionContext
{
    public static ToolExecutionContext CreateUnbound()
        => new(new ToolRunScope
        {
            Session = new ToolSessionScope.Unbound(),
            Audience = TrustAudience.Public,
            InlineOutputBudget = InlineOutputBudget.Default,
            SupportsInteractiveApproval = true,
        }, ToolExecutionTimeout.Default);
}
