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
/// - raw user ID (26-char alphanumeric Mattermost ID)
/// - @userId (same, with @ prefix stripped)
/// - channel:channelId
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
                ErrorMessage: "Invalid Mattermost channel ID. Use channel:<channelId>."));
        }

        if (raw.StartsWith('@'))
            raw = raw[1..].Trim();

        if (IsMattermostId(raw))
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
            ErrorMessage: $"Could not resolve Mattermost target '{target}'. Use a Mattermost user ID, @userId, or channel:<channelId>."));
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
