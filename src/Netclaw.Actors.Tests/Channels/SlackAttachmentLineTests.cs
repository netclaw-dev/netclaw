using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Unit tests for <see cref="AttachmentIngressFormatting.EscapeQuoted"/> and
/// <see cref="AttachmentIngressFormatting.BuildAttachmentLine"/> covering
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
        var result = AttachmentIngressFormatting.EscapeQuoted(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeQuoted_escapes_backslash_and_double_quote()
    {
        Assert.Equal("has \\\"quotes\\\"", AttachmentIngressFormatting.EscapeQuoted("has \"quotes\""));
        Assert.Equal("has \\\\backslash", AttachmentIngressFormatting.EscapeQuoted("has \\backslash"));
    }

    [Fact]
    public void EscapeQuoted_passes_through_normal_text_unchanged()
    {
        const string plain = "report-Q4_2025 final.pdf";
        Assert.Equal(plain, AttachmentIngressFormatting.EscapeQuoted(plain));
    }

    [Theory]
    [InlineData("evil\nfile\r\nname.pdf", "application/pdf", null)]
    [InlineData("ok.pdf", "application/pdf", "some\nnote with\r\nnewlines")]
    public void BuildAttachmentLine_with_hostile_metadata_produces_single_parseable_line(
        string name, string mimeType, string? note)
    {
        var line = AttachmentIngressFormatting.BuildAttachmentLine(
            name: name,
            mimeType: mimeType,
            size: 1234,
            relativePath: "inbox/test.pdf",
            inlined: false,
            note: note);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.StartsWith("[attachment]", line, StringComparison.Ordinal);
    }
}
