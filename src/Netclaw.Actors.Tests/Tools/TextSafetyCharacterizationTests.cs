// -----------------------------------------------------------------------
// <copyright file="TextSafetyCharacterizationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Pins the exact output of text-safety callers that share character sets or naming rules.
/// The shared definitions must not change any of these outputs.
/// </summary>
public sealed class TextSafetyCharacterizationTests(ITestOutputHelper output)
{
    private const string ApprovalDisplayGolden = """
        0000=server/tool(value="\u0000") 0009=server/tool(value="\t") 000A=server/tool(value="\n") 000D=server/tool(value="\r") 001B=server/tool(value="\u001B") 007F=server/tool(value="\u007F") 0085=server/tool(value="\u0085") 009F=server/tool(value="\u009F") 061B=server/tool(value="\u061B") 061C=server/tool(value="\u061C") 061D=server/tool(value="\u061D") 200D=server/tool(value="\u200D") 200E=server/tool(value="\u200E") 200F=server/tool(value="\u200F") 2010=server/tool(value="\u2010") 2027=server/tool(value="\u2027") 2028=server/tool(value="\u2028") 2029=server/tool(value="\u2029") 202A=server/tool(value="\u202A") 202B=server/tool(value="\u202B") 202C=server/tool(value="\u202C") 202D=server/tool(value="\u202D") 202E=server/tool(value="\u202E") 202F=server/tool(value="\u202F") 2065=server/tool(value="\u2065") 2066=server/tool(value="\u2066") 2067=server/tool(value="\u2067") 2068=server/tool(value="\u2068") 2069=server/tool(value="\u2069") 206A=server/tool(value="\u206A") FEFE=server/tool(value="\uFEFE") FEFF=server/tool(value="\uFEFF") 0060=server/tool(value="\u0060") 0078=server/tool(value="x")
        """;
    private const string ApprovalToolNameGolden = """
        0000=server/\u0000 0009=server/\u0009 000A=server/\u000A 000D=server/\u000D 001B=server/\u001B 007F=server/\u007F 0085=server/\u0085 009F=server/\u009F 061B=server/<U+061B> 061C=server/\u061C 061D=server/<U+061D> 200D=server/<U+200D> 200E=server/\u200E 200F=server/\u200F 2010=server/<U+2010> 2027=server/<U+2027> 2028=server/\u2028 2029=server/\u2029 202A=server/\u202A 202B=server/\u202B 202C=server/\u202C 202D=server/\u202D 202E=server/\u202E 202F=server/<U+202F> 2065=server/<U+2065> 2066=server/\u2066 2067=server/\u2067 2068=server/\u2068 2069=server/\u2069 206A=server/<U+206A> FEFE=server/<U+FEFE> FEFF=server/\uFEFF 0060=server/\u0060 0078=server/x
        """;
    private const string ShellTraceGolden = """
        0000=\u0000 0009=\u0009 000A=\u000A 000D=\u000D 001B=\u001B 007F=\u007F 0085=\u0085 009F=\u009F 061B=<U+061B> 061C=\u061C 061D=<U+061D> 200D=<U+200D> 200E=\u200E 200F=\u200F 2010=<U+2010> 2027=<U+2027> 2028=\u2028 2029=\u2029 202A=\u202A 202B=\u202B 202C=\u202C 202D=\u202D 202E=\u202E 202F=<U+202F> 2065=<U+2065> 2066=\u2066 2067=\u2067 2068=\u2068 2069=\u2069 206A=<U+206A> FEFE=<U+FEFE> FEFF=\uFEFF 0060=` 0078=x
        """;

    [Fact]
    public void Approval_display_output_for_each_probe_character_is_unchanged()
    {
        var described = TextSafetyProbe.Describe(value => McpApprovalMatcher.Instance.FormatForDisplay(
            new ToolName("server/tool"),
            new Dictionary<string, object?> { ["value"] = value }));

        output.WriteLine(described);
        Assert.Equal(ApprovalDisplayGolden, described);
    }

    /// <summary>
    /// The tool name reaches the display without JSON encoding, so this is the path where the
    /// presentation character set itself decides the output.
    /// </summary>
    [Fact]
    public void Approval_display_tool_name_output_for_each_probe_character_is_unchanged()
    {
        var described = TextSafetyProbe.Describe(value => McpApprovalMatcher.Instance.FormatForDisplay(
            new ToolName("server/" + value),
            null));

        output.WriteLine(described);
        Assert.Equal(ApprovalToolNameGolden, described);
    }

    [Fact]
    public void Shell_policy_trace_output_for_each_probe_character_is_unchanged()
    {
        var described = TextSafetyProbe.Describe(ShellPolicyDecisionTraceBuilder.SanitizeText);

        output.WriteLine(described);
        Assert.Equal(ShellTraceGolden, described);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("C0A/123.4", "C0A_123_4")]
    [InlineData("///", "___")]
    [InlineData("caf\u00E9-\u00DF_\u03A9 1", "caf<U+00E9>-<U+00DF>_<U+03A9>_1")]
    [InlineData("\u202Ex\u2028y", "_x_y")]
    public void Session_folder_name_is_unchanged(string sessionId, string expected)
    {
        var rendered = TextSafetyProbe.Render(SessionDirectoryHelper.SanitizeSessionId(new SessionId(sessionId)));

        Assert.Equal(expected, rendered);
    }
}
