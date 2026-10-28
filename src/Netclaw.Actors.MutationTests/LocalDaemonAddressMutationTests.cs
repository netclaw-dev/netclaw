// -----------------------------------------------------------------------
// <copyright file="LocalDaemonAddressMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class LocalDaemonAddressMutationTests
{
    [Theory]
    [InlineData("netclaw.json", false)]
    [InlineData("netclaw.json", true)]
    [InlineData("secrets.json", false)]
    public void An_unreadable_source_rejects_the_local_address(string fileName, bool directory)
    {
        using var home = new DisposableTempDir();
        var paths = new NetclawPaths(home.Path);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var source = fileName == "netclaw.json" ? paths.NetclawConfigPath : paths.SecretsPath;
        if (fileName == "secrets.json")
            File.WriteAllText(paths.NetclawConfigPath, """{"Daemon":{"Port":6200}}""");
        if (directory)
            Directory.CreateDirectory(source);
        else
            File.WriteAllText(source, "{");

        // Resolve only. The unsafe mutation must never send a request to an inferred address.
        var error = Assert.Throws<InvalidDataException>(() => DaemonApi.ResolveLocalControlEndpoint(paths));

        Assert.Contains(fileName, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Readable_or_absent_optional_sources_resolve_the_local_address(bool configured)
    {
        using var home = new DisposableTempDir();
        var paths = new NetclawPaths(home.Path);
        Directory.CreateDirectory(paths.ConfigDirectory);
        if (configured)
            File.WriteAllText(paths.NetclawConfigPath, """{"Daemon":{"Port":6200}}""");

        var endpoint = DaemonApi.ResolveLocalControlEndpoint(paths);

        Assert.Equal(configured ? "http://127.0.0.1:6200" : DaemonApi.DefaultEndpoint, endpoint);
    }
}
