// -----------------------------------------------------------------------
// <copyright file="DiscordReminderTargetResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Actors.Reminders;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Resolves Discord reminder targets to canonical guild text-channel or user IDs.
/// Supported inputs:
/// - channel mention: &lt;#123...&gt;
/// - explicit channel ID: channel:123...
/// - user mention: &lt;@123...&gt; or &lt;@!123...&gt;
/// - explicit user ID: dm:123... or @123...
/// </summary>
public sealed class DiscordReminderTargetResolver(DiscordChannelOptions options) : IReminderTargetResolver
{
    private static readonly Regex MentionRegex =
        new("^<@!?([0-9]{17,20})>$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelMentionRegex =
        new("^<#([0-9]{17,20})>$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SnowflakeRegex =
        new("^[0-9]{17,20}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Transport => "discord";

    public Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Target is required."));
        }

        var raw = target.Trim();

        if (raw.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
        {
            var userId = raw[3..].Trim();
            if (SnowflakeRegex.IsMatch(userId))
                return Task.FromResult(ResolveUser(userId));

            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Invalid Discord direct-message target. Use dm:<userId>, @<userId>, or <@userId>."));
        }

        if (raw.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
        {
            var channelId = raw[8..].Trim();
            if (SnowflakeRegex.IsMatch(channelId))
                return Task.FromResult(ResolveChannel(channelId));

            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Invalid Discord channel target. Use channel:<channelId> or <#channelId>."));
        }

        var channelMention = ChannelMentionRegex.Match(raw);
        if (channelMention.Success)
        {
            return Task.FromResult(ResolveChannel(channelMention.Groups[1].Value));
        }

        if (SnowflakeRegex.IsMatch(raw))
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Bare Discord snowflakes are ambiguous. Use channel:<channelId> or <#channelId> for channel delivery, or dm:<userId> / <@userId> for direct-message delivery."));
        }

        var mention = MentionRegex.Match(raw);
        if (mention.Success)
        {
            return Task.FromResult(ResolveUser(mention.Groups[1].Value));
        }

        if (raw.StartsWith("@", StringComparison.Ordinal))
        {
            var userId = raw[1..].Trim();
            if (SnowflakeRegex.IsMatch(userId))
                return Task.FromResult(ResolveUser(userId));

            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Invalid Discord user target. Use @<userId>, <@userId>, or dm:<userId>."));
        }

        return Task.FromResult(new ReminderTargetResolution(
            Success: false,
            ResolvedId: null,
            Kind: ReminderTargetKind.Unknown,
            ErrorMessage: $"Could not resolve Discord target '{target}'. Use channel:<channelId>, <#channelId>, dm:<userId>, or <@userId>."));
    }

    private ReminderTargetResolution ResolveChannel(string channelId)
    {
        return DiscordAclPolicy.IsAllowedChannel(new DiscordChannelId(channelId), options, GetDefaultChannelId())
            ? new ReminderTargetResolution(
                Success: true,
                ResolvedId: channelId,
                Kind: ReminderTargetKind.Channel,
                ErrorMessage: null)
            : new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: $"Discord channel '{channelId}' is not in the allowed channels list.");
    }

    private ReminderTargetResolution ResolveUser(string userId)
    {
        if (!options.AllowDirectMessages)
        {
            return new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Discord direct messages are disabled in configuration.");
        }

        return DiscordAclPolicy.IsAllowedUser(new DiscordUserId(userId), options)
            ? new ReminderTargetResolution(
                Success: true,
                ResolvedId: userId,
                Kind: ReminderTargetKind.User,
                ErrorMessage: null)
            : new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: $"Discord user '{userId}' is not in the allowed users list.");
    }

    private DiscordChannelId? GetDefaultChannelId()
        => string.IsNullOrWhiteSpace(options.DefaultChannelId)
            ? null
            : new DiscordChannelId(options.DefaultChannelId);
}
