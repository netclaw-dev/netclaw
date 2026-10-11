// -----------------------------------------------------------------------
// <copyright file="StreamCommitGateMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class StreamCommitGateMutationTests
{
    [Fact]
    public void Lifecycle_update_is_held_and_replaced_with_liveness_placeholder()
    {
        var gate = new StreamCommitGate();
        var lifecycle = Lifecycle("resp-1");

        var emitted = gate.Accept(lifecycle);

        Assert.False(gate.Committed);
        var placeholder = Assert.Single(emitted);
        Assert.NotSame(lifecycle, placeholder);
        Assert.Null(placeholder.ResponseId);
        Assert.Null(placeholder.MessageId);
        Assert.Empty(placeholder.Contents);
    }

    [Fact]
    public void Role_only_keepalive_passes_through_without_committing()
    {
        var gate = new StreamCommitGate();
        var keepalive = new ChatResponseUpdate { Role = ChatRole.Assistant };

        var emitted = gate.Accept(keepalive);

        Assert.False(gate.Committed);
        Assert.Same(keepalive, Assert.Single(emitted));
    }

    [Fact]
    public void First_substantive_update_commits_and_releases_held_updates()
    {
        var gate = new StreamCommitGate();
        var lifecycle = Lifecycle("resp-1");
        var text = new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("done")]
        };
        gate.Accept(lifecycle);

        var emitted = gate.Accept(text);

        Assert.True(gate.Committed);
        Assert.Collection(
            emitted,
            update => Assert.Same(lifecycle, update),
            update => Assert.Same(text, update));
        Assert.Empty(gate.Complete());
    }

    [Fact]
    public void Completion_without_output_releases_held_updates()
    {
        var gate = new StreamCommitGate();
        var lifecycle = Lifecycle("resp-1");
        gate.Accept(lifecycle);

        var emitted = gate.Complete();

        Assert.True(gate.Committed);
        Assert.Same(lifecycle, Assert.Single(emitted));
        Assert.Empty(gate.Complete());
    }

    private static ChatResponseUpdate Lifecycle(string responseId) => new()
    {
        Role = ChatRole.Assistant,
        ResponseId = responseId,
        MessageId = responseId + "-msg"
    };
}
