// -----------------------------------------------------------------------
// <copyright file="ShellExecutionEnvironmentResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Daemon.Tests;

public class ShellExecutionEnvironmentResolverTests
{
    [Theory]
    [InlineData(ShellPlatform.Linux)]
    [InlineData(ShellPlatform.MacOS)]
    public async Task Unix_platform_selects_Bash_without_a_probe(ShellPlatform platform)
    {
        var probe = new SequencePowerShellProbe();
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var resolution = await resolver.ResolveAsync(
            platform,
            TestContext.Current.CancellationToken);

        Assert.Equal(platform, resolution.Environment.Platform);
        Assert.Equal("/bin/bash", resolution.Environment.ExecutablePath);
        Assert.Empty(probe.Calls);
        Assert.Null(resolution.FallbackReason);
    }

    [Theory]
    [InlineData("7.6.4")]
    [InlineData("7.6.99")]
    public async Task Compatible_PowerShell7_wins_without_fallback(string version)
    {
        var probe = new SequencePowerShellProbe(
            ("pwsh.exe", Found("C:\\PowerShell\\7\\pwsh.exe", version)));
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var resolution = await resolver.ResolveAsync(
            ShellPlatform.Windows,
            TestContext.Current.CancellationToken);

        Assert.Equal("C:\\PowerShell\\7\\pwsh.exe", resolution.Environment.ExecutablePath);
        Assert.Equal(PwshDialect.PowerShell7, resolution.Environment.PowerShellDialect);
        Assert.Equal(["pwsh.exe"], probe.Calls);
        Assert.Null(resolution.FallbackReason);
    }

    [Fact]
    public async Task Missing_preferred_host_selects_Windows_PowerShell51()
    {
        var probe = new SequencePowerShellProbe(
            ("pwsh.exe", new PowerShellHostProbeResult.NotFound()),
            ("powershell.exe", Found(
                "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
                "5.1.26100.1")));
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var resolution = await resolver.ResolveAsync(
            ShellPlatform.Windows,
            TestContext.Current.CancellationToken);

        Assert.Equal(PwshDialect.WindowsPowerShell51, resolution.Environment.PowerShellDialect);
        Assert.Equal(PowerShellFallbackReason.PreferredHostNotFound, resolution.FallbackReason);
        Assert.Null(resolution.RejectedPreferredVersion);
        Assert.Equal(["pwsh.exe", "powershell.exe"], probe.Calls);
    }

    [Theory]
    [InlineData("7.6.3")]
    [InlineData("7.7.0")]
    [InlineData("8.0.0")]
    public async Task Unsupported_preferred_version_selects_Windows_PowerShell51(
        string version)
    {
        var rejected = Version.Parse(version);
        var probe = new SequencePowerShellProbe(
            ("pwsh.exe", new PowerShellHostProbeResult.Found(
                "C:\\PowerShell\\7\\pwsh.exe",
                rejected)),
            ("powershell.exe", Found(
                "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
                "5.1")));
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var resolution = await resolver.ResolveAsync(
            ShellPlatform.Windows,
            TestContext.Current.CancellationToken);

        Assert.Equal(PwshDialect.WindowsPowerShell51, resolution.Environment.PowerShellDialect);
        Assert.Equal(PowerShellFallbackReason.PreferredVersionUnsupported, resolution.FallbackReason);
        Assert.Equal(rejected, resolution.RejectedPreferredVersion);
    }

    [Theory]
    [InlineData((int)PowerShellProbeFailure.AccessDenied)]
    [InlineData((int)PowerShellProbeFailure.StartFailed)]
    [InlineData((int)PowerShellProbeFailure.Timeout)]
    [InlineData((int)PowerShellProbeFailure.MalformedVersion)]
    public async Task Preferred_probe_failure_selects_Windows_PowerShell51(
        int failureValue)
    {
        var failure = (PowerShellProbeFailure)failureValue;
        var probe = new SequencePowerShellProbe(
            ("pwsh.exe", new PowerShellHostProbeResult.Failed(failure)),
            ("powershell.exe", Found(
                "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
                "5.1")));
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var resolution = await resolver.ResolveAsync(
            ShellPlatform.Windows,
            TestContext.Current.CancellationToken);

        Assert.Equal(PwshDialect.WindowsPowerShell51, resolution.Environment.PowerShellDialect);
        Assert.Equal(PowerShellFallbackReason.PreferredHostProbeFailed, resolution.FallbackReason);
        Assert.Null(resolution.RejectedPreferredVersion);
        Assert.Equal(["pwsh.exe", "powershell.exe"], probe.Calls);
    }

    [Fact]
    public async Task Preferred_and_fallback_probe_failure_stops_startup()
    {
        var probe = new SequencePowerShellProbe(
            ("pwsh.exe", new PowerShellHostProbeResult.Failed(PowerShellProbeFailure.Timeout)),
            ("powershell.exe", new PowerShellHostProbeResult.Failed(
                PowerShellProbeFailure.AccessDenied)));
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(ShellPlatform.Windows, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(PowerShellProbeFailure.Timeout), exception.Message);
        Assert.Contains(nameof(PowerShellProbeFailure.AccessDenied), exception.Message);
        Assert.Equal(["pwsh.exe", "powershell.exe"], probe.Calls);
    }

    [Fact]
    public async Task Incompatible_fallback_fails_with_actionable_versions()
    {
        var probe = new SequencePowerShellProbe(
            ("pwsh.exe", Found("C:\\PowerShell\\7\\pwsh.exe", "7.7.0")),
            ("powershell.exe", Found("C:\\Windows\\powershell.exe", "5.2")));
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(ShellPlatform.Windows, TestContext.Current.CancellationToken));

        Assert.Contains("7.7.0", exception.Message);
        Assert.Contains("5.2", exception.Message);
        Assert.Contains("PowerShell 5.1", exception.Message);
    }

    [Fact]
    public async Task Fallback_operational_failure_stops_startup()
    {
        var probe = new SequencePowerShellProbe(
            ("pwsh.exe", new PowerShellHostProbeResult.NotFound()),
            ("powershell.exe", new PowerShellHostProbeResult.Failed(
                PowerShellProbeFailure.NonZeroExit,
                9)));
        var resolver = new ShellExecutionEnvironmentResolver(probe);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(ShellPlatform.Windows, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(PowerShellProbeFailure.NonZeroExit), exception.Message);
        Assert.Equal(["pwsh.exe", "powershell.exe"], probe.Calls);
    }

    private static PowerShellHostProbeResult Found(string path, string version) =>
        new PowerShellHostProbeResult.Found(path, Version.Parse(version));

    private sealed class SequencePowerShellProbe(
        params (string ExecutableName, PowerShellHostProbeResult Result)[] results)
        : IPowerShellHostProbe
    {
        private readonly Queue<(string ExecutableName, PowerShellHostProbeResult Result)> _results =
            new(results);

        public List<string> Calls { get; } = [];

        public Task<PowerShellHostProbeResult> ProbeAsync(
            string executableName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(executableName);
            var next = _results.Dequeue();
            Assert.Equal(next.ExecutableName, executableName);
            return Task.FromResult(next.Result);
        }
    }
}
