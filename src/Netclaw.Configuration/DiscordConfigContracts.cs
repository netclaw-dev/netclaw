// -----------------------------------------------------------------------
// <copyright file="DiscordConfigContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Response for <c>GET /api/config/discord</c>.
/// </summary>
public sealed record GetDiscordConfigResponse
{
    public bool Enabled { get; init; }

    /// <summary>
    /// The response never includes the bot token's plaintext.
    /// </summary>
    public bool BotTokenIsSet { get; init; }

    public string? DefaultChannelId { get; init; }

    public bool AllowDirectMessages { get; init; }

    public bool MentionOnly { get; init; } = true;

    public bool MentionRequiredInDm { get; init; }

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Request for <c>PUT /api/config/discord</c>.
/// </summary>
public sealed record PutDiscordConfigRequest
{
    public bool Enabled { get; init; }

    /// <summary>
    /// <c>null</c> leaves the stored token untouched. Empty string clears the
    /// token. Any other value replaces it.
    /// </summary>
    public string? BotToken { get; init; }

    public string? DefaultChannelId { get; init; }

    public bool AllowDirectMessages { get; init; }

    public bool MentionOnly { get; init; } = true;

    public bool MentionRequiredInDm { get; init; }

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Response for <c>PUT /api/config/discord</c>.
/// </summary>
public sealed record PutDiscordConfigResponse
{
    public required string ConfigPath { get; init; }

    public required string SecretsPath { get; init; }

    /// <summary>
    /// Discord settings take effect after the daemon restarts.
    /// </summary>
    public bool RestartRequired { get; init; } = true;
}
