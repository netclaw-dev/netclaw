// -----------------------------------------------------------------------
// <copyright file="MattermostIdentifiers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Mattermost channel identifier.
/// </summary>
public readonly record struct MattermostChannelId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Mattermost post identifier.
/// </summary>
public readonly record struct MattermostPostId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Root post identifier for thread-based session identity.
/// Empty when the message is a top-level post (not in a thread).
/// </summary>
public readonly record struct MattermostRootPostId(string Value)
{
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

/// <summary>
/// Deduplication key for Mattermost WebSocket events.
/// </summary>
public readonly record struct MattermostEventId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Mattermost user identifier.
/// </summary>
public readonly record struct MattermostUserId(string Value)
{
    public override string ToString() => Value;
}
