// -----------------------------------------------------------------------
// <copyright file="SubAgentMarkdownParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Netclaw.Configuration;

/// <summary>
/// Parses subagent definition files in the markdown-with-YAML-frontmatter format:
/// one <c>.md</c> file per agent where the frontmatter carries metadata and
/// the body is the system prompt verbatim. Matches the <c>SKILL.md</c> convention
/// from <c>SkillScanner</c> and the de facto format used by Claude Code and OpenCode.
/// </summary>
public static class SubAgentMarkdownParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Extract the YAML frontmatter block (between the leading and trailing
    /// <c>---</c> delimiters) and deserialize it into a <see cref="SubAgentFrontmatter"/>.
    /// Returns <c>null</c> when the file has no frontmatter block or the YAML is unparseable.
    /// </summary>
    public static SubAgentFrontmatter? ExtractFrontmatter(string content)
    {
        if (content is null)
            return null;

        if (!content.StartsWith("---", StringComparison.Ordinal))
            return null;

        var closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closingIndex < 0)
            return null;

        var firstNewline = content.IndexOf('\n', 0);
        if (firstNewline < 0 || firstNewline >= closingIndex)
            return null;

        var yamlBlock = content[(firstNewline + 1)..closingIndex];

        try
        {
            return YamlDeserializer.Deserialize<SubAgentFrontmatter>(yamlBlock);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extract the markdown body that follows the YAML frontmatter closing delimiter.
    /// Returns the full content when no frontmatter is present so callers can still
    /// decide how to fail (typically: reject the file with a loud warning).
    /// </summary>
    public static string ExtractBody(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        if (!content.StartsWith("---", StringComparison.Ordinal))
            return content;

        var closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closingIndex < 0)
            return content;

        var bodyStart = content.IndexOf('\n', closingIndex + 4);
        return bodyStart < 0 ? string.Empty : content[(bodyStart + 1)..].TrimStart();
    }
}

/// <summary>
/// Shape of a subagent definition's YAML frontmatter. Maps 1:1 onto the
/// runtime <see cref="SubAgentProfile"/>. Deserialization uses camelCase naming
/// (so <c>timeoutSeconds</c>, <c>modelRole</c>, <c>emitStructuredFindings</c>,
/// <c>visibility</c>). Unknown fields are ignored for forward compatibility.
/// </summary>
public sealed class SubAgentFrontmatter
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<string>? Tools { get; set; }
    public string? ModelRole { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int? PrefillTimeoutSeconds { get; set; }
    public bool? EmitStructuredFindings { get; set; }
    public string? Visibility { get; set; }
}
