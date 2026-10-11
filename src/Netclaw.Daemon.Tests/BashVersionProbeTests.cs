// -----------------------------------------------------------------------
// <copyright file="BashVersionProbeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Xunit;

namespace Netclaw.Daemon.Tests;

public sealed class BashVersionProbeTests
{
    [Fact]
    public void Probe_process_uses_an_absolute_sanitized_Bash_contract()
    {
        var startInfo = BashVersionProbe.CreateStartInfo(ShellPlatform.Linux);

        Assert.Equal("/bin/bash", startInfo.FileName);
        Assert.Equal("-c", startInfo.ArgumentList[0]);
        Assert.Contains("BASH_VERSINFO", startInfo.ArgumentList[1], StringComparison.Ordinal);
        Assert.DoesNotContain(startInfo.Environment.Keys, static key =>
            key is "BASH_ENV" or "ENV" or "SHELLOPTS" or "BASHOPTS"
                or "LIBPATH" or "SHLIB_PATH"
            || key.StartsWith("BASH_FUNC_", StringComparison.Ordinal)
            || key.StartsWith("LD_", StringComparison.Ordinal)
            || key.StartsWith("DYLD_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Probe_reads_the_local_Bash_major_and_minor_version()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var probe = new BashVersionProbe(TimeProvider.System);

        var version = await probe.ProbeAsync(
            OperatingSystem.IsMacOS() ? ShellPlatform.MacOS : ShellPlatform.Linux,
            TestContext.Current.CancellationToken);

        Assert.True(version.Major > 0);
        Assert.True(version.Minor >= 0);
        Assert.Equal(-1, version.Build);
        Assert.Equal(-1, version.Revision);
    }
}
