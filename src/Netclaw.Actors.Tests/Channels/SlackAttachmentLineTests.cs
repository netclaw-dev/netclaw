using Netclaw.Channels.Slack;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Unit tests for <see cref="SlackThreadBindingActor.EscapeQuoted"/> and
/// <see cref="SlackThreadBindingActor.BuildAttachmentLine"/> covering
/// control-character sanitization and quote/backslash escaping.
/// </summary>
public sealed class SlackAttachmentLineTests
{
    [Theory]
    [InlineData("normal.pdf", "normal.pdf")]
    [InlineData("file\nname.pdf", "file name.pdf")]
    [InlineData("file\r\nname.pdf", "file  name.pdf")]
    [InlineData("file\tname.pdf", "file name.pdf")]
    [InlineData("inject\u0001\u0002control.pdf", "inject  control.pdf")]
    public void EscapeQuoted_normalizes_control_chars_to_spaces(string input, string expected)
    {
        var result = SlackThreadBindingActor.EscapeQuoted(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeQuoted_escapes_backslash_and_double_quote()
    {
        Assert.Equal("has \\\"quotes\\\"", SlackThreadBindingActor.EscapeQuoted("has \"quotes\""));
        Assert.Equal("has \\\\backslash", SlackThreadBindingActor.EscapeQuoted("has \\backslash"));
    }

    [Fact]
    public void EscapeQuoted_passes_through_normal_text_unchanged()
    {
        const string plain = "report-Q4_2025 final.pdf";
        Assert.Equal(plain, SlackThreadBindingActor.EscapeQuoted(plain));
    }

    [Fact]
    public void BuildAttachmentLine_with_hostile_name_produces_single_parseable_line()
    {
        var line = SlackThreadBindingActor.BuildAttachmentLine(
            name: "evil\nfile\r\nname.pdf",
            mimeType: "application/pdf",
            size: 1234,
            relativePath: "inbox/evil.pdf",
            inlined: false,
            note: null);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.StartsWith("[attachment]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAttachmentLine_with_hostile_note_produces_single_parseable_line()
    {
        var line = SlackThreadBindingActor.BuildAttachmentLine(
            name: "ok.pdf",
            mimeType: "application/pdf",
            size: 100,
            relativePath: "inbox/ok.pdf",
            inlined: false,
            note: "some\nnote with\r\nnewlines");

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
    }
}
