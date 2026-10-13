// -----------------------------------------------------------------------
// <copyright file="PluginCommandProgramTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Skills;

public sealed class PluginCommandProgramTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData(null)]
    public async Task Plugin_help_does_not_build_the_host_first(string? helpToken)
    {
        var home = Path.Combine(_dir.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "netclaw.json"), "{ bad json");
        string[] args = helpToken is null ? ["plugin"] : ["plugin", helpToken!];

        var (exitCode, output) = await RunCliAsync(home, args);

        Assert.True(exitCode == 0, output);
        Assert.Contains("Usage: netclaw plugin", output);
        Assert.DoesNotContain("could not load local configuration", output, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output)> RunCliAsync(string home, string[] args)
    {
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "netclaw.dll");
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir.Path,
        };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        foreach (var key in startInfo.Environment.Keys.Where(static key => key.StartsWith("NETCLAW_", StringComparison.Ordinal)).ToList())
            startInfo.Environment.Remove(key);
        startInfo.Environment["NETCLAW_HOME"] = home;
        startInfo.Environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
}
