// -----------------------------------------------------------------------
// <copyright file="ConfigFileModeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tests.Cli;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

/// <summary>
/// Only daemon.env, secrets.json, and devices.json are owner-only. A netclaw.json that netclaw creates
/// keeps the default mode, because a read-only single-file bind mount in a container runs the daemon
/// as another user and that user must read the file.
/// </summary>
public sealed class ConfigFileModeTests : IDisposable
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [SlopwatchSuppress("SW001", "Unix file modes mean nothing on Windows.")]
    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix file modes mean nothing on Windows.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_new_netclaw_json_gets_the_default_mode_of_a_new_file()
    {
        var plain = Path.Combine(_dir.Path, "plain.txt");
        File.WriteAllText(plain, "x");
        var path = Path.Combine(_dir.Path, "fresh", "netclaw.json");

        ConfigFileHelper.WriteConfigFile(path, new Dictionary<string, object> { ["configVersion"] = 1 });

        Assert.Equal(File.GetUnixFileMode(plain), File.GetUnixFileMode(path));
    }

    [SlopwatchSuppress("SW001", "Unix file modes mean nothing on Windows.")]
    [Theory(SkipUnless = nameof(IsUnix), Skip = "Unix file modes mean nothing on Windows.")]
    [InlineData((int)(UnixFileMode.UserRead | UnixFileMode.UserWrite))]
    [InlineData((int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead))]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_rewrite_keeps_the_mode_of_the_netclaw_json_that_it_replaces(int mode)
    {
        var path = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText(path, """{ "configVersion": 1 }""");
        File.SetUnixFileMode(path, (UnixFileMode)mode);

        ConfigFileHelper.WriteConfigFile(path, new Dictionary<string, object> { ["configVersion"] = 2 });

        Assert.Equal((UnixFileMode)mode, File.GetUnixFileMode(path));
    }
}
