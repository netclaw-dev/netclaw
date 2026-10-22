// -----------------------------------------------------------------------
// <copyright file="MidTurnUserMessageSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Regression spec for the mid-turn message of a person (0.27.1 behavior). Alice's turn runs a tool loop. While its
/// model call runs, a person sends a message in the same session. At the next tool-batch end, a message of Alice joins
/// the running turn: the next model request of that turn carries it after the tool result, and that turn records and
/// closes it. A message of another requester does not join. It waits for its own turn (#2505 security boundary).
/// <para>
/// Live failure on dev c2295af (session D0AC6CKBK5K/1792687336.669269): a child consent request made the session admit
/// an approval notice at a tool-batch end. The notice waits at the front of the buffer for its own turn. The drain at
/// the batch end takes only the leading inputs that may join, so it stopped at the notice. No message of the person
/// joined the turn for more than 40 minutes.
/// </para>
/// </summary>
public sealed class MidTurnUserMessageSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    private const string Steer = "Stop and use the other branch.";
    private const string PlanContent = "MID-TURN-PLAN-CONTENT-5521";

    public enum Row
    {
        /// <summary>A plain tool loop. No child run.</summary>
        PlainToolLoop,

        /// <summary>The live state: a child of Alice asks for consent during the loop, and the request stays open.</summary>
        OpenChildConsentRequest,

        /// <summary>A child of Alice asks for consent during the loop, and the operator answers before the batch ends.</summary>
        AnsweredChildConsentRequest,

        /// <summary>Negative row: Bob sends the message during Alice's loop. It must not join her turn.</summary>
        OtherRequester
    }

    [Theory]
    [InlineData(Row.PlainToolLoop)]
    [InlineData(Row.OpenChildConsentRequest)]
    [InlineData(Row.AnsweredChildConsentRequest)]
    [InlineData(Row.OtherRequester)]
    public async Task A_message_sent_during_a_tool_loop_joins_the_running_turn_of_its_requester_only(Row row)
    {
        // Arrange
        var sessionId = new SessionId($"steer/{row}");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        ChildRunAccepted? child = null;
        var childGate = NewGate();
        var childAfterAnswer = NewGate();
        if (row is Row.OpenChildConsentRequest or Row.AnsweredChildConsentRequest)
            (child, childGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-steer");
        var projectFile = Path.Combine(TestPaths.WorkspacesDirectory, "project", "plan.md");
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        await File.WriteAllTextAsync(projectFile, PlanContent, Cancellation);
        var read = $"read:{projectFile}";
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains(read), hold);

        try
        {
            // Act: Alice's turn holds its first model call. The model then asks for file_read, so a tool batch runs.
            await SendAsync(sessionId, read, Operator(Alice));
            await entered.WaitAsync(Deadline, Cancellation);
            if (child is not null)
            {
                childGate.TrySetResult();
                var prompt = await FishChildPromptAsync(subscriber, child.Run.RunId);
                if (row == Row.AnsweredChildConsentRequest)
                {
                    // The answered child stops at its next model call, so its result notice does not change the queue.
                    Clients.A.NextResponseGate = childAfterAnswer;
                    AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));
                }
            }

            await SendAsync(sessionId, Steer, Operator(row == Row.OtherRequester ? Bob : Alice));
            hold.TrySetResult();

            // Assert
            await AssertMidTurnMessageAsync(journal, sessionId, read, joins: row != Row.OtherRequester);
        }
        finally
        {
            hold.TrySetResult();
            childGate.TrySetResult();
            childAfterAnswer.TrySetResult();
            ParentGate.Disarm();
        }
    }

    // The shared postconditions of each row. The message is consumed exactly once. When it joins, the first model
    // request that carries it is the next request of Alice's tool loop: the file_read result and the message are both
    // new in that request. Alice's turn records and closes it, so it does not stay pending and does not start a turn.
    // When it does not join, no request of Alice's loop carries it, and a later turn consumes it.
    private async Task AssertMidTurnMessageAsync(AcceptanceFault journal, SessionId sessionId, string read, bool joins)
    {
        var message = await AwaitInputAdmittedAsync(journal, Steer);
        var readInput = await AwaitInputAdmittedAsync(journal, read);
        await AwaitAssertAsync(async () =>
        {
            await BarrierAsync(sessionId);
            Assert.True(ConsumedAt(journal, message.InputId) >= 0, "The message is still pending.");
            Assert.True(await IsIdleAsync(sessionId), "The session still runs a turn.");
        }, Deadline, cancellationToken: Cancellation);

        var events = journal.Committed.ToArray();
        var loopStart = ConsumedAt(journal, readInput.InputId);
        var aliceTurnEnd = Array.FindIndex(events, loopStart, evt => evt is TurnRecorded);
        var messageAt = ConsumedAt(journal, message.InputId);
        var firstRequest = Clients.Parent.ReceivedMessages.First(request => request.Any(item =>
            item.Role == Microsoft.Extensions.AI.ChatRole.User && item.Text == Steer));
        var joinedAfterToolResult = Results(FreshTail(firstRequest))
            .Any(result => result.Result?.ToString()?.Contains(PlanContent, StringComparison.Ordinal) == true);
        Note($"loopStart={loopStart} aliceTurnEnd={aliceTurnEnd} messageAt={messageAt} joinedAfterToolResult={joinedAfterToolResult}");

        Assert.True(loopStart >= 0 && aliceTurnEnd > loopStart, "Alice's tool loop did not run and end.");
        Assert.Equal(1, ConsumptionCount(journal, message.InputId));
        if (joins)
        {
            Assert.True(joinedAfterToolResult,
                "The next model request of the running turn must carry the message after the tool result.");
            Assert.True(messageAt <= aliceTurnEnd, "The running turn must record and close the message.");
        }
        else
        {
            Assert.False(joinedAfterToolResult, "A message of another requester joined the running turn.");
            Assert.True(messageAt > aliceTurnEnd, "A message of another requester must wait for its own turn.");
        }
    }

    // The messages after the last assistant message. They are the new content of the request.
    private static IReadOnlyList<ChatMessage> FreshTail(IReadOnlyList<ChatMessage> request)
    {
        var lastAssistant = -1;
        for (var index = 0; index < request.Count; index++)
        {
            if (request[index].Role == Microsoft.Extensions.AI.ChatRole.Assistant)
                lastAssistant = index;
        }

        return request.Skip(lastAssistant + 1).ToArray();
    }
}
