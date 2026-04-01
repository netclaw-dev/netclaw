using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Security.Skills;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class SecurityServiceExtensionsTests
{
    [Fact]
    public async Task AddContentSecurity_registers_magic_byte_scanner_that_blocks_executables()
    {
        var services = new ServiceCollection();
        services.AddContentSecurity();

        using var provider = services.BuildServiceProvider();
        var scanner = provider.GetRequiredService<IContentScanner>();

        Assert.IsType<MagicByteContentScanner>(scanner);

        var disguisedExecutable = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        var result = await scanner.ScanAsync(disguisedExecutable, "photo.png", "image/png", TestContext.Current.CancellationToken);

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.ExecutableContent, result.Error);
    }

    [Fact]
    public void AddContentSecurity_registers_regex_prompt_injection_detector()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContentSecurity();

        using var provider = services.BuildServiceProvider();
        var detector = provider.GetRequiredService<IPromptInjectionDetector>();

        Assert.IsType<RegexPromptInjectionDetector>(detector);
    }

    [Fact]
    public void AddContentSecurity_registers_regex_skill_content_scanner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContentSecurity();

        using var provider = services.BuildServiceProvider();
        var scanner = provider.GetRequiredService<ISkillContentScanner>();

        Assert.IsType<RegexSkillContentScanner>(scanner);
    }
}
