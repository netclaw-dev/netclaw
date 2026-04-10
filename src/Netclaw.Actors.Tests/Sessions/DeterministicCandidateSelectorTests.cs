using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class DeterministicCandidateSelectorTests
{
    private static DeterministicRetrievalRequestPlan MakePlan(
        IReadOnlyList<string>? lexicalTerms = null,
        IReadOnlyList<string>? anchorHints = null,
        IReadOnlyList<string>? facets = null,
        IReadOnlyList<string>? softScopes = null) => new(
        SoftScopes: softScopes ?? [],
        RetrievalMode: DeterministicRetrievalMode.Ranked,
        LexicalTerms: lexicalTerms ?? [],
        Facets: facets ?? [],
        AnchorHints: anchorHints ?? [],
        CandidateLimit: 30,
        AllowedMemoryClasses: [MemoryClass.DurableFact.ToWireValue(), MemoryClass.Evidence.ToWireValue()],
        ExcludedSensitivity: [MemorySensitivity.Secret.ToWireValue()],
        ExcludeExpired: true);

    private static SQLiteMemoryHydratedItem MakeItem(
        string id,
        string title,
        string content,
        string memoryClass = "durable_fact") => new(
        Id: id,
        Kind: "document",
        MemoryClass: memoryClass,
        Title: title,
        Content: content,
        AliasesJson: null,
        FacetsJson: null,
        SlotsJson: null,
        Boundary: "boundary:trusted-instance",
        Audience: "public",
        Sensitivity: "normal",
        RecallMode: "auto",
        UpdateSemantics: "merge-document",
        ExpiresAtMs: null,
        UpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Fact]
    public void Candidate_with_no_feature_match_is_rejected()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(lexicalTerms: ["session"]);
        var item = MakeItem("doc-1", "User Identity Profile", "Aaron runs Petabridge.");

        var result = selector.Select(plan, [item]);

        Assert.Empty(result);
    }

    [Fact]
    public void Candidate_with_single_lexical_match_survives_threshold()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(lexicalTerms: ["petabridge"]);
        var item = MakeItem("doc-1", "Company Profile", "Petabridge builds Akka.NET.");

        var result = selector.Select(plan, [item]);

        Assert.Single(result);
    }

    [Fact]
    public void Baseline_only_candidates_excluded_from_scored_results()
    {
        var selector = new DeterministicCandidateSelector();
        // Use a lexical term that matches document text exactly (no plural
        // normalization mismatch). Token "docker" is unaffected by the
        // tokenizer's plural rules.
        var plan = MakePlan(lexicalTerms: ["docker"]);
        var noise = MakeItem("doc-noise", "Unrelated", "Something about databases.");
        var relevant = MakeItem("doc-relevant", "Docker Guide", "Docker container deployment notes.");

        var result = selector.SelectWithScores(plan, [noise, relevant]);

        Assert.Single(result);
        Assert.Equal("doc-relevant", result[0].Item.Id);
        Assert.True(result[0].SelectorScore >= 2.0);
    }

    [Fact]
    public void Evidence_class_candidates_are_selected()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(lexicalTerms: ["reelfarm"]);

        var evidence = MakeItem("doc-evidence", "Reel.Farm Research", "ReelFarm costs $39/mo.", memoryClass: "evidence");

        var result = selector.Select(plan, [evidence]);

        Assert.Single(result);
    }

    // Score geometry documentation. These tests document the gradient a
    // downstream composite-score floor will see: weaker matches score lower
    // than stronger matches, and the spread between "single feature hit" and
    // "multi-feature hit" is large enough to be a useful discriminator.
    //
    // Note: TextTokenizer normalizes plurals ("streams" -> "stream") and
    // treats hyphenated words as single tokens. Lexical terms here are kept
    // in normalized singular form so they match what the tokenizer produces.
    // In production, the planner also runs prompts through TextTokenizer so
    // plan.LexicalTerms is consistent with document tokens by construction.

    [Fact]
    public void Score_geometry_stronger_matches_outrank_weaker_matches()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(
            lexicalTerms: ["akka", "stream", "backpressure", "demand"]);

        var weak = MakeItem(
            "doc-weak",
            "Unrelated Guide",
            "This note mentions akka once, nothing else.");
        var medium = MakeItem(
            "doc-medium",
            "Akka Stream Overview",
            "Akka stream uses demand signalling.");
        var strong = MakeItem(
            "doc-strong",
            "Akka Stream Backpressure",
            "Demand backpressure flow control in akka stream.");

        var result = selector.SelectWithScores(plan, [weak, medium, strong]);

        Assert.Equal(3, result.Count);
        // Results are returned in descending order of SelectorScore.
        Assert.Equal("doc-strong", result[0].Item.Id);
        Assert.Equal("doc-medium", result[1].Item.Id);
        Assert.Equal("doc-weak", result[2].Item.Id);

        // Document the spread: strongest match should be at least 2x the
        // weakest. If this ever drops below that, the composite floor loses
        // its ability to discriminate and we need to rebalance.
        Assert.True(
            result[0].SelectorScore >= result[2].SelectorScore * 2,
            $"Expected strong/weak spread of 2x+, got {result[0].SelectorScore}/{result[2].SelectorScore}");
    }

    [Fact]
    public void Score_geometry_facet_match_adds_meaningful_weight()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(
            lexicalTerms: ["stream"],
            facets: ["akka-streams"]);

        var withoutFacet = MakeItem(
            "doc-no-facet",
            "Akka Stream",
            "Backpressure in akka stream.");
        var withFacet = new SQLiteMemoryHydratedItem(
            Id: "doc-with-facet",
            Kind: "document",
            MemoryClass: "durable_fact",
            Title: "Akka Stream",
            Content: "Backpressure in akka stream.",
            AliasesJson: null,
            FacetsJson: "[\"akka-streams\"]",
            SlotsJson: null,
            Boundary: "boundary:trusted-instance",
            Audience: "public",
            Sensitivity: "normal",
            RecallMode: "auto",
            UpdateSemantics: "merge-document",
            ExpiresAtMs: null,
            UpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var result = selector.SelectWithScores(plan, [withoutFacet, withFacet]);

        Assert.Equal(2, result.Count);
        Assert.Equal("doc-with-facet", result[0].Item.Id);
        Assert.True(
            result[0].SelectorScore - result[1].SelectorScore >= 5.0,
            $"Facet match should add at least 5 points; got delta {result[0].SelectorScore - result[1].SelectorScore}");
    }

    [Fact]
    public void Score_geometry_anchor_match_adds_meaningful_weight()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(
            lexicalTerms: ["stream"],
            anchorHints: ["Akka Stream Backpressure"]);

        var noAnchor = MakeItem(
            "doc-no-anchor",
            "Something Else",
            "Akka stream is useful.");
        var withAnchor = MakeItem(
            "doc-with-anchor",
            "Akka Stream Backpressure",
            "Demand in akka stream.");

        var result = selector.SelectWithScores(plan, [noAnchor, withAnchor]);

        Assert.Equal(2, result.Count);
        Assert.Equal("doc-with-anchor", result[0].Item.Id);
        Assert.True(
            result[0].SelectorScore - result[1].SelectorScore >= 7.0,
            $"Anchor match should add at least 7 points; got delta {result[0].SelectorScore - result[1].SelectorScore}");
    }

}
