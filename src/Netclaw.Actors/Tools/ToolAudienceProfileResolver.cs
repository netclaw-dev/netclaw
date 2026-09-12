// -----------------------------------------------------------------------
// <copyright file="ToolAudienceProfileResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ToolAudienceProfileResolver
{
    private readonly ToolConfig _toolConfig;
    private readonly NetclawPaths? _paths;

    public ToolAudienceProfileResolver(ToolConfig toolConfig, NetclawPaths? paths = null)
    {
        _toolConfig = toolConfig;
        _paths = paths;
    }

    public ToolAudienceProfile ResolveProfile(ToolInvocationContext context)
    {
        return ResolveProfile(ResolveAudience(context));
    }

    public ToolAudienceProfile ResolveProfile(TrustAudience audience)
    {
        return ToolAudienceProfileDefaults.GetResolvedProfile(_toolConfig.AudienceProfiles, audience);
    }

    public IReadOnlyList<string> ResolveRoots(ToolFilesystemAccessProfile access, ToolInvocationContext context)
    {
        if (access.Mode != ToolFilesystemMode.Roots)
            return [];

        var roots = new List<string>();
        foreach (var root in access.Roots)
        {
            var resolved = ResolveToken(root, context);
            if (resolved is not null)
                roots.Add(resolved);
        }

        return roots;
    }

    /// <summary>
    /// Resolves <see cref="ToolAudienceProfiles.GlobalReadRoots"/> tokens into absolute paths.
    /// Token-based roots (e.g. <c>{skills_dir}</c>) require <see cref="_paths"/> to resolve;
    /// literal paths are always included.
    /// </summary>
    public IReadOnlyList<string> ResolveGlobalReadRoots()
    {
        var profiles = _toolConfig.AudienceProfiles;
        var roots = new List<string>();
        foreach (var root in profiles.GlobalReadRoots)
        {
            var resolved = ResolvePathToken(root);
            if (resolved is not null)
                roots.Add(resolved);
        }

        return roots;
    }

    /// <summary>
    /// Resolves the operator-configured workspaces directory — the designated
    /// writable working area shared across sessions. Returns null when
    /// <see cref="_paths"/> is unavailable (e.g. a policy constructed without
    /// paths), so callers fail closed rather than inventing a default root.
    /// </summary>
    public string? ResolveWorkspacesDirectory() => _paths?.WorkspacesDirectory;

    public bool IsToolAllowed(ToolName toolName, ToolInvocationContext context)
        => IsToolAllowed(toolName, context.Audience);

    public bool IsToolAllowed(ToolName toolName, TrustAudience audience)
    {
        if (!IsProfileManagedTool(toolName))
            return true;

        var profile = ResolveProfile(audience);

        if (profile.ToolsMode == ToolProfileMode.All)
            return true;

        return profile.AllowedTools.Contains(toolName.Value, StringComparer.Ordinal);
    }

    public bool IsMcpServerAllowed(McpServerName serverName, ToolInvocationContext context)
    {
        var profile = ResolveProfile(context);
        return IsMcpServerAllowed(serverName, profile);
    }

    public bool IsMcpServerAllowed(McpServerName serverName, TrustAudience audience)
    {
        var profile = ResolveProfile(audience);
        return IsMcpServerAllowed(serverName, profile);
    }

    /// <summary>
    /// Checks whether an MCP tool passes the audience profile.
    /// Per-tool grants apply only when the profile uses <see cref="ToolProfileMode.Allowlist"/>.
    /// </summary>
    public bool IsMcpToolAllowed(McpServerName serverName, ToolName toolName, TrustAudience audience)
    {
        var profile = ResolveProfile(audience);
        return IsMcpToolAllowed(serverName, toolName, profile);
    }

    public bool IsMcpToolAllowed(McpServerName serverName, ToolName toolName, ToolInvocationContext context)
    {
        var profile = ResolveProfile(context);
        return IsMcpToolAllowed(serverName, toolName, profile);
    }

    /// <summary>
    /// Resolves a path token (e.g. <c>{skills_dir}</c>, <c>{identity_dir}</c>, <c>{workspaces_dir}</c>) to an absolute path.
    /// Returns null for empty input or if <see cref="_paths"/> is not available for path-based tokens.
    /// Unrecognized values are treated as literal paths and have shell home tokens
    /// (<c>~</c>, <c>$HOME</c>, <c>${HOME}</c>, <c>%USERPROFILE%</c>) expanded so they
    /// resolve correctly when the daemon's CWD is not the user's home directory.
    /// </summary>
    private string? ResolvePathToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var trimmed = token.Trim();

        if (string.Equals(trimmed, ToolAudienceProfileDefaults.SkillsDirectoryToken, StringComparison.OrdinalIgnoreCase))
            return _paths?.SkillsDirectory;

        if (string.Equals(trimmed, ToolAudienceProfileDefaults.IdentityDirectoryToken, StringComparison.OrdinalIgnoreCase))
            return _paths?.IdentityDirectory;

        if (string.Equals(trimmed, ToolAudienceProfileDefaults.WorkspacesDirectoryToken, StringComparison.OrdinalIgnoreCase))
            return _paths?.WorkspacesDirectory;

        return PathUtility.ExpandHome(trimmed);
    }

    private string? ResolveToken(string root, ToolInvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var trimmed = root.Trim();

        // Session token requires context, not paths
        if (string.Equals(trimmed, ToolAudienceProfileDefaults.SessionDirectoryToken, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(context.SessionDirectory) ? null : context.SessionDirectory;

        // Delegate to shared path token resolver
        return ResolvePathToken(trimmed);
    }

    private static TrustAudience ResolveAudience(ToolInvocationContext context)
        => context.Audience;

    private static bool IsMcpServerAllowed(McpServerName serverName, ToolAudienceProfile profile)
    {
        if (profile.McpServersMode == ToolProfileMode.All)
            return true;

        return profile.AllowedMcpServers.Contains(serverName.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsMcpToolAllowed(McpServerName serverName, ToolName toolName, ToolAudienceProfile profile)
    {
        if (profile.McpServersMode == ToolProfileMode.All)
            return true;

        if (profile.McpServerToolGrants is not { } grants)
            return true;

        if (!grants.TryGetValue(serverName.Value, out var allowedTools))
            return true;

        return allowedTools.Contains(toolName.Value, StringComparer.Ordinal);
    }

    private static bool IsProfileManagedTool(ToolName toolName)
        => ToolAudienceProfileToolCatalog.IsProfileManaged(toolName.Value);
}
