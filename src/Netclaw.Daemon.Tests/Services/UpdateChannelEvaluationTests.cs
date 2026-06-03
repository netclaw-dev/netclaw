// -----------------------------------------------------------------------
// <copyright file="UpdateChannelEvaluationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// Channel-aware behavior of <see cref="UpdateCheckService.EvaluateManifest"/>:
/// a stable client is never offered a prerelease, while a beta client tracks
/// <c>latestPrerelease</c> and rolls onto a stable release once it supersedes the beta.
/// </summary>
public sealed class UpdateChannelEvaluationTests
{
    // Build a manifest with stable + prerelease entries, each carrying an asset for the
    // current RID so matching assets are found regardless of the test host platform.
    private static BinaryFeedManifest Manifest(string latest, string latestPrerelease)
    {
        var rid = UpdateCheckService.GetCurrentRid();

        BinaryRelease Release(string version) => new()
        {
            Version = version,
            ReleaseNotesUrl = $"https://github.com/netclaw-dev/netclaw/releases/tag/{version}",
            Assets =
            [
                new BinaryAsset
                {
                    Component = "netclaw",
                    Rid = rid,
                    Url = $"https://releases.netclaw.dev/{version}/netclaw-{version}-{rid}.tar.gz",
                    Sha256 = "abc",
                    SizeBytes = 1,
                },
            ],
        };

        var releases = new List<BinaryRelease>();
        if (!string.IsNullOrEmpty(latestPrerelease) && latestPrerelease != latest)
            releases.Add(Release(latestPrerelease));
        if (!string.IsNullOrEmpty(latest))
            releases.Add(Release(latest));

        return new BinaryFeedManifest
        {
            Latest = latest,
            LatestPrerelease = latestPrerelease,
            Releases = releases,
        };
    }

    [Fact]
    public void Stable_IsNeverOfferedAPrerelease()
    {
        // A newer prerelease exists, but a stable client must never see it.
        var manifest = Manifest("0.18.1", "0.19.0-beta1");

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.18.1", UpdateChannel.Stable);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("0.18.1", result.LatestVersion);
    }

    [Fact]
    public void Stable_OffersNewerStable()
    {
        var manifest = Manifest("0.19.0", "0.19.0");

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.18.1", UpdateChannel.Stable);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.19.0", result.LatestVersion);
    }

    [Fact]
    public void Beta_OffersNewerPrerelease()
    {
        var manifest = Manifest("0.18.1", "0.19.0-beta2");

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.19.0-beta1", UpdateChannel.Beta);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.19.0-beta2", result.LatestVersion);
    }

    [Fact]
    public void Beta_OnNewestPrerelease_ReportsNoUpdate()
    {
        var manifest = Manifest("0.18.1", "0.19.0-beta1");

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.19.0-beta1", UpdateChannel.Beta);

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void Beta_RollsOntoSupersedingStable()
    {
        // 0.19.0 stable shipped; the generator sets latestPrerelease to the max of all,
        // so a beta client is moved onto the stable that supersedes its beta.
        var manifest = Manifest("0.19.0", "0.19.0");

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.19.0-beta1", UpdateChannel.Beta);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.19.0", result.LatestVersion);
    }

    [Fact]
    public void Beta_FallsBackToLatest_WhenManifestHasNoPrerelease()
    {
        // Manifest published before the prerelease channel existed.
        var manifest = Manifest("0.19.0", "");

        var result = UpdateCheckService.EvaluateManifest(manifest, "0.18.1", UpdateChannel.Beta);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.19.0", result.LatestVersion);
    }
}
