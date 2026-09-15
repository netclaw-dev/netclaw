// -----------------------------------------------------------------------
// <copyright file="TeamsChannelOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Netclaw.Configuration;
using System.Text.Json.Serialization;

namespace Netclaw.Channels.Teams;

public enum TeamsAuthenticationMode
{
    ClientSecret
}

/// <summary>
/// Delimiter-safe audience override for one Teams channel or an entire team.
/// </summary>
public sealed class TeamsChannelAudienceOverride
{
    public string TeamId { get; init; } = string.Empty;

    /// <summary>
    /// Exact channel identity. Leave empty to apply the override to the entire team.
    /// </summary>
    public string? ChannelId { get; init; }

    public string Audience { get; init; } = string.Empty;
}

/// <summary>
/// Delimiter-safe user and group restrictions for one canonical Teams channel.
/// Team and channel membership never grant access by themselves.
/// </summary>
public sealed class TeamsChannelAccessOverride
{
    public string TeamId { get; init; } = string.Empty;

    public string ChannelId { get; init; } = string.Empty;

    public string[] AllowedUserIds { get; init; } = [];

    public string[] AllowedGroupIds { get; init; } = [];
}

/// <summary>
/// Configuration for the disabled-by-default Microsoft Teams integration.
/// ClientSecret is loaded only from the existing secrets overlay or NETCLAW_
/// environment variables and is never included in the normal configuration schema.
/// </summary>
public sealed class TeamsChannelOptions : IRemoteChatChannelOptions
{
    public bool Enabled { get; init; }

    public string? TenantId { get; init; }

    public string? ClientId { get; init; }

    /// <summary>
    /// The Teams bot registration ID used to qualify structured mention entities.
    /// It is not an Entra client credential.
    /// </summary>
    public string? BotId { get; init; }

    public TeamsAuthenticationMode AuthenticationMode { get; init; } = TeamsAuthenticationMode.ClientSecret;

    [JsonIgnore]
    public SensitiveString? ClientSecret { get; init; }

    public bool AllowDirectMessages { get; init; }

    /// <summary>
    /// Enables explicit GroupChat ingress. A configured chat and authorized
    /// global principal remain required after this switch is enabled.
    /// </summary>
    public bool AllowGroupChats { get; init; }

    public bool AllowAttachments { get; init; }

    public bool MentionOnly { get; init; } = true;

    public string[] AllowedTeamIds { get; init; } = [];

    public string[] AllowedChannelIds { get; init; } = [];

    /// <summary>
    /// Canonical Teams chat IDs that can send GroupChat ingress. Display names
    /// remain TUI metadata and never enter this authorization check.
    /// </summary>
    public string[] AllowedGroupChatIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    /// <summary>
    /// Entra group object IDs permitted across all Teams conversations. Group
    /// membership is verified through the bounded directory boundary.
    /// </summary>
    public string[] AllowedGroupIds { get; init; } = [];

    /// <summary>
    /// Per-channel audience overrides. Keys use canonical team/channel or team
    /// identities; values are personal, team, or public.
    /// </summary>
    public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Structured audience overrides for canonical Teams identities that contain
    /// configuration path delimiters. Exact team/channel entries take precedence
    /// over team-wide entries and the public fallback.
    /// </summary>
    public TeamsChannelAudienceOverride[] ChannelAudienceOverrides { get; init; } = [];

    /// <summary>
    /// Per-channel user and group restrictions. These entries are intentionally
    /// structured so canonical IDs containing delimiters are never composed into
    /// a configuration key.
    /// </summary>
    public TeamsChannelAccessOverride[] ChannelAccessOverrides { get; init; } = [];
}
