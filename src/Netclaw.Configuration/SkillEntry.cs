namespace Netclaw.Configuration;

/// <summary>
/// Indicates the metadata format used by a skill file.
/// </summary>
public enum SkillFormat
{
    /// <summary>
    /// HTML comment metadata (<c>&lt;!-- description: ... --&gt;</c>).
    /// </summary>
    Legacy,

    /// <summary>
    /// AgentSkills.io YAML frontmatter (<c>---\nname: ...\n---</c>).
    /// </summary>
    Standard
}

/// <summary>
/// Metadata for a single skill file discovered in the skills directory.
/// Skills are procedural knowledge (how-to instructions) loaded on demand.
/// </summary>
public sealed record SkillEntry(
    string Name,         // skill name, e.g. "git-workflow"
    string DisplayName,  // from first # heading, or titlecased name
    string Description,  // from YAML frontmatter or <!-- description: ... --> comment
    string FilePath,     // absolute path to .md or SKILL.md file
    string? Category)    // subdirectory name, or null if in root
{
    /// <summary>
    /// Activation triggers parsed from <c>&lt;!-- triggers: ... --&gt;</c> comment
    /// or YAML frontmatter <c>metadata.triggers</c>.
    /// Pipe-separated conditions indicating when the agent should load this skill.
    /// </summary>
    public string? Triggers { get; init; }

    /// <summary>
    /// Metadata format of the source skill file.
    /// </summary>
    public SkillFormat Format { get; init; } = SkillFormat.Legacy;

    /// <summary>
    /// Skill version from YAML frontmatter <c>metadata.version</c>.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// License from YAML frontmatter <c>license</c> field.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// Environment requirements from YAML frontmatter <c>compatibility</c> field.
    /// </summary>
    public string? Compatibility { get; init; }

    /// <summary>
    /// Pre-approved tools from YAML frontmatter <c>allowed-tools</c> field.
    /// Space-delimited list.
    /// </summary>
    public string? AllowedTools { get; init; }

    /// <summary>
    /// For directory-based skills (<c>foo/SKILL.md</c>), the absolute path to the
    /// skill directory. Null for flat file skills.
    /// Enables Tier 3 resource resolution (scripts/, references/, assets/).
    /// </summary>
    public string? SkillDirectory { get; init; }

    /// <summary>
    /// Relative paths to resource files within the skill directory
    /// (e.g. <c>references/flight-pricing.md</c>, <c>scripts/deploy.sh</c>).
    /// Null for flat file skills or directory skills with no resources.
    /// </summary>
    public IReadOnlyList<string>? ResourcePaths { get; init; }
}
