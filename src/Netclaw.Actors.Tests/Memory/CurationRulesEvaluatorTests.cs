// -----------------------------------------------------------------------
// <copyright file="CurationRulesEvaluatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class CurationRulesEvaluatorTests
{
    private static SQLiteMemoryCurationOperation MakeProposal(
        string anchor = "test-anchor",
        string content = "test content",
        string kind = "document",
        string updateSemantics = "merge-document",
        long? freshnessAtMs = null) =>
        new(
            Kind: kind,
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: anchor,
            AnchorType: "concept",
            Title: $"Title for {anchor}",
            Content: content,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: updateSemantics,
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Team,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: freshnessAtMs ?? 1000,
            ExpiresAtMs: null);

    private static ExistingMemoryCandidate MakeCandidate(
        string docId = "doc-123",
        string anchorId = "anchor:test-anchor",
        string anchorName = "test-anchor",
        string content = "test content",
        long? freshnessAtMs = null,
        double confidence = 0.9,
        bool isExact = true) =>
        new(
            DocumentId: docId,
            AnchorId: anchorId,
            AnchorCanonicalName: anchorName,
            Content: content,
            FreshnessAtMs: freshnessAtMs ?? 900,
            Confidence: confidence,
            IsExactAnchorMatch: isExact);

    // ── No candidates -> Create ─────────────────────────────────────

    [Fact]
    public void Evaluate_returns_Create_when_no_candidates()
    {
        var proposal = MakeProposal();
        var decision = CurationRulesEvaluator.Evaluate(proposal, []);

        Assert.Equal(CurationDecisionKind.Create, decision.Kind);
    }

    // ── Immutable records always create ─────────────────────────────

    [Fact]
    public void Evaluate_returns_Create_for_immutable_record()
    {
        var proposal = MakeProposal(kind: "record", updateSemantics: "immutable-record");
        var candidates = new[] { MakeCandidate() };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(CurationDecisionKind.Create, decision.Kind);
        Assert.Contains("immutable record", decision.Reason);
    }

    // ── Exact match + high overlap -> Skip ──────────────────────────

    [Fact]
    public void Evaluate_returns_Skip_for_exact_anchor_with_high_content_overlap()
    {
        var proposal = MakeProposal(content: "favorite color is blue");
        var candidates = new[]
        {
            MakeCandidate(content: "favorite color is blue", isExact: true)
        };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(CurationDecisionKind.Skip, decision.Kind);
        Assert.Contains("high content overlap", decision.Reason);
    }

    // ── Exact match + different content + newer -> Update ───────────

    [Fact]
    public void Evaluate_returns_Update_for_exact_anchor_with_fresher_content()
    {
        var proposal = MakeProposal(
            content: "latest version is 1.5.62",
            freshnessAtMs: 2000);
        var candidates = new[]
        {
            MakeCandidate(
                content: "latest version is 1.5.60",
                freshnessAtMs: 1000,
                isExact: true)
        };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Equal("doc-123", decision.TargetDocumentId);
    }

    // ── Exact match + different content + older -> Skip ─────────────

    [Fact]
    public void Evaluate_returns_Skip_for_exact_anchor_with_stale_proposal()
    {
        var proposal = MakeProposal(
            content: "latest version is 1.5.58",
            freshnessAtMs: 500);
        var candidates = new[]
        {
            MakeCandidate(
                content: "latest version is 1.5.62",
                freshnessAtMs: 2000,
                isExact: true)
        };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(CurationDecisionKind.Skip, decision.Kind);
        Assert.Contains("stale proposal", decision.Reason);
    }

    // ── Fuzzy match + high overlap -> Consolidate ───────────────────

    [Fact]
    public void Evaluate_returns_Consolidate_for_fuzzy_match_with_high_overlap()
    {
        var proposal = MakeProposal(
            anchor: "akka-net-release",
            content: "Akka.NET latest release version is 1.5.62");
        var candidates = new[]
        {
            MakeCandidate(
                docId: "doc-456",
                anchorId: "anchor:akka-net-latest-release",
                anchorName: "akka-net-latest-release",
                content: "Akka.NET latest release version is 1.5.62",
                isExact: false)
        };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(CurationDecisionKind.Consolidate, decision.Kind);
        Assert.NotNull(decision.ConsolidationTargetIds);
        Assert.Contains("doc-456", decision.ConsolidationTargetIds);
        // Best match doubles as the primary write target for the collapse write.
        Assert.Equal("doc-456", decision.TargetDocumentId);
    }

    // ── Fuzzy match + ambiguous overlap -> Ambiguous ────────────────

    [Fact]
    public void Evaluate_returns_Ambiguous_for_fuzzy_match_with_middling_overlap()
    {
        // Content shares ~50% of tokens — in the 40-80% gray zone
        // Shared tokens: akka, net, release, version, support (5)
        // Unique to proposal: 1.5.62, march, clustering, improvements (4)
        // Unique to existing: 1.5.60, initial, sharding, stable (4)
        // Jaccard = 5/13 ~ 0.38 ... need more overlap
        // Let's craft content that hits 40-80%:
        var proposal = MakeProposal(
            anchor: "akka-net-release",
            content: "Akka.NET release version 1.5.62 added cluster sharding improvements and new persistence features for production workloads");
        var candidates = new[]
        {
            MakeCandidate(
                anchorName: "akka-net-latest-release",
                content: "Akka.NET release version 1.5.60 added cluster sharding support and initial persistence features for testing environments",
                isExact: false)
        };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(CurationDecisionKind.Ambiguous, decision.Kind);
    }

    // ── Fuzzy match + low overlap -> Create ─────────────────────────

    [Fact]
    public void Evaluate_returns_Create_for_fuzzy_match_with_low_overlap()
    {
        var proposal = MakeProposal(
            anchor: "akka-net-release",
            content: "The CI pipeline deploys to staging on every PR merge");
        var candidates = new[]
        {
            MakeCandidate(
                anchorName: "akka-net-latest-release",
                content: "PostgreSQL 15 is the primary datastore for user profiles and session history",
                isExact: false)
        };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(CurationDecisionKind.Create, decision.Kind);
    }

    // ── Multiple exact matches: picks most recent ───────────────────

    [Fact]
    public void Evaluate_prefers_most_recent_exact_match()
    {
        var proposal = MakeProposal(
            content: "latest version is 1.5.62",
            freshnessAtMs: 3000);

        var candidates = new[]
        {
            MakeCandidate(
                docId: "doc-old",
                content: "latest version is 1.5.58",
                freshnessAtMs: 500,
                isExact: true),
            MakeCandidate(
                docId: "doc-recent",
                content: "latest version is 1.5.60",
                freshnessAtMs: 2000,
                isExact: true)
        };

        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        // Should compare against doc-recent (most recent) and decide Update
        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Equal("doc-recent", decision.TargetDocumentId);
    }

    // ── TryAutoResolveAmbiguous ─────────────────────────────────────

    [Fact]
    public void TryAutoResolveAmbiguous_returns_Skip_when_high_overlap_and_anchor_similarity()
    {
        // Content overlap > 60% and anchor Jaccard > 50%
        var proposal = MakeProposal(
            anchor: "netclaw-github-repo",
            content: "Netclaw GitHub repository at https://github.com/netclaw-dev/netclaw, private repo");
        var candidates = new[]
        {
            MakeCandidate(
                anchorName: "netclaw-github-repository",
                content: "Netclaw GitHub repository: https://github.com/netclaw-dev/netclaw. The repository is private.",
                isExact: false)
        };

        var decision = CurationRulesEvaluator.TryAutoResolveAmbiguous(proposal, candidates);

        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Skip, decision.Kind);
        Assert.Contains("auto-resolved", decision.Reason);
    }

    [Fact]
    public void TryAutoResolveAmbiguous_returns_null_when_content_overlap_below_threshold()
    {
        // High anchor similarity but low content overlap
        var proposal = MakeProposal(
            anchor: "akka-net-release",
            content: "The CI pipeline deploys to staging on every merge");
        var candidates = new[]
        {
            MakeCandidate(
                anchorName: "akka-net-latest-release",
                content: "PostgreSQL 15 is the primary datastore for user profiles",
                isExact: false)
        };

        var decision = CurationRulesEvaluator.TryAutoResolveAmbiguous(proposal, candidates);

        Assert.Null(decision);
    }

    [Fact]
    public void TryAutoResolveAmbiguous_returns_null_when_anchor_jaccard_below_threshold()
    {
        // High content overlap but very different anchor names
        var proposal = MakeProposal(
            anchor: "snake-game-location",
            content: "The project is at /home/user/projects/snakey-trail with HTML and CSS");
        var candidates = new[]
        {
            MakeCandidate(
                anchorName: "deployment-url-config",
                content: "The project is at /home/user/projects/snakey-trail with HTML and JavaScript",
                isExact: false)
        };

        var decision = CurationRulesEvaluator.TryAutoResolveAmbiguous(proposal, candidates);

        Assert.Null(decision);
    }

    [Fact]
    public void TryAutoResolveAmbiguous_returns_null_for_empty_candidates()
    {
        var proposal = MakeProposal();
        var decision = CurationRulesEvaluator.TryAutoResolveAmbiguous(proposal, []);

        Assert.Null(decision);
    }

    // ── GuardDestructiveUpdate (lossless update guard) ──────────────

    [Fact]
    public void GuardDestructiveUpdate_downgrades_to_skip_when_proposal_drops_existing_content()
    {
        // Existing memory is rich; proposal is narrower and would clobber it.
        var target = MakeCandidate(docId: "doc-rich",
            content: "Widget specs: 16 cores, 64GB RAM, 2 NICs. Pricing on file. Vendor contacts listed.");
        var proposal = MakeProposal(content: "Widget pricing is TBD as of Q2.");
        var update = new CurationDecision(CurationDecisionKind.Update, "doc-rich", null, null, "rules: newer");

        var guarded = CurationRulesEvaluator.GuardDestructiveUpdate(update, proposal, [target]);

        Assert.Equal(CurationDecisionKind.Skip, guarded.Kind);
        Assert.Equal("doc-rich", guarded.TargetDocumentId);
    }

    [Fact]
    public void GuardDestructiveUpdate_allows_update_when_proposal_is_a_superset()
    {
        var target = MakeCandidate(docId: "doc-1", content: "Latest version is 1.5.62.");
        var proposal = MakeProposal(content: "Latest version is 1.5.62. Released with the new serializer.");
        var update = new CurationDecision(CurationDecisionKind.Update, "doc-1", null, null, "rules: newer");

        var guarded = CurationRulesEvaluator.GuardDestructiveUpdate(update, proposal, [target]);

        Assert.Equal(CurationDecisionKind.Update, guarded.Kind);
    }

    [Fact]
    public void GuardDestructiveUpdate_ignores_whitespace_and_case_when_checking_preservation()
    {
        var target = MakeCandidate(docId: "doc-1", content: "Config   path:\n/etc/app/config");
        var proposal = MakeProposal(content: "config path: /etc/app/config and it is read-only.");
        var update = new CurationDecision(CurationDecisionKind.Update, "doc-1", null, null, "rules: newer");

        var guarded = CurationRulesEvaluator.GuardDestructiveUpdate(update, proposal, [target]);

        Assert.Equal(CurationDecisionKind.Update, guarded.Kind);
    }

    [Fact]
    public void GuardDestructiveUpdate_passes_non_update_decisions_through_unchanged()
    {
        var proposal = MakeProposal();
        var target = MakeCandidate();
        foreach (var kind in new[] { CurationDecisionKind.Create, CurationDecisionKind.Skip, CurationDecisionKind.Consolidate })
        {
            var decision = new CurationDecision(kind, target.DocumentId, null, null, "test");
            Assert.Equal(kind, CurationRulesEvaluator.GuardDestructiveUpdate(decision, proposal, [target]).Kind);
        }
    }

    [Fact]
    public void GuardDestructiveUpdate_leaves_update_unchanged_when_target_not_in_candidates()
    {
        var proposal = MakeProposal(content: "anything");
        var update = new CurationDecision(CurationDecisionKind.Update, "doc-missing", null, null, "test");

        var guarded = CurationRulesEvaluator.GuardDestructiveUpdate(update, proposal, [MakeCandidate(docId: "doc-other")]);

        Assert.Equal(CurationDecisionKind.Update, guarded.Kind);
    }
}
