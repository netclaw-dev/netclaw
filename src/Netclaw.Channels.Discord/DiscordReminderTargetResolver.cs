// -----------------------------------------------------------------------
// <copyright file="DiscordReminderTargetResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Actors.Reminders;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Resolves Discord DM reminder targets to canonical IDs.
/// Supported inputs:
/// - user mention: &lt;@123...&gt; or &lt;@!123...&gt;
/// - user shorthand: @123...
/// - raw user ID: 123...
/// - explicit DM channel ID: dm:123...
/// </summary>
public sealed class DiscordReminderTargetResolver : IReminderTargetResolver
{
    private static readonly Regex MentionRegex =
        new("^<@!?([0-9]{17,20})>$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            var dmChannelId = raw[3..].Trim();
            if (SnowflakeRegex.IsMatch(dmChannelId))
            {
                return Task.FromResult(new ReminderTargetResolution(
                    Success: true,
                    ResolvedId: dmChannelId,
                    Kind: ReminderTargetKind.Channel,
                    ErrorMessage: null));
            }

            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Invalid Discord DM channel ID. Use dm:<channelId>."));
        }

        var mention = MentionRegex.Match(raw);
        if (mention.Success)
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: true,
                ResolvedId: mention.Groups[1].Value,
                Kind: ReminderTargetKind.User,
                ErrorMessage: null));
        }

        if (raw.StartsWith("@", StringComparison.Ordinal))
            raw = raw[1..].Trim();

        if (SnowflakeRegex.IsMatch(raw))
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: true,
                ResolvedId: raw,
                Kind: ReminderTargetKind.User,
                ErrorMessage: null));
        }

        return Task.FromResult(new ReminderTargetResolution(
            Success: false,
            ResolvedId: null,
            Kind: ReminderTargetKind.Unknown,
            ErrorMessage: $"Could not resolve Discord target '{target}'. Use a Discord user ID, @userId, user mention (<@id>), or dm:<channelId>."));
    }
}
