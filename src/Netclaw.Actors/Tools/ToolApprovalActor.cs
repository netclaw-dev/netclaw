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
using Netclaw.Tools;
using static Netclaw.Actors.Tools.ToolApprovalProtocol;

namespace Netclaw.Actors.Tools;

internal sealed class ToolApprovalActor : ReceiveActor
{
    private readonly ToolApprovalStore? _persistentStore;
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _sessionApprovals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, List<ApprovalEntry>>> _structuredSessionApprovals =
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
            var candidateMatches = new List<ShellGrantCandidateMatch>(msg.Candidates.Count);
            foreach (var candidate in msg.Candidates)
            {
                var grantEvaluation = EvaluateShellApproval(
                    msg.SessionId,
                    msg.Audience,
                    msg.ToolName,
                    candidate.Candidate,
                    candidate.RealDirectory,
                    snapshot.Approvals);
                candidateMatches.Add(new ShellGrantCandidateMatch(
                    candidate.CandidateId,
                    grantEvaluation.Match,
                    grantEvaluation.Coverage,
                    grantEvaluation.NearMisses)
                {
                    GrantCreatedAt = grantEvaluation.GrantCreatedAt
                });
            }

            var storeStatus = snapshot.Failure is { } failure
                ? (PersistentGrantStoreStatus)new PersistentGrantStoreStatus.Unavailable(failure)
                : new PersistentGrantStoreStatus.Ready();
            Sender.Tell(new ShellApprovalMatchResponse(
                new ShellApprovalMatchResult(
                    storeStatus,
                    Array.AsReadOnly(candidateMatches.ToArray()))));
        });

        Receive<RecordToolApproval>(msg =>
        {
            if (string.Equals(msg.ToolName.Value, ShellTool.ToolName, StringComparison.Ordinal))
            {
                Sender.Tell(new ToolApprovalRecorded(ApprovalStoreFailure.InvalidData));
                return;
            }

            ApprovalStoreFailure? storeFailure = null;
            foreach (var pattern in msg.Patterns)
            {
                if (msg.Persistent)
                {
                    // The cwd field encodes scope: a non-null value writes a
                    // folder-scoped (verb, cwd) entry that matches future
                    // invocations only under that directory tree, while null
                    // writes a global wildcard (verb, null) that matches any
                    // cwd. The caller (LlmSessionActor) chooses based on the
                    // user's button click — Always here → cwd; Always
                    // anywhere → null.
                    if (_persistentStore is null)
                    {
                        storeFailure = ApprovalStoreFailure.IoFailure;
                        break;
                    }

                    var change = _persistentStore.TryAddApproval(
                        msg.Audience,
                        msg.ToolName.Value,
                        new ApprovalEntry(pattern) { Directory = msg.Cwd });
                    ReportMigrationOmissions();
                    if (change is ApprovalStoreChangeResult.Unavailable unavailable)
                    {
                        storeFailure = unavailable.Failure;
                        break;
                    }
                }

                AddSessionApproval(msg.SessionId, msg.Audience, msg.ToolName, pattern);
            }

            Sender.Tell(storeFailure is null
                ? ToolApprovalRecorded.Success
                : new ToolApprovalRecorded(storeFailure));
        });

        Receive<RecordStructuredToolApproval>(msg =>
        {
            if (!TryCreateEntries(msg.ToolName, msg.Grants, out var persistentEntries, out var sessionEntries))
            {
                Sender.Tell(new ToolApprovalRecorded(ApprovalStoreFailure.InvalidData));
                return;
            }

            if (msg.Persistent)
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
                AddStructuredSessionApproval(
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
            IsSessionApproved(sessionId.Value, audience, toolName, candidate))
            return new ToolApprovalMatch(candidate.Verb, "session", "this chat");

        return MatchPersistedEntry(toolName, candidate, cwd, persistedApprovals);
    }

    private ShellActorGrantEvaluation EvaluateShellApproval(
        SessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        ApprovalCandidate candidate,
        string? cwd,
        IReadOnlyList<ApprovalEntry> persistedApprovals)
    {
        if (sessionId.HasValue
            && IsSessionApproved(sessionId.Value, audience, toolName, candidate))
        {
            return new ShellActorGrantEvaluation(
                new ToolApprovalMatch(candidate.Verb, "session", "this chat"),
                ShellCoverageKind.Session,
                GrantCreatedAt: null,
                NearMisses: []);
        }

        var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
            candidate,
            cwd,
            persistedApprovals,
            maximumNearMisses: 1);
        if (evaluation.MatchedEntry is { } entry)
        {
            return new ShellActorGrantEvaluation(
                new ToolApprovalMatch(candidate.Verb, "persistent", entry.FormatScope()),
                entry.Directory is null
                    ? ShellCoverageKind.PersistentGlobal
                    : ShellCoverageKind.PersistentFolder,
                entry.CreatedAt,
                NearMisses: []);
        }

        return new ShellActorGrantEvaluation(
            Match: null,
            Coverage: null,
            GrantCreatedAt: null,
            evaluation.NearMisses);
    }

    private bool IsSessionApproved(
        SessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        ApprovalCandidate candidate)
    {
        // Walk up the scope chain: sub-agent scopes inherit parent session approvals.
        // Scope format: "{parentSessionId}/subagent/{name}/{runId}" — parent is the prefix before "/subagent/".
        var scopeId = sessionId.Value;
        while (true)
        {
            var sessionKey = BuildSessionKey((SessionId)scopeId, audience);
            if (_sessionApprovals.TryGetValue(sessionKey, out var toolMap)
                && toolMap.TryGetValue(toolName.Value, out var verbs)
                && verbs.Contains(candidate.Verb))
            {
                return true;
            }

            if (_structuredSessionApprovals.TryGetValue(sessionKey, out var structuredTools)
                && structuredTools.TryGetValue(toolName.Value, out var entries))
            {
                var matches = string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal)
                    ? ApprovalPatternMatching.MatchesShellApproval(candidate, cwd: null, entries)
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

    private void AddSessionApproval(SessionId sessionId, TrustAudience audience, ToolName toolName, string candidateVerb)
    {
        var sessionKey = BuildSessionKey(sessionId, audience);
        if (!_sessionApprovals.TryGetValue(sessionKey, out var toolMap))
        {
            toolMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            _sessionApprovals[sessionKey] = toolMap;
        }

        if (!toolMap.TryGetValue(toolName.Value, out var verbs))
        {
            // Session approvals use the same platform-correct comparer as the
            // persistent store (Ordinal on POSIX, OrdinalIgnoreCase on Windows)
            // so a grant for `git` cannot be redeemed by a planted `Git`
            // earlier in $PATH on case-sensitive filesystems.
            verbs = new HashSet<string>(ToolApprovalEntryComparer.Comparer);
            toolMap[toolName.Value] = verbs;
        }

        verbs.Add(candidateVerb);
    }

    private void AddStructuredSessionApproval(
        SessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        ApprovalEntry entry)
    {
        var sessionKey = BuildSessionKey(sessionId, audience);
        if (!_structuredSessionApprovals.TryGetValue(sessionKey, out var toolMap))
        {
            toolMap = new Dictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal);
            _structuredSessionApprovals[sessionKey] = toolMap;
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

    private static bool TryCreateEntries(
        ToolName toolName,
        IReadOnlyList<ToolApprovalGrant> grants,
        out IReadOnlyList<ApprovalEntry> persistentEntries,
        out IReadOnlyList<ApprovalEntry> sessionEntries)
    {
        var persisted = new List<ApprovalEntry>(grants.Count);
        var session = new List<ApprovalEntry>(grants.Count);
        try
        {
            foreach (var grant in grants)
            {
                ApprovalEntry persistedEntry;
                if (string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal))
                {
                    if (grant.Candidate.Shell is not { } shell ||
                        grant.Candidate.VerbTokens is not { } tokens)
                    {
                        persistentEntries = [];
                        sessionEntries = [];
                        return false;
                    }

                    persistedEntry = ApprovalEntry.CreateTokenPrefix(
                        shell,
                        tokens,
                        grant.Directory);
                }
                else
                {
                    persistedEntry = ApprovalEntry.CreateNonShell(
                        grant.Candidate.Verb,
                        grant.Directory);
                }

                persisted.Add(persistedEntry);
                session.Add(persistedEntry with { Directory = null });
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            persistentEntries = [];
            sessionEntries = [];
            return false;
        }

        persistentEntries = persisted;
        sessionEntries = session;
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
                return new ToolApprovalMatch(candidate.Verb, "persistent", entry.FormatScope());
        }

        return null;
    }

    private static string BuildSessionKey(SessionId sessionId, TrustAudience audience)
        => $"{sessionId.Value}|{audience.ToWireValue()}";

    private sealed record PersistentApprovalSnapshot(
        IReadOnlyList<ApprovalEntry> Approvals,
        ApprovalStoreFailure? Failure);

    private sealed record ShellActorGrantEvaluation(
        ToolApprovalMatch? Match,
        ShellCoverageKind? Coverage,
        DateTimeOffset? GrantCreatedAt,
        IReadOnlyList<ShellApprovalNearMiss> NearMisses);
}

internal sealed record ToolApprovalRecorded(ApprovalStoreFailure? Failure)
{
    public static ToolApprovalRecorded Success { get; } = new((ApprovalStoreFailure?)null);
}
