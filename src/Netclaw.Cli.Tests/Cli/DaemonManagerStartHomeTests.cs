// -----------------------------------------------------------------------
// <copyright file="DaemonManagerStartHomeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// #2527 rule 3: <see cref="DaemonManager.Start"/> gives the child daemon the home of the manager
/// in <c>NETCLAW_HOME</c>. Without it, the child inherits the environment of the CLI process, or
/// falls back to <c>~/.netclaw</c>, and can serve a home other than the one that the CLI acted on.
/// </summary>
/// <remarks>
/// The test replaces <c>netclawd</c> with a script that records its own <c>NETCLAW_HOME</c> and
/// exits with an error. No real daemon starts. The class sets <c>NETCLAW_DAEMON_PATH</c> and
/// <c>NETCLAW_HOME</c> for the whole process, so it joins the collection that serializes such tests.
/// </remarks>
[Collection(LegacyModelEnvironmentCollection.Name)]
public sealed class DaemonManagerStartHomeTests : IDisposable
{
    private const int MaxExecAttempts = 50;

    private readonly DisposableTempDir _dir = new();
    private readonly string? _originalDaemonPath = Environment.GetEnvironmentVariable("NETCLAW_DAEMON_PATH");
    private readonly string? _originalHome = Environment.GetEnvironmentVariable("NETCLAW_HOME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", _originalDaemonPath);
        Environment.SetEnvironmentVariable("NETCLAW_HOME", _originalHome);
        _dir.Dispose();
    }

    [Theory]
    [InlineData("a-home-that-the-process-already-names")]
    [InlineData("the-home-of-the-test-run")]
    public async Task Start_GivesTheChildTheHomeOfTheManager(string processHomeName)
    {
        var servedHome = Path.Combine(_dir.Path, "served-home");
        var paths = new NetclawPaths(servedHome);
        // The CLI creates the home before it starts the daemon. A missing home makes the lock probe
        // answer "held".
        paths.EnsureDirectoriesExist();
        var capture = Path.Combine(_dir.Path, "child-home.txt");
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", WriteCaptureScript(capture));
        if (processHomeName == "a-home-that-the-process-already-names")
            Environment.SetEnvironmentVariable("NETCLAW_HOME", Path.Combine(_dir.Path, "decoy-home"));

        var manager = new DaemonManager(paths, TimeProvider.System, new UnsupervisedHost());

        // The script exits with code 1 at once, so Start reports a failure. Only the capture matters.
        // A freshly written script can fail to start with "Text file busy" when another thread
        // forks at the same moment. Retry that start; it has no side effect.
        DaemonResult result;
        var attempt = 0;
        do
        {
            result = manager.Start();
            await Task.Yield();
        }
        while (result.Message.Contains("Text file busy", StringComparison.Ordinal) && ++attempt < MaxExecAttempts);

        Assert.True(File.Exists(capture), $"The child daemon did not run: {result.Message}");
        Assert.Equal(paths.BasePath, (await File.ReadAllTextAsync(capture, TestContext.Current.CancellationToken)).Trim());
    }

    private string WriteCaptureScript(string capturePath)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir.Path, "netclawd.cmd");
            File.WriteAllText(cmd, $"@echo off\r\n> \"{capturePath}\" echo %NETCLAW_HOME%\r\nexit /b 1\r\n");
            return cmd;
        }

        var script = Path.Combine(_dir.Path, "netclawd");
        File.WriteAllText(script, $"#!/bin/sh\nprintf '%s' \"$NETCLAW_HOME\" > '{capturePath}'\nexit 1\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private sealed class UnsupervisedHost : IContainerSupervisor
    {
        public bool IsExternallySupervised => false;
    }
}
