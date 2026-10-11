// -----------------------------------------------------------------------
// <copyright file="ToolRecurrencePersistenceFaultTests.CompactionSafety.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiRole = Microsoft.Extensions.AI.ChatRole;
using StoredRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class ToolRecurrencePersistenceFaultTests
{
    private const string ConfirmedCompactionSummary = "## 1. Primary Request and Intent\nInspect the neutral fixture.\n"
        + "## 2. Key Technical Concepts\nrecurrence_probe\n## 3. Files and Code Sections\n"
        + "## 4. Problem Solving\nThe probe returned same.\n## 5. Pending Tasks\nContinue the original task.\n"
        + "## 6. Task Evolution\nOriginal: Inspect the neutral fixture.\n## 7. Current Work\nOne probe completed.\n"
        + "## 8. Next Step\nContinue the original task.\n## 9. Required Files\n";

    [Theory]
    [InlineData("failure")]
    [InlineData("empty")]
    [InlineData("summary")]
    public async Task Compaction_must_preserve_the_original_task_when_the_observer_cannot_replace_the_entire_window(string observer)
    {
        var session = new SessionId($"signalr/compaction-task-preservation-{observer}");
        _main.CompactionRequestNumber = 1;
        _main.HoldRequestNumber = 2;
        _compaction.ResponseText = observer == "summary" ? ConfirmedCompactionSummary : string.Empty;
        if (observer == "failure")
            _compaction.Failure = new InvalidOperationException("The controlled observer failed.");
        var releaseSnapshot = NewSignal();
        await Snapshots.OnSave.FailIf(async (persistenceId, _) =>
        {
            if (persistenceId != $"session-{session.Value}") return false;
            await releaseSnapshot.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        try
        {
            await StartAsync(manager, session);
            await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            var notice = await subscriber.FishForMessageAsync<object>(message => message is ErrorOutput or CompactionOutput,
                FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
            var journal = await ReadJournalAsync(session, new Akka.Persistence.Recovery(Akka.Persistence.SnapshotSelectionCriteria.None));
            var actualRequest = _main.Requests[1];
            Assert.Equal(1, _compaction.Count);
            Assert.Equal(1, _executor.Count);
            Assert.Equal("operator-a", Assert.Single(_executor.Requesters));
            Assert.Equal("original", Assert.Single(journal.Events.OfType<ToolTaskAdopted>()).TurnContext.TurnId);
            var firstAdmission = Assert.Single(journal.Events.OfType<ToolBatchStarted>()).LoopAdmission!;
            Assert.Equal("original", firstAdmission.TaskId);
            var firstResult = Assert.Single(journal.Events.OfType<ToolCallRecorded>());
            Assert.False(firstResult.LoopObservation!.Synthetic);
            Assert.False(firstResult.LoopObservation.MissingReceipt);
            Assert.Equal("call-1", firstResult.LoopObservation.CallId);
            Assert.Equal("same", firstResult.ToolResult.Content);
            if (observer == "summary")
            {
                var compacted = Assert.Single(journal.Events.OfType<SessionCompacted>());
                Assert.Equal(ConfirmedCompactionSummary, compacted.Summary);
                var expected = ObservationPromptBuilder.WrapObservations(ConfirmedCompactionSummary, session);
                Assert.Equal(expected, Assert.Single(compacted.CompactedMessages).Content);
                Assert.Contains(actualRequest, message => message.Role == AiRole.User && message.Text == expected);
                Assert.True(Assert.IsType<CompactionOutput>(notice).Summarized);
                releaseSnapshot.TrySetResult();
                await AwaitAssertAsync(async () => Assert.NotNull((await ReadJournalAsync(session)).Snapshot),
                    FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
                var stored = Assert.IsType<SessionSnapshot>((await ReadJournalAsync(session)).Snapshot);
                Assert.Equal("original", stored.LoopCheckpoint!.TaskId);
                Assert.Equal(1, Assert.Single(stored.LoopCheckpoint.Entries).EqualRounds);
                Assert.Equal("operator-a", stored.AdoptedTaskContext!.DefaultDeliveryTarget!.DestinationId);
            }
            else
            {
                Assert.Empty(journal.Events.OfType<SessionCompacted>());
                Assert.Null((await ReadJournalAsync(session)).Snapshot);
                Assert.IsType<ErrorOutput>(notice);
                Assert.Contains(actualRequest, message => message.Role == AiRole.User && message.Text == "Inspect the neutral fixture.");
                var call = Assert.Single(actualRequest.SelectMany(message => message.Contents.OfType<FunctionCallContent>()));
                var result = Assert.Single(actualRequest.SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
                Assert.Equal("call-1", call.CallId);
                Assert.Equal("recurrence_probe", call.Name);
                Assert.Equal(call.CallId, result.CallId);
                Assert.Equal(firstResult.ToolResult.Content, result.Result);
            }
        }
        finally
        {
            releaseSnapshot.TrySetResult();
            _main.ReleaseRequest.TrySetResult();
        }
        await CompletedAsync(subscriber);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(4, _main.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var settled = await ReadJournalAsync(session);
        // The summary snapshot carries the first round. Its journal suffix carries the second round.
        Assert.Equal(observer == "summary" ? 1 : 2,
            settled.Events.OfType<ToolCallRecorded>().Count(result => !result.LoopObservation!.Synthetic));
        Assert.Equal(observer == "summary", settled.Snapshot is not null);
        var correction = Assert.Single(settled.Events.OfType<ToolCallRecorded>(), result => result.LoopObservation!.Synthetic);
        Assert.Equal("call-3", correction.ToolResult.ToolCallId!.Value.Value);
        Assert.StartsWith(ToolCycleMessages.Correction, correction.ToolResult.Content);
        Assert.Equal(ToolCycleMessages.Final, Assert.Single(settled.Events.OfType<TurnRecorded>()).AssistantReply.Content);
    }

    [Fact]
    public async Task A_failed_observer_keeps_a_nonempty_extractive_task_window_and_its_exact_result_pair()
    {
        var session = new SessionId("signalr/nonempty-extractive-observer-fallback");
        var keptCall = new SerializableChatMessage
        {
            Role = StoredRole.Assistant,
            ToolCalls = [new SerializableToolCall { CallId = new Netclaw.Tools.ToolCallId("kept-call"),
                Name = new Netclaw.Tools.ToolName("recurrence_probe"), ArgumentsJson = "{}" }]
        };
        var keptResult = new SerializableChatMessage
        { Role = StoredRole.Tool, ToolCallId = new Netclaw.Tools.ToolCallId("kept-call"), Content = "The exact retained result." };
        var state = SessionState.Empty with { History = ImmutableList.Create(
            new SerializableChatMessage { Role = StoredRole.System, Content = "The system contract." },
            new SerializableChatMessage { Role = StoredRole.User, Content = "The old completed task." },
            new SerializableChatMessage { Role = StoredRole.Assistant, Content = "The old task is complete." },
            new SerializableChatMessage { Role = StoredRole.User, Content = "The active task must survive." }, keptCall, keptResult) };
        _compaction.Failure = new InvalidOperationException("The controlled observer failed.");
        var observer = CreateTestProbe();
        await SessionCompactionPipeline.ExecuteAsync(state,
            new CompactionParameters(session, 800, 1, 2, 1000, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5)),
            _compaction, observer.Ref, Logging.GetLogger(Sys, typeof(ToolRecurrencePersistenceFaultTests)), 17);
        var completed = await observer.ExpectMsgAsync<CompactionWorkCompleted>(FaultCeiling,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(17, completed.OperationId);
        Assert.Equal(1, _compaction.Count);
        Assert.Empty(completed.Summary);
        Assert.Equal(new[] { "The active task must survive.", keptCall.Content, "The exact retained result." },
            completed.CompactedMessages.Select(message => message.Content));
        Assert.Equal(keptCall.ToolCalls[0].CallId, Assert.Single(completed.CompactedMessages.SelectMany(message => message.ToolCalls)).CallId);
        Assert.Equal(keptResult.ToolCallId, Assert.Single(completed.CompactedMessages, message => message.Role == StoredRole.Tool).ToolCallId);
        var restored = state.Apply(new SessionCompacted { SessionId = session, CompactedMessages = completed.CompactedMessages });
        Assert.Equal("The system contract.", restored.History[0].Content);
        Assert.DoesNotContain(restored.History, message => message.Content == "The old completed task.");
        Assert.Contains(restored.History, message => message.Content == "The active task must survive.");
    }
}
