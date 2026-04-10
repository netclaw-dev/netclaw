using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
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
                new MemoryAnchor("user-travel-airline", "preference"),
                "Preferred Airline",
                "Preferred airline: United",
                ["preferred airline", "united airlines"],
                ["travel_profile", "user_preference"],
                null,
                null,
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
                new MemoryAnchor("stirtrek-2026-travel-plan", "event"),
                "Hotel Options",
                "Hilton Easton and Courtyard Easton were found.",
                ["hotel options", "easton hotel"],
                ["trip_planning"],
                null,
                null,
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
                new MemoryAnchor("assistant-communication-style", "preference"),
                "Communication style",
                "Prefer concise responses.",
                ["communication preference", "response style"],
                ["user_preference"],
                null,
                null,
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
                new MemoryAnchor("user-identity-update", "preference"),
                "Identity profile update",
                "Should not route here",
                ["identity profile"],
                ["user_preference"],
                null,
                null,
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

        Assert.Single(result.MemoryOperations);
        Assert.Contains(result.MemoryOperations, x => x.MemoryClass == "durable_fact" && x.Kind == "document");
        Assert.DoesNotContain(result.MemoryOperations, x => x.Title == "Communication style");
        Assert.DoesNotContain(result.MemoryOperations, x => x.Title == "Identity profile update");

        Assert.Equal(2, result.IdentityUpdates.Count);
        Assert.Contains(result.IdentityUpdates, x => x.Title == "Communication style");
        Assert.Contains(result.IdentityUpdates, x => x.Title == "Identity profile update");
        Assert.Equal(3, result.AcceptedProposals.Count);
    }

    [Fact]
    public void ProposalGate_mirrors_stable_user_identity_fact_into_durable_memory()
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
                new MemoryAnchor("user-cat-ardbeg", "pet"),
                "Cat",
                "Aaron's cat is named Ardbeg.",
                ["Ardbeg", "cat"],
                ["personal_profile", "pet_profile"],
                ["pet_name"],
                null,
                "auto",
                "normal",
                0.95,
                now,
                null,
                "identity_profile",
                "Stable personal fact useful for future recall")
        ],
        "project:test",
        "normal",
        now);

        var identityUpdate = Assert.Single(result.IdentityUpdates);
        Assert.Equal("Cat", identityUpdate.Title);

        var mirrored = Assert.Single(result.MemoryOperations);
        Assert.Equal("durable_fact", mirrored.MemoryClass);
        Assert.Equal("document", mirrored.Kind);
        Assert.Equal("Cat", mirrored.Title);
        Assert.Contains("pet_profile", mirrored.FacetsJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProposalGate_does_not_mirror_volatile_identity_status_into_durable_memory()
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
                new MemoryAnchor("user-current-location", "location"),
                "Current location",
                "Aaron is working out of the RV this week and will be home Friday.",
                ["RV", "working remotely"],
                ["personal_profile"],
                ["current_location"],
                null,
                "auto",
                "normal",
                0.9,
                now,
                null,
                "identity_profile",
                "Current temporary status update")
        ],
        "project:test",
        "normal",
        now);

        Assert.Single(result.IdentityUpdates);
        Assert.Empty(result.MemoryOperations);
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
                new MemoryAnchor("travel-research", "event"),
                "Hotel options",
                "Found hotel options near Easton.",
                ["hotel options"],
                ["trip_planning"],
                null,
                null,
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
                new MemoryAnchor("debug-step", "event"),
                "Trace breadcrumb",
                "Called web search tool.",
                null,
                null,
                null,
                null,
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
    public void ProposalGate_rejects_durable_fact_without_anchor_aliases_or_facets()
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
                null,
                "Preferred Airline",
                "Preferred airline: United",
                null,
                null,
                null,
                null,
                "auto",
                "normal",
                0.95,
                now,
                null,
                null,
                "missing retrieval metadata")
        ],
        "project:test",
        "normal",
        now);

        Assert.Empty(accepted);
    }

    [Fact]
    public void RecallPlanGate_forces_automatic_mode_to_durable_fact_only()
    {
        var gate = new RecallPlanGate();
        var request = new RecallPlanningRequest(
            "slack/thread",
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

    [Fact]
    public void ProposalGate_forces_evidence_to_record_even_when_llm_says_upsert_document()
    {
        var gate = new MemoryProposalGate();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var accepted = gate.Accept(
        [
            new MemoryProposal(
                "upsert_document", // LLM incorrectly chose upsert_document
                "evidence",        // but memoryClass is evidence
                "project",
                "netclaw",
                new MemoryAnchor("reddit-scanner-tuning", "analysis"),
                "Reddit Scanner Tuning",
                "Tuned scanner parameters for better accuracy.",
                ["reddit scanner", "tuning"],
                ["project_artifact"],
                null,
                null,
                "searchable",
                "normal",
                0.78,
                now,
                null,
                null,
                "agent research finding")
        ],
        "project:test",
        "normal",
        now);

        var op = Assert.Single(accepted);
        Assert.Equal("record", op.Kind);
        Assert.Equal("immutable-record", op.UpdateSemantics);
        Assert.Equal("evidence", op.MemoryClass);
    }

    [Fact]
    public void ProposalGate_applies_supplied_audience_and_boundary_to_operations()
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
                new MemoryAnchor("user-travel-airline", "preference"),
                "Preferred Airline",
                "Preferred airline: United",
                ["preferred airline", "united airlines"],
                ["travel_profile", "user_preference"],
                null,
                null,
                "auto",
                "normal",
                0.95,
                now,
                null,
                null,
                "stable preference")
        ],
        "user:aaron",
        "normal",
        now,
        boundary: SecurityPolicyDefaults.PersonalBoundary,
        audience: TrustAudience.Personal);

        var operation = Assert.Single(result.MemoryOperations);
        Assert.Equal(SecurityPolicyDefaults.PersonalBoundary, operation.Boundary);
        Assert.Equal(TrustAudience.Personal, operation.Audience);
    }

    [Fact]
    public void ProposalGate_caps_total_accepted_proposals_including_identity_only_entries()
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
                new MemoryAnchor("accepted-1", "preference"),
                "Accepted 1",
                "Content 1",
                ["accepted 1"],
                ["travel_profile"],
                null,
                null,
                "auto",
                "normal",
                0.99,
                now,
                null,
                null,
                null),
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "user",
                "self",
                new MemoryAnchor("accepted-2", "preference"),
                "Accepted 2",
                "Content 2",
                ["accepted 2"],
                ["travel_profile"],
                null,
                null,
                "auto",
                "normal",
                0.98,
                now,
                null,
                null,
                null),
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "assistant",
                "self",
                new MemoryAnchor("identity-accepted", "preference"),
                "Communication style",
                "Prefer concise responses.",
                ["communication preference"],
                ["user_preference"],
                null,
                null,
                "auto",
                "normal",
                0.97,
                now,
                null,
                "identity_profile",
                "standing communication preference"),
            new MemoryProposal(
                "upsert_document",
                "durable_fact",
                "user",
                "self",
                new MemoryAnchor("trimmed", "preference"),
                "Trimmed",
                "Content trimmed by cap.",
                ["trimmed"],
                ["travel_profile"],
                null,
                null,
                "auto",
                "normal",
                0.10,
                now,
                null,
                null,
                null)
        ],
        "project:test",
        "normal",
        now);

        Assert.Equal(3, result.AcceptedProposals.Count);
        Assert.Equal(2, result.MemoryOperations.Count);
        Assert.Single(result.IdentityUpdates);
        Assert.DoesNotContain(result.AcceptedProposals, x => x.Title == "Trimmed");
        Assert.Equal(1, result.Summary.RejectionReasons["max-proposals-exceeded"]);
    }
}
