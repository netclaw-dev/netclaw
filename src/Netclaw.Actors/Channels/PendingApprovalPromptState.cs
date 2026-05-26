// -----------------------------------------------------------------------
// <copyright file="PendingApprovalPromptState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Journaled by a channel binding actor after it successfully posts an approval
/// prompt and captures the transport-specific locator needed to redraw that
/// prompt after a later cold spawn.
/// </summary>
public sealed record PendingApprovalPromptTracked : INetclawSerializableMessage
{
    public string CallId { get; init; } = string.Empty;

    public string? RequesterSenderId { get; init; }

    public PrincipalClassification? RequesterPrincipal { get; init; }

    public IReadOnlyList<string> OptionKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Opaque transport-specific prompt locator: Slack message ts, Discord
    /// message id, or Mattermost post id.
    /// </summary>
    public string PromptId { get; init; } = string.Empty;
}

/// <summary>
/// Journaled by a channel binding actor when a previously tracked approval
/// prompt is no longer pending locally.
/// </summary>
public sealed record PendingApprovalPromptCleared : INetclawSerializableMessage
{
    public string CallId { get; init; } = string.Empty;
}
