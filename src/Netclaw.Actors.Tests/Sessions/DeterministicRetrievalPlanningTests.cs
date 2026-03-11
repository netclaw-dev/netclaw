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
            SessionId: "signalr/thread-1",
            Query: "I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me?",
            RecentUserMessages: ["I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me?"],
            MaxItems: 3,
            HardScopeOverride: "user:aaron",
            ThreadTitle: "Stir Trek 2026 travel planning"));

        Assert.Equal("user:aaron", plan.HardScope);
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
            SessionId: "signalr/thread-2",
            Query: "What's the pricing model for TextForge?",
            RecentUserMessages: ["What's the pricing model for TextForge?"],
            MaxItems: 3,
            HardScopeOverride: "user:aaron",
            ThreadTitle: "General DM"));

        Assert.Equal("user:aaron", plan.HardScope);
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
        await store.InitializeAsync();

        var coordinator = new SQLiteMemoryRecallCoordinator(
            store,
            NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sessionConfig: new SessionConfig { DeterministicRetrievalEnabled = true, MemorySidecarsEnabled = false });

        var result = await coordinator.RecallAsync(new AutomaticRecallRequest(
            SessionId: "signalr/thread-3",
            Query: "What's the pricing model for TextForge?",
            RecentUserMessages: ["What's the pricing model for TextForge?"],
            MaxItems: 3,
            HardScopeOverride: "user:aaron",
            ThreadTitle: "General DM"));

        Assert.False(result.Degraded);
        Assert.Null(result.DegradeStage);
    }
}
