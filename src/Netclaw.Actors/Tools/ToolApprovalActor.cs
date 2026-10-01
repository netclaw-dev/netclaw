// -----------------------------------------------------------------------
// <copyright file="ToolApprovalActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using static Netclaw.Actors.Tools.ToolApprovalProtocol;

namespace Netclaw.Actors.Tools;

internal sealed class ToolApprovalActor : ReceiveActor
{
    private readonly ToolApprovalStore? _persistentStore;
    private readonly Dictionary<string, Dictionary<string, List<ApprovalEntry>>> _sessionApprovals =
        new(StringComparer.Ordinal);
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private bool _reportedMigrationOmissions;

    public ToolApprovalActor(ToolApprovalStore? persistentStore = null)
    {
        _persistentStore = persistentStore;

        Receive<GetUnapprovedPatterns>(msg =>
        {
            var snapshot = LoadPersistentSnapshot(msg.Audience, msg.ToolName);

            var unapproved = new List<string>(msg.Candidates.Count);
            var candidateChecks = new List<ToolApprovalCandidateCheck>(msg.Candidates.Count);
            var approvedMatches = new List<ToolApprovalMatch>(msg.Candidates.Count);
            foreach (var candidate in msg.Candidates)
            {
                var match = MatchApproval(
                    msg.SessionId,
                    msg.Audience,
                    msg.ToolName,
                    candidate,
                    msg.Cwd,
                    snapshot.Approvals);
                candidateChecks.Add(new ToolApprovalCandidateCheck(candidate, match));
                if (match is null)
                {
                    unapproved.Add(candidate.Verb);
                    continue;
                }

                approvedMatches.Add(match);
            }

            Sender.Tell(new UnapprovedPatternsResponse(
                new ToolApprovalCheckResult(unapproved, approvedMatches)
                {
                    CandidateChecks = candidateChecks,
                    PersistentStoreFailure = snapshot.Failure
                }));
        });

        Receive<MatchShellCandidates>(msg =>
        {
            var snapshot = LoadPersistentSnapshot(msg.Audience, msg.ToolName);
            var candidateMatches = new List<ShellGrantCandidateResult>(msg.Candidates.Count);
            foreach (var candidate in msg.Candidates)
            {
                candidateMatches.Add(EvaluateShellApproval(
                    msg.SessionId,
                    msg.Audience,
                    msg.ToolName,
                    candidate,
                    snapshot.Approvals));
            }

            Sender.Tell(new ShellApprovalMatchResponse(
                ShellApprovalMatchResult.Create(
                    msg.Candidates,
                    snapshot.Failure,
                    candidateMatches)));
        });

        Receive<RecordStructuredToolApproval>(msg =>
        {
            if (!TryCreateEntries(msg.ToolName, msg.Grants, out var persistentEntries, out var sessionEntries))
            {
                Sender.Tell(new ToolApprovalRecorded(ApprovalStoreFailure.InvalidData));
                return;
            }

            if (persistentEntries.Count > 0)
            {
                if (_persistentStore is null)
                {
                    Sender.Tell(new ToolApprovalRecorded(ApprovalStoreFailure.IoFailure));
                    return;
                }

                var change = _persistentStore.TryAddApprovals(
                    msg.Audience,
                    msg.ToolName.Value,
                    persistentEntries);
                ReportMigrationOmissions();
                if (change is ApprovalStoreChangeResult.Unavailable unavailable)
                {
                    Sender.Tell(new ToolApprovalRecorded(unavailable.Failure));
                    return;
                }
            }

            foreach (var entry in sessionEntries)
            {
                AddSessionApproval(
                    msg.SessionId,
                    msg.Audience,
                    msg.ToolName,
                    entry);
            }

            Sender.Tell(ToolApprovalRecorded.Success);
        });
    }

    private void ReportMigrationOmissions()
    {
        if (_reportedMigrationOmissions || _persistentStore is null)
        {
            return;
        }

        var omitted = _persistentStore.LastMigrationOmittedEntryCount;
        if (omitted == 0)
        {
            return;
        }

        _reportedMigrationOmissions = true;
        _log.Warning(
            "Approval store version-2 conversion omitted {OmittedEntryCount} unrepresentable entries.",
            omitted);
    }

    public static Props CreateProps(ToolApprovalStore? persistentStore = null)
        => Props.Create(() => new ToolApprovalActor(persistentStore));

