using Xunit;

namespace Netclaw.Security.Tests;

public sealed class FilenameSanitizerTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\config", "config")]
    [InlineData("/etc/shadow", "shadow")]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "cmd.exe")]
    public void Sanitize_PathTraversalAttempts_StripsDirectoryComponents(string input, string expected)
    {
        var result = FilenameSanitizer.Sanitize(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, "attachment")]
    [InlineData("", "attachment")]
    [InlineData("   ", "attachment")]
    public void Sanitize_EmptyOrNull_ReturnsDefault(string? input, string expected)
    {
        var result = FilenameSanitizer.Sanitize(input!);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Sanitize_ControlCharacters_Stripped()
    {
        var result = FilenameSanitizer.Sanitize("file\x00name\x1F.png");
        Assert.Equal("filename.png", result);
    }

    [Fact]
    public void Sanitize_PlatformProblemChars_Replaced()
    {
        var result = FilenameSanitizer.Sanitize("file<name>with|chars?.png");
        Assert.Equal("file_name_with_chars_.png", result);
    }

    [Fact]
    public void Sanitize_DoubleDots_Replaced()
    {
        var result = FilenameSanitizer.Sanitize("file..name.png");
        Assert.Equal("file_name.png", result);
    }

    [Fact]
    public void Sanitize_LongFilename_TruncatedPreservingExtension()
    {
        var longName = new string('a', 300) + ".png";
        var result = FilenameSanitizer.Sanitize(longName);

        Assert.True(result.Length <= 255);
        Assert.EndsWith(".png", result);
    }

    [Fact]
    public void HasSuspiciousDoubleExtension_SingleExtension_ReturnsFalse()
    {
        Assert.False(FilenameSanitizer.HasSuspiciousDoubleExtension("photo.png"));
    }

    [Fact]
    public void HasSuspiciousDoubleExtension_DoubleExtension_ReturnsTrue()
    {
        Assert.True(FilenameSanitizer.HasSuspiciousDoubleExtension("malware.exe.png"));
    }

    [Fact]
    public void HasSuspiciousDoubleExtension_NoExtension_ReturnsFalse()
    {
        Assert.False(FilenameSanitizer.HasSuspiciousDoubleExtension("README"));
    }
}
