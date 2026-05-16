// -----------------------------------------------------------------------
// <copyright file="ToolApprovalActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

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

            var unapproved = new List<string>(msg.Patterns.Count);
            foreach (var pattern in msg.Patterns)
            {
                if (!IsApproved(msg.SessionId, msg.Audience, msg.ToolName, pattern, msg.Cwd, approved))
                {
                    unapproved.Add(pattern);
                    LogApprovalNearMisses(msg.ToolName, pattern, msg.Cwd, approved);
                }
            }

            Sender.Tell(new UnapprovedPatternsResponse(unapproved));
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
                        new ApprovalEntry { Verb = pattern, Directory = msg.Cwd });
                }
            }

            Sender.Tell(ToolApprovalRecorded.Instance);
        });
    }

    public static Props CreateProps(ToolApprovalStore? persistentStore = null)
        => Props.Create(() => new ToolApprovalActor(persistentStore));

    private bool IsApproved(SessionId? sessionId, TrustAudience audience, ToolName toolName, string candidateVerb, string? cwd, IReadOnlyList<ApprovalEntry> persistedApprovals)
    {
        if (sessionId.HasValue && IsSessionApproved(sessionId.Value, audience, toolName, candidateVerb))
            return true;

        return MatchesPersistedEntry(toolName, candidateVerb, cwd, persistedApprovals);
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

            var subagentMarker = scopeId.IndexOf("/subagent/", StringComparison.Ordinal);
            if (subagentMarker <= 0)
                break;

            scopeId = scopeId[..subagentMarker];
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

    private static bool MatchesPersistedEntry(ToolName toolName, string candidateVerb, string? cwd, IReadOnlyList<ApprovalEntry> approved)
        => string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal)
            ? ApprovalPatternMatching.MatchesShellApproval(candidateVerb, cwd, approved)
            : ApprovalPatternMatching.MatchesAny(candidateVerb, approved);

    /// <summary>
    /// Emits a diagnostic when a shell pattern is prompted for despite a
    /// persisted grant existing for the same verb — the operator's
    /// "I already approved this" case. Read-only: it does not affect the
    /// gate decision. Non-shell tools authorize on a verb match alone, so a
    /// same-verb persisted entry would have approved them; nothing to explain.
    /// </summary>
    private void LogApprovalNearMisses(ToolName toolName, string candidateVerb, string? cwd, IReadOnlyList<ApprovalEntry> approved)
    {
        if (!string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal))
            return;

        var nearMisses = ApprovalPatternMatching.ExplainShellNearMisses(
            candidateVerb, candidateDirectory: null, cwd, approved);

        foreach (var miss in nearMisses)
        {
            _log.Info(
                "Approval near-miss for {0} '{1}' (cwd '{2}'): prompted despite persisted grant '{3}' (added {4}) — {5}",
                toolName.Value,
                candidateVerb,
                cwd ?? "(none)",
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
