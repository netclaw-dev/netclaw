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

    public bool IsToolAllowed(string toolName, ToolExecutionContext? context)
    {
        if (!IsProfileManagedTool(toolName))
            return true;

        var profile = ResolveProfile(context);

        if (profile.ToolsMode == ToolProfileMode.All)
            return true;

        return profile.AllowedTools.Contains(toolName, StringComparer.Ordinal);
    }

    public bool IsMcpServerAllowed(string serverName, ToolExecutionContext? context)
    {
        var profile = ResolveProfile(context);
        return IsMcpServerAllowed(serverName, profile);
    }

    public bool IsMcpServerAllowed(string serverName, TrustAudience audience)
    {
        var profile = ResolveProfile(audience);
        return IsMcpServerAllowed(serverName, profile);
    }

    /// <summary>
    /// Resolves a path token (e.g. <c>{skills_dir}</c>, <c>{identity_dir}</c>) to an absolute path.
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

    private static bool IsMcpServerAllowed(string serverName, ToolAudienceProfile profile)
    {
        if (profile.McpServersMode == ToolProfileMode.All)
            return true;

        return profile.AllowedMcpServers.Contains(serverName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsProfileManagedTool(string toolName)
        => toolName is "shell_execute" or "file_read" or "file_write" or "attach_file" or "skill_manage";
}
