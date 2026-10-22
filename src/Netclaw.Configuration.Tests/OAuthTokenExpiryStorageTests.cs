// -----------------------------------------------------------------------
// <copyright file="OAuthTokenExpiryStorageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class OAuthTokenExpiryStorageTests
{
    [Fact]
    public void RetiredKeyWarnings_names_each_provider_that_holds_a_value_and_reads_comments()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, """
            // The operator's note.
            { "Providers": {
                "codex": { "Type": "openai", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00", },
                "copilot": { "Type": "github-copilot", "oauthtokenexpiry": null },
                "Plain": { "Type": "ollama" } }, }
            """);

        var warning = Assert.Single(OAuthTokenExpiryStorage.RetiredKeyWarnings(paths));

        Assert.Contains("Providers:codex:OAuthTokenExpiry", warning, StringComparison.Ordinal);
        Assert.Contains("netclaw doctor --fix", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredKeyWarnings_is_empty_without_netclaw_json()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);

        Assert.Empty(OAuthTokenExpiryStorage.RetiredKeyWarnings(paths));
    }
}
