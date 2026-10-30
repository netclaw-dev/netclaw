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
        0000= 0009= 000A= 000D= 001B= 007F= 0085= 009F= 061B=<U+061B> 061C=<U+061C> 061D=<U+061D> 180E=<U+180E> 200A=<U+200A> 200B=<U+200B> 200C=<U+200C> 200D=<U+200D> 200E=<U+200E> 200F=<U+200F> 2010=<U+2010> 2027=<U+2027> 2028= 2029= 202A=<U+202A> 202B=<U+202B> 202C=<U+202C> 202D=<U+202D> 202E=<U+202E> 202F=<U+202F> 205F=<U+205F> 2060=<U+2060> 2061=<U+2061> 2062=<U+2062> 2063=<U+2063> 2064=<U+2064> 2065=<U+2065> 2066=<U+2066> 2067=<U+2067> 2068=<U+2068> 2069=<U+2069> 206A=<U+206A> FEFE=<U+FEFE> FEFF=<U+FEFF> FFF8=<U+FFF8> FFF9=<U+FFF9> FFFA=<U+FFFA> FFFB=<U+FFFB> FFFC=<U+FFFC> 0060=` 0078=x
        """;

    [Fact]
    public void Output_for_each_probe_character_is_unchanged()
    {
        var described = TextSafetyProbe.Describe(DaemonLifecycleNotifier.SanitizeForLog);

        output.WriteLine(described);
        Assert.Equal(Golden, described);
    }
}
