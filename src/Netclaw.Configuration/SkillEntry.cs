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

    /// <summary>
    /// When <c>true</c>, the LLM cannot auto-load this skill — it is excluded
    /// from the compressed index. The user can still invoke it via <c>/name</c>.
    /// From frontmatter <c>disable-model-invocation</c>.
    /// </summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>
    /// When <c>false</c>, the user cannot invoke this skill via <c>/name</c>.
    /// The LLM can still auto-load it from the compressed index.
    /// From frontmatter <c>user-invocable</c>. Default: <c>true</c>.
    /// </summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>
    /// Hint shown after the slash command name for discoverability.
    /// From frontmatter <c>argument-hint</c>. Example: <c>[subsystem]</c>.
    /// </summary>
    public string? ArgumentHint { get; init; }

    /// <summary>
    /// When <c>true</c>, this skill was loaded from a flat <c>.md</c> file
    /// rather than the standard <c>skill-name/SKILL.md</c> directory layout.
    /// Flat-file skills cannot have resources (no subdirectories).
    /// </summary>
    public bool IsFlatFile { get; init; }
}
