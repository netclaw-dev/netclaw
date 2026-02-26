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

        if (root!["Slack"] is not JsonObject slack || !ReadBool(slack, "Enabled"))
            return Task.FromResult(DoctorCheckResult.Pass("Slack ACL", "Slack connector disabled or not configured."));

        var hasAllowedChannels = ReadStringArray(slack, "AllowedChannelIds").Count > 0;
        var hasDefaultChannel = !string.IsNullOrWhiteSpace(slack["DefaultChannelId"]?.GetValue<string>())
                                || !string.IsNullOrWhiteSpace(slack["DefaultChannelName"]?.GetValue<string>());

        if (!hasAllowedChannels && !hasDefaultChannel)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Slack ACL",
                "Slack is enabled with no channel allow-list/default channel; channel traffic will be denied.",
                "Set `Slack:AllowedChannelIds` or `Slack:DefaultChannelId`/`Slack:DefaultChannelName`."));
        }

        return Task.FromResult(DoctorCheckResult.Pass("Slack ACL", "Slack channel policy has explicit channel scope."));
    }

    private static bool ReadBool(JsonObject obj, string property)
        => obj[property] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static List<string> ReadStringArray(JsonObject obj, string property)
    {
        if (obj[property] is not JsonArray arr)
            return [];

        return arr.Select(v => v?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
    }
}