    private PersistentApprovalSnapshot LoadPersistentSnapshot(
        TrustAudience audience,
        ToolName toolName)
    {
        if (_persistentStore is null)
            return new PersistentApprovalSnapshot([], Failure: null);

        var load = _persistentStore.TryLoad();
        ReportMigrationOmissions();
        if (load is ApprovalStoreLoadResult.Ready ready
            && ready.Data.Audiences.TryGetValue(audience.ToWireValue(), out var tools)
            && tools.TryGetValue(toolName.Value, out var entries))
        {
            return new PersistentApprovalSnapshot(entries, Failure: null);
        }

        return load is ApprovalStoreLoadResult.Unavailable unavailable
            ? new PersistentApprovalSnapshot([], unavailable.Failure)
            : new PersistentApprovalSnapshot([], Failure: null);
    }

    private ToolApprovalMatch? MatchApproval(SessionId? sessionId, TrustAudience audience, ToolName toolName, ApprovalCandidate candidate, string? cwd, IReadOnlyList<ApprovalEntry> persistedApprovals)
    {
        if (sessionId.HasValue &&
            IsSessionApproved(sessionId.Value, audience, toolName, candidate, cwd))
            return new ToolApprovalMatch(candidate.Verb, GrantScope.Session.Instance);

        return MatchPersistedEntry(toolName, candidate, cwd, persistedApprovals);
    }

    private ShellGrantCandidateResult EvaluateShellApproval(
        SessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        ShellGrantCandidate candidate,
        IReadOnlyList<ApprovalEntry> persistedApprovals)
    {
        if (sessionId.HasValue
            && IsSessionApproved(
                sessionId.Value,
                audience,
                toolName,
                candidate.Candidate,
                candidate.RealDirectory))
        {
            return ShellGrantCandidateResult.Session(candidate);
        }

        var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
            candidate.Candidate,
            candidate.RealDirectory,
            persistedApprovals,
            maximumNearMisses: 1);
        if (evaluation.MatchedEntry is { } entry)
        {
            return ShellGrantCandidateResult.Persistent(candidate, entry);
        }

