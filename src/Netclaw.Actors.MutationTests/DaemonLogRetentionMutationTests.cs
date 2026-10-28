// -----------------------------------------------------------------------
// <copyright file="DaemonLogRetentionMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class DaemonLogRetentionMutationTests
{
    [Theory]
    [InlineData("2026-05-05", true)]
    [InlineData("2026-05-06", false)]
    [InlineData("2026-05-07", false)]
    public void Only_segments_before_the_cutoff_are_deleted(string date, bool delete)
    {
        using var directory = new DisposableTempDir();
        // Three newer files keep the candidate outside the clock safeguard.
        foreach (var day in new[] { 18, 19, 20 })
            File.WriteAllText(Path.Join(directory.Path, $"daemon-2026-05-{day}.log"), "recent");
        var candidate = Path.Join(directory.Path, $"daemon-{date}.1.log");
        File.WriteAllText(candidate, "candidate");
        var unrelated = Path.Join(directory.Path, "provider-probe.log");
        File.WriteAllText(unrelated, "unrelated");

        var result = DaemonLogRetention.Prune(directory.Path, new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero), 14);

        Assert.Equal((delete ? 1 : 0, 0), result);
        Assert.Equal(!delete, File.Exists(candidate));
        Assert.Equal("unrelated", File.ReadAllText(unrelated));
    }
}
