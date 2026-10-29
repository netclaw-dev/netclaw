// -----------------------------------------------------------------------
// <copyright file="TextControlCharacters.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text;

namespace Netclaw.Configuration;

/// <summary>
/// The one definition of the invisible characters that can reorder or split text for a
/// reader. Callers combine these sets with <see cref="char.IsControl(char)"/> as their
/// output medium requires; a caller must not keep its own member list, because two lists
/// drift apart. This assembly holds the definitions because it is the lowest one that
/// approval validation, approval display, the shell trace, and the log sinks all reference.
/// </summary>
public static class TextControlCharacters
{
    /// <summary>
    /// Bidirectional controls: ARABIC LETTER MARK, LEFT-TO-RIGHT and RIGHT-TO-LEFT MARK, the
    /// embedding and override controls, and the isolate controls. Each one can make a reader
    /// see characters in a different order than the program processes them.
    /// </summary>
    public static bool IsBidiControl(char character)
        => character is '\u061C' or '\u200E' or '\u200F'
            or >= '\u202A' and <= '\u202E'
            or >= '\u2066' and <= '\u2069';

    /// <summary>
    /// LINE SEPARATOR and PARAGRAPH SEPARATOR. They are not in the Cc category, so
    /// <see cref="char.IsControl(char)"/> misses them, but many readers break lines on them.
    /// </summary>
    public static bool IsUnicodeLineSeparator(char character)
        => character is '\u2028' or '\u2029';

    /// <summary>ZERO WIDTH NO-BREAK SPACE, also used as the byte-order mark.</summary>
    public static bool IsByteOrderMark(char character)
        => character == '\uFEFF';

    /// <summary>
    /// Replaces each character that <paramref name="shouldEscape"/> matches with a
    /// <c>\uXXXX</c> escape in uppercase hexadecimal. Returns the same instance when no
    /// character matches.
    /// </summary>
    public static string EscapeAsUnicode(string value, Func<char, bool> shouldEscape)
    {
        if (!value.Any(shouldEscape))
            return value;

        var escaped = new StringBuilder(value.Length + 16);
        foreach (var character in value)
        {
            if (shouldEscape(character))
                escaped.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            else
                escaped.Append(character);
        }

        return escaped.ToString();
    }
}
