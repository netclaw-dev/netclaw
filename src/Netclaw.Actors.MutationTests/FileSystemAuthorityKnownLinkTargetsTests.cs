// -----------------------------------------------------------------------
// <copyright file="FileSystemAuthorityKnownLinkTargetsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class FileSystemAuthorityKnownLinkTargetsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Known_targets_survive_the_chain_result(bool laterUnknown)
    {
        using var directory = new DisposableTempDir();
        var first = Path.Combine(directory.Path, "first");
        var second = Path.Combine(directory.Path, "second");
        var target = Directory.CreateDirectory(Path.Combine(directory.Path, "target")).FullName;
        Directory.CreateSymbolicLink(first, "second");
        IReadOnlyList<string> expectedTargets;
        if (laterUnknown)
        {
            var third = Path.Combine(directory.Path, "third");
            Directory.CreateSymbolicLink(second, third);
            Directory.CreateSymbolicLink(Path.Combine(directory.Path, "gate"), target);
            // The parent segment leaves a link, so the final hop has no safe lexical target.
            Directory.CreateSymbolicLink(third, Path.Combine("gate", "..", "outside.txt"));
            expectedTargets = [second, third];
        }
        else
        {
            Directory.CreateSymbolicLink(second, target);
            expectedTargets = [second, target];
        }

        var end = FileSystemAuthority.FollowLinkChain(first, out var finalTarget, out var knownTargets);
        var legacyEnd = FileSystemAuthority.FollowLinkChain(first, out var legacyTarget);

        Assert.Equal(laterUnknown ? LinkChainEnd.Unknown : LinkChainEnd.Target, end);
        Assert.Equal(laterUnknown ? string.Empty : target, finalTarget);
        Assert.Equal(end, legacyEnd);
        Assert.Equal(finalTarget, legacyTarget);
        Assert.Equal(expectedTargets, knownTargets);
    }
}
