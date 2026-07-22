// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotModelCatalogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Providers.GitHubCopilot;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class GitHubCopilotModelCatalogTests
{
    [Fact]
    public void Catalog_UsesEndpointAndCaseInsensitiveModelLookup()
    {
        var catalog = new GitHubCopilotModelCatalog();
        var capability = new GitHubCopilotModelCapability(
            "GPT-5.5", true, false, ["/responses"]);

        catalog.Store(new Uri("https://api.tenant.ghe.com"), [capability]);

        Assert.Same(capability,
            catalog.Find(new Uri("https://api.tenant.ghe.com/"), "gpt-5.5"));
        Assert.Null(catalog.Find(new Uri("https://api.githubcopilot.com"), "gpt-5.5"));
    }

    [Fact]
    public async Task LazyClient_InitializesExactlyOnceForConcurrentFirstRequests()
    {
        var initializeCount = 0;
        var client = new GitHubCopilotCapabilityResolvingChatClient(() =>
        {
            Interlocked.Increment(ref initializeCount);
            return Task.FromResult<IChatClient>(new TestChatClient());
        });

        await Task.WhenAll(
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "one")],
                cancellationToken: TestContext.Current.CancellationToken),
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "two")],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, initializeCount);
    }

    private sealed class TestChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
