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
        => Create(new ToolSessionScope.Sessionless(), TrustAudience.Public);

    public static ToolExecutionContext CreateUnbound(
        TrustAudience audience,
        string? channelType = null)
        => Create(new ToolSessionScope.Sessionless(), audience, channelType);

    public static ToolExecutionContext CreateBound(
        string sessionId,
        string? sessionDirectory,
        TrustAudience audience,
        string? channelType = null,
        ChannelDeliveryTargetInfo? requestedDeliveryTarget = null)
        => Create(
            new ToolSessionScope.Bound(sessionId, sessionDirectory),
            audience,
            channelType,
            requestedDeliveryTarget);

    private static ToolExecutionContext Create(
        ToolSessionScope session,
        TrustAudience audience,
        string? channelType = null,
        ChannelDeliveryTargetInfo? requestedDeliveryTarget = null)
        => new(new ToolRunScope
        {
            Session = session,
            Audience = audience,
            InlineOutputBudget = InlineOutputBudget.Default,
            ChannelType = channelType,
            RequestedDeliveryTarget = requestedDeliveryTarget,
            SupportsInteractiveApproval = true,
        }, ToolExecutionTimeout.Default);
}
