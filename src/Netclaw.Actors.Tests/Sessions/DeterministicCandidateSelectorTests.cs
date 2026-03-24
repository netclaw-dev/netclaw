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
    public void Candidate_with_no_lexical_overlap_survives_baseline_score()
    {
        var selector = new DeterministicCandidateSelector();
        var plan = MakePlan(lexicalTerms: ["session"]);
        var item = MakeItem("doc-1", "User Identity Profile", "Aaron runs Petabridge.");

        var result = selector.Select(plan, [item]);

        Assert.Single(result);
        Assert.Equal("doc-1", result[0].Id);
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
