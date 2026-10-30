// -----------------------------------------------------------------------
// <copyright file="SkillScanStartupLogging.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Skills;

namespace Netclaw.Daemon.Services;

/// <summary>Logs the startup skill scan's rejected items through the host's logger.</summary>
internal static class SkillScanStartupLogging
{
    public static void Log(ILogger logger, MergedSkillScanResult scan)
    {
        if (scan.Issues.Count == 0)
            return;

        logger.LogWarning(
            "Skill inventory is degraded at startup: accepted={AcceptedSkillCount} rejected={RejectedIssueCount}",
            scan.AcceptedSkills.Count,
            scan.Issues.Count);

        foreach (var issue in scan.Issues)
        {
            logger.LogWarning(
                "Rejected skill item during startup scan: kind={IssueKind} path={Path} message={Message}",
                issue.Kind,
                issue.Path,
                issue.Message);
        }
    }
}
