// -----------------------------------------------------------------------
// <copyright file="MattermostReminderTargetResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;

namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Resolves Mattermost reminder targets to canonical IDs.
/// Supported inputs:
/// - @userId (direct-message delivery to that user)
/// - channel:channelId
/// A bare ID with no prefix is rejected: Mattermost user IDs and channel IDs
/// are both 26-char alphanumeric strings and are indistinguishable, so the
/// resolver does not guess which one a bare ID refers to.
/// </summary>
public sealed class MattermostReminderTargetResolver : IReminderTargetResolver
{
    public string Transport => "mattermost";

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

        if (raw.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
        {
            var channelId = raw[8..].Trim();
            if (IsMattermostId(channelId))
            {
                // Preserve the "channel:" prefix in the canonical form. Mattermost
                // channel IDs and user IDs are both 26-char alphanumeric strings,
                // so the bare ID is indistinguishable downstream — the reminder
                // prompt builder and send_mattermost_message dispatcher need the
                // prefix to know whether to target a channel or open a DM.
                return Task.FromResult(new ReminderTargetResolution(
                    Success: true,
                    ResolvedId: $"channel:{channelId}",
                    Kind: ReminderTargetKind.Channel,
                    ErrorMessage: null));
            }

            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: "Invalid Mattermost channel ID. Use channel:<channelId>."));
        }

        if (raw.StartsWith('@'))
        {
            var userId = raw[1..].Trim();
            if (IsMattermostId(userId))
            {
                // See the "channel:" branch above: the "@" prefix is preserved in
                // the canonical form so the prompt builder and the DM-open path
                // can distinguish a user target from a channel target. Without
                // this the bare ID looks identical to a channel ID.
                return Task.FromResult(new ReminderTargetResolution(
                    Success: true,
                    ResolvedId: $"@{userId}",
                    Kind: ReminderTargetKind.User,
                    ErrorMessage: null));
            }

            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: $"Could not resolve Mattermost target '{target}'. Use @<userId> with a 26-character Mattermost user ID."));
        }

        // A bare ID carries no user-vs-channel signal. Mattermost user IDs and
        // channel IDs are both 26-char alphanumeric strings, so guessing here
        // could silently deliver a reminder to the wrong audience. Require an
        // explicit prefix instead of guessing.
        if (IsMattermostId(raw))
        {
            return Task.FromResult(new ReminderTargetResolution(
                Success: false,
                ResolvedId: null,
                Kind: ReminderTargetKind.Unknown,
                ErrorMessage: $"Ambiguous Mattermost target '{target}': a bare ID could be a user or a channel. "
                    + "Use @<userId> for a direct message or channel:<channelId> for a channel."));
        }

        return Task.FromResult(new ReminderTargetResolution(
            Success: false,
            ResolvedId: null,
            Kind: ReminderTargetKind.Unknown,
            ErrorMessage: $"Could not resolve Mattermost target '{target}'. Use @<userId> or channel:<channelId>."));
    }

    private static bool IsMattermostId(string value)
    {
        if (value.Length != 26)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]))
                return false;
        }

        return true;
    }
}
