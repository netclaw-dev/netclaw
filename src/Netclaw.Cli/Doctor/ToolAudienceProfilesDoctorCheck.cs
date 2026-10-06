// -----------------------------------------------------------------------
// <copyright file="ToolAudienceProfilesDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Security;

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
                "Run `netclaw init` to choose a security posture."));

        if (root["Tools"] is not JsonObject)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Tool Audience Profiles",
                "Tools section is missing; tool trust policy cannot be evaluated.",
                "Run `netclaw init` again to write the security posture and the Tools section."));
        }

        // Bind the same way as the daemon: the Tools section on top of the posture defaults. An
        // absent profile or list is the posture default, which is the normal state after
        // `netclaw init`, so it is not a warning. Doctor reads only netclaw.json.
        ToolConfig toolConfig;
        try
        {
            toolConfig = ConfigFileHelper.LoadToolConfig(paths);
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Tool Audience Profiles",
                $"Failed to parse Tools configuration: {ex.Message}",
                "Fix Tools.AudienceProfiles values or rerun `netclaw init`."));
        }

        var mcpServers = root["McpServers"] is JsonObject mcpObj
            ? JsonSerializer.Deserialize<Dictionary<string, McpServerEntry>>(mcpObj, JsonDefaults.ConfigRead) ?? []
            : [];

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

        CheckExplicitPersonalShellAuto(toolConfig, warnings);
        CheckMissingToolOutputRead(toolConfig.AudienceProfiles, warnings);

        CheckDefaultAllowedToolsCopies(root, warnings);

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

        // Warning: MCP servers reachable for Personal with no approval default.
        var missingApproval = FindMcpServersMissingPersonalApprovalDefault(toolConfig.AudienceProfiles, mcpServers);
        if (missingApproval.Count > 0)
        {
            warnings.Add(
                $"MCP server(s) {string.Join(", ", missingApproval)} have no approval default on Personal — " +
                "tools invoke without prompting. Run `netclaw mcp permissions` to set a server default.");
        }

        // Advisory only, with no auto-fix: a Personal allowlist can be intentional, and a fix
        // that adds servers would widen access without the operator.
        var outsidePersonalAllowlist = FindEnabledMcpServersOutsidePersonalAllowlist(toolConfig.AudienceProfiles, mcpServers);
        if (outsidePersonalAllowlist.Count > 0)
        {
            warnings.Add(
                $"MCP server(s) {string.Join(", ", outsidePersonalAllowlist)} are enabled, but the Personal audience cannot use them: "
                + "Tools.AudienceProfiles.Personal.AllowedMcpServers does not list them. "
                + "`netclaw mcp permissions` in Netclaw 0.27.1-beta.1 and earlier wrote this allowlist when you enabled one server. "
                + "Enable each server in `netclaw mcp permissions`, or delete McpServersMode and AllowedMcpServers "
                + "from the Personal profile to allow every server.");
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
            "Public and Team tool restrictions remain scoped."));
    }

    // Advisory only, with no auto-fix: a narrow allowlist can be intentional. A large tool
    // result spills to a file, and the inline notice tells the model to call tool_output_read.
    // Without that tool, the model cannot read the rest of the output.
    private static void CheckMissingToolOutputRead(ToolAudienceProfiles profiles, List<string> warnings)
    {
        foreach (var (audience, profile) in (ReadOnlySpan<(TrustAudience, ToolAudienceProfile)>)
                 [(TrustAudience.Public, profiles.Public), (TrustAudience.Team, profiles.Team)])
        {
            if (profile.ToolsMode != ToolProfileMode.Allowlist
                || profile.AllowedTools.Contains(ToolAudienceProfileToolCatalog.ToolOutputRead, StringComparer.Ordinal)
                || ToolAudienceProfileDefaults.IsLegacyDefaultAllowedTools(audience, profile.AllowedTools))
            {
                continue;
            }

            warnings.Add(
                $"Tools.AudienceProfiles.{audience}.AllowedTools does not include {ToolAudienceProfileToolCatalog.ToolOutputRead}. "
                + "When a tool result is too large, Netclaw spills it to a file and tells the model to call "
                + $"{ToolAudienceProfileToolCatalog.ToolOutputRead}. Add it to the list unless you want to block that.");
        }
    }

    // A stored copy of a shipped default list does not follow later defaults. The daemon maps an
    // exact older list to the current default and logs a warning. Doctor reports each copy, which
    // includes a copy of the current default, because `netclaw doctor --fix` deletes it.
    private static void CheckDefaultAllowedToolsCopies(JsonObject root, List<string> warnings)
    {
        foreach (var copy in DefaultAllowedToolsCopies.Find(root))
            warnings.Add(ToolAudienceProfileDefaults.DescribeLegacyDefaultAllowedTools(copy.Audience, copy.AllowedTools));
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

        return [.. allowedServers.Except(grantedServers, StringComparer.OrdinalIgnoreCase).Order()];
    }

    /// <summary>
    /// Finds enabled MCP servers that are reachable by the Personal audience
    /// (via <c>McpServersMode = All</c>) but have no
    /// <c>ApprovalPolicy.McpServerDefaults[server]</c> entry AND no
    /// <c>ToolOverrides</c> entries keyed by <c>{server}/*</c>. Such servers
    /// invoke their tools without any approval prompt on Personal.
    /// </summary>
    private static List<string> FindMcpServersMissingPersonalApprovalDefault(
        ToolAudienceProfiles profiles,
        IReadOnlyDictionary<string, McpServerEntry> mcpServers)
    {
        var personal = profiles.Personal;
        if (personal.McpServersMode != ToolProfileMode.All)
            return [];

        var result = new List<string>();
        foreach (var (serverName, entry) in mcpServers)
        {
            if (!entry.Enabled)
                continue;

            var approvalPolicy = personal.ApprovalPolicy;
            if (approvalPolicy is null)
            {
                result.Add(serverName);
                continue;
            }

            if (approvalPolicy.McpServerDefaults.ContainsKey(serverName))
                continue;

            var canonicalPrefix = $"{serverName}/";
            var aliasPrefix = $"{serverName}__";
            var hasPerToolOverride = approvalPolicy.ToolOverrides.Keys.Any(
                k => k.StartsWith(canonicalPrefix, StringComparison.Ordinal)
                  || k.StartsWith(aliasPrefix, StringComparison.Ordinal));
            if (hasPerToolOverride)
                continue;

            result.Add(serverName);
        }

        return [.. result.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Finds enabled MCP servers that a Personal allowlist does not include. The Personal
    /// default allows every server, so this state comes from an explicit allowlist.
    /// </summary>
    private static List<string> FindEnabledMcpServersOutsidePersonalAllowlist(
        ToolAudienceProfiles profiles,
        IReadOnlyDictionary<string, McpServerEntry> mcpServers)
    {
        var personal = profiles.Personal;
        if (personal.McpServersMode != ToolProfileMode.Allowlist)
            return [];

        return
        [
            .. mcpServers
                .Where(server => server.Value.Enabled
                    && !personal.AllowedMcpServers.Contains(server.Key, StringComparer.OrdinalIgnoreCase))
                .Select(server => server.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];
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

    private static void CheckExplicitPersonalShellAuto(ToolConfig toolConfig, List<string> warnings)
    {
        if (toolConfig.ShellMode != ShellExecutionMode.HostAllowed)
            return;

        var personal = toolConfig.AudienceProfiles.Personal;
        if (!PersonalProfileAllowsShell(personal))
            return;

        if (personal.ApprovalPolicy is null
            || !personal.ApprovalPolicy.TryGetExplicitMode(ShellTool.ToolName, out var approvalMode)
            || approvalMode != ToolApprovalMode.Auto)
            return;

        warnings.Add(
            "Personal profile explicitly sets shell_execute to Auto while host shell is enabled. " +
            "Commands that pass earlier security gates run without approval. " +
            "Set Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute to Approval.");
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

        try
        {
            var nativeShell = OperatingSystem.IsWindows()
                ? ApprovalShell.PowerShell
                : ApprovalShell.Bash;
            var store = new ToolApprovalStore(
                approvalsPath,
                timeProvider: null,
                migrationContext: new ApprovalStoreMigrationContext(nativeShell));
            var data = store.Load();

            foreach (var (audienceKey, tools) in data.Audiences)
            {
                if (!tools.TryGetValue(ShellTool.ToolName, out var entries))
                    continue;
                if (entries.Count == 0)
                    continue;

                if (toolConfig.ShellMode == ShellExecutionMode.Off)
                {
                    warnings.Add(
                        $"Persistent approvals exist for {audienceKey}.{ShellTool.ToolName} " +
                        "but shell is disabled.");
                }

                // R1: such a grant has no directory, so its relative program path
                // names no single file. It still matches, so it adds no prompt.
                var legacySpellings = entries.Count(static entry => entry.HasLegacyProgramSpelling);
                if (legacySpellings > 0)
                {
                    warnings.Add(
                        $"{legacySpellings} {audienceKey}.{ShellTool.ToolName} approval(s) use a legacy program spelling: " +
                        "a relative program path with no folder. Each one covers every file that the path can reach. " +
                        "Run 'netclaw approvals list' to see them, then revoke each one and approve the program again.");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read tool-approvals.json: {ex.Message}");
        }
    }
}
