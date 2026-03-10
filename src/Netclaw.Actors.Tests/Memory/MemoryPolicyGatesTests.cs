using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryPolicyGatesTests
{
    [Fact]
    public void ProposalGate_accepts_durable_fact_and_evidence_but_blocks_identity_surface()
    {
        var gate = new MemoryProposalGate();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var accepted = gate.Accept(
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

        Assert.Equal(2, accepted.Count);
        Assert.Contains(accepted, x => x.MemoryClass == "durable_fact" && x.Kind == "document");
        Assert.Contains(accepted, x => x.MemoryClass == "evidence" && x.Kind == "record");
        Assert.DoesNotContain(accepted, x => x.Title == "Identity profile update");
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
