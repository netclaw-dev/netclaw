// -----------------------------------------------------------------------
// <copyright file="ChannelOutputEnginePromptLifetimeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Task 1.10 (engine part). At the end of a parent turn the shared output engine clears only the prompts whose
/// lifetime is the turn. A child prompt stays pending until an answer or a close (#2523). The engine journals the
/// lifetime with the tracked prompt, so a cold-spawned binding keeps the child prompt too.
/// Source: channel-binding-parity "Shared output-completion bookkeeping", design D3.
/// </summary>
public sealed class ChannelOutputEnginePromptLifetimeTests
{
    private static readonly SessionId Session = new("slack/C1/1700.1");

    /// <summary>
    /// Planted bug: the engine clears every pending prompt at a turn end, as it does today, also when the parent turn
    /// fails.
    /// </summary>
    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public async Task A_turn_end_clears_the_parent_prompt_and_keeps_the_child_prompt(TurnOutcome outcome)
    {
        // Arrange
        var harness = new Harness();
        await harness.Engine.HandleOutputAsync(Request("r-7f2/subagent-approval/1", ToolInteractionLifetime.UntilClosed));
        await harness.Engine.HandleOutputAsync(Request("call-parent", ToolInteractionLifetime.UntilTurnEnd));

        // Act
        var cleared = await harness.Engine.HandleOutputAsync(new TurnCompleted
        {
            SessionId = Session, TurnNumber = new TurnNumber(1), Outcome = outcome, OpenChildRunIds = []
        });

        // Assert
        Assert.Equal(["call-parent"], cleared.Select(prompt => prompt.CallId).ToArray());
        var kept = Assert.Single(harness.Pending);
        Assert.Equal("r-7f2/subagent-approval/1", kept.CallId.Value);
        Assert.Equal(1L, kept.PromptId);
    }

    /// <summary>
    /// The binding journal is the only durable copy of a pending prompt. It must carry the lifetime, or a cold spawn
    /// reads the child prompt as a turn prompt.
    /// Planted bug: the engine builds the tracked record without the lifetime.
    /// </summary>
    [Fact]
    public async Task The_tracked_record_of_a_child_prompt_carries_its_lifetime()
    {
        // Arrange
        var harness = new Harness();

        // Act
        await harness.Engine.HandleOutputAsync(Request("r-7f2/subagent-approval/1", ToolInteractionLifetime.UntilClosed));
        await harness.Engine.HandleOutputAsync(Request("call-parent", ToolInteractionLifetime.UntilTurnEnd));

        // Assert
        Assert.Collection(harness.Tracked,
            child => Assert.Equal(ToolInteractionLifetime.UntilClosed, child.Lifetime),
            parent => Assert.Equal(ToolInteractionLifetime.UntilTurnEnd, parent.Lifetime));
    }

    private static ToolInteractionRequest Request(string callId, ToolInteractionLifetime lifetime) => new()
    {
        SessionId = Session,
        Kind = "approval",
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("shell_execute"),
        DisplayText = "gh pr view 12",
        RequesterSenderId = new SenderId("U-alice"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Lifetime = lifetime,
        Options =
        [
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ]
    };

    private sealed class Harness
    {
        private long _nextPromptId;

        public Harness()
        {
            Engine = new ChannelOutputEngine<PendingApprovalRequest<long>, long>(
                StringComparer.Ordinal,
                Pending,
                request => new PendingApprovalRequest<long>(request),
                _ => true,
                text => text.Text,
                error => error.Message,
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                _ => Task.FromResult<long?>(Interlocked.Increment(ref _nextPromptId)),
                promptId => promptId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                Tracked.Add,
                _ => Task.CompletedTask,
                _ => { },
                _ => Task.CompletedTask,
                _ => Task.CompletedTask);
        }

        public List<PendingApprovalRequest<long>> Pending { get; } = [];

        public List<PendingApprovalPromptTracked> Tracked { get; } = [];

        public ChannelOutputEngine<PendingApprovalRequest<long>, long> Engine { get; }
    }
}
