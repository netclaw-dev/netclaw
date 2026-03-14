namespace Netclaw.Configuration;

/// <summary>
/// Central policy for tools exposed to user-facing subagents.
/// File-authored agents are intentionally constrained to a conservative,
/// read-oriented tool set so they do not bypass the main session's safety model.
/// </summary>
public static class SubAgentToolPolicy
{
    private static readonly HashSet<string> SafeUserFacingToolNames = new(StringComparer.Ordinal)
    {
        "attach_file",
        "file_read",
        "web_fetch",
        "web_search"
    };

    public static bool IsAllowedForUserFacing(string toolName)
        => SafeUserFacingToolNames.Contains(toolName);

    public static IReadOnlyList<string> GetAllowedUserFacingTools()
        => SafeUserFacingToolNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();
}
