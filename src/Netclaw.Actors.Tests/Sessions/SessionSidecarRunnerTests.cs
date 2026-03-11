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
            { "proposals": [ { "operation": "upsert_document", "memoryClass": "durable_fact", "subjectKind": "user", "subjectValue": "self", "title": "Travel Profile", "content": "IAH", "recallMode": "auto", "sensitivity": "normal", "confidence": 0.9, "freshUntilMs": null, "expiresAtMs": null, "targetSurface": null, "rationale": "test" } ] }
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
