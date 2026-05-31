// -----------------------------------------------------------------------
// <copyright file="CurationPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class CurationPromptBuilderTests
{
    // ── ParseResponse ───────────────────────────────────────────────

    [Fact]
    public void ParseResponse_parses_SKIP()
    {
        var decision = CurationPromptBuilder.ParseResponse("SKIP");
        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Skip, decision.Kind);
    }

    [Fact]
    public void ParseResponse_parses_CREATE()
    {
        var decision = CurationPromptBuilder.ParseResponse("CREATE");
        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Create, decision.Kind);
    }

    [Fact]
    public void ParseResponse_parses_UPDATE_with_id()
    {
        var decision = CurationPromptBuilder.ParseResponse("UPDATE doc-abc123");
        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Equal("doc-abc123", decision.TargetDocumentId);
    }

    [Fact]
    public void ParseResponse_parses_CONSOLIDATE_with_multiple_ids()
    {
        var decision = CurationPromptBuilder.ParseResponse("CONSOLIDATE doc-abc123 doc-def456");
        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Consolidate, decision.Kind);
        Assert.NotNull(decision.ConsolidationTargetIds);
        Assert.Equal(2, decision.ConsolidationTargetIds.Count);
        Assert.Equal("doc-abc123", decision.ConsolidationTargetIds[0]);
        Assert.Equal("doc-def456", decision.ConsolidationTargetIds[1]);
    }

    [Fact]
    public void ParseResponse_handles_case_insensitivity()
    {
        Assert.Equal(CurationDecisionKind.Skip, CurationPromptBuilder.ParseResponse("skip")?.Kind);
        Assert.Equal(CurationDecisionKind.Create, CurationPromptBuilder.ParseResponse("create")?.Kind);
        Assert.Equal(CurationDecisionKind.Update, CurationPromptBuilder.ParseResponse("update doc-1")?.Kind);
    }

    [Fact]
    public void ParseResponse_handles_whitespace()
    {
        Assert.Equal(CurationDecisionKind.Skip, CurationPromptBuilder.ParseResponse("  SKIP  ")?.Kind);
        Assert.Equal(CurationDecisionKind.Create, CurationPromptBuilder.ParseResponse("\nCREATE\n")?.Kind);
    }

    [Fact]
    public void ParseResponse_returns_null_for_empty_or_invalid()
    {
        Assert.Null(CurationPromptBuilder.ParseResponse(""));
        Assert.Null(CurationPromptBuilder.ParseResponse("   "));
        Assert.Null(CurationPromptBuilder.ParseResponse("UNKNOWN_COMMAND"));
    }

    [Fact]
    public void ParseResponse_strips_think_block_before_keyword()
    {
        // Reasoning models prepend hidden chain-of-thought; the decision must still parse.
        var decision = CurationPromptBuilder.ParseResponse(
            "<think>These look similar but the dates differ, so they're distinct.</think>\nCREATE");
        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Create, decision.Kind);
    }

    [Fact]
    public void ParseResponse_strips_multiline_think_block_with_inline_keyword()
    {
        var decision = CurationPromptBuilder.ParseResponse(
            "<think>\nstep 1\nstep 2\n</think>UPDATE doc-42");
        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Equal("doc-42", decision.TargetDocumentId);
    }

    [Fact]
    public void ParseResponse_returns_null_for_unclosed_think_block()
    {
        // A truncated reasoning trace (budget exhausted mid-think) yields no decision —
        // the caller must treat this as "no decision", not parse it into garbage.
        Assert.Null(CurationPromptBuilder.ParseResponse("<think>reasoning with no closing tag and no answer"));
    }

    // ── BuildUserMessage ────────────────────────────────────────────

    [Fact]
    public void BuildUserMessage_includes_proposal_fields()
    {
        var proposal = new SQLiteMemoryCurationOperation(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: "akka-net-release",
            AnchorType: "concept",
            Title: "Akka.NET Release",
            Content: "Latest version is 1.5.62",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: "merge-document",
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Team,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: 1000,
            ExpiresAtMs: null);

        var message = CurationPromptBuilder.BuildUserMessage(proposal, []);

        Assert.Contains("PROPOSED:", message);
        Assert.Contains("anchor: akka-net-release", message);
        Assert.Contains("title: Akka.NET Release", message);
        Assert.Contains("content: Latest version is 1.5.62", message);
        Assert.Contains("EXISTING CANDIDATES: none", message);
    }

    [Fact]
    public void BuildUserMessage_includes_candidates()
    {
        var proposal = new SQLiteMemoryCurationOperation(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: "test",
            AnchorType: "concept",
            Title: "Test",
            Content: "test content",
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: "merge-document",
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Team,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: 1000,
            ExpiresAtMs: null);

        var candidates = new[]
        {
            new ExistingMemoryCandidate(
                DocumentId: "doc-abc123",
                AnchorId: "anchor:existing",
                AnchorCanonicalName: "existing",
                Content: "existing content",
                FreshnessAtMs: 900,
                Confidence: 0.85,
                IsExactAnchorMatch: false)
        };

        var message = CurationPromptBuilder.BuildUserMessage(proposal, candidates);

        Assert.Contains("EXISTING CANDIDATES:", message);
        Assert.Contains("[1] id=doc-abc123 anchor=existing", message);
        Assert.Contains("content: existing content", message);
    }

    [Fact]
    public void BuildUserMessage_truncates_long_content()
    {
        var longContent = new string('x', 500);
        var proposal = new SQLiteMemoryCurationOperation(
            Kind: "document",
            MemoryClass: "durable_fact",
            MemoryId: null,
            AnchorCanonicalName: "test",
            AnchorType: "concept",
            Title: "Test",
            Content: longContent,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            Relations: null,
            UpdateSemantics: "merge-document",
            Boundary: TrustBoundary.TrustedInstanceValue,
            Audience: TrustAudience.Team,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: 1000,
            ExpiresAtMs: null);

        var message = CurationPromptBuilder.BuildUserMessage(proposal, []);

        // Content should be truncated with "..."
        Assert.Contains("...", message);
        // Full 500-char content should NOT appear
        Assert.DoesNotContain(longContent, message);
    }

    // ── SystemPrompt ────────────────────────────────────────────────

    [Fact]
    public void SystemPrompt_contains_all_decision_keywords()
    {
        var prompt = CurationPromptBuilder.SystemPrompt;
        Assert.Contains("SKIP", prompt);
        Assert.Contains("UPDATE", prompt);
        Assert.Contains("CONSOLIDATE", prompt);
        Assert.Contains("CREATE", prompt);
    }
}
