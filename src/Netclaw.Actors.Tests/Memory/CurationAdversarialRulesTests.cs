// -----------------------------------------------------------------------
// <copyright file="CurationAdversarialRulesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class CurationAdversarialRulesTests
{
    internal const string CanonicalName = "synthetic-widget-manual";
    internal const string DocumentId = "doc-synthetic-widget";
    internal const string OriginalBody =
        "Synthetic widget specifications: 16 cores, 64GB RAM. Documentation: https://example.invalid/widget. " +
        "Store spare cables inside the blue cabinet.";
    internal const string ProposalBody =
        "synthetic-widget-manual Consult https://example.invalid/widget: this device provides 16 processors plus 64GB memory. " +
        "Extra leads belong within the azure cupboard.";

    [Theory]
    [InlineData(CanonicalName, false, CurationDecisionKind.Ambiguous)]
    [InlineData(" SYNTHETIC-WIDGET-MANUAL ", false, CurationDecisionKind.Ambiguous)]
    [InlineData(CanonicalName, true, CurationDecisionKind.Ambiguous)]
    [InlineData("synthetic widget manual", true, CurationDecisionKind.Ambiguous)]
    [InlineData("synthetic-cabinet-notes", false, CurationDecisionKind.Create)]
    [InlineData("synthetic-cabinet-notes", true, CurationDecisionKind.Consolidate)]
    public void Rejected_same_name_candidate_requires_its_own_resolution(
        string candidateName, bool hasHigherConfidenceDuplicate, CurationDecisionKind expected)
    {
        var proposal = RewriteProposal(CanonicalName);
        var rejected = new ExistingMemoryCandidate(DocumentId, MemoryTypedId.AnchorId(candidateName),
            candidateName, OriginalBody, 1, 0.8, IsExactAnchorMatch: false);
        var candidates = new List<ExistingMemoryCandidate> { rejected };
        if (hasHigherConfidenceDuplicate)
        {
            candidates.Add(new ExistingMemoryCandidate("doc-synthetic-sibling", "anchor:synthetic-sibling",
                "synthetic-sibling", ProposalBody, 1, 0.99, IsExactAnchorMatch: false));
        }

        Assert.True(AnchorNameMatcher.ComputeContentOverlap(proposal.Content, rejected.Content) < 0.4);
        var decision = CurationRulesEvaluator.Evaluate(proposal, candidates);

        Assert.Equal(expected, decision.Kind);
        if (expected == CurationDecisionKind.Ambiguous)
        {
            Assert.Contains(DocumentId, decision.ConsolidationTargetIds!);
            Assert.Equal(MemoryTypedId.AnchorId(CanonicalName), MemoryTypedId.AnchorId(decision.CanonicalAnchorName!));
        }
        if (expected == CurationDecisionKind.Consolidate)
            Assert.Equal("doc-synthetic-sibling", decision.TargetDocumentId);
    }

    [Theory]
    [InlineData("CREATE_EXTRA")]
    [InlineData("SKIPPER")]
    [InlineData("UPDATE_EXTRA doc-synthetic-widget")]
    [InlineData("CONSOLIDATE_EXTRA doc-synthetic-widget")]
    public void Keyword_prefix_is_not_a_decision(string response)
        => Assert.Null(CurationPromptBuilder.ParseResponse(response));

    internal static SQLiteMemoryCurationOperation RewriteProposal(string anchor) => new(
        Kind: "document", MemoryClass: "durable_fact", MemoryId: null,
        AnchorCanonicalName: anchor, AnchorType: "concept", Title: "Synthetic widget manual",
        Content: ProposalBody, AliasesJson: null, FacetsJson: null, SlotsJson: null, Relations: null,
        UpdateSemantics: "merge-document", Boundary: TrustBoundary.TrustedInstanceValue,
        Audience: TrustAudience.Public, Sensitivity: "normal", RecallMode: "auto", Confidence: 0.9,
        FreshnessAtMs: 2, ExpiresAtMs: null);
}
