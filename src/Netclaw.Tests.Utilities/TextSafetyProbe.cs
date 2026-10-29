// -----------------------------------------------------------------------
// <copyright file="TextSafetyProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Inputs and a renderer for tests that pin how a text-safety caller treats unsafe
/// characters. The probe holds every member of the bidi, line-separator, and byte-order-mark
/// sets, the code points just outside each range, and the ASCII cases that callers treat
/// differently. A golden description of the probe fails if a refactor drops or adds one
/// member for one caller.
/// </summary>
internal static class TextSafetyProbe
{
    public static readonly IReadOnlyList<char> Characters =
    [
        '\u0000', '\u0009', '\u000A', '\u000D', '\u001B', '\u007F', '\u0085', '\u009F',
        '\u061B', '\u061C', '\u061D',
        '\u200D', '\u200E', '\u200F', '\u2010',
        '\u2027', '\u2028', '\u2029',
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u202F',
        '\u2065', '\u2066', '\u2067', '\u2068', '\u2069', '\u206A',
        '\uFEFE', '\uFEFF',
        '`', 'x',
    ];

    /// <summary>
    /// Runs <paramref name="transform"/> on each probe character alone and returns one
    /// <c>CODE=output</c> entry per character. A caller with a length limit sees each
    /// character, which a single joined probe would not guarantee.
    /// </summary>
    public static string Describe(Func<string, string> transform)
        => string.Join(' ', Characters.Select(character =>
            $"{(int)character:X4}={Render(transform(character.ToString()))}"));

    /// <summary>Shows each character outside printable ASCII as <c>&lt;U+XXXX&gt;</c>.</summary>
    public static string Render(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var character in value)
        {
            if (character is >= ' ' and <= '~')
                builder.Append(character);
            else
                builder.Append("<U+").Append(((int)character).ToString("X4")).Append('>');
        }

        return builder.ToString();
    }
}
