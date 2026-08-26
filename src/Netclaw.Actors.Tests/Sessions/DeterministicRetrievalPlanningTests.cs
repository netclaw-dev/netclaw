// -----------------------------------------------------------------------
// <copyright file="DeterministicRetrievalPlanningTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class DeterministicRetrievalPlanningTests
{
    [Fact]
    public void Planner_uses_runtime_hard_scope_and_bundle_mode_for_trip_prompt()
    {
        var planner = new DeterministicRetrievalRequestPlanner();
        var plan = planner.Plan(new AutomaticRecallRequest(
            SessionId: (SessionId)"signalr/thread-1",
            Query: "I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me?",
            RecentUserMessages: ["I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me?"],
            MaxItems: 3,
            ThreadTitle: "Stir Trek 2026 travel planning"));

        Assert.Equal(DeterministicRetrievalMode.Bundle, plan.RetrievalMode);
        Assert.Contains("travel_profile", plan.Facets);
        Assert.Contains("trip_planning", plan.Facets);
        Assert.Contains(plan.SoftScopes, x => x.Contains("Stir Trek", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Planner_prefers_named_entity_soft_scope_for_project_prompt()
    {
        var planner = new DeterministicRetrievalRequestPlanner();
        var plan = planner.Plan(new AutomaticRecallRequest(
            SessionId: (SessionId)"signalr/thread-2",
            Query: "What's the pricing model for TextForge?",
            RecentUserMessages: ["What's the pricing model for TextForge?"],
            MaxItems: 3,
            ThreadTitle: "General DM"));

        Assert.Equal(DeterministicRetrievalMode.Ranked, plan.RetrievalMode);
        Assert.Contains("project_fact", plan.Facets);
        Assert.Contains(plan.AnchorHints, x => x.Contains("TextForge", StringComparison.OrdinalIgnoreCase) || x.Contains("textforge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Coordinator_keeps_stage_empty_when_deterministic_planning_succeeds_but_sidecars_are_disabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-deterministic-planning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SQLiteMemoryStore(Path.Combine(dir, "memory.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var coordinator = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"signalr/thread-3",
            Query: "What's the pricing model for TextForge?",
            RecentUserMessages: ["What's the pricing model for TextForge?"],
            MaxItems: 3,
            ThreadTitle: "General DM"), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Null(result.DegradeStage);
    }

    [Fact]
    public async Task Coordinator_returns_ranked_candidates_from_deterministic_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-deterministic-candidate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SQLiteMemoryStore(Path.Combine(dir, "memory.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = store.CreateDefaultAnchor("textforge-pricing-model");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-textforge-pricing",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "TextForge Pricing Model",
            MarkdownBody: "TextForge uses a monthly subscription with a discounted annual plan.",
            AliasesJson: "[\"textforge\",\"pricing model\"]",
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var coordinator = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"signalr/thread-4",
            Query: "What's the pricing model for TextForge?",
            RecentUserMessages: ["What's the pricing model for TextForge?"],
            MaxItems: 3,
            ThreadTitle: "Product planning"), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Id.Value == "doc-textforge-pricing");
    }

    [Fact]
    public async Task Coordinator_reports_composite_score_used_for_final_ordering()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-deterministic-score-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SQLiteMemoryStore(Path.Combine(dir, "memory.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = store.CreateDefaultAnchor("textforge-pricing-model");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-textforge-pricing-score",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "TextForge Pricing Model",
            MarkdownBody: "TextForge pricing uses a monthly subscription.",
            AliasesJson: "[\"textforge\",\"pricing model\"]",
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var coordinator = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"signalr/thread-score",
            Query: "What's the pricing model for TextForge?",
            RecentUserMessages: ["What's the pricing model for TextForge?"],
            MaxItems: 3,
            ThreadTitle: "Product planning"), TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items, x => x.Id.Value == "doc-textforge-pricing-score");
        Assert.True(item.Score > 4.0, $"Expected composite score to exceed raw lexical score, got {item.Score:F2}");
    }

    [Fact]
    public void Planner_caps_lexical_terms_for_long_messages()
    {
        var planner = new DeterministicRetrievalRequestPlanner();
        var longMessage = "I need to book a flight from Houston to Columbus for the Stir Trek conference " +
            "and I want to know about hotel recommendations near the venue and also " +
            "what restaurants are good for dinner with speakers and attendees and organizers " +
            "because we are planning a group outing after the keynote sessions conclude";

        var plan = planner.Plan(new AutomaticRecallRequest(
            SessionId: (SessionId)"signalr/thread-long",
            Query: longMessage,
            RecentUserMessages: [longMessage],
            MaxItems: 3));

        Assert.True(plan.LexicalTerms.Count <= 12,
            $"Expected at most 12 lexical terms but got {plan.LexicalTerms.Count}: [{string.Join(", ", plan.LexicalTerms)}]");
    }

    [Fact]
    public void Planner_includes_evidence_in_allowed_memory_classes()
    {
        var planner = new DeterministicRetrievalRequestPlanner();
        var plan = planner.Plan(new AutomaticRecallRequest(
            SessionId: (SessionId)"D0AC6CKBK5K/1774371415.126439",
            Query: "what did we find about Reel.Farm?",
            RecentUserMessages: ["what did we find about Reel.Farm?"],
            MaxItems: 3));

        Assert.Contains(MemoryClass.DurableFact.ToWireValue(), plan.AllowedMemoryClasses);
        Assert.Contains(MemoryClass.Evidence.ToWireValue(), plan.AllowedMemoryClasses);
    }

    [Fact]
    public async Task Coordinator_recalls_evidence_class_memories()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-evidence-recall-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SQLiteMemoryStore(Path.Combine(dir, "memory.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = store.CreateDefaultAnchor("reelfarm-research");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-reelfarm-research",
            Anchor: anchor,
            MemoryClass: "evidence",
            Title: "Reel.Farm Marketing Tool Research",
            MarkdownBody: "Reel.Farm costs $39/mo and generates AI-powered short-form videos for TikTok and Instagram Reels.",
            AliasesJson: "[\"reelfarm\",\"reel farm\",\"marketing automation\"]",
            FacetsJson: "[\"project_artifact\",\"marketing_tools\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "searchable",
            Confidence: 0.85,
            FreshnessAtMs: now,
            ExpiresAtMs: now + 2_592_000_000,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        // This test pins the CLASS plumbing (evidence memories can flow through
        // the coordinator), not floor policy. Under default scoring, evidence
        // carries a small class prior (+0.4 composite vs durable_fact's +4.8),
        // so a two-term match sits below the default floor by design — loosen
        // the floor explicitly to observe the plumbing.
        var coordinator = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning
            {
                DeterministicRetrievalEnabled = true,
                MinimumRecallCompositeScore = 10.0
            });

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"D0AC6CKBK5K/1774371415.126439",
            Query: "what did we find about Reel.Farm?",
            RecentUserMessages: ["what did we find about Reel.Farm?"],
            MaxItems: 3), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Id.Value == "doc-reelfarm-research");
    }

    [Fact]
    public async Task Coordinator_enforces_recall_char_budget_dropping_whole_items()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-char-budget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SQLiteMemoryStore(Path.Combine(dir, "memory.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = store.CreateDefaultAnchor("budget-fixture");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Three durable facts that all match the query strongly; each body is
        // ~600 chars so two of them exceed a 700-char budget.
        for (var i = 1; i <= 3; i++)
        {
            await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
                DocumentId: $"doc-budget-{i}",
                Anchor: anchor,
                MemoryClass: "durable_fact",
                Title: $"Grafana dashboard convention {i}",
                MarkdownBody: "Grafana dashboard provisioning convention. " + new string('x', 560),
                AliasesJson: null,
                FacetsJson: null,
                SlotsJson: null,
                UpdateSemantics: "merge-document",
                Sensitivity: "normal",
                RecallMode: "auto",
                Confidence: 0.9,
                FreshnessAtMs: now,
                ExpiresAtMs: null,
                CreatedAtMs: now,
                UpdatedAtMs: now), TestContext.Current.CancellationToken);
        }

        var request = new AutomaticRecallRequest(
            SessionId: (SessionId)"D0AC6CKBK5K/1774371415.126439",
            Query: "what is our grafana dashboard provisioning convention?",
            RecentUserMessages: ["what is our grafana dashboard provisioning convention?"],
            MaxItems: 3);

        var budgeted = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { MaxRecallInjectedChars = 700 });
        var budgetedResult = await budgeted.RecallAsync(request, TestContext.Current.CancellationToken);

        // First-ranked item is always admitted; the rest exceed the budget and
        // are dropped whole (never truncated).
        var single = Assert.Single(budgetedResult.Items);
        Assert.EndsWith("x", single.Content);

        var unbounded = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { MaxRecallInjectedChars = 0 });
        var unboundedResult = await unbounded.RecallAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(3, unboundedResult.Items.Count);
    }

    [Fact]
    public async Task Coordinator_recalls_memories_via_audience_primary_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-audience-primary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SQLiteMemoryStore(Path.Combine(dir, "memory.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = store.CreateDefaultAnchor("user-company");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-company-info",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "Company: Petabridge",
            MarkdownBody: "Aaron works at Petabridge, an Akka.NET consultancy.",
            AliasesJson: "[\"petabridge\",\"company\"]",
            FacetsJson: "[\"personal_profile\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.94,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var coordinator = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"D0AC6CKBK5K/1774371415.126439",
            Query: "what company does Aaron work at",
            RecentUserMessages: ["what company does Aaron work at"],
            MaxItems: 3), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Id.Value == "doc-company-info");
    }

    [Fact]
    public async Task Coordinator_recalls_named_project_entities()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-deterministic-project-entity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SQLiteMemoryStore(Path.Combine(dir, "memory.db"), TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var anchor = store.CreateDefaultAnchor("textforge-project");
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: "doc-textforge-business-context",
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: "TextForge Business Context",
            MarkdownBody: "TextForge is an AI sales tool focused on safe email automation and Gmail integration.",
            AliasesJson: "[\"textforge\",\"ai sales tool\"]",
            FacetsJson: "[\"project_fact\"]",
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.95,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now), TestContext.Current.CancellationToken);

        var coordinator = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            TimeProvider.System,
            sessionTuning: new SessionTuning { DeterministicRetrievalEnabled = true });

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: (SessionId)"signalr/thread-5",
            Query: "what is TextForge",
            RecentUserMessages: ["what is TextForge"],
            MaxItems: 3,
            ThreadTitle: "General DM"), TestContext.Current.CancellationToken);

        Assert.False(result.Degraded);
        Assert.Contains(result.Items, x => x.Id.Value == "doc-textforge-business-context");
    }
}
