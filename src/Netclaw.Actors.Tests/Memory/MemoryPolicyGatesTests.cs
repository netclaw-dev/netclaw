using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryPolicyGatesTests
{
    [Fact]
    public void ProposalGate_accepts_durable_fact_and_evidence_but_blocks_non_identity_soul_promotions()
    {
        var gate = new MemoryProposalGate();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = gate.Evaluate(
        [
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "user",
                "self",
                "Preferred Airline",
                "Preferred airline: United",
                "auto",
                "normal",
                0.95,
                now,
                null,
                null,
                "stable preference"),
            new MemoryProposal(
                "append_record",
                "evidence",
                "event",
                "travel-research",
                "Hotel Options",
                "Hilton Easton and Courtyard Easton were found.",
                "searchable",
                "normal",
                0.80,
                now,
                now + 86400000,
                null,
                "one-off research"),
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "assistant",
                "self",
                "Communication style",
                "Prefer concise responses.",
                "auto",
                "normal",
                0.9,
                now,
                null,
                "identity_profile",
                "standing communication preference"),
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "user",
                "self",
                "Identity profile update",
                "Should not route here",
                "auto",
                "normal",
                0.9,
                now,
                null,
                "identity_profile",
                "identity path")
        ],
        "project:test",
        "normal",
        now);

        Assert.Equal(2, result.MemoryOperations.Count);
        Assert.Contains(result.MemoryOperations, x => x.MemoryClass == "durable_fact" && x.Kind == "document");
        Assert.Contains(result.MemoryOperations, x => x.MemoryClass == "evidence" && x.Kind == "record");
        Assert.DoesNotContain(result.MemoryOperations, x => x.Title == "Identity profile update");

        var identityUpdate = Assert.Single(result.IdentityUpdates);
        Assert.Equal("Communication style", identityUpdate.Title);
    }

    [Fact]
    public void ProposalGate_derives_default_expiry_for_evidence_and_trace()
    {
        var gate = new MemoryProposalGate();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var accepted = gate.Accept(
        [
            new MemoryProposal(
                "append_record",
                "evidence",
                "event",
                "travel-research",
                "Hotel options",
                "Found hotel options near Easton.",
                "searchable",
                "normal",
                0.8,
                now,
                null,
                null,
                "one-off research"),
            new MemoryProposal(
                "append_record",
                "trace",
                "event",
                "debug-step",
                "Trace breadcrumb",
                "Called web search tool.",
                "never",
                "normal",
                0.6,
                now,
                null,
                null,
                "execution trace")
        ],
        "project:test",
        "normal",
        now);

        var evidence = Assert.Single(accepted, x => x.MemoryClass == "evidence");
        var trace = Assert.Single(accepted, x => x.MemoryClass == "trace");

        Assert.Equal(now + (long)TimeSpan.FromDays(30).TotalMilliseconds, evidence.ExpiresAtMs);
        Assert.Equal(now + (long)TimeSpan.FromHours(72).TotalMilliseconds, trace.ExpiresAtMs);
        Assert.Equal("never", trace.RecallMode);
    }

    [Fact]
    public void RecallPlanGate_forces_automatic_mode_to_durable_fact_only()
    {
        var gate = new RecallPlanGate();
        var request = new RecallPlanningRequest(
            "slack/thread",
            "project:slack",
            "automatic",
            "What hotel should I stay in there",
            ["I am speaking at Stir Trek in Ohio"],
            ["We found Easton hotel options"],
            ["Stir Trek", "Easton"],
            8,
            3);

        var plan = gate.Clamp(
            new RecallQueryPlan(
                "automatic",
                "lodging",
                ["Stir Trek"],
                ["near venue"],
                ["Stir Trek", "Easton", "hotel"],
                ["durable_fact", "evidence"],
                10,
                true),
            request);

        Assert.Equal(["durable_fact"], plan.MemoryClasses);
        Assert.False(plan.AllowExpiredEvidence);
        Assert.True(plan.MaxResults <= 3);
    }
}
