// -----------------------------------------------------------------------
// <copyright file="FileSubAgentDefinitionLoader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;

namespace Netclaw.Configuration;

/// <summary>
/// Loads subagent definitions from <c>.md</c> files in the agents directory.
/// Each agent is a single markdown file: YAML frontmatter carries metadata and
/// the body is the system prompt verbatim. Matches the de facto Claude Code /
/// OpenCode format and the <c>SKILL.md</c> pattern Netclaw already uses for skills.
/// Called during startup after MCP discovery so tool names are resolvable.
/// </summary>
public sealed class FileSubAgentDefinitionLoader
{
    private readonly string _agentsDirectory;
    private readonly ILogger<FileSubAgentDefinitionLoader> _logger;

    public FileSubAgentDefinitionLoader(NetclawPaths paths, ILogger<FileSubAgentDefinitionLoader> logger)
    {
        _agentsDirectory = paths.AgentsDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Scan the agents directory for <c>*.md</c> files and return parsed profiles.
    /// Malformed files are rejected with a loud warning and skipped — no silent fallback.
    /// Duplicate <c>name</c> values across files are rejected for all but the first occurrence.
    /// </summary>
    public IReadOnlyList<SubAgentProfile> LoadAll()
    {
        if (!Directory.Exists(_agentsDirectory))
        {
            _logger.LogWarning("Agents directory does not exist: {Path}", _agentsDirectory);
            return [];
        }

        var files = Directory.GetFiles(_agentsDirectory, "*.md");
        if (files.Length == 0)
        {
            _logger.LogWarning("No agent definition files found in {Path}", _agentsDirectory);
            return [];
        }

        var results = new List<SubAgentProfile>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Enumerate files in a stable order so duplicate-name diagnostics are deterministic.
        foreach (var filePath in files.OrderBy(p => p, StringComparer.Ordinal))
        {
            var profile = TryParse(filePath);
            if (profile is null)
                continue;

            if (!seenNames.Add(profile.Name))
            {
                _logger.LogWarning(
                    "Agent definition at {Path} declares duplicate name '{Name}' — skipping",
                    filePath,
                    profile.Name);
                continue;
            }

            results.Add(profile);
            _logger.LogInformation(
                "Loaded agent definition: {Name} ({ToolCount} tools, timeout={Timeout}s)",
                profile.Name, profile.ToolNames.Count, profile.TimeoutSeconds);
        }

        return results;
    }

    private SubAgentProfile? TryParse(string filePath)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read agent definition: {Path}", filePath);
            return null;
        }

        var frontmatter = SubAgentMarkdownParser.ExtractFrontmatter(content);
        if (frontmatter is null)
        {
            _logger.LogWarning(
                "Agent definition at {Path} has missing or unparseable YAML frontmatter — skipping",
                filePath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Name))
        {
            _logger.LogWarning("Agent definition at {Path} has no 'name' in frontmatter — skipping", filePath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Description))
        {
            _logger.LogWarning(
                "Agent '{Name}' at {Path} has no 'description' in frontmatter — skipping",
                frontmatter.Name, filePath);
            return null;
        }

        var systemPrompt = SubAgentMarkdownParser.ExtractBody(content);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            _logger.LogWarning(
                "Agent '{Name}' at {Path} has an empty system prompt body — skipping",
                frontmatter.Name, filePath);
            return null;
        }

        // Tools are optional. When omitted, the subagent inherits session tools at spawn time.
        // This matches Claude Code's agent format where tools are not specified.
        var tools = frontmatter.Tools ?? [];

        var modelRole = ParseModelRole(frontmatter.ModelRole);
        var visibility = ParseVisibility(frontmatter.Visibility);

        return new SubAgentProfile
        {
            Name = frontmatter.Name.Trim(),
            Description = frontmatter.Description.Trim(),
            SystemPrompt = systemPrompt,
            ToolNames = tools,
            ModelRole = modelRole,
            TimeoutSeconds = frontmatter.TimeoutSeconds ?? 60,
            EmitStructuredFindings = frontmatter.EmitStructuredFindings ?? false,
            Visibility = visibility
        };
    }

    private static ModelRole ParseModelRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ModelRole.Compaction;

        return Enum.TryParse<ModelRole>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ModelRole.Compaction;
    }

    private static SubAgentVisibility ParseVisibility(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SubAgentVisibility.UserFacing;

        // Accept both "user-facing" (hyphenated, matches frontmatter convention)
        // and "UserFacing" (PascalCase, matches the enum value name).
        var normalized = value.Replace("-", "", StringComparison.Ordinal);
        return Enum.TryParse<SubAgentVisibility>(normalized, ignoreCase: true, out var parsed)
            ? parsed
            : SubAgentVisibility.UserFacing;
    }
}
