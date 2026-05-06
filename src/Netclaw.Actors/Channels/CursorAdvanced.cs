// -----------------------------------------------------------------------
// <copyright file="CursorAdvanced.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Channels;

/// <summary>
/// Persistence event indicating a channel binding actor's inbound cursor moved forward.
/// The cursor value is channel-specific (e.g. Slack timestamp, Discord snowflake)
/// but serialized as an opaque string — only the owning actor interprets it.
/// </summary>
public readonly record struct CursorAdvanced(string Cursor);
