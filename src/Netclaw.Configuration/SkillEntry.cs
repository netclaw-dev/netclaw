namespace Netclaw.Configuration;

/// <summary>
/// Metadata for a single skill discovered in the skills directory.
/// Skills are procedural knowledge (how-to instructions) loaded on demand.
/// All skills use the AgentSkills.io directory layout: <c>skill-name/SKILL.md</c>
/// with YAML frontmatter for metadata.
/// </summary>
public sealed record SkillEntry(
    string Name,            // skill name, e.g. "git-workflow"
    string DisplayName,     // from first # heading, or titlecased name
    string Description,     // from YAML frontmatter description field
    string FilePath,        // absolute path to SKILL.md
    string SkillDirectory,  // absolute path to the skill directory
    string? Category)       // parent subdirectory name, or null if in root
{
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
    /// Relative paths to resource files within the skill directory
    /// (e.g. <c>references/flight-pricing.md</c>, <c>scripts/deploy.sh</c>).
    /// Null if the skill has no resources.
    /// </summary>
    public IReadOnlyList<string>? ResourcePaths { get; init; }
}
