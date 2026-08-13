// -----------------------------------------------------------------------
// <copyright file="ShellPolicyDecisionTrace.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

internal enum ShellPolicyTraceStage
{
    StoredGrantMatch = 0,
    ReviewedSafePolicy = 1,
    OneTimeApproval = 2,
    Completion = 3,
    Trace = 4,
}

internal enum ShellPolicyTraceOutcome
{
    Covered = 0,
    Uncovered = 1,
    Allow = 2,
    RequiresApproval = 3,
    Deny = 4,
    TraceTruncated = 5,
}

internal enum ShellPolicyTraceReason
{
    None = 0,
    NoGrant = 1,
    OneTimeGrant = 2,
    SessionGrant = 3,
    PersistentGlobalGrant = 4,
    PersistentFolderGrant = 5,
    ReviewedSafePhrase = 6,
    ApprovalExemptSideEffect = 7,
    OutsideDirectory = 8,
    Symlink = 9,
    MissingDirectory = 10,
    TokenMismatch = 11,
    ShellMismatch = 12,
    AllCandidatesCovered = 13,
    UncoveredCandidates = 14,
    ApprovalStoreUnavailable = 15,
    InternalPolicyFailure = 16,
    PolicyAuto = 17,
    BackgroundJobLifecycle = 18,
    SafeVerbInTrustedScope = 19,
    ApprovalExemptShellCandidates = 20,
    TraceLimitReached = 21,
    PolicyDenied = 22,
}

internal enum ShellScopeRelation
{
    None = 0,
    ThisChat = 1,
    Global = 2,
    UnderGrantRoot = 3,
    UnderRealRoot = 4,
    OutsideGrantRoot = 5,
    SymlinkBoundary = 6,
    UnderIntentRoot = 7,
}

internal sealed record ShellPolicyTraceRow(
    ShellPolicyTraceStage Stage,
    ShellPolicyTraceOutcome Outcome,
    ShellPolicyTraceReason Reason,
    ShellPolicyCandidateId? CandidateId,
    string? ExecutableBasename,
    ShellCoverageKind? Coverage,
    ShellScopeRelation ScopeRelation,
    DateTimeOffset? GrantTimestamp);

internal sealed record ShellPolicyDecisionTrace(IReadOnlyList<ShellPolicyTraceRow> Rows)
{
    internal static ShellPolicyDecisionTrace Empty { get; } = new([]);
}

internal sealed class ShellPolicyDecisionTraceBuilder
{
    internal const int MaximumRows = 256;
    internal const int MaximumTextCodeUnits = 128;

    private const int MaximumDetailRows = MaximumRows - 1;
    private const int MaximumSourceTextCodeUnits = 512;
    private const string RedactedText = "***REDACTED***";

    private readonly List<ShellPolicyTraceRow> _rows = [];
    private readonly HashSet<(ShellPolicyTraceStage Stage, int? CandidateId)> _rowKeys = [];
    private bool _truncated;
    private ShellPolicyDecisionTrace? _completedTrace;

    internal void AddActorEvidence(
        ShellPolicyCandidate candidate,
        ShellGrantCandidateMatch actorMatch)
    {
        if (actorMatch.Match is not null && actorMatch.GrantCoverage is { } coverage)
        {
            AddDetail(new ShellPolicyTraceRow(
                ShellPolicyTraceStage.StoredGrantMatch,
                ShellPolicyTraceOutcome.Covered,
                ToTraceReason(coverage),
                candidate.Id,
                GetExecutableBasename(candidate.Candidate),
                coverage,
                ToScopeRelation(coverage),
                actorMatch.GrantCreatedAt));
            return;
        }

        var nearMiss = actorMatch.NearMisses.FirstOrDefault();
        AddDetail(new ShellPolicyTraceRow(
            ShellPolicyTraceStage.StoredGrantMatch,
            ShellPolicyTraceOutcome.Uncovered,
            nearMiss is null ? ShellPolicyTraceReason.NoGrant : ToTraceReason(nearMiss.Reason),
            candidate.Id,
            GetExecutableBasename(candidate.Candidate),
            nearMiss is null
                ? ShellCoverageKind.Uncovered
                : nearMiss.Grant.Directory is null
                    ? ShellCoverageKind.PersistentGlobal
                    : ShellCoverageKind.PersistentFolder,
            nearMiss is null
                ? ShellScopeRelation.None
                : ToScopeRelation(nearMiss),
            nearMiss?.Grant.CreatedAt));
    }

