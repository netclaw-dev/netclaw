using Netclaw.Configuration;
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

    public ToolAudienceProfile ResolveProfile(ToolExecutionContext? context)
    {
        return ResolveProfile(ResolveAudience(context));
    }

    public ToolAudienceProfile ResolveProfile(TrustAudience audience)
    {
        return ToolAudienceProfileDefaults.GetResolvedProfile(_toolConfig.AudienceProfiles, audience);
    }

    public IReadOnlyList<string> ResolveRoots(ToolFilesystemAccessProfile access, ToolExecutionContext context)
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

    public bool IsToolAllowed(ToolName toolName, ToolExecutionContext? context)
    {
        if (!IsProfileManagedTool(toolName))
            return true;

        var profile = ResolveProfile(context);

        if (profile.ToolsMode == ToolProfileMode.All)
            return true;

        return profile.AllowedTools.Contains(toolName.Value, StringComparer.Ordinal);
    }

    public bool IsMcpServerAllowed(McpServerName serverName, ToolExecutionContext? context)
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
    /// Checks whether a specific tool from an MCP server is allowed for the given audience.
    /// Returns true if:
    /// - The profile has no <see cref="ToolAudienceProfile.McpServerToolGrants"/> (null), or
    /// - The server has no entry in the grants dictionary, or
    /// - The tool name appears in the server's grant list.
    /// </summary>
    public bool IsMcpToolAllowed(McpServerName serverName, ToolName toolName, TrustAudience audience)
    {
        var profile = ResolveProfile(audience);
        return IsMcpToolAllowed(serverName, toolName, profile);
    }

    public bool IsMcpToolAllowed(McpServerName serverName, ToolName toolName, ToolExecutionContext? context)
    {
        var profile = ResolveProfile(context);
        return IsMcpToolAllowed(serverName, toolName, profile);
    }

    /// <summary>
    /// Resolves a path token (e.g. <c>{skills_dir}</c>, <c>{identity_dir}</c>, <c>{workspaces_dir}</c>) to an absolute path.
    /// Returns null for empty input or if <see cref="_paths"/> is not available for path-based tokens.
    /// Unrecognized values are treated as literal paths.
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

        // Literal path
        return trimmed;
    }

    private string? ResolveToken(string root, ToolExecutionContext context)
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

    private static TrustAudience ResolveAudience(ToolExecutionContext? context)
        => SecurityPolicyDefaults.TryParseAudience(context?.Audience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(context?.SessionId);

    private static bool IsMcpServerAllowed(McpServerName serverName, ToolAudienceProfile profile)
    {
        if (profile.McpServersMode == ToolProfileMode.All)
            return true;

        return profile.AllowedMcpServers.Contains(serverName.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsMcpToolAllowed(McpServerName serverName, ToolName toolName, ToolAudienceProfile profile)
    {
        if (profile.McpServerToolGrants is not { } grants)
            return true;

        if (!grants.TryGetValue(serverName.Value, out var allowedTools))
            return true;

        return allowedTools.Contains(toolName.Value, StringComparer.Ordinal);
    }

    private static bool IsProfileManagedTool(ToolName toolName)
        => toolName.Value is "shell_execute"
            or "file_read"
            or "file_write"
            or "attach_file"
            or "skill_manage"
            or "set_webhook"
            or "list_webhooks"
            or "delete_webhook";
}
