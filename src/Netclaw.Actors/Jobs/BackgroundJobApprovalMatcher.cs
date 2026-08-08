// -----------------------------------------------------------------------
// <copyright file="BackgroundJobApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;namespace Netclaw.Actors.Jobs;

/// <summary>
/// Approval matcher for <see cref="CheckBackgroundJobTool"/>.
///
/// <para>
/// A status query (<c>Cancel=false</c>) is read-only: it asks the
/// <c>BackgroundJobManagerActor</c> for the caller's own job state and the
/// manager enforces an exact <c>SessionId + Audience + Boundary</c> match
/// before answering, so no cross-session data can leak. Status queries must
/// NOT be fail-closed on the Personal audience — otherwise a non-interactive
/// turn (reminder, webhook, sub-agent without an approval bridge) wedges
/// waiting on an approval prompt no one can answer, which stalls the very
/// background-job workflow the tool exists to serve.
/// </para>
///
/// <para>
/// A cancellation (<c>Cancel=true</c>) IS a mutation and stays fail-closed:
/// it kills a running process, so it keeps requiring interactive approval
/// exactly like the shell command that launched the job.
/// </para>
///
/// <para>
/// Launching a background job is unaffected by this matcher. A background
/// launch is a <c>shell_execute</c> call with <c>_background: true</c>, which
/// routes through <see cref="ShellApprovalMatcher"/> and the ordinary shell
/// approval gate. This matcher only governs the query/cancel follow-up tool.
/// </para>
/// </summary>
public sealed class BackgroundJobApprovalMatcher : IToolApprovalMatcher
{
    public static readonly BackgroundJobApprovalMatcher Instance = new();

    /// <summary>
    /// Argument key carrying the cancellation flag on <c>check_background_job</c>.
    /// </summary>
    private const string CancelArgumentKey = "Cancel";

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    /// <summary>
    /// Only a cancellation request fails closed. A status query is read-only
    /// and job-scoped, so it must not force interactive approval on Personal.
    ///
    /// Cancellation detection delegates to <see cref="ToolArgumentHelper.GetBoolStrict"/>
    /// — the same helper the generated tool binding uses to parse the
    /// <c>Cancel</c> argument. This keeps the approval decision and the
    /// execution decision aligned by construction: any argument shape the
    /// binding accepts as a cancellation (case-insensitive/normalized key,
    /// CLR <c>bool</c>, <c>JsonElement</c>, or string <c>"true"</c>) is
    /// detected here too, so a real cancel can never slip through as a
    /// "status query" and auto-allow. A missing/absent value is not a cancel;
    /// an unparsable value is not a cancel either — the binding rejects those
    /// before execution, so failing toward the read-only path is safe.
    /// </summary>
    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => IsCancelRequest(arguments);

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
        => [toolName.Value];

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => [toolName.Value];

    public IReadOnlyList<ApprovalCandidate> ExtractCandidates(ToolName toolName, IDictionary<string, object?>? arguments)
        => [new ApprovalCandidate(toolName.Value, Directory: null)];

    public bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
        => ApprovalPatternMatching.MatchesAny(toolName.Value, approvedEntries);

    public bool IsMessy(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    private static bool IsCancelRequest(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return false;

        try
        {
            // Absent/null → not a cancel. Invalid values throw here; the tool
            // binding rejects them before execution, so treating them as
            // non-cancel (read-only path) cannot mask a real cancellation.
            return ToolArgumentHelper.GetBoolStrict(arguments, CancelArgumentKey) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
