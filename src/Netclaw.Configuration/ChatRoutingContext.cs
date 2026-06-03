// -----------------------------------------------------------------------
// <copyright file="ChatRoutingContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The inputs a chat-client router uses to select which composed pipeline to invoke
/// for a call. Minimal today — only <see cref="Role"/> is consulted — but shaped so
/// per-session / per-provider routing slots in later as a new router policy without
/// changing this type's shape or any caller.
/// </summary>
public sealed record ChatRoutingContext
{
    /// <summary>The model role being requested (today's only routing signal).</summary>
    public required ModelRole Role { get; init; }

    /// <summary>
    /// Owning session id. Unused today; a future per-session router policy reads this
    /// to route a session's chats to a specific model/provider.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Index of the current failover attempt within a single call. Unused today; a
    /// future policy can use it to re-rank candidates across attempts.
    /// </summary>
    public int AttemptIndex { get; init; }
}
