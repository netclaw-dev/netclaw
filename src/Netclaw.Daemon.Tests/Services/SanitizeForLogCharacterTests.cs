// -----------------------------------------------------------------------
// <copyright file="SanitizeForLogCharacterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// Pins which characters the lifecycle notifier strips from a shutdown reason. Its output
/// reaches logs, alerts, and an HTTP response, so a shared definition must not change it.
/// </summary>
public sealed class SanitizeForLogCharacterTests(ITestOutputHelper output)
{
    private const string Golden = """
        0000= 0009= 000A= 000D= 001B= 007F= 0085= 009F= 061B=<U+061B> 061C=<U+061C> 061D=<U+061D> 200D=<U+200D> 200E=<U+200E> 200F=<U+200F> 2010=<U+2010> 2027=<U+2027> 2028= 2029= 202A=<U+202A> 202B=<U+202B> 202C=<U+202C> 202D=<U+202D> 202E=<U+202E> 202F=<U+202F> 2065=<U+2065> 2066=<U+2066> 2067=<U+2067> 2068=<U+2068> 2069=<U+2069> 206A=<U+206A> FEFE=<U+FEFE> FEFF=<U+FEFF> 0060=` 0078=x
        """;

    [Fact]
    public void Output_for_each_probe_character_is_unchanged()
    {
        var described = TextSafetyProbe.Describe(DaemonLifecycleNotifier.SanitizeForLog);

        output.WriteLine(described);
        Assert.Equal(Golden, described);
    }
}
