using Netclaw.Actors.Reminders;

namespace Netclaw.Channels.Slack;

/// <summary>
/// <see cref="IReminderTargetResolver"/> implementation that delegates to
/// <see cref="ISlackTargetResolver"/>. Lives in the Slack transport assembly
/// so <c>Netclaw.Actors</c> stays transport-agnostic.
/// </summary>
public sealed class SlackReminderTargetResolver(ISlackTargetResolver slackResolver) : IReminderTargetResolver
{
    public string Transport => "slack";

    public async Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default)
    {
        var result = await slackResolver.ResolveAsync(target, ct);
        var kind = result.ChannelId is not null
            ? ReminderTargetKind.Channel
            : result.UserId is not null
                ? ReminderTargetKind.User
                : ReminderTargetKind.Unknown;

        return new ReminderTargetResolution(
            result.Success,
            result.ChannelId ?? result.UserId,
            kind,
            result.ErrorMessage);
    }
}
