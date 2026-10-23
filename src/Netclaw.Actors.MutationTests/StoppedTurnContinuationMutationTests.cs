// -----------------------------------------------------------------------
// <copyright file="StoppedTurnContinuationMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// <see cref="SessionExecutionLifetime.ContinuesStoppedTurn"/> decides if a restart drain keeps a turn that stopped
/// after its tool batch (#2632). A wrong "yes" resumes a turn whose model call sees an incomplete history, starts a
/// killed background job again, or hides partial text from the notice. A wrong "no" loses the turn. Each fact alone
/// must refuse the continuation.
/// </summary>
public sealed class StoppedTurnContinuationMutationTests
{
    [Fact]
    public void A_clean_stop_after_a_tool_batch_continues()
    {
        Assert.True(Decide());
    }

    [Theory]
    [InlineData("emitted text")]
    [InlineData("pending input")]
    [InlineData("replay waits")]
    [InlineData("live background job")]
    [InlineData("unanswered tool call")]
    [InlineData("no channel type")]
    public void Each_unsafe_fact_alone_refuses_the_continuation(string fact)
    {
        Assert.False(fact switch
        {
            "emitted text" => Decide(emittedText: true),
            "pending input" => Decide(pendingInputCount: 1),
            "replay waits" => Decide(replayWaits: true),
            "live background job" => Decide(hasLiveBackgroundJob: true),
            "unanswered tool call" => Decide(hasUnansweredToolCall: true),
            "no channel type" => Decide(hasChannelType: false),
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, null)
        });
    }

    private static bool Decide(
        bool emittedText = false,
        int pendingInputCount = 0,
        bool replayWaits = false,
        bool hasLiveBackgroundJob = false,
        bool hasUnansweredToolCall = false,
        bool hasChannelType = true)
        => SessionExecutionLifetime.ContinuesStoppedTurn(
            emittedText, pendingInputCount, replayWaits, hasLiveBackgroundJob, hasUnansweredToolCall, hasChannelType);
}
