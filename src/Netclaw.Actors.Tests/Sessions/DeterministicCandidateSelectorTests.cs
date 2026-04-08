using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class DeterministicCandidateSelectorTests
{
    private static DeterministicRetrievalRequestPlan MakePlan(
        string hardScope = "project:d0ac6ckbk5k",
        IReadOnlyList<string>? lexicalTerms = null,
        IReadOnlyList<string>? anchorHints = null,
        IReadOnlyList<string>? facets = null,
        IReadOnlyList<string>? softScopes = null) => new(
        HardScope: hardScope,
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
        string domain = "project:d0ac6ckbk5k",
        string memoryClass = "durable_fact") => new(
        Id: id,
        Kind: "document",
        MemoryClass: memoryClass,
        Title: title,
        Content: content,
        AliasesJson: null,
        FacetsJson: null,
        SlotsJson: null,
        Domain: domain,
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
        // Cross-domain item with no lexical overlap — baseline score only.
        var item = MakeItem("doc-1", "User Identity Profile", "Aaron runs Petabridge.", domain: "project:other");

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
        var plan = MakePlan(lexicalTerms: ["kubernetes"]);
        // Cross-domain noise item has no lexical or domain match — baseline only.
        var noise = MakeItem("doc-noise", "Unrelated", "Something about databases.", domain: "project:other");
        var relevant = MakeItem("doc-relevant", "K8s Guide", "Deploy to kubernetes cluster.");

        var result = selector.SelectWithScores(plan, [noise, relevant]);

        Assert.Single(result);
        Assert.Equal("doc-relevant", result[0].Item.Id);
        Assert.True(result[0].SelectorScore >= 2.0);
    }

    [Fact]
    public void Same_domain_candidate_ranks_higher_than_cross_domain()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(
            hardScope: "project:d0ac6ckbk5k",
            lexicalTerms: ["petabridge"]);

        var sameDomain = MakeItem("doc-same", "Company: Petabridge", "Petabridge builds Akka.NET.", domain: "project:d0ac6ckbk5k");
        var crossDomain = MakeItem("doc-cross", "Company: Petabridge", "Petabridge builds Akka.NET.", domain: "project:signalr");

        var result = selector.Select(plan, [crossDomain, sameDomain]);

        Assert.Equal(2, result.Count);
        Assert.Equal("doc-same", result[0].Id);
    }

    [Fact]
    public void Cross_domain_candidate_not_excluded()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(
            hardScope: "project:d0ac6ckbk5k",
            lexicalTerms: ["petabridge"]);

        var crossDomain = MakeItem("doc-cross", "Company: Petabridge", "Petabridge builds Akka.NET.", domain: "project:signalr");

        var result = selector.Select(plan, [crossDomain]);

        Assert.Single(result);
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
}
