using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ToolAudienceProfileResolver
{
    private readonly ToolConfig _toolConfig;

    public ToolAudienceProfileResolver(ToolConfig toolConfig)
    {
        _toolConfig = toolConfig;
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
            if (string.IsNullOrWhiteSpace(root))
                continue;

            if (string.Equals(root.Trim(), ToolAudienceProfileDefaults.SessionDirectoryToken, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
                    roots.Add(context.SessionDirectory!);
                continue;
            }

            roots.Add(root.Trim());
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

    private static TrustAudience ResolveAudience(ToolExecutionContext? context)
        => SecurityPolicyDefaults.TryParseAudience(context?.Audience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(context?.SessionId);

    private static bool IsProfileManagedTool(string toolName)
        => toolName is "shell_execute" or "file_read" or "file_write" or "attach_file";
}
