// -----------------------------------------------------------------------
// <copyright file="SkillScanStartupLoggingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class SkillScanStartupLoggingTests
{
    private static ILogger CapturedLogger(CapturingLoggerProvider provider)
        => LoggerFactory.Create(b => b.AddProvider(provider)).CreateLogger("Netclaw.Startup");

    [Fact]
    public void Rejected_items_are_warned_through_the_logger_provider()
    {
        var provider = new CapturingLoggerProvider();
        var scan = new MergedSkillScanResult(
            [],
            [
                new SkillScanIssue("/skills/a/SKILL.md", SkillScanIssueKind.MissingFrontmatter, "no frontmatter"),
                new SkillScanIssue("/skills/b/SKILL.md", SkillScanIssueKind.DuplicateName, "duplicate of a")
            ]);

        SkillScanStartupLogging.Log(CapturedLogger(provider), scan);

        var entries = provider.Entries.ToArray();
        Assert.Equal(3, entries.Length);
        Assert.All(entries, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.Contains("Skill inventory is degraded at startup: accepted=0 rejected=2", entries[0].Message);
        Assert.Contains("kind=MissingFrontmatter path=/skills/a/SKILL.md message=no frontmatter", entries[1].Message);
        Assert.Contains("kind=DuplicateName path=/skills/b/SKILL.md", entries[2].Message);
    }

    [Fact]
    public void Clean_scan_logs_nothing()
    {
        var provider = new CapturingLoggerProvider();

        SkillScanStartupLogging.Log(CapturedLogger(provider), new MergedSkillScanResult([], []));

        Assert.Empty(provider.Entries);
    }
}
