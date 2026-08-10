// -----------------------------------------------------------------------
// <copyright file="RetrievalPrototypeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.MemoryRetrievalPoC.Tests.Prototype;
using System.Text;
using Xunit;

namespace Netclaw.MemoryRetrievalPoC.Tests;

public sealed class RetrievalPrototypeTests : IDisposable
{
    private readonly RetrievalFixture _fixture = RetrievalFixture.Load();
    private readonly PrototypeSqliteStore _store = new();

    [Fact]
    public async Task Deterministic_retrieval_matches_expected_hits_and_no_hits()
    {
        await _store.InitializeAndSeedAsync(_fixture, TestContext.Current.CancellationToken);

        var documents = await _store.LoadDocumentsAsync("project:signalr", TestContext.Current.CancellationToken);
        var edges = await _store.LoadEdgesAsync("project:signalr", TestContext.Current.CancellationToken);
        var engine = new DeterministicRecallEngine(documents, edges);

        var failures = new List<string>();
        foreach (var testCase in _fixture.Cases)
        {
            var hits = engine.Search(testCase.Prompt, 3);
            var bundle = engine.SearchBundle(testCase.Prompt);

            if (testCase.ExpectEmpty && hits.Count != 0)
            {
                failures.Add($"{testCase.Id}: expected empty but got [{string.Join(", ", hits.Select(x => x.DocumentId + "=" + x.Score.ToString("F1")))}]");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(testCase.ExpectedTopDocumentId))
            {
                var top = hits.FirstOrDefault()?.DocumentId;
                if (!string.Equals(top, testCase.ExpectedTopDocumentId, StringComparison.Ordinal))
                {
                    failures.Add($"{testCase.Id}: expected top {testCase.ExpectedTopDocumentId} but got {top ?? "<none>"}; hits=[{string.Join(", ", hits.Select(x => x.DocumentId + "=" + x.Score.ToString("F1") + "{" + string.Join("|", x.Reasons) + "}"))}]");
                }
            }

            if (testCase.ExpectedContainsDocumentIds is { Count: > 0 })
            {
                foreach (var expected in testCase.ExpectedContainsDocumentIds)
                {
                    if (!hits.Any(x => x.DocumentId == expected))
                        failures.Add($"{testCase.Id}: expected result set to include {expected}; hits=[{string.Join(", ", hits.Select(x => x.DocumentId))}]");
                }
            }

            if (testCase.ForbiddenDocumentIds is { Count: > 0 })
            {
                foreach (var forbidden in testCase.ForbiddenDocumentIds)
                {
                    if (hits.Any(x => x.DocumentId == forbidden))
                        failures.Add($"{testCase.Id}: forbidden hit {forbidden} surfaced");
                }
            }

            if (testCase.ExpectedBundle is { Count: > 0 })
            {
                foreach (var pair in testCase.ExpectedBundle)
                {
                    if (!bundle.Slots.TryGetValue(pair.Key, out var hit))
                    {
                        failures.Add($"{testCase.Id}: expected bundle slot {pair.Key} but it was missing; bundle=[{string.Join(", ", bundle.Slots.Select(x => x.Key + "=" + x.Value.DocumentId))}]");
                        continue;
                    }

                    if (!string.Equals(hit.DocumentId, pair.Value, StringComparison.Ordinal))
                        failures.Add($"{testCase.Id}: expected bundle slot {pair.Key} -> {pair.Value} but got {hit.DocumentId}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Deterministic_retrieval_explains_ranked_hits_bundles_and_neighbors()
    {
        await _store.InitializeAndSeedAsync(_fixture, TestContext.Current.CancellationToken);

        var documents = await _store.LoadDocumentsAsync("project:signalr", TestContext.Current.CancellationToken);
        var edges = await _store.LoadEdgesAsync("project:signalr", TestContext.Current.CancellationToken);
        var engine = new DeterministicRecallEngine(documents, edges);

        var sb = new StringBuilder();
        foreach (var testCase in _fixture.Cases)
        {
            var explanation = engine.Explain(testCase.Prompt, 4);
            sb.AppendLine($"CASE {testCase.Id}");
            sb.AppendLine($"PROMPT {explanation.Prompt}");
            sb.AppendLine($"FACETS [{string.Join(", ", explanation.Facets)}]");
            sb.AppendLine("RANKED");
            foreach (var hit in explanation.RankedHits)
                sb.AppendLine($"- {hit.DocumentId} score={hit.Score:F1} facets=[{string.Join(", ", hit.Facets)}] slots=[{string.Join(", ", hit.Slots)}] reasons=[{string.Join(", ", hit.Reasons)}]");
            sb.AppendLine($"BUNDLE [{string.Join(", ", explanation.BundleSlots.Select(x => x.Key + "=" + x.Value))}]");
            sb.AppendLine("NEIGHBORS");
            foreach (var pair in explanation.InferredNeighbors)
                sb.AppendLine($"- {pair.Key}: [{string.Join(", ", pair.Value)}]");
            sb.AppendLine();
        }

        Assert.Contains("CASE stirtrek-flight-hotel-combo", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("preferred_airline=doc-travel-airline", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("facet:travel_profile", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scope_request_planner_builds_reasonable_hard_and_soft_scopes()
    {
        await _store.InitializeAndSeedAsync(_fixture, TestContext.Current.CancellationToken);

        var documents = await _store.LoadDocumentsAsync("project:signalr", TestContext.Current.CancellationToken);
        var userDocuments = await _store.LoadDocumentsAsync("user:aaron", TestContext.Current.CancellationToken);
        var allDocuments = documents.Concat(userDocuments).ToArray();
        var edges = await _store.LoadEdgesAsync("project:signalr", TestContext.Current.CancellationToken);
        var planner = new ScopeRequestPlanner(allDocuments, edges);

        var dmTravel = planner.Plan(new QueryContext(
            Surface: "slack_dm",
            Prompt: "I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me?",
            UserDomain: "user:aaron",
            ChannelDomain: null,
            ThreadTitle: "Stir Trek 2026 travel planning"));

        Assert.Equal("user:aaron", dmTravel.HardScope);
        Assert.Equal("bundle", dmTravel.RetrievalMode);
        Assert.Contains("travel_profile", dmTravel.Facets);
        Assert.Contains("trip_planning", dmTravel.Facets);
        Assert.Contains(dmTravel.SoftScopes, x => x.Contains("Stir Trek", StringComparison.OrdinalIgnoreCase) || x.Contains("stirtrek", StringComparison.OrdinalIgnoreCase));

        var dmTextForge = planner.Plan(new QueryContext(
            Surface: "slack_dm",
            Prompt: "What's the pricing model for TextForge?",
            UserDomain: "user:aaron",
            ChannelDomain: null,
            ThreadTitle: "Product planning"));

        Assert.Equal("user:aaron", dmTextForge.HardScope);
        Assert.Contains(dmTextForge.SoftScopes, x => x.Contains("textforge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("project_fact", dmTextForge.Facets);

        var opsChannel = planner.Plan(new QueryContext(
            Surface: "slack_channel",
            Prompt: "The queue is piling up again. What did we do last time to get backlog under control?",
            UserDomain: "user:aaron",
            ChannelDomain: "project:signalr",
            ThreadTitle: "worker-b alerts"));

        Assert.Equal("project:signalr", opsChannel.HardScope);
        Assert.Equal("ranked", opsChannel.RetrievalMode);
        Assert.Contains("incident_recovery", opsChannel.Facets);
        Assert.Contains(opsChannel.SoftScopes, x => x.Contains("worker-b", StringComparison.OrdinalIgnoreCase) || x.Contains("ops", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Candidate_selector_filters_corpus_before_reranking()
    {
        await _store.InitializeAndSeedAsync(_fixture, TestContext.Current.CancellationToken);

        var signalrDocuments = await _store.LoadDocumentsAsync("project:signalr", TestContext.Current.CancellationToken);
        var userDocuments = await _store.LoadDocumentsAsync("user:aaron", TestContext.Current.CancellationToken);
        var allDocuments = signalrDocuments.Concat(userDocuments).ToArray();
        var signalrEdges = await _store.LoadEdgesAsync("project:signalr", TestContext.Current.CancellationToken);
        var userEdges = await _store.LoadEdgesAsync("user:aaron", TestContext.Current.CancellationToken);
        var allEdges = signalrEdges.Concat(userEdges).ToArray();
        var planner = new ScopeRequestPlanner(allDocuments, allEdges);
        var selector = new CandidateSelector();

        var dmTextForge = planner.Plan(new QueryContext(
            Surface: "slack_dm",
            Prompt: "What's the pricing model for TextForge?",
            UserDomain: "user:aaron",
            ChannelDomain: null,
            ThreadTitle: "Product planning"));

        var dmCandidates = selector.Select(dmTextForge, userDocuments);
        Assert.Contains(dmCandidates, x => x.DocumentId == "doc-textforge-pricing");
        Assert.DoesNotContain(dmCandidates, x => x.DocumentId == "doc-travel-origin");

        var opsPlan = planner.Plan(new QueryContext(
            Surface: "slack_channel",
            Prompt: "The queue is piling up again. What did we do last time to get backlog under control?",
            UserDomain: "user:aaron",
            ChannelDomain: "project:signalr",
            ThreadTitle: "worker-b alerts"));

        var opsCandidates = selector.Select(opsPlan, signalrDocuments);
        Assert.Contains(opsCandidates, x => x.DocumentId == "doc-beta-recovery");
        Assert.Contains(opsCandidates, x => x.DocumentId == "doc-beta-dashboard");
        Assert.DoesNotContain(opsCandidates, x => x.DocumentId == "doc-secret-token");
    }

    [Fact]
    public async Task End_to_end_trace_shows_plan_candidates_ranked_hits_and_bundle_for_stirtrek_trip()
    {
        await _store.InitializeAndSeedAsync(_fixture, TestContext.Current.CancellationToken);

        var signalrDocuments = await _store.LoadDocumentsAsync("project:signalr", TestContext.Current.CancellationToken);
        var userDocuments = await _store.LoadDocumentsAsync("user:aaron", TestContext.Current.CancellationToken);
        var allDocuments = signalrDocuments.Concat(userDocuments).ToArray();
        var signalrEdges = await _store.LoadEdgesAsync("project:signalr", TestContext.Current.CancellationToken);
        var userEdges = await _store.LoadEdgesAsync("user:aaron", TestContext.Current.CancellationToken);
        var allEdges = signalrEdges.Concat(userEdges).ToArray();

        var planner = new ScopeRequestPlanner(allDocuments, allEdges);
        var selector = new CandidateSelector();

        const string prompt = "I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me? Closest to the venue preferably. And do you think I'll need a rental car?";
        var plan = planner.Plan(new QueryContext(
            Surface: "slack_dm",
            Prompt: prompt,
            UserDomain: "user:aaron",
            ChannelDomain: null,
            ThreadTitle: "Stir Trek 2026 travel planning"));

        var candidates = selector.Select(plan, allDocuments);
        var candidateEdges = allEdges.Where(e => candidates.Any(d => d.AnchorId == e.FromAnchorId || d.AnchorId == e.ToAnchorId)).ToArray();
        var engine = new DeterministicRecallEngine(candidates, candidateEdges);
        var ranked = engine.Search(prompt, 4);
        var bundle = engine.SearchBundle(prompt);
        var explanation = engine.Explain(prompt, 4);

        var sb = new StringBuilder();
        sb.AppendLine($"HARD_SCOPE {plan.HardScope}");
        sb.AppendLine($"SOFT_SCOPES [{string.Join(", ", plan.SoftScopes)}]");
        sb.AppendLine($"MODE {plan.RetrievalMode}");
        sb.AppendLine($"FACETS [{string.Join(", ", plan.Facets)}]");
        sb.AppendLine($"ANCHOR_HINTS [{string.Join(", ", plan.AnchorHints)}]");
        sb.AppendLine($"CANDIDATES [{string.Join(", ", candidates.Select(x => x.DocumentId))}]");
        sb.AppendLine("RANKED");
        foreach (var hit in ranked)
            sb.AppendLine($"- {hit.DocumentId} score={hit.Score:F1} reasons=[{string.Join(", ", hit.Reasons)}]");
        sb.AppendLine($"BUNDLE [{string.Join(", ", bundle.Slots.Select(x => x.Key + "=" + x.Value.DocumentId))}]");
        sb.AppendLine("EXPLAIN_FACETS");
        sb.AppendLine($"- [{string.Join(", ", explanation.Facets)}]");

        Assert.Contains("HARD_SCOPE user:aaron", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("MODE bundle", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("doc-stirtrek-travel-plan", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("preferred_airline=doc-travel-airline", sb.ToString(), StringComparison.Ordinal);
        Assert.Contains("origin_airport=doc-travel-origin", sb.ToString(), StringComparison.Ordinal);
    }
}
