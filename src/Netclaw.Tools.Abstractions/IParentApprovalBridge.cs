// -----------------------------------------------------------------------
// <copyright file="IParentApprovalBridge.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

public abstract record InteractiveApprovalCapability
{
    private InteractiveApprovalCapability()
    {
    }

    public sealed record Unavailable : InteractiveApprovalCapability;

    public sealed record Available : InteractiveApprovalCapability
    {
        public Available(IParentApprovalBridge bridge)
        {
            ArgumentNullException.ThrowIfNull(bridge);
            Bridge = bridge;
        }

        public IParentApprovalBridge Bridge { get; }
    }
}

/// <summary>
/// Thrown when a sub-agent needs parent approval but the parent session cannot
/// safely emit an approval prompt with complete authority context.
/// </summary>
public sealed class ParentApprovalUnavailableException : InvalidOperationException
{
    public ParentApprovalUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// An opaque handle that says that an interactive requester can answer a consent request, so that
/// <see cref="ToolRunScope"/> can carry the capability. A session call asks through it. A child run parks at a
/// consent need, and its owning session asks the run requester.
/// </summary>
public interface IParentApprovalBridge
{
}
