namespace Netclaw.Configuration;

/// <summary>
/// Stable load methods for skill-usage telemetry.
/// Stored as lowercase wire values in SQLite and JSON.
/// </summary>
public enum SkillLoadMethod
{
    SlashCommand,
    SkillLoadTool,
    FileRead,
}

public static class SkillLoadMethodExtensions
{
    public static string ToWireValue(this SkillLoadMethod value) => value switch
    {
        SkillLoadMethod.SlashCommand => "slash-command",
        SkillLoadMethod.SkillLoadTool => "skill-load",
        SkillLoadMethod.FileRead => "file-read",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wireValue, out SkillLoadMethod value)
    {
        switch (wireValue?.Trim())
        {
            case "slash-command":
                value = SkillLoadMethod.SlashCommand;
                return true;
            case "skill-load":
                value = SkillLoadMethod.SkillLoadTool;
                return true;
            case "file-read":
                value = SkillLoadMethod.FileRead;
                return true;
            default:
                value = default;
                return false;
        }
    }
}
