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
    private readonly ILoggingAdapter _log = Context.GetLogger();

    public ToolApprovalActor(ToolApprovalStore? persistentStore = null)
    {
        _persistentStore = persistentStore;

        Receive<GetUnapprovedPatterns>(msg =>
        {
            // Snapshot the persisted approvals once per message — every
            // pattern in the same call evaluates against the same on-disk
            // state, and Load() does a synchronous file read + JSON parse
            // each call. For a compound shell with N candidate verbs this
            // collapses N reads into 1.
            var approved = _persistentStore is not null
                ? _persistentStore.GetApprovedEntries(msg.Audience, msg.ToolName.Value)
                : (IReadOnlyList<ApprovalEntry>)[];

            var unapproved = new List<string>(msg.Candidates.Count);
            var candidateChecks = new List<ToolApprovalCandidateCheck>(msg.Candidates.Count);
            var approvedMatches = new List<ToolApprovalMatch>(msg.Candidates.Count);
            foreach (var candidate in msg.Candidates)
            {
                var match = MatchApproval(msg.SessionId, msg.Audience, msg.ToolName, candidate, msg.Cwd, approved);
                candidateChecks.Add(new ToolApprovalCandidateCheck(candidate, match));
                if (match is null)
                {
                    unapproved.Add(candidate.Verb);
                    LogApprovalNearMisses(msg.ToolName, candidate, msg.Cwd, approved);
                    continue;
                }

                approvedMatches.Add(match);
            }

            Sender.Tell(new UnapprovedPatternsResponse(
                new ToolApprovalCheckResult(unapproved, approvedMatches)
                {
                    CandidateChecks = candidateChecks
                }));
        });

        Receive<RecordToolApproval>(msg =>
        {
            foreach (var pattern in msg.Patterns)
            {
                AddSessionApproval(msg.SessionId, msg.Audience, msg.ToolName, pattern);

                if (msg.Persistent)
                {
                    // The cwd field encodes scope: a non-null value writes a
                    // folder-scoped (verb, cwd) entry that matches future
                    // invocations only under that directory tree, while null
                    // writes a global wildcard (verb, null) that matches any
                    // cwd. The caller (LlmSessionActor) chooses based on the
                    // user's button click — Always here → cwd; Always
                    // anywhere → null.
                    _persistentStore?.AddApproval(
                        msg.Audience,
                        msg.ToolName.Value,
                        new ApprovalEntry(pattern) { Directory = msg.Cwd });
                }
            }

            Sender.Tell(ToolApprovalRecorded.Instance);
        });
    }

    public static Props CreateProps(ToolApprovalStore? persistentStore = null)
        => Props.Create(() => new ToolApprovalActor(persistentStore));

    private ToolApprovalMatch? MatchApproval(SessionId? sessionId, TrustAudience audience, ToolName toolName, ApprovalCandidate candidate, string? cwd, IReadOnlyList<ApprovalEntry> persistedApprovals)
    {
        if (sessionId.HasValue && IsSessionApproved(sessionId.Value, audience, toolName, candidate.Verb))
            return new ToolApprovalMatch(candidate.Verb, "session", "this chat");

        return MatchPersistedEntry(toolName, candidate, cwd, persistedApprovals);
    }

    private bool IsSessionApproved(SessionId sessionId, TrustAudience audience, ToolName toolName, string candidateVerb)
    {
        // Walk up the scope chain: sub-agent scopes inherit parent session approvals.
        // Scope format: "{parentSessionId}/subagent/{name}/{runId}" — parent is the prefix before "/subagent/".
        var scopeId = sessionId.Value;
        while (true)
        {
            var sessionKey = BuildSessionKey((SessionId)scopeId, audience);
            if (_sessionApprovals.TryGetValue(sessionKey, out var toolMap)
                && toolMap.TryGetValue(toolName.Value, out var verbs)
                && verbs.Contains(candidateVerb))
            {
                return true;
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

    private static ToolApprovalMatch? MatchPersistedEntry(ToolName toolName, ApprovalCandidate candidate, string? cwd, IReadOnlyList<ApprovalEntry> approved)
    {
        foreach (var entry in approved)
        {
            if (!ToolApprovalEntryComparer.Equals(entry.Verb, candidate.Verb))
                continue;

            var matches = string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal)
                ? ApprovalPatternMatching.MatchesShellApproval(candidate.Verb, candidate.Directory, cwd, [entry])
                : ApprovalPatternMatching.MatchesAny(candidate.Verb, [entry]);

            if (matches)
                return new ToolApprovalMatch(candidate.Verb, "persistent", entry.FormatScope());
        }

        return null;
    }

    /// <summary>
    /// Emits a diagnostic when a shell pattern is prompted for despite a
    /// persisted grant existing for the same verb — the operator's
    /// "I already approved this" case. Read-only: it does not affect the
    /// gate decision. Non-shell tools authorize on a verb match alone, so a
    /// same-verb persisted entry would have approved them; nothing to explain.
    /// </summary>
    private void LogApprovalNearMisses(ToolName toolName, ApprovalCandidate candidate, string? cwd, IReadOnlyList<ApprovalEntry> approved)
    {
        if (!string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal))
            return;

        var nearMisses = ApprovalPatternMatching.ExplainShellNearMisses(
            candidate.Verb, candidate.Directory, cwd, approved);

        foreach (var miss in nearMisses)
        {
            _log.Info(
                "approval_near_miss tool={ToolName} verb={CandidateVerb} candidate_dir={CandidateDirectory} cwd={Cwd}",
                toolName.Value,
                candidate.Verb,
                candidate.Directory ?? "(none)",
                cwd ?? "(none)");
            _log.Info(
                "approval_near_miss grant={GrantScope} grant_created_at={GrantCreatedAt} reason={Reason}",
                miss.Grant.FormatScope(),
                miss.Grant.CreatedAt?.ToString("u") ?? "unknown",
                miss.Describe());
        }
    }

    private static string BuildSessionKey(SessionId sessionId, TrustAudience audience)
        => $"{sessionId.Value}|{audience.ToWireValue()}";
}

internal sealed record ToolApprovalRecorded
{
    public static ToolApprovalRecorded Instance { get; } = new();
}
