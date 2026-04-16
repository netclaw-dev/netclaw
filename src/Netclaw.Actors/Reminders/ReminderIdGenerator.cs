using System.Text.RegularExpressions;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

public static partial class ReminderIdGenerator
{
    [GeneratedRegex("[^a-z0-9\\-]", RegexOptions.Compiled)]
    private static partial Regex InvalidChars();

    /// <summary>
    /// Normalizes a caller-provided ID to a safe kebab-case slug.
    /// Enforces lowercase, kebab-case, and a 50-character length cap.
    /// </summary>
    public static string Normalize(string id)
    {
        var slug = id.ToLowerInvariant().Trim()
            .Replace(' ', '-')
            .Replace('_', '-');

        slug = InvalidChars().Replace(slug, string.Empty);
        if (string.IsNullOrWhiteSpace(slug))
            slug = "reminder";
        if (slug.Length > 50)
            slug = slug[..50];

        return slug;
    }

    public static ReminderId Generate(string title)
    {
        var slug = Normalize(title);
        var suffix = IdGen.Suffix();
        return new ReminderId($"{slug}-{suffix}");
    }
}
