// -----------------------------------------------------------------------
// <copyright file="MattermostCallbackActionStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostCallbackActionStoreTests
{
    [Fact]
    public void TryConsume_returns_action_for_valid_token_exactly_once()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var store = new MattermostCallbackActionStore(time);
        var token = store.CreateAction("ch-1", "prompt-1", "call-1", "approve_once", "root-1", "requester-1");

        Assert.True(store.TryConsume(token, out var first));
        Assert.NotNull(first);
        Assert.Equal("ch-1", first!.ChannelId);

        // Single-use: a second consume on the same token must fail closed.
        Assert.False(store.TryConsume(token, out var second));
        Assert.Null(second);
    }

    [Fact]
    public void Store_caps_outstanding_entries_at_MaxEntries()
    {
        // Memory-pressure defense: if a session mints buttons faster than
        // users click them (or never clicks them), the action store must not
        // grow unbounded. The cap is enforced by FIFO eviction of the oldest
        // unconsumed tokens.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var store = new MattermostCallbackActionStore(time);

        // Mint cap + overflow so the eviction path runs deterministically.
        var overflow = 250;
        var firstTokens = new List<string>();
        for (var i = 0; i < MattermostCallbackActionStore.MaxEntries + overflow; i++)
        {
            var token = store.CreateAction("ch-1", $"prompt-{i}", $"call-{i}", "approve_once", "root-1", "requester-1");
            if (i < overflow)
                firstTokens.Add(token);
        }

        Assert.Equal(MattermostCallbackActionStore.MaxEntries, store.Count);

        // The oldest tokens were evicted FIFO; consuming any of them must
        // surface as if they had never been minted.
        foreach (var token in firstTokens)
        {
            Assert.False(store.TryConsume(token, out _));
        }
    }

    [Fact]
    public void Eviction_preserves_recently_minted_tokens()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var store = new MattermostCallbackActionStore(time);

        // Fill to cap with throwaway tokens.
        for (var i = 0; i < MattermostCallbackActionStore.MaxEntries; i++)
            store.CreateAction("ch-1", $"prompt-{i}", $"call-{i}", "approve_once", "root-1", "requester-1");

        // Newly-minted tokens must survive even though the store is at cap;
        // older tokens get evicted instead.
        var fresh = store.CreateAction("ch-2", "prompt-fresh", "call-fresh", "approve_session", "root-2", "requester-2");
        Assert.True(store.TryConsume(fresh, out var action));
        Assert.NotNull(action);
        Assert.Equal("call-fresh", action!.CallId);
    }

    [Fact]
    public void AssociatePromptPostId_scopes_by_prompt_correlation_id()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var store = new MattermostCallbackActionStore(time);

        var first = store.CreateAction("ch-1", "prompt-a", "call-1", "approve_once", "root-1", "requester-1");
        var second = store.CreateAction("ch-1", "prompt-b", "call-1", "approve_once", "root-1", "requester-2");

        store.AssociatePromptPostId("prompt-a", "post-a");

        Assert.True(store.TryGet(first, out var firstAction));
        Assert.True(store.TryGet(second, out var secondAction));
        Assert.Equal("post-a", firstAction!.PromptPostId);
        Assert.Null(secondAction!.PromptPostId);
    }
}
