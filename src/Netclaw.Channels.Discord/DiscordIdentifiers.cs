// -----------------------------------------------------------------------
// <copyright file="DiscordIdentifiers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Discord;

/// <summary>
/// Discord channel identifier.
/// For threaded conversations, this is the parent/root channel id used for
/// stable session identity derivation.
/// </summary>
public readonly record struct DiscordChannelId(string Value)
{
    public static explicit operator DiscordChannelId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Discord channel identifier used for outbound delivery.
/// For threaded conversations, this is usually the thread channel id.
/// </summary>
public readonly record struct DiscordReplyChannelId(string Value)
{
    public static explicit operator DiscordReplyChannelId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Discord message id.
/// </summary>
public readonly record struct DiscordMessageId(string Value)
{
    public static explicit operator DiscordMessageId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Stable per-session thread key segment: Discord thread id when threaded,
/// otherwise root message id.
/// </summary>
public readonly record struct DiscordThreadOrMessageId(string Value)
{
    public static explicit operator DiscordThreadOrMessageId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Deduplication key for Discord events.
/// </summary>
public readonly record struct DiscordEventId(string Value)
{
    public static explicit operator DiscordEventId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Discord user id.
/// </summary>
public readonly record struct DiscordUserId(string Value)
{
    public static explicit operator DiscordUserId(string value) => new(value);

    public override string ToString() => Value;
}
