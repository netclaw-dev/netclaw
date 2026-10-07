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
/// A parent interactive session that can ask its operator for consent on behalf
/// of a sub-agent. At this layer it is an opaque handle, so that
/// <see cref="ToolRunScope"/> can carry the capability. The consent request
/// carries candidate and option types that live in <c>Netclaw.Actors</c>, so the
/// request contract (<c>IParentConsentBridge</c>) is declared there. A sub-agent
/// fails loudly when its handle does not implement that contract.
/// </summary>
public interface IParentApprovalBridge
{
}
