using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionSidecarRunnerTests
{
    [Fact]
    public async Task RunJsonAsync_unwraps_fenced_proposals_object()
    {
        var client = new StubChatClient("""
            ```json
            { "proposals": [ { "operation": "upsert_document", "memoryClass": "durable_fact", "subjectKind": "user", "subjectValue": "self", "anchor": { "canonicalName": "user-travel-origin", "anchorType": "preference" }, "title": "Travel Profile", "content": "IAH", "aliases": ["IAH", "origin airport"], "facets": ["travel_profile"], "slots": ["origin_airport"], "relations": [], "recallMode": "auto", "sensitivity": "normal", "confidence": 0.9, "freshUntilMs": null, "expiresAtMs": null, "targetSurface": null, "rationale": "test" } ] }
            ```
            """);

        var result = await SessionSidecarRunner.RunJsonAsync<IReadOnlyList<MemoryProposal>>(
            client,
            "system",
            "user",
            TimeSpan.FromSeconds(1),
            _ => { });

        var proposal = Assert.Single(result!);
        Assert.Equal("upsert_document", proposal.Operation);
        Assert.Equal("durable_fact", proposal.MemoryClass);
    }

    [Fact]
    public async Task RunJsonAsync_normalizes_near_miss_operation_and_memory_class_values()
    {
        var client = new StubChatClient("""
            {
              "proposals": [
                {
                  "operation": "store",
                  "memoryClass": "fact",
                  "subjectKind": "user",
                  "subjectValue": "self",
                  "anchor": { "canonicalName": "user-travel-airline", "anchorType": "preference" },
                  "title": "Travel Profile: Preferred Airline",
                  "content": "Preferred airline is United Airlines.",
                  "aliases": ["preferred airline", "United Airlines"],
                  "facets": ["travel_profile"],
                  "slots": ["preferred_airline"],
                  "relations": [],
                  "recallMode": "auto",
                  "sensitivity": "normal",
                  "confidence": 0.9,
                  "freshUntilMs": null,
                  "expiresAtMs": null,
                  "targetSurface": null,
                  "rationale": "test"
                }
              ]
            }
            """);

        var result = await SessionSidecarRunner.RunJsonAsync<IReadOnlyList<MemoryProposal>>(
            client,
            "system",
            "user",
            TimeSpan.FromSeconds(1),
            _ => { });

        var proposal = Assert.Single(result!);
        Assert.Equal("upsert_document", proposal.Operation);
        Assert.Equal("durable_fact", proposal.MemoryClass);
    }

    [Fact]
    public async Task RunJsonAsync_extracts_nested_proposals_and_normalizes_snake_case_fields()
    {
        var client = new StubChatClient("""
            Here you go:
            {
              "data": {
                "items": [
                  {
                    "op": "appendRecord",
                    "memory_class": "evidence",
                    "subject_kind": "trip",
                    "subject_value": "conference travel",
                    "anchor": { "canonical_name": "stir-trek-easton", "anchor_type": "trip_plan" },
                    "title": "Conference Hotel Research",
                    "content": "Easton hotels were reviewed.",
                    "aliases": ["Easton", "hotel research"],
                    "facets": ["travel_research"],
                    "slots": [],
                    "relations": [
                      {
                        "relation_type": "related_to",
                        "targetAnchor": { "canonical_name": "stir-trek", "anchor_type": "event" }
                      }
                    ],
                    "recall_mode": "searchable",
                    "sensitivity": "normal",
                    "confidence": 0.91,
                    "fresh_until_ms": null,
                    "expires_at_ms": null,
                    "target_surface": null,
                    "rationale": "Stable research finding"
                  }
                ]
              }
            }
            """);

        var result = await SessionSidecarRunner.RunJsonAsync<IReadOnlyList<MemoryProposal>>(
            client,
            "system",
            "user",
            TimeSpan.FromSeconds(1),
            _ => { });

        var proposal = Assert.Single(result!);
        Assert.Equal("append_record", proposal.Operation);
        Assert.Equal("evidence", proposal.MemoryClass);
        Assert.Equal("trip", proposal.SubjectKind);
        Assert.Equal("conference travel", proposal.SubjectValue);
        Assert.NotNull(proposal.Anchor);
        Assert.Equal("stir-trek-easton", proposal.Anchor!.CanonicalName);
        Assert.Equal("trip_plan", proposal.Anchor.AnchorType);
        var relation = Assert.Single(proposal.Relations!);
        Assert.Equal("related_to", relation.RelationType);
        Assert.Equal("stir-trek", relation.TargetAnchor.CanonicalName);
        Assert.Equal("event", relation.TargetAnchor.AnchorType);
    }

    [Fact]
    public async Task RunJsonAsync_unwraps_plan_object_wrapper()
    {
        var client = new StubChatClient("""
            { "plan": { "mode": "automatic", "intent": "test", "entities": [], "constraints": [], "searchTerms": ["alpha"], "memoryClasses": ["durable_fact"], "maxResults": 3, "allowExpiredEvidence": false } }
            """);

        var result = await SessionSidecarRunner.RunJsonAsync<RecallQueryPlan>(
            client,
            "system",
            "user",
            TimeSpan.FromSeconds(1),
            _ => { });

        Assert.NotNull(result);
        Assert.Equal("automatic", result!.Mode);
        Assert.Contains("alpha", result.SearchTerms);
    }

    [Fact]
    public async Task RunJsonAsync_normalizes_recall_plan_snake_case_fields()
    {
        var client = new StubChatClient("""
            {
              "recall_plan": {
                "mode": "intentional",
                "intent": "travel",
                "entities": ["Stir Trek"],
                "constraints": ["Easton"],
                "search_terms": ["Stir Trek", "Easton hotels"],
                "memory_classes": ["durable_fact", "evidence"],
                "max_results": 4,
                "allow_expired_evidence": true
              }
            }
            """);

        var result = await SessionSidecarRunner.RunJsonAsync<RecallQueryPlan>(
            client,
            "system",
            "user",
            TimeSpan.FromSeconds(1),
            _ => { });

        Assert.NotNull(result);
        Assert.Equal("intentional", result!.Mode);
        Assert.Contains("Easton hotels", result.SearchTerms);
        Assert.Contains("evidence", result.MemoryClasses);
        Assert.Equal(4, result.MaxResults);
        Assert.True(result.AllowExpiredEvidence);
    }

    private sealed class StubChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
