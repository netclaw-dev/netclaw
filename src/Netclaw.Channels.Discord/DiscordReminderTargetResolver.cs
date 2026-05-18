// -----------------------------------------------------------------------
// <copyright file="DiscordReminderTargetResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Actors.Reminders;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Resolves Discord reminder targets to canonical guild text-channel IDs.
/// Current reminder delivery is channel-only: proactive DM delivery is deferred
/// until Discord gains a session model that can preserve thread context.
/// Supported inputs:
/// - channel mention: &lt;#123...&gt;
/// - explicit channel ID: channel:123...
/// </summary>
public sealed class DiscordReminderTargetResolver : IReminderTargetResolver
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
            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Discord reminder delivery currently supports guild text channels only; DM targets are not supported. Use channel:<channelId> or <#channelId>."));
        }

        if (raw.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
        {
            var channelId = raw[8..].Trim();
            if (SnowflakeRegex.IsMatch(channelId))
            {
                return Task.FromResult(new ReminderTargetResolution(
                    Success: true,
                    ResolvedId: channelId,
                    Kind: ReminderTargetKind.Channel,
                    ErrorMessage: null));
            }

            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Invalid Discord channel target. Use channel:<channelId> or <#channelId>."));
        }

        var channelMention = ChannelMentionRegex.Match(raw);
        if (channelMention.Success)
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: true,
                ResolvedId: channelMention.Groups[1].Value,
                Kind: ReminderTargetKind.Channel,
                ErrorMessage: null));
        }

        if (SnowflakeRegex.IsMatch(raw))
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Bare Discord snowflakes are ambiguous. Use channel:<channelId> or <#channelId> for channel delivery."));
        }

        var mention = MentionRegex.Match(raw);
        if (mention.Success || raw.StartsWith("@", StringComparison.Ordinal))
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Discord reminder delivery currently supports guild text channels only; user/DM targets are not supported. Use channel:<channelId> or <#channelId>."));
        }

        return Task.FromResult(new ReminderTargetResolution(
            Success: false,
            ResolvedId: null,
            Kind: ReminderTargetKind.Unknown,
            ErrorMessage: $"Could not resolve Discord target '{target}'. Use channel:<channelId> or <#channelId>."));
    }
}
