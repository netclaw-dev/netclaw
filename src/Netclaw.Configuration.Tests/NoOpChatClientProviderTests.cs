// -----------------------------------------------------------------------
// <copyright file="NoOpChatClientProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class NoOpChatClientProviderTests
{
    [Fact]
    public void IsDegraded_IsTrue()
    {
        var provider = new NoOpChatClientProvider();
        Assert.True(provider.IsDegraded);
    }

    [Fact]
    public void DefaultIChatClientProviderIsDegraded_DefaultsToFalse()
    {
        // The default interface implementation should leave IsDegraded false
        // so existing test doubles and SingleClientProvider don't need updating.
        IChatClientProvider provider = new SingleClientProvider(new NoOpChatClient());
        Assert.False(provider.IsDegraded);
    }

    [Fact]
    public void ReturnsSameClientInstance_ForEveryRole()
    {
        var provider = new NoOpChatClientProvider();
        var main = provider.GetClient(ModelRole.Main);
        var fallback = provider.GetClient(ModelRole.Fallback);
        var compaction = provider.GetClient(ModelRole.Compaction);

        Assert.Same(main, fallback);
        Assert.Same(main, compaction);
    }

    [Fact]
    public async Task ProvidedClient_RendersBannerWithAvailableProviders()
    {
        var provider = new NoOpChatClientProvider(new[] { "openrouter" });
        var client = provider.GetClient(ModelRole.Main);

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "anything") },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("openrouter", response.Text);
    }
}
