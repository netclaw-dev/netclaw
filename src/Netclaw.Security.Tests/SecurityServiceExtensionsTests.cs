using Microsoft.Extensions.DependencyInjection;
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
        var result = await scanner.ScanAsync(disguisedExecutable, "photo.png", "image/png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.ExecutableContent, result.Error);
    }
}
