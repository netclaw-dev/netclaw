// -----------------------------------------------------------------------
// <copyright file="McpToolGrantDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>Checks whether All posture MCP grants describe the live tool catalog.</summary>
public sealed class McpToolGrantDoctorCheck : IDoctorCheck
{
    private readonly NetclawPaths _paths;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _getToolNames;

    public McpToolGrantDoctorCheck(NetclawPaths paths, DaemonApi daemonApi)
        : this(paths, async (serverName, cancellationToken) =>
            await daemonApi.GetMcpToolNamesAsync(serverName, cancellationToken).ConfigureAwait(false))
    {
    }

    internal McpToolGrantDoctorCheck(
        NetclawPaths paths,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> getToolNames)
    {
        _paths = paths;
        _getToolNames = getToolNames;
    }

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(_paths);
        if (error is not null)
            return error;

        if (root is null || root["Tools"] is not JsonObject toolsObject
            || toolsObject["AudienceProfiles"] is not JsonObject)
        {
            return DoctorCheckResult.Pass(
                "MCP Tool Grant Snapshots",
                "Audience profiles are not configured; no MCP grant snapshot check is needed.");
        }

        ToolConfig toolConfig;
        try
        {
            toolConfig = JsonSerializer.Deserialize<ToolConfig>(toolsObject, JsonDefaults.ConfigRead)
                ?? new ToolConfig();
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error(
                "MCP Tool Grant Snapshots",
                $"Failed to parse Tools configuration: {ex.Message}",
                "Fix Tools.AudienceProfiles values or rerun `netclaw init`.");
        }

        var mcpServers = ReadMcpServers(root);
        if (mcpServers.Count == 0)
        {
            return DoctorCheckResult.Pass("MCP Tool Grant Snapshots", "No MCP servers are configured.");
        }

        var findings = new List<string>();
        var unavailableServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileName, profile) in Profiles(toolConfig.AudienceProfiles))
        {
            if (profile.McpServersMode != ToolProfileMode.All
                || profile.McpServerToolGrants is not { } grants)
                continue;

            foreach (var (serverName, grantedTools) in grants)
            {
                if (grantedTools is null
                    || !mcpServers.TryGetValue(serverName, out var server)
                    || !server.Enabled)
                    continue;

                IReadOnlyList<string> discoveredTools;
                try
                {
                    discoveredTools = await _getToolNames(serverName, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    unavailableServers.Add(serverName);
                    continue;
                }

                var granted = new HashSet<string>(grantedTools, StringComparer.Ordinal);
                var omitted = discoveredTools
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .Where(name => !granted.Contains(name))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (omitted.Length == 0)
                    continue;

                findings.Add(
                    $"{profileName} profile has McpServerToolGrants for '{serverName}' while "
                    + $"McpServersMode=All, so {omitted.Length} discovered tool(s) remain exposed: "
                    + string.Join(", ", omitted) + ".");
            }
        }

        if (unavailableServers.Count > 0)
        {
            findings.Add(
                "The live tool catalog could not be checked for "
                + string.Join(", ", unavailableServers.Order(StringComparer.Ordinal))
                + "; start the daemon and run `netclaw doctor` again.");
        }

        if (findings.Count > 0)
        {
            return DoctorCheckResult.Warning(
                "MCP Tool Grant Snapshots",
                string.Join(" ", findings),
                "Set the audience to Allowlist to enforce a curated snapshot, use `netclaw mcp permissions` to add a Deny override, or run `netclaw mcp tools <server> --revoke <tool> --audience <name>` for each exposed tool.");
        }

        return DoctorCheckResult.Pass(
            "MCP Tool Grant Snapshots",
            "All configured MCP tool snapshots match their audience access mode.");
    }

    private static Dictionary<string, McpServerEntry> ReadMcpServers(JsonObject root)
    {
        if (root["McpServers"] is not JsonObject mcpObject)
            return new Dictionary<string, McpServerEntry>(StringComparer.OrdinalIgnoreCase);

        var servers = JsonSerializer.Deserialize<Dictionary<string, McpServerEntry>>(
            mcpObject, JsonDefaults.ConfigRead) ?? [];
        return new Dictionary<string, McpServerEntry>(servers, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(string Name, ToolAudienceProfile Profile)> Profiles(
        ToolAudienceProfiles profiles) =>
    [
        ("Public", profiles.Public),
        ("Team", profiles.Team),
        ("Personal", profiles.Personal),
    ];
}
