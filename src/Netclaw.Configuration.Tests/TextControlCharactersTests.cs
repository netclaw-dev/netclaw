// -----------------------------------------------------------------------
// <copyright file="TextControlCharactersTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class TextControlCharactersTests
{
    public static TheoryData<string, int[]> Sets => new()
    {
        { "bidi", [0x061C, 0x200E, 0x200F, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069] },
        { "line-separator", [0x2028, 0x2029] },
        { "byte-order-mark", [0xFEFF] },
    };

    /// <summary>Scans every UTF-16 code unit, so a member added to or dropped from a set fails.</summary>
    [Theory]
    [MemberData(nameof(Sets))]
    public void Each_set_has_exactly_its_documented_members(string set, int[] expected)
    {
        Func<char, bool> predicate = set switch
        {
            "bidi" => TextControlCharacters.IsBidiControl,
            "line-separator" => TextControlCharacters.IsUnicodeLineSeparator,
            "byte-order-mark" => TextControlCharacters.IsByteOrderMark,
            _ => throw new ArgumentOutOfRangeException(nameof(set)),
        };

        var members = Enumerable.Range(char.MinValue, char.MaxValue + 1)
            .Where(code => predicate((char)code))
            .ToArray();

        Assert.Equal(expected, members);
    }

    [Fact]
    public void EscapeAsUnicode_escapes_only_matching_characters_in_uppercase_hex()
    {
        var input = "a" + (char)0x202E + "b" + (char)0x001B;

        var escaped = TextControlCharacters.EscapeAsUnicode(input, TextControlCharacters.IsBidiControl);

        Assert.Equal("a" + "\\" + "u202E" + "b" + (char)0x001B, escaped);
    }

    [Fact]
    public void EscapeAsUnicode_returns_the_same_instance_when_nothing_matches()
    {
        var input = "plain text";

        Assert.Same(input, TextControlCharacters.EscapeAsUnicode(input, TextControlCharacters.IsBidiControl));
    }
}
