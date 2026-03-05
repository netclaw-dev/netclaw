using System.Text.RegularExpressions;

namespace Netclaw.Actors.Reminders;

internal static partial class ReminderIdGenerator
{
    [GeneratedRegex("[^a-z0-9\\-]", RegexOptions.Compiled)]
    private static partial Regex InvalidChars();

    public static ReminderId Generate(string title)
    {
        var slug = title.ToLowerInvariant().Trim()
            .Replace(' ', '-')
            .Replace('_', '-');

        slug = InvalidChars().Replace(slug, string.Empty);
        if (string.IsNullOrWhiteSpace(slug))
            slug = "reminder";
        if (slug.Length > 30)
            slug = slug[..30];

        var suffix = Guid.NewGuid().ToString("N")[..6];
        return new ReminderId($"{slug}-{suffix}");
    }
}
