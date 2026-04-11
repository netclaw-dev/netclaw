using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class ToolAudienceProfilesDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
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
            return Task.FromResult(DoctorCheckResult.Error(
                "Tool Audience Profiles",
                "Tools section is missing; tool trust policy cannot be evaluated.",
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
            toolConfig = JsonSerializer.Deserialize<ToolConfig>(toolsObject, DoctorJsonConfigReader.JsonOptions) ?? new ToolConfig();
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Tool Audience Profiles",
                $"Failed to parse Tools configuration: {ex.Message}",
                "Fix Tools.AudienceProfiles values or rerun `netclaw init`."));
        }

        var mcpServers = root["McpServers"] is JsonObject mcpObj
            ? JsonSerializer.Deserialize<Dictionary<string, McpServerEntry>>(mcpObj, DoctorJsonConfigReader.JsonOptions) ?? new()
            : new();

        var errors = new List<string>();
        ValidateNonPersonalProfile("public", toolConfig.AudienceProfiles.Public, errors);
        ValidateNonPersonalProfile("team", toolConfig.AudienceProfiles.Team, errors);

        // Channel attachment policy cap-vs-allowlist consistency.
        foreach (var err in toolConfig.AudienceProfiles.ValidateChannelAttachments())
            errors.Add(err);

        if (errors.Count > 0)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Tool Audience Profiles",
                string.Join("; ", errors),
                "Restrict public/team profiles to allowlists and rooted filesystem access, and ensure ChannelAttachments caps are positive when categories are allowed."));
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

        CheckMissingPersonalShellApproval(toolConfig, warnings);

        // Advisory: approval mode configured but shell is off
        CheckApprovalMismatch(toolConfig, warnings);

        // Advisory: session directory base path is under the OS temp directory.
        // Attachment files and session journals will not survive reboots or
        // tmpfiles cleanup in that configuration.
        if (SessionDirectoryHelper.IsUnderTempPath(paths.SessionsDirectory))
        {
            warnings.Add(
                $"Session directory base path ({paths.SessionsDirectory}) is under the OS temp directory. " +
                "Inbound attachments written to inbox/ and other session files will be lost on reboot, " +
                "leaving dangling references in the persisted turn journal.");
        }

        // Advisory: stale patterns in tool-approvals.json
        CheckStaleApprovals(toolConfig, paths, warnings);

        // Advisory: MCP servers allowed by any audience but with no McpServerToolGrants
        var ungatedServers = FindUngatedMcpServers(toolConfig.AudienceProfiles, mcpServers);
        if (ungatedServers.Count > 0)
        {
            warnings.Add(
                $"MCP server(s) {string.Join(", ", ungatedServers)} have no McpServerToolGrants on any audience — " +
                "all discovered tools are exposed. Consider adding per-tool grants for supply-chain protection.");
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

    /// <summary>
    /// Finds MCP servers that are allowed by at least one audience profile
    /// but have no <see cref="ToolAudienceProfile.McpServerToolGrants"/> on any profile.
    /// </summary>
    private static List<string> FindUngatedMcpServers(
        ToolAudienceProfiles profiles,
        IReadOnlyDictionary<string, McpServerEntry> mcpServers)
    {
        // Collect all server names that are allowed by any audience
        var allowedServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var grantedServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles.GetAllProfiles())
        {
            if (profile.McpServersMode == ToolProfileMode.All)
            {
                foreach (var name in mcpServers.Keys)
                    allowedServers.Add(name);
            }
            else
            {
                foreach (var server in profile.AllowedMcpServers)
                    allowedServers.Add(server);
            }

            if (profile.McpServerToolGrants is { } grants)
            {
                foreach (var server in grants.Keys)
                    grantedServers.Add(server);
            }
        }

        return allowedServers.Except(grantedServers, StringComparer.OrdinalIgnoreCase).Order().ToList();
    }

    private static void CheckApprovalMismatch(ToolConfig toolConfig, List<string> warnings)
    {
        var profiles = new (string Name, ToolAudienceProfile Profile)[]
        {
            ("personal", toolConfig.AudienceProfiles.Personal),
            ("team", toolConfig.AudienceProfiles.Team),
            ("public", toolConfig.AudienceProfiles.Public)
        };

        foreach (var (name, profile) in profiles)
        {
            if (profile.ApprovalPolicy is null)
                continue;

            var shellOverride = profile.ApprovalPolicy.GetEffectiveMode(ShellTool.ToolName);
            if (shellOverride == ToolApprovalMode.Approval && toolConfig.ShellMode == ShellExecutionMode.Off)
            {
                warnings.Add(
                    $"{name} profile has shell_execute in Approval mode but ShellMode is Off — " +
                    "approval config has no effect.");
            }
        }
    }

    private static void CheckMissingPersonalShellApproval(ToolConfig toolConfig, List<string> warnings)
    {
        if (toolConfig.ShellMode != ShellExecutionMode.HostAllowed)
            return;

        var personal = toolConfig.AudienceProfiles.Personal;
        if (!PersonalProfileAllowsShell(personal))
            return;

        var approvalMode = personal.ApprovalPolicy?.GetEffectiveMode(ShellTool.ToolName) ?? ToolApprovalMode.Auto;
        if (approvalMode is ToolApprovalMode.Approval or ToolApprovalMode.Deny)
            return;

        warnings.Add(
            "Personal profile enables host shell without an explicit shell_execute approval gate. " +
            "Run `netclaw init` again or set Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute to Approval.");
    }

    private static bool PersonalProfileAllowsShell(ToolAudienceProfile profile)
    {
        if (profile.ToolsMode == ToolProfileMode.All)
            return true;

        return profile.AllowedTools.Contains(ShellTool.ToolName, StringComparer.Ordinal);
    }

    private static void CheckStaleApprovals(ToolConfig toolConfig, NetclawPaths netclawPaths, List<string> warnings)
    {
        var approvalsPath = netclawPaths.ToolApprovalsPath;
        if (!File.Exists(approvalsPath))
            return;

        if (toolConfig.ShellMode != ShellExecutionMode.Off)
            return;

        try
        {
            var store = new ToolApprovalStore(approvalsPath);
            var data = store.Load();

            foreach (var (audienceKey, tools) in data.Audiences)
            {
                if (tools.ContainsKey(ShellTool.ToolName))
                {
                    warnings.Add(
                        $"Persistent approvals exist for {audienceKey}.{ShellTool.ToolName} " +
                        "but shell is disabled.");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read tool-approvals.json: {ex.Message}");
        }
    }
}
