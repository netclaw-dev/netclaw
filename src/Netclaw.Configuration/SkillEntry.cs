namespace Netclaw.Configuration;

/// <summary>
/// Metadata for a single skill file discovered in the skills directory.
/// Skills are procedural knowledge (how-to instructions) loaded on demand.
/// </summary>
public sealed record SkillEntry(
    string Name,         // filename without extension, e.g. "git-workflow"
    string DisplayName,  // from first # heading, or titlecased name
    string Description,  // first paragraph or <!-- description: ... --> comment
    string FilePath,     // absolute path to .md file
    string? Category)    // subdirectory name, or null if in root
{
    /// <summary>
    /// Activation triggers parsed from <c>&lt;!-- triggers: ... --&gt;</c> comment.
    /// Pipe-separated conditions indicating when the agent should load this skill.
    /// </summary>
    public string? Triggers { get; init; }
}
