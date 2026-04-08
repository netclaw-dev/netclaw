using Netclaw.Configuration;

namespace Netclaw.Actors.Skills;

public enum SkillScanIssueKind
{
    MissingFrontmatter,
    InvalidFrontmatter,
    MissingDescription,
    UnreadableFile,
    InvalidPath,
    SymlinkNotAllowed,
    DuplicateName,
    FrontmatterNameMismatch,
    ResourceEnumerationFailed,
    FlatFileMissingFrontmatter,
    FlatFileNoDescription,
}

public sealed record SkillScanIssue(
    string Path,
    SkillScanIssueKind Kind,
    string Message,
    string? SkillName = null);

public sealed record SkillScanResult(
    IReadOnlyList<SkillEntry> AcceptedSkills,
    IReadOnlyList<SkillScanIssue> Issues)
{
    public static SkillScanResult Empty { get; } = new([], []);

    public bool HasIssues => Issues.Count > 0;

    public string Summary => $"accepted {AcceptedSkills.Count} skill(s), rejected {Issues.Count} item(s)";

    public IEnumerable<string> FormatIssueLines(int maxIssues = 5)
    {
        foreach (var issue in Issues.Take(maxIssues))
            yield return $"[{issue.Kind}] {issue.Path} - {issue.Message}";

        if (Issues.Count > maxIssues)
            yield return $"... and {Issues.Count - maxIssues} more issue(s)";
    }
}

/// <summary>
/// Merged skill scan result from native + external sources.
/// </summary>
public sealed record MergedSkillScanResult(
    IReadOnlyList<SkillEntry> AcceptedSkills,
    IReadOnlyList<SkillScanIssue> Issues);
