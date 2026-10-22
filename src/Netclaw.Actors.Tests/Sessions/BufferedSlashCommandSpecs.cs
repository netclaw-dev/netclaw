// -----------------------------------------------------------------------
// <copyright file="BufferedSlashCommandSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for issue #2591. Alice sends a slash command and then a text input while her turn holds a model
/// call. Both inputs wait in the buffer. A slash command never joins a turn. It stays in the buffer, and when the
/// running turn ends, it dispatches as its own input on the path of a live command. The text input runs after it.
/// Bug on dev: the buffer drain adds the slash command to the model history as user text.
/// </summary>
public sealed class BufferedSlashCommandSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    private const string Typo = "/typo";
    private const string After = "Alice work after the buffered input.";

    public enum Row
    {
        /// <summary>The command waits during a tool loop. The drain at the tool-batch end must not take it.</summary>
        MidTurnCommand,

        /// <summary>The command waits behind a turn with no tool loop. The drain at the turn end must not take it.</summary>
        BetweenTurnsCommand,

        /// <summary>An unknown command waits behind a turn. It must get the reply of a live unknown command.</summary>
        BetweenTurnsUnknownCommand,

        /// <summary>Guard row: plain text waits during a tool loop. It still joins the running turn (#2607).</summary>
        MidTurnPlainText
    }

    [Theory]
    [InlineData(Row.MidTurnCommand)]
    [InlineData(Row.BetweenTurnsCommand)]
    [InlineData(Row.BetweenTurnsUnknownCommand)]
    [InlineData(Row.MidTurnPlainText)]
    public async Task A_buffered_slash_command_dispatches_as_a_command_and_never_reaches_the_model_as_text(Row row)
    {
        // Arrange
        var sessionId = new SessionId($"buffered-slash/{row}");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var held = "Hold this turn.";
        if (row is Row.MidTurnCommand or Row.MidTurnPlainText)
        {
            // The model reads this file, so the held turn runs a tool batch.
            var file = Path.Combine(TestPaths.WorkspacesDirectory, "project", "plan.md");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "plan", Cancellation);
            held = $"read:{file}";
        }

        var buffered = row switch
        {
            Row.MidTurnPlainText => "Plain text that joins the turn.",
            Row.BetweenTurnsUnknownCommand => Typo,
            _ => $"/{RoutedSkill} review the plan"
        };
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains(held), hold);

        try
        {
            // Act: Alice's turn holds its first model call. Her two new inputs wait in the buffer.
            await SendAsync(sessionId, held, Operator(Alice));
            await entered.WaitAsync(Deadline, Cancellation);
            await SendAsync(sessionId, buffered, Operator(Alice));
            await SendAsync(sessionId, After, Operator(Alice));
            hold.TrySetResult();

            // Assert: each input is consumed, and the session is idle.
            var ids = new List<InputId>();
            foreach (var text in new[] { held, buffered, After })
                ids.Add((await AwaitInputAdmittedAsync(journal, text)).InputId);
            await AwaitAssertAsync(async () =>
            {
                await BarrierAsync(sessionId);
                Assert.All(ids, id => Assert.True(ConsumedAt(journal, id) >= 0, $"The input {id} is still pending."));
                Assert.True(await IsIdleAsync(sessionId), "The session still runs a turn.");
            }, Deadline, cancellationToken: Cancellation);
            await AssertBufferedInputAsync(journal, sessionId, subscriber, row, buffered, ids);
        }
        finally
        {
            hold.TrySetResult();
            ParentGate.Disarm();
        }
    }

    // The shared postconditions. Each input is consumed once. Plain text joins the running turn. A slash command never
    // reaches the model as text: it is consumed alone, after the held turn and before the later input. A known command
    // starts one routed run. An unknown command gets the same reply as a live unknown command.
    private async Task AssertBufferedInputAsync(AcceptanceFault journal, SessionId sessionId,
        Akka.TestKit.TestProbe subscriber, Row row, string buffered, IReadOnlyList<InputId> ids)
    {
        var at = ids.Select(id => ConsumedAt(journal, id)).ToArray();
        var heldEnd = Array.FindIndex(journal.Committed.ToArray(), at[0], evt => evt is TurnRecorded);
        Note($"row={row} consumedAt=[{string.Join(",", at)}] heldTurnEnd={heldEnd}");
        Assert.All(ids, id => Assert.Equal(1, ConsumptionCount(journal, id)));
        if (row == Row.MidTurnPlainText)
        {
            Assert.Equal(heldEnd, at[1]);
            Assert.Equal(heldEnd, at[2]);
            return;
        }

        Assert.False(Clients.Parent.ReceivedMessages.Any(request => request.Any(message =>
                message.Role == Microsoft.Extensions.AI.ChatRole.User && message.Text.Contains(buffered, StringComparison.Ordinal))),
            $"A model request carried the buffered command \"{buffered}\" as user text.");
        Assert.True(heldEnd < at[1] && at[1] < at[2],
            "The command must run alone, after the held turn and before the later input.");
        if (row != Row.BetweenTurnsUnknownCommand)
        {
            var start = Assert.Single(Accepted(journal, sessionId)).Run.StartKey;
            Assert.Equal(ids[1], Assert.IsType<ChildRunStartKey.Slash>(start).InputId);
            return;
        }

        // A live unknown command in the idle session gives the reference reply.
        var bufferedReply = await FishUnknownCommandReplyAsync(subscriber);
        await SendAsync(sessionId, Typo, Operator(Alice));
        Assert.Equal((await FishUnknownCommandReplyAsync(subscriber)).Text, bufferedReply.Text);
    }

    private static async Task<TextOutput> FishUnknownCommandReplyAsync(Akka.TestKit.TestProbe subscriber)
        => (TextOutput)await subscriber.FishForMessageAsync(
            message => message is TextOutput text && text.Text.StartsWith("Unknown command:", StringComparison.Ordinal),
            Deadline, cancellationToken: Cancellation);
}