    internal void AddCoverage(
        ShellPolicyTraceStage stage,
        ShellPolicyCandidate candidate,
        ShellCoverageKind coverage,
        ShellPolicyReason reason,
        ShellScopeRelation scopeRelation)
        => AddDetail(new ShellPolicyTraceRow(
            stage,
            ShellPolicyTraceOutcome.Covered,
            ToTraceReason(reason),
            candidate.Id,
            GetExecutableBasename(candidate.Candidate),
            coverage,
            scopeRelation,
            GrantTimestamp: null));

    internal ShellPolicyDecisionTrace Complete(ToolAuthorizationDecision decision)
    {
        if (_completedTrace is not null)
            return _completedTrace;

        var completion = ToCompletionRow(decision);
        _rows.Add(completion);
        _completedTrace = new ShellPolicyDecisionTrace(Array.AsReadOnly(_rows.ToArray()));
        return _completedTrace;
    }

    internal static string SanitizeText(string value)
    {
        // A partial private-key block cannot be recognized after truncation.
        // Fail closed for oversized executable text and redact complete
        // bounded input before projecting the trace field.
        var redacted = value.Length <= MaximumSourceTextCodeUnits
            ? SecretOutputRedactor.Redact(value)
            : RedactedText;
        var result = new StringBuilder(Math.Min(redacted.Length, MaximumTextCodeUnits));
        for (var index = 0; index < redacted.Length; index++)
        {
            var current = redacted[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < redacted.Length
                && char.IsLowSurrogate(redacted[index + 1]))
            {
                if (result.Length + 2 > MaximumTextCodeUnits)
                    break;

                result.Append(current).Append(redacted[++index]);
                continue;
            }

            if (char.IsSurrogate(current) || IsUnsafeTextCodeUnit(current))
            {
                if (result.Length + 6 > MaximumTextCodeUnits)
                    break;

                result.Append("\\u").Append(((int)current).ToString("X4"));
                continue;
            }

            if (result.Length == MaximumTextCodeUnits)
                break;

            result.Append(current);
        }

        return result.ToString();
    }

    private void AddDetail(ShellPolicyTraceRow row)
    {
        if (_completedTrace is not null || _truncated)
            return;

        var key = (row.Stage, row.CandidateId?.Value);
        if (!_rowKeys.Add(key))
            return;

        if (_rows.Count < MaximumDetailRows)
        {
            _rows.Add(row);
            return;
        }

        _rows[^1] = new ShellPolicyTraceRow(
            ShellPolicyTraceStage.Trace,
            ShellPolicyTraceOutcome.TraceTruncated,
            ShellPolicyTraceReason.TraceLimitReached,
            CandidateId: null,
            ExecutableBasename: null,
            Coverage: null,
            ShellScopeRelation.None,
            GrantTimestamp: null);
        _truncated = true;
    }

    private static ShellPolicyTraceRow ToCompletionRow(ToolAuthorizationDecision decision)
    {
        var (outcome, reason) = decision.Outcome switch
        {
            ToolAuthorizationOutcome.Allowed => (
                ShellPolicyTraceOutcome.Allow,
                ToTraceReason(decision.AllowReason)),
            ToolAuthorizationOutcome.RequiresApproval => (
                ShellPolicyTraceOutcome.RequiresApproval,
                ShellPolicyTraceReason.UncoveredCandidates),
            ToolAuthorizationOutcome.Denied => (
                ShellPolicyTraceOutcome.Deny,
                ToTraceReason(decision.DenyReason)),
            _ => (
                ShellPolicyTraceOutcome.Deny,
                ShellPolicyTraceReason.InternalPolicyFailure),
        };
        return new ShellPolicyTraceRow(
            ShellPolicyTraceStage.Completion,
            outcome,
            reason,
            CandidateId: null,
            ExecutableBasename: null,
            Coverage: null,
            ShellScopeRelation.None,
            GrantTimestamp: null);
    }

    private static string? GetExecutableBasename(ApprovalCandidate candidate)
    {
        if (candidate.VerbTokens is not { Count: > 0 })
            return null;

        var executable = candidate.VerbTokens[0];
        var separator = executable.LastIndexOfAny(['/', '\\']);
        var basename = separator < 0 ? executable : executable[(separator + 1)..];
        return SanitizeText(basename);
    }

