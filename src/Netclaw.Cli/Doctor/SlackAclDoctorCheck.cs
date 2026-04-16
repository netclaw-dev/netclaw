using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class SlackAclDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return Task.FromResult(DoctorCheckResult.Pass("Slack ACL", "Skipped (base config is missing or invalid)."));

        if (root!["Slack"] is not JsonObject slack || !DoctorJsonConfigReader.ReadBool(slack, "Enabled"))
            return Task.FromResult(DoctorCheckResult.Pass("Slack ACL", "Slack connector disabled or not configured."));

        var hasAllowedChannels = DoctorJsonConfigReader.ReadStringArray(slack, "AllowedChannelIds").Count > 0;
        var hasDefaultChannel = !string.IsNullOrWhiteSpace(slack["DefaultChannelId"]?.GetValue<string>())
                                || !string.IsNullOrWhiteSpace(slack["DefaultChannelName"]?.GetValue<string>());

        if (!hasAllowedChannels && !hasDefaultChannel)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Slack ACL",
                "Slack is enabled with no channel allow-list/default channel; channel traffic will be denied.",
                "Set `Slack:AllowedChannelIds` or `Slack:DefaultChannelId`/`Slack:DefaultChannelName`."));
        }

        var allowDirectMessages = DoctorJsonConfigReader.ReadBool(slack, "AllowDirectMessages");
        var allowedUserIds = DoctorJsonConfigReader.ReadStringArray(slack, "AllowedUserIds");

        if (allowDirectMessages && allowedUserIds.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Slack ACL",
                "Slack DMs enabled with no user allowlist; any workspace member can DM the bot.",
                "Set `Slack:AllowedUserIds` to restrict DM access."));
        }

        return Task.FromResult(DoctorCheckResult.Pass("Slack ACL", "Slack channel policy has explicit channel scope."));
    }
}
