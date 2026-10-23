// -----------------------------------------------------------------------
// <copyright file="ApprovalOverrideKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

/// <summary>
/// Matching rules for the keys of <see cref="ToolApprovalConfig.ToolOverrides"/> and
/// <see cref="ToolApprovalConfig.McpServerDefaults"/>. Keys match without regard to letter
/// case, as every other Netclaw configuration key does. A dictionary that holds two keys
/// differing only by case resolves to the most restrictive mode (Deny, then Approval, then
/// Auto), so the spelling of a key can never loosen a restriction. Stored spelling is never
/// rewritten.
/// </summary>
public static partial class ApprovalOverrideKeys
{
    /// <summary>
    /// Looks up <paramref name="key"/> without regard to case. When several keys match, the
    /// most restrictive mode wins. <see cref="ToolApprovalMode"/> is declared in order of
    /// increasing restriction, so the highest value is the most restrictive.
    /// </summary>
    public static bool TryGetMode(IReadOnlyDictionary<string, ToolApprovalMode> map, string key, out ToolApprovalMode mode)
    {
        var found = false;
        mode = default;
        foreach (var (candidate, candidateMode) in map)
        {
            if (!string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!found || candidateMode > mode)
                mode = candidateMode;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Removes every key that equals <paramref name="key"/> without regard to case, so a write
    /// of <paramref name="key"/> cannot leave a second spelling beside it.
    /// </summary>
    public static bool Remove<TValue>(IDictionary<string, TValue> map, string key)
    {
        var matches = map.Keys.Where(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var match in matches)
            map.Remove(match);

        return matches.Count > 0;
    }

    /// <summary>Sets <paramref name="key"/>, replacing any spelling that differs only by case.</summary>
    public static void Set<TValue>(IDictionary<string, TValue> map, string key, TValue value)
    {
        Remove(map, key);
        map[key] = value;
    }

    /// <summary>
    /// Whether <paramref name="key"/> can be the name of a tool: a built-in name, or an MCP name
    /// as <c>server/tool</c> or <c>server__tool</c>. The tool set is not fixed (skills,
    /// sub-agents, channels and MCP servers add tools at runtime, and an MCP server's tools are
    /// known only while it is connected), so this checks the shape that the runtime requires of
    /// every tool name and not membership in a list. A key that fails it can never match a tool.
    /// </summary>
    public static bool CanBeToolName(string key)
        => key.Length > 0 && ToolNameShape().IsMatch(key.Replace("/", "__", StringComparison.Ordinal));

    /// <summary>
    /// One line for each key of <paramref name="policy"/> whose spelling differs from the name of
    /// the tool or MCP server that it applies to. Keys used to match with case, so such a key may
    /// not have applied before. A first-party tool name is lowercase. For an MCP tool the server
    /// part is compared with <paramref name="serverNames"/>; the tool part of an MCP name is known
    /// only while the server is connected, so it is not compared.
    /// </summary>
    public static IEnumerable<string> DescribeSpellingNotices(
        string policyPath, ToolApprovalConfig policy, IReadOnlyCollection<string> serverNames)
    {
        foreach (var key in policy.ToolOverrides.Keys.Order(StringComparer.Ordinal))
        {
            var server = serverNames
                .Where(name => StartsWithServer(key, name))
                .OrderByDescending(static name => name.Length)
                .FirstOrDefault();
            if (server is not null)
            {
                if (!key.StartsWith(server, StringComparison.Ordinal))
                    yield return $"{policyPath}.ToolOverrides key '{key}' now applies to the tools of MCP server '{server}'{MatchNote}";
            }
            else if (!key.Contains('/', StringComparison.Ordinal)
                     && !key.Contains("__", StringComparison.Ordinal)
                     && !string.Equals(key, key.ToLowerInvariant(), StringComparison.Ordinal))
            {
                yield return $"{policyPath}.ToolOverrides key '{key}' now applies to tool '{key.ToLowerInvariant()}'{MatchNote}";
            }
        }

        foreach (var key in policy.McpServerDefaults.Keys.Order(StringComparer.Ordinal))
        {
            var server = serverNames.FirstOrDefault(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
            if (server is not null && !string.Equals(server, key, StringComparison.Ordinal))
                yield return $"{policyPath}.McpServerDefaults key '{key}' now applies to MCP server '{server}'{MatchNote}";
        }
    }

    private const string MatchNote = " (keys are matched without regard to case).";

    private static bool StartsWithServer(string key, string server)
        => key.StartsWith(server + "/", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith(server + "__", StringComparison.OrdinalIgnoreCase);

    /// <summary>The startup and doctor message for a key that fails <see cref="CanBeToolName"/>.</summary>
    public static string DescribeUnusableKey(string path, string key)
        => $"{path} key '{key}' was ignored because it cannot be a tool name. "
           + "No tool has this name, so the mode you wrote is not applied. "
           + "Valid forms are tool_name, server/tool, or server__tool.";

    // The pattern that LlmFacingToolName applies to every registered tool name.
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,128}$")]
    private static partial Regex ToolNameShape();
}
