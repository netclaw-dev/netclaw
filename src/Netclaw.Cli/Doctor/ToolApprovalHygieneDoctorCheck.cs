// -----------------------------------------------------------------------
// <copyright file="ToolApprovalHygieneDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Reports stored grants that add nothing: a grant whose words name a file,
/// and a grant that another grant covers. <c>netclaw doctor --fix</c> removes
/// them (<see cref="DoctorFixService"/>). A grant whose folder no longer exists
/// is reported and kept, because the doctor cannot tell what it covered.
/// </summary>
public sealed class ToolApprovalHygieneDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    internal const string CheckName = "Tool approval grants";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.ToolApprovalsPath))
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "No stored grants."));

        ApprovalHygieneReport report;
        try
        {
            report = CreateStore(paths).AnalyzeHygiene();
        }
        catch (Exception ex)
        {
            // An unreadable or invalid store is the store check's finding. This check reports it and stops.
            return Task.FromResult(DoctorCheckResult.Warning(CheckName, $"Could not read tool-approvals.json: {ex.Message}"));
        }

        if (report.Findings.Count == 0)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "Each stored grant adds authority."));

        var lines = report.Findings
            .Select(static finding => $"{finding.Audience}.{finding.ToolName}: {finding.Entry.FormatScope()}: {Describe(finding)}");
        var removable = report.Findings.Count(static finding => finding.Removable);
        return Task.FromResult(DoctorCheckResult.Warning(
            CheckName,
            $"{report.Findings.Count} stored grant(s) need attention:\n  " + string.Join("\n  ", lines),
            removable > 0
                ? $"Run `netclaw doctor --fix` to remove {removable} grant(s) that add nothing. A grant with a missing folder stays."
                : "Revoke a grant with a missing folder with `netclaw approvals` when you no longer need it."));
    }

    internal static ToolApprovalStore CreateStore(NetclawPaths paths)
        => new(
            paths.ToolApprovalsPath,
            timeProvider: null,
            new ApprovalStoreMigrationContext(OperatingSystem.IsWindows() ? ApprovalShell.PowerShell : ApprovalShell.Bash));

    private static string Describe(ApprovalHygieneFinding finding) => finding.Issue switch
    {
        ApprovalHygieneIssue.FileWord => $"names a file ({finding.Detail})",
        ApprovalHygieneIssue.Covered => $"covered by {finding.Detail}",
        ApprovalHygieneIssue.MissingFolder => "the folder does not exist; kept",
        _ => finding.Issue.ToString()
    };
}
