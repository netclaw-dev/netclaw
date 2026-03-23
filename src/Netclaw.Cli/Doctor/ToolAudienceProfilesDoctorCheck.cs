using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class ToolAudienceProfilesDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (error is not null)
            return Task.FromResult(error);

        if (root is null)
            return Task.FromResult(DoctorCheckResult.Warning(
                "Tool Audience Profiles",
                "Config file is missing; strict tool trust defaults are active.",
                "Run `netclaw init` to scaffold recommended audience profiles."));

        if (root["Tools"] is not JsonObject toolsObject)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Tool Audience Profiles",
                "Tools section is missing; strict tool trust defaults are active.",
                "Add a Tools section with AudienceProfiles or run `netclaw init` again."));
        }

        var missingProfiles = new List<string>();
        if (toolsObject["AudienceProfiles"] is not JsonObject rawProfiles)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Tool Audience Profiles",
                "Tools.AudienceProfiles is missing; built-in strict defaults are active.",
                "Add explicit public/team/personal audience profiles to make tool policy visible."));
        }

        if (rawProfiles["Public"] is null)
            missingProfiles.Add("public");
        if (rawProfiles["Team"] is null)
            missingProfiles.Add("team");
        if (rawProfiles["Personal"] is null)
            missingProfiles.Add("personal");

        ToolConfig toolConfig;
        try
        {
            toolConfig = JsonSerializer.Deserialize<ToolConfig>(toolsObject.ToJsonString(), JsonOptions) ?? new ToolConfig();
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Tool Audience Profiles",
                $"Failed to parse Tools configuration: {ex.Message}",
                "Fix Tools.AudienceProfiles values or rerun `netclaw init`."));
        }

        var errors = new List<string>();
        ValidateNonPersonalProfile("public", toolConfig.AudienceProfiles.Public, errors);
        ValidateNonPersonalProfile("team", toolConfig.AudienceProfiles.Team, errors);

        if (errors.Count > 0)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Tool Audience Profiles",
                string.Join("; ", errors),
                "Restrict public/team profiles to allowlists and rooted filesystem access. Unrestricted modes are only safe for personal profiles."));
        }

        var warnings = new List<string>();
        if (missingProfiles.Count > 0)
        {
            warnings.Add($"Missing explicit profiles for {string.Join(", ", missingProfiles)}; fallback defaults are in effect.");
        }

        if (IsUnrestrictedPersonalProfile(toolConfig.AudienceProfiles.Personal))
        {
            warnings.Add("Personal profile allows all tools and unrestricted filesystem access.");
            if (toolConfig.ShellMode == ShellExecutionMode.HostAllowed)
                warnings.Add("Personal profile also enables host shell, which has a high blast radius.");
        }

        if (warnings.Count > 0)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Tool Audience Profiles",
                string.Join(" ", warnings),
                "Tighten personal scope if needed, or accept the warning if the machine is intentionally owner-only."));
        }

        return Task.FromResult(DoctorCheckResult.Pass(
            "Tool Audience Profiles",
            "Audience profiles are explicit and public/team restrictions remain scoped."));
    }

    private static void ValidateNonPersonalProfile(string profileName, ToolAudienceProfile profile, List<string> errors)
    {
        if (profile.ToolsMode == ToolProfileMode.All)
            errors.Add($"{profileName} profile cannot set ToolsMode=All.");

        if (profile.McpServersMode == ToolProfileMode.All)
            errors.Add($"{profileName} profile cannot set McpServersMode=All.");

        if (profile.ReadFiles.Mode == ToolFilesystemMode.All)
            errors.Add($"{profileName} profile cannot set ReadFiles.Mode=All.");

        if (profile.WriteFiles.Mode == ToolFilesystemMode.All)
            errors.Add($"{profileName} profile cannot set WriteFiles.Mode=All.");

        if (profile.AttachFiles.Mode == ToolFilesystemMode.All)
            errors.Add($"{profileName} profile cannot set AttachFiles.Mode=All.");
    }

    private static bool IsUnrestrictedPersonalProfile(ToolAudienceProfile profile)
    {
        return profile.ToolsMode == ToolProfileMode.All
            && profile.McpServersMode == ToolProfileMode.All
            && profile.ReadFiles.Mode == ToolFilesystemMode.All
            && profile.WriteFiles.Mode == ToolFilesystemMode.All
            && profile.AttachFiles.Mode == ToolFilesystemMode.All;
    }
}
