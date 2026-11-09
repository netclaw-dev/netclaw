// -----------------------------------------------------------------------
// <copyright file="ApprovalEntryValidationCharacterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Pins which characters a persisted approval field accepts. Saved entries must keep
/// loading, so a shared character definition must not change any verdict here.
/// </summary>
public sealed class ApprovalEntryValidationCharacterTests(ITestOutputHelper output)
{
    private const string AllowWhitespaceGolden = """
        0000=reject 0009=reject 000A=reject 000D=reject 001B=reject 007F=reject 0085=reject 009F=reject 061B=accept 061C=reject 061D=accept 180E=accept 200A=accept 200B=accept 200C=accept 200D=accept 200E=reject 200F=reject 2010=accept 2027=accept 2028=accept 2029=accept 202A=reject 202B=reject 202C=reject 202D=reject 202E=reject 202F=accept 205F=accept 2060=accept 2061=accept 2062=accept 2063=accept 2064=accept 2065=accept 2066=reject 2067=reject 2068=reject 2069=reject 206A=accept FEFE=accept FEFF=accept FFF8=accept FFF9=accept FFFA=accept FFFB=accept FFFC=accept 0060=accept 0078=accept
        """;
    private const string DenyWhitespaceGolden = """
        0000=reject 0009=reject 000A=reject 000D=reject 001B=reject 007F=reject 0085=reject 009F=reject 061B=accept 061C=reject 061D=accept 180E=accept 200A=reject 200B=accept 200C=accept 200D=accept 200E=reject 200F=reject 2010=accept 2027=accept 2028=reject 2029=reject 202A=reject 202B=reject 202C=reject 202D=reject 202E=reject 202F=reject 205F=reject 2060=accept 2061=accept 2062=accept 2063=accept 2064=accept 2065=accept 2066=reject 2067=reject 2068=reject 2069=reject 206A=accept FEFE=accept FEFF=accept FFF8=accept FFF9=accept FFFA=accept FFFB=accept FFFC=accept 0060=accept 0078=accept
        """;

    [Theory]
    [InlineData(true, AllowWhitespaceGolden)]
    [InlineData(false, DenyWhitespaceGolden)]
    public void Verdict_for_each_probe_character_is_unchanged(bool allowWhitespace, string expected)
    {
        var described = TextSafetyProbe.Describe(value => Verdict(value, allowWhitespace));

        output.WriteLine($"{allowWhitespace}:{described}");
        Assert.Equal(expected, described);
    }

    private static string Verdict(string value, bool allowWhitespace)
    {
        try
        {
            ApprovalEntryValidation.ValidatePersistedString(value, "pattern", allowWhitespace);
            return "accept";
        }
        catch (JsonException)
        {
            return "reject";
        }
    }
}