    private static ShellPolicyTraceReason ToTraceReason(ShellCoverageKind coverage) => coverage switch
    {
        ShellCoverageKind.Session => ShellPolicyTraceReason.SessionGrant,
        ShellCoverageKind.PersistentGlobal => ShellPolicyTraceReason.PersistentGlobalGrant,
        ShellCoverageKind.PersistentFolder => ShellPolicyTraceReason.PersistentFolderGrant,
        _ => ShellPolicyTraceReason.None,
    };

    private static ShellPolicyTraceReason ToTraceReason(ShellPolicyReason reason) => reason switch
    {
        ShellPolicyReason.OneTimeGrant => ShellPolicyTraceReason.OneTimeGrant,
        ShellPolicyReason.SessionGrant => ShellPolicyTraceReason.SessionGrant,
        ShellPolicyReason.PersistentGlobalGrant => ShellPolicyTraceReason.PersistentGlobalGrant,
        ShellPolicyReason.PersistentFolderGrant => ShellPolicyTraceReason.PersistentFolderGrant,
        ShellPolicyReason.ReviewedSafePhrase => ShellPolicyTraceReason.ReviewedSafePhrase,
        ShellPolicyReason.ApprovalExemptSideEffect => ShellPolicyTraceReason.ApprovalExemptSideEffect,
        _ => ShellPolicyTraceReason.None,
    };

    private static ShellPolicyTraceReason ToTraceReason(ShellApprovalNearMissReason reason) => reason switch
    {
        ShellApprovalNearMissReason.OutsideDirectory => ShellPolicyTraceReason.OutsideDirectory,
        ShellApprovalNearMissReason.Symlink => ShellPolicyTraceReason.Symlink,
        ShellApprovalNearMissReason.MissingDirectory => ShellPolicyTraceReason.MissingDirectory,
        ShellApprovalNearMissReason.TokenMismatch => ShellPolicyTraceReason.TokenMismatch,
        ShellApprovalNearMissReason.ShellMismatch => ShellPolicyTraceReason.ShellMismatch,
        _ => ShellPolicyTraceReason.None,
    };

    private static ShellPolicyTraceReason ToTraceReason(ToolAllowReason? reason) => reason switch
    {
        ToolAllowReason.PolicyAuto => ShellPolicyTraceReason.PolicyAuto,
        ToolAllowReason.BackgroundJobLifecycle => ShellPolicyTraceReason.BackgroundJobLifecycle,
        ToolAllowReason.SafeVerbInTrustedScope => ShellPolicyTraceReason.SafeVerbInTrustedScope,
        ToolAllowReason.ApprovalExemptShellCandidates => ShellPolicyTraceReason.ApprovalExemptShellCandidates,
        ToolAllowReason.StoredApproval or ToolAllowReason.OneTimeApproval =>
            ShellPolicyTraceReason.AllCandidatesCovered,
        _ => ShellPolicyTraceReason.InternalPolicyFailure,
    };

    private static ShellPolicyTraceReason ToTraceReason(string? denyReason) => denyReason switch
    {
        "approval_store_unavailable" => ShellPolicyTraceReason.ApprovalStoreUnavailable,
        "internal_policy_failure" => ShellPolicyTraceReason.InternalPolicyFailure,
        _ => ShellPolicyTraceReason.PolicyDenied,
    };

    private static ShellScopeRelation ToScopeRelation(ShellCoverageKind coverage) => coverage switch
    {
        ShellCoverageKind.Session => ShellScopeRelation.ThisChat,
        ShellCoverageKind.PersistentGlobal => ShellScopeRelation.Global,
        ShellCoverageKind.PersistentFolder => ShellScopeRelation.UnderGrantRoot,
        _ => ShellScopeRelation.None,
    };

    private static ShellScopeRelation ToScopeRelation(ShellApprovalNearMiss nearMiss)
        => nearMiss.Reason switch
        {
            ShellApprovalNearMissReason.OutsideDirectory => ShellScopeRelation.OutsideGrantRoot,
            ShellApprovalNearMissReason.Symlink => ShellScopeRelation.SymlinkBoundary,
            _ => nearMiss.Grant.Directory is null
                ? ShellScopeRelation.Global
                : ShellScopeRelation.None,
        };

    private static bool IsUnsafeTextCodeUnit(char value)
        => char.IsControl(value)
           || value is '\u061C' or '\u200E' or '\u200F'
           or >= '\u2028' and <= '\u202E'
           or >= '\u2066' and <= '\u2069'
           or '\uFEFF';
}
