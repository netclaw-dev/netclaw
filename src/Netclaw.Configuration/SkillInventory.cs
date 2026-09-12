// -----------------------------------------------------------------------
// <copyright file="SkillInventory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Configuration;

/// <summary>
/// Wire contract for <c>GET /api/skills</c>: the live skill inventory the daemon
/// has loaded. Unlike a filesystem scan, this includes DYNAMIC skills that never
/// exist as files — MCP prompt skills (<see cref="McpPromptSkillSource"/>) and any
/// server-feed skills the daemon syncs — so an operator can see exactly what the
/// agent can load, not just what is on disk.
/// </summary>
public static class SkillInventory
{
    /// <summary>The full inventory, one row per registered skill.</summary>
    public sealed class Response : IWireType
    {
        public required List<SkillRow> Skills { get; init; }
    }

    /// <summary>One registered skill and the metadata a client needs to present it.</summary>
    public sealed class SkillRow : IWireType
    {
        public required string Name { get; init; }

        public required string DisplayName { get; init; }

        public required string Description { get; init; }

        /// <summary>
        /// Where the skill came from: <c>system</c>, <c>native</c>, or <c>external</c>
        /// for file skills, or <c>mcp</c> for a skill derived from an MCP server prompt.
        /// </summary>
        public required string Source { get; init; }

        public string? Category { get; init; }

        public string? Version { get; init; }

        /// <summary>Whether a user can invoke the skill with <c>/name</c>.</summary>
        public bool UserInvocable { get; init; }

        /// <summary>Whether the model can auto-load the skill from the compressed index.</summary>
        public bool ModelInvocable { get; init; }

        /// <summary>Human-readable argument hint (e.g. <c>&lt;property&gt; [days]</c>), when the skill takes arguments.</summary>
        public string? ArgumentHint { get; init; }

        /// <summary>The MCP server that owns the prompt, when <see cref="Source"/> is <c>mcp</c>.</summary>
        public string? ServerName { get; init; }

        /// <summary>The underlying MCP prompt name (before the <c>mcp__server__</c> prefix), when <see cref="Source"/> is <c>mcp</c>.</summary>
        public string? PromptName { get; init; }

        /// <summary>The prompt's declared arguments, when <see cref="Source"/> is <c>mcp</c>. Null for file skills.</summary>
        public List<SkillArgument>? Arguments { get; init; }
    }

    /// <summary>A single declared argument of an MCP prompt skill.</summary>
    public sealed class SkillArgument : IWireType
    {
        public required string Name { get; init; }

        public string? Description { get; init; }

        public bool Required { get; init; }
    }

    /// <summary>
    /// Projects the daemon's live skill registry into the wire response. The source
    /// label for file skills is derived from the skill's path (mirroring the CLI's
    /// offline classification); MCP prompt skills report <c>mcp</c> plus their server,
    /// prompt name, and declared arguments.
    /// </summary>
    public static Response From(IEnumerable<SkillEntry> skills, NetclawPaths paths)
    {
        var systemPrefix = paths.SystemSkillsDirectory + Path.DirectorySeparatorChar;
        var nativePrefix = paths.SkillsDirectory + Path.DirectorySeparatorChar;

        var rows = skills
            .OrderBy(static skill => skill.Name, StringComparer.Ordinal)
            .Select(skill =>
            {
                var mcp = skill.Source as McpPromptSkillSource;
                return new SkillRow
                {
                    Name = skill.Name,
                    DisplayName = skill.DisplayName,
                    Description = skill.Description,
                    Source = Classify(skill, systemPrefix, nativePrefix),
                    Category = skill.Category,
                    Version = skill.Version,
                    UserInvocable = skill.UserInvocable,
                    ModelInvocable = !skill.DisableModelInvocation,
                    ArgumentHint = skill.ArgumentHint,
                    ServerName = mcp?.ServerName,
                    PromptName = mcp?.PromptName,
                    Arguments = mcp is null
                        ? null
                        : mcp.Arguments
                            .Select(static argument => new SkillArgument
                            {
                                Name = argument.Name,
                                Description = argument.Description,
                                Required = argument.Required,
                            })
                            .ToList(),
                };
            })
            .ToList();

        return new Response { Skills = rows };
    }

    private static string Classify(SkillEntry skill, string systemPrefix, string nativePrefix)
    {
        switch (skill.Source)
        {
            case McpPromptSkillSource:
                return "mcp";
            case FileSkillSource file when file.FilePath.StartsWith(systemPrefix, StringComparison.OrdinalIgnoreCase):
                return "system";
            case FileSkillSource file when file.FilePath.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase):
                return "native";
            case FileSkillSource:
                return "external";
            default:
                return "unknown";
        }
    }
}
