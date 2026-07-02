// -----------------------------------------------------------------------
// <copyright file="SkillResourcePath.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Tools;

internal static class SkillResourcePath
{
    private const string SkillFileName = "SKILL.md";

    internal static bool TryNormalize(string? path, out string normalized, out SkillResourcePathError error)
    {
        normalized = string.Empty;
        error = SkillResourcePathError.None;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = SkillResourcePathError.Required;
            return false;
        }

        if (Path.IsPathRooted(path))
        {
            error = SkillResourcePathError.Absolute;
            return false;
        }

        normalized = path.Trim().Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.EndsWith('/'))
        {
            error = SkillResourcePathError.NotRelativeFile;
            return false;
        }

        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            error = SkillResourcePathError.InvalidSegment;
            return false;
        }

        normalized = string.Join('/', segments);
        if (string.Equals(normalized, SkillFileName, StringComparison.OrdinalIgnoreCase))
        {
            error = SkillResourcePathError.SkillFile;
            return false;
        }

        return true;
    }

    internal static string FormatReadError(SkillResourcePathError error)
        => error switch
        {
            SkillResourcePathError.Required => "ResourcePath is required.",
            SkillResourcePathError.Absolute => "Absolute paths are not allowed. Use a relative path like 'references/doc.md'.",
            SkillResourcePathError.NotRelativeFile => "Resource path must be a relative file path inside the skill directory.",
            SkillResourcePathError.InvalidSegment => "Resource path cannot contain empty, '.', or '..' segments.",
            SkillResourcePathError.SkillFile => "Use skill_load to read SKILL.md; resource paths must refer to additional files.",
            _ => "Invalid resource path."
        };

    internal static string FormatManageError(SkillResourcePathError error)
        => error switch
        {
            SkillResourcePathError.Required => "FilePath is required.",
            SkillResourcePathError.Absolute => "Absolute paths are not allowed.",
            SkillResourcePathError.NotRelativeFile => "FilePath must be a relative file path inside the skill directory.",
            SkillResourcePathError.InvalidSegment => "FilePath cannot contain empty, '.', or '..' segments.",
            SkillResourcePathError.SkillFile => "Use create or edit to update SKILL.md; file operations are for additional resources.",
            _ => "Invalid file path."
        };
}

internal enum SkillResourcePathError
{
    None,
    Required,
    Absolute,
    NotRelativeFile,
    InvalidSegment,
    SkillFile
}