        return ShellGrantCandidateResult.Uncovered(
            candidate,
            evaluation.NearMisses.SingleOrDefault());
    }

    private bool IsSessionApproved(
        SessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        ApprovalCandidate candidate,
        string? cwd)
    {
        // Walk up the scope chain: sub-agent scopes inherit parent session approvals.
        // Scope format: "{parentSessionId}/subagent/{name}/{runId}" — parent is the prefix before "/subagent/".
        var scopeId = sessionId.Value;
        while (true)
        {
            var sessionKey = BuildSessionKey((SessionId)scopeId, audience);
            if (_sessionApprovals.TryGetValue(sessionKey, out var tools)
                && tools.TryGetValue(toolName.Value, out var entries))
            {
                var matches = string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal)
                    ? ApprovalPatternMatching.MatchesShellApproval(candidate, cwd, entries)
                    : ApprovalPatternMatching.MatchesAny(candidate.Verb, entries);
                if (matches)
                {
                    return true;
                }
            }

            // Walk to the parent session so a sub-agent inherits its parent's approvals.
            // SubAgentSessionScope.NormalizeSessionId owns the "/subagent/" split (one
            // implementation shared with log routing); break once there is nothing left to strip.
            var parent = SubAgentSessionScope.NormalizeSessionId(scopeId);
            if (string.IsNullOrEmpty(parent) || parent == scopeId)
                break;

            scopeId = parent;
        }

        return false;
    }

    private void AddSessionApproval(
        SessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        ApprovalEntry entry)
    {
        var sessionKey = BuildSessionKey(sessionId, audience);
        if (!_sessionApprovals.TryGetValue(sessionKey, out var toolMap))
        {
            toolMap = new Dictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal);
            _sessionApprovals[sessionKey] = toolMap;
        }

        if (!toolMap.TryGetValue(toolName.Value, out var entries))
        {
            entries = [];
            toolMap[toolName.Value] = entries;
        }

        if (!entries.Any(existing => ToolApprovalEntryComparer.Equals(existing, entry)))
        {
            entries.Add(entry);
        }
    }

    /// <summary>
    /// Converts reviewed grants to store entries. It refuses the whole batch when
    /// one grant does not fit its tool or when a repository grant no longer
    /// resolves to the repository and worktree that the builder saw.
    /// </summary>
    internal static bool TryCreateEntries(
        ToolName toolName,
        IReadOnlyList<ToolApprovalGrant> grants,
        out IReadOnlyList<ApprovalEntry> persistentEntries,
        out IReadOnlyList<ApprovalEntry> sessionEntries)
    {
        var persisted = new List<ApprovalEntry>(grants.Count);
        var session = new List<ApprovalEntry>(grants.Count);
        persistentEntries = [];
        sessionEntries = [];
        try
        {
            foreach (var grant in grants)
            {
                if (!TryCreateEntry(toolName, grant, out var entry))
                    return false;

                if (grant.Scope.IsPersistent)
                    persisted.Add(entry);

                // A session match ignores the folder, so the session copy of a
                // folder grant drops it. A repository grant keeps its repository.
                session.Add(entry.Repository is null
                    ? entry with { Directory = null }
                    : entry);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            return false;
        }

        persistentEntries = persisted;
        sessionEntries = session;
        return true;
    }

    private static bool TryCreateEntry(
        ToolName toolName,
        ToolApprovalGrant grant,
        out ApprovalEntry entry)
    {
        entry = null!;
        if (grant.Scope is not GrantScope.Repository && grant.RepositoryWorktree is not null)
            return false;

        // The v3 store encodes the scope in two nullable fields.
        var directory = grant.Scope is GrantScope.Folder folder ? folder.Directory : null;
        if (!string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal))
        {
            if (grant.Scope is GrantScope.Repository || grant.Candidate.AssignmentDigest is not null)
                return false;

            entry = ApprovalEntry.CreateNonShell(grant.Candidate.Verb, directory);
            return true;
        }

        // The prompt offers no reusable grant for an unproven verb chain. This
        // last boundary before persistence refuses one as well.
        if (grant.Candidate.Shell is not { } shell ||
            grant.Candidate.HasUnprovenVerbChain ||
            grant.Candidate.VerbTokens is not { } tokens)
        {
            return false;
        }

        if (grant.Scope is not GrantScope.Repository repository)
        {
            entry = ApprovalEntry.CreateTokenPrefix(
                shell,
                tokens,
                directory,
                assignmentDigest: grant.Candidate.AssignmentDigest);
            return true;
        }

        // The approval actor is the last boundary before persistence. It reads
        // the Git metadata again, so a worktree registration that changed after
        // the prompt cannot redirect the grant.
        if (grant.RepositoryWorktree is null)
            return false;

        var candidateResolved = RepositoryIdentity.TryResolve(
            grant.Candidate.Directory, cwd: null, out var scope);
        if (!candidateResolved)
            return false;

        if (!ToolApprovalEntryComparer.Equals(scope!.CommonDirectory, repository.CommonDirectory)
            || !PathUtility.AreEquivalentPaths(
                scope.WorktreeRoot, grant.RepositoryWorktree))
        {
            return false;
        }

        entry = ApprovalEntry.CreateRepositoryTokenPrefix(
            shell,
            tokens,
            repository.CommonDirectory,
            assignmentDigest: grant.Candidate.AssignmentDigest);
        return true;
    }

    private static ToolApprovalMatch? MatchPersistedEntry(ToolName toolName, ApprovalCandidate candidate, string? cwd, IReadOnlyList<ApprovalEntry> approved)
    {
        foreach (var entry in approved)
        {
            var matches = string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal)
                ? ApprovalPatternMatching.MatchesShellApproval(candidate, cwd, [entry])
                : ToolApprovalEntryComparer.Equals(entry.Verb, candidate.Verb);

            if (matches)
                return new ToolApprovalMatch(candidate.Verb, GrantScope.OfStoredEntry(entry));
        }

        return null;
    }

    private static string BuildSessionKey(SessionId sessionId, TrustAudience audience)
        => $"{sessionId.Value}|{audience.ToWireValue()}";

    private sealed record PersistentApprovalSnapshot(
        IReadOnlyList<ApprovalEntry> Approvals,
        ApprovalStoreFailure? Failure);

}

internal sealed record ToolApprovalRecorded(ApprovalStoreFailure? Failure)
{
    public static ToolApprovalRecorded Success { get; } = new((ApprovalStoreFailure?)null);
}
