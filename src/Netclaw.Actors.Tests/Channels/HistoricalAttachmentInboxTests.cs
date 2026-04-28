// -----------------------------------------------------------------------
// <copyright file="HistoricalAttachmentInboxTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class HistoricalAttachmentInboxTests
{
    [Fact]
    public void PromoteOrReuse_returns_existing_target_when_concurrent_writer_wins()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        const string rawFilename = "report.pdf";
        const string sourceKey = "slack:F123";

        var firstStage = Path.Combine(root, "stage-1.tmp");
        var secondStage = Path.Combine(root, "stage-2.tmp");
        File.WriteAllBytes(firstStage, [1, 2, 3]);
        File.WriteAllBytes(secondStage, [4, 5, 6]);

        var firstPath = HistoricalAttachmentInbox.PromoteOrReuse(root, rawFilename, sourceKey, firstStage);
        var secondPath = HistoricalAttachmentInbox.PromoteOrReuse(root, rawFilename, sourceKey, secondStage);

        Assert.Equal(firstPath, secondPath);
        Assert.True(File.Exists(firstPath));
        Assert.False(File.Exists(secondStage));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(firstPath));
    }
}
