// -----------------------------------------------------------------------
// <copyright file="SecurityServiceExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    public void AddContentSecurity_registers_noop_skill_content_scanner()
    {
        // Skill content scanning is temporarily wired to the no-op until the
        // regex detector is hardened against false positives on legitimate ops
        // documentation. RegexSkillContentScanner remains exercised by its own
        // unit tests so the implementation does not bit-rot.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContentSecurity();

        using var provider = services.BuildServiceProvider();
        var scanner = provider.GetRequiredService<ISkillContentScanner>();

        Assert.IsType<NoOpSkillContentScanner>(scanner);
    }
}
