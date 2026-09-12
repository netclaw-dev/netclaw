// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryEnvironmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ManagedTemporaryEnvironmentTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Child_process_receives_all_temporary_variables_without_daemon_mutation()
    {
        var managed = Path.Combine(_directory.Path, "managed");
        var daemonTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
        var daemonTmp = Environment.GetEnvironmentVariable("TMP");
        var daemonTemp = Environment.GetEnvironmentVariable("TEMP");
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Write-Output \"$env:TMPDIR|$env:TMP|$env:TEMP\"");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("printf '%s' \"$TMPDIR|$TMP|$TEMP\"");
        }

        Assert.Null(ManagedTemporaryEnvironment.Prepare(
            startInfo,
            ManagedTemporaryLocation.FromPersistedPaths(managed, _directory.Path)));
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal($"{managed}|{managed}|{managed}", output.Trim());
        Assert.Equal(daemonTmpDir, Environment.GetEnvironmentVariable("TMPDIR"));
        Assert.Equal(daemonTmp, Environment.GetEnvironmentVariable("TMP"));
        Assert.Equal(daemonTemp, Environment.GetEnvironmentVariable("TEMP"));
    }

    [Fact]
    public async Task Dotnet_temporary_api_returns_the_managed_directory()
    {
        var managed = Path.Combine(_directory.Path, "dotnet-managed");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("[Console]::Write([IO.Path]::GetTempPath())");

        Assert.Null(ManagedTemporaryEnvironment.Prepare(
            startInfo,
            ManagedTemporaryLocation.FromPersistedPaths(managed, _directory.Path)));
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(managed)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(output.Trim())));
    }

    [Fact]
    public void Preparation_failure_does_not_inject_a_host_fallback()
    {
        var blockingFile = Path.Combine(_directory.Path, "blocking-file");
        File.WriteAllText(blockingFile, "not a directory");
        var managed = Path.Combine(blockingFile, "managed");
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        var error = ManagedTemporaryEnvironment.Prepare(
            startInfo,
            ManagedTemporaryLocation.FromPersistedPaths(managed, _directory.Path));

        Assert.StartsWith("Error preparing managed temporary directory:", error, StringComparison.Ordinal);
        Assert.False(startInfo.Environment.ContainsKey("TMPDIR"));
        Assert.False(startInfo.Environment.ContainsKey("TMP"));
        Assert.False(startInfo.Environment.ContainsKey("TEMP"));
    }

    [Fact]
    public void Linked_managed_directory_is_rejected_before_environment_injection()
    {
        var outside = Path.Combine(_directory.Path, "outside");
        var root = Path.Combine(_directory.Path, "root");
        var linked = Path.Combine(root, "tmp");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(linked, outside);
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        var error = ManagedTemporaryEnvironment.Prepare(
            startInfo,
            ManagedTemporaryLocation.FromPersistedPaths(linked, root));

        Assert.Equal("Error: The managed temporary directory contains an unsafe filesystem link.", error);
        Assert.False(startInfo.Environment.ContainsKey("TMPDIR"));
        Assert.False(startInfo.Environment.ContainsKey("TMP"));
        Assert.False(startInfo.Environment.ContainsKey("TEMP"));
    }

    [Fact]
    public void Linked_storage_root_is_rejected_before_environment_injection()
    {
        var outside = Path.Combine(_directory.Path, "outside-root");
        var linkedRoot = Path.Combine(_directory.Path, "linked-root");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linkedRoot, outside);
        var managed = Path.Combine(linkedRoot, "tmp", "parent");
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        var error = ManagedTemporaryEnvironment.Prepare(
            startInfo,
            ManagedTemporaryLocation.FromPersistedPaths(managed, linkedRoot));

        Assert.Equal("Error: The managed temporary directory contains an unsafe filesystem link.", error);
        Assert.False(startInfo.Environment.ContainsKey("TMPDIR"));
        Assert.False(startInfo.Environment.ContainsKey("TMP"));
        Assert.False(startInfo.Environment.ContainsKey("TEMP"));
    }

    private static ProcessStartInfo CreateStartInfoWithoutTemporaryVariables()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Remove("TMPDIR");
        startInfo.Environment.Remove("TMP");
        startInfo.Environment.Remove("TEMP");
        return startInfo;
    }
}
