// -----------------------------------------------------------------------
// <copyright file="DiscordConfigWire.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Wire contracts for <c>/api/config/discord</c>.
///
/// Token handling:
/// <list type="bullet">
/// <item>The GET response NEVER includes the bot token's plaintext — only
/// <see cref="GetResponse.BotTokenIsSet"/>. The Web UI must not expect a token
/// value, and the daemon must not leak one onto the wire.</item>
/// <item>The PUT request treats <see cref="PutRequest.BotToken"/> as
/// "leave unchanged" when null, "clear" when an empty string, and "replace"
/// otherwise. This matches the affordance of a password field that
/// only mutates state when the operator types into it.</item>
/// </list>
/// </summary>
public static class DiscordConfigWire
{
    public sealed class GetResponse : IWireType
    {
        public bool Enabled { get; init; }

        public bool BotTokenIsSet { get; init; }

        public string? DefaultChannelId { get; init; }

        public bool AllowDirectMessages { get; init; }

        public bool MentionOnly { get; init; } = true;

        public bool MentionRequiredInDm { get; init; }

        public string[] AllowedChannelIds { get; init; } = [];

        public string[] AllowedUserIds { get; init; } = [];

        public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);
    }

    public sealed class PutRequest : IWireType
    {
        public bool Enabled { get; set; }

        /// <summary>
        /// <c>null</c> = leave existing token untouched. Empty string = clear
        /// the token. Any other value = replace the stored token. The daemon
        /// is responsible for persisting the token to <c>secrets.json</c>
        /// with the standard secrets protector.
        /// </summary>
        public string? BotToken { get; set; }

        public string? DefaultChannelId { get; set; }

        public bool AllowDirectMessages { get; set; }

        public bool MentionOnly { get; set; } = true;

        public bool MentionRequiredInDm { get; set; }

        public string[] AllowedChannelIds { get; set; } = [];

        public string[] AllowedUserIds { get; set; } = [];

        public Dictionary<string, string> ChannelAudiences { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class PutResponse : IWireType
    {
        public required string ConfigPath { get; init; }

        public required string SecretsPath { get; init; }

        /// <summary>
        /// Always true for the Discord connector: <see cref="DiscordChannelOptions"/>
        /// is bound once at host construction, so configuration changes do not
        /// take effect until the daemon process is restarted. The Web UI should
        /// surface this clearly.
        /// </summary>
        public bool RestartRequired { get; init; } = true;
    }
}
