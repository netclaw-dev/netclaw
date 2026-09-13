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
        // First listed id doubles as the primary write target for the collapse write.
        Assert.Equal("doc-abc123", decision.TargetDocumentId);
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

    // ── ParseResponse: merged-body protocol (memory-core-redesign Slice 3 task 3.2) ──

    [Fact]
    public void ParseResponse_parses_UPDATE_with_merged_body()
    {
        var response = "UPDATE doc-abc123\n---\nConfig path is /etc/app/config.yaml (previously /etc/app/config.json).";

        var decision = CurationPromptBuilder.ParseResponse(response);

        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Equal("doc-abc123", decision.TargetDocumentId);
        Assert.Equal(
            "Config path is /etc/app/config.yaml (previously /etc/app/config.json).",
            decision.MergedBody);
        Assert.True(decision.FromLlmTier);
    }

    [Fact]
    public void ParseResponse_parses_CONSOLIDATE_with_merged_body()
    {
        var response =
            "CONSOLIDATE doc-abc123 doc-def456\n---\n" +
            "Akka.NET GitHub repository: https://github.com/akkadotnet/akka.net.\n" +
            "Latest stable release is 1.5.62 (previously 1.5.60).";

        var decision = CurationPromptBuilder.ParseResponse(response);

        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Consolidate, decision.Kind);
        Assert.Equal(2, decision.ConsolidationTargetIds!.Count);
        Assert.NotNull(decision.MergedBody);
        Assert.Contains("1.5.62", decision.MergedBody);
        Assert.Contains("1.5.60", decision.MergedBody);
        Assert.True(decision.FromLlmTier);
    }

    [Fact]
    public void ParseResponse_UPDATE_keyword_only_still_valid_with_no_body()
    {
        var decision = CurationPromptBuilder.ParseResponse("UPDATE doc-42");

        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Equal("doc-42", decision.TargetDocumentId);
        Assert.Null(decision.MergedBody);
    }

    [Fact]
    public void ParseResponse_CONSOLIDATE_keyword_only_still_valid_with_no_body()
    {
        var decision = CurationPromptBuilder.ParseResponse("CONSOLIDATE doc-1 doc-2");

        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Consolidate, decision.Kind);
        Assert.Equal(2, decision.ConsolidationTargetIds!.Count);
        Assert.Null(decision.MergedBody);
    }

    [Fact]
    public void ParseResponse_UPDATE_with_malformed_empty_body_treats_body_as_absent()
    {
        // Separator present but nothing meaningful follows it (just whitespace).
        var decision = CurationPromptBuilder.ParseResponse("UPDATE doc-42\n---\n   \n  ");

        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Null(decision.MergedBody);
    }

    [Fact]
    public void ParseResponse_SKIP_and_CREATE_never_carry_a_merged_body_even_with_a_separator()
    {
        // SKIP/CREATE are keyword-only per protocol; a stray "---" after them should not be
        // misread as introducing a body for a decision kind that never carries one.
        var skip = CurationPromptBuilder.ParseResponse("SKIP\n---\nirrelevant trailing text");
        var create = CurationPromptBuilder.ParseResponse("CREATE\n---\nirrelevant trailing text");

        Assert.NotNull(skip);
        Assert.Null(skip.MergedBody);
        Assert.NotNull(create);
        Assert.Null(create.MergedBody);
    }

    [Fact]
    public void ParseResponse_strips_think_block_before_parsing_merged_body()
    {
        var response =
            "<think>These are the same fact, worded differently.</think>\n" +
            "UPDATE doc-abc123\n---\nMerged content preserving both sources.";

        var decision = CurationPromptBuilder.ParseResponse(response);

        Assert.NotNull(decision);
        Assert.Equal(CurationDecisionKind.Update, decision.Kind);
        Assert.Equal("Merged content preserving both sources.", decision.MergedBody);
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
        // Preview cap is 700 chars (raised from 200 — the decider needs to see
        // distinguishing detail like dates/readings); exceed it to prove truncation.
        var longContent = new string('x', 1_000);
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

    [Fact]
    public void BuildUserMessage_truncates_candidate_content_by_default()
    {
        // Legacy default (task 3.2): candidates shown as a 700-char preview, same as today,
        // until Stage B passes useFullCandidateContent: true for nominated candidates.
        var longCandidateContent = new string('y', 1_000);
        var proposal = MakeMinimalProposal();
        var candidates = new[] { MakeCandidate(longCandidateContent) };

        var message = CurationPromptBuilder.BuildUserMessage(proposal, candidates);

        Assert.DoesNotContain(longCandidateContent, message);
    }

    [Fact]
    public void BuildUserMessage_shows_full_candidate_content_when_requested()
    {
        var longCandidateContent = new string('y', 1_000);
        var proposal = MakeMinimalProposal();
        var candidates = new[] { MakeCandidate(longCandidateContent) };

        var message = CurationPromptBuilder.BuildUserMessage(proposal, candidates, useFullCandidateContent: true);

        Assert.Contains(longCandidateContent, message);
    }

    private static SQLiteMemoryCurationOperation MakeMinimalProposal() => new(
        Kind: "document",
        MemoryClass: "durable_fact",
        MemoryId: null,
        AnchorCanonicalName: "test",
        AnchorType: "concept",
        Title: "Test",
        Content: "proposal content",
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

    private static ExistingMemoryCandidate MakeCandidate(string content) => new(
        DocumentId: "doc-abc123",
        AnchorId: "anchor:existing",
        AnchorCanonicalName: "existing",
        Content: content,
        FreshnessAtMs: 900,
        Confidence: 0.85,
        IsExactAnchorMatch: false);

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
