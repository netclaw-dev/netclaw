// -----------------------------------------------------------------------
// <copyright file="SessionPersistFailureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Persistence.Journal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Production incident, 21 Oct 2026: the journal write of a <see cref="ToolBatchStarted"/> event
/// waited on a SQLite lock. The session actor stopped and sent nothing to the Slack thread.
/// A persist failure of a non-input event during a turn must tell the channel
/// (error, then a failed turn completion) before the stop, and the next message must recover.
/// </summary>
public sealed class SessionToolBatchPersistFailureTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<ToolBatchStartedFailingJournal>(output)
{
    [Fact]
    public async Task Failed_tool_batch_write_reports_the_error_then_fails_the_turn_then_stops_the_session_and_the_next_message_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var turn = await StartToolTurnAsync("persist-failure/tool-batch");

        await AssertErrorThenFailedTurnThenStopAsync(turn);

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var secondChannel = CreateTestProbe("persist-failure-recovery-channel");
        await JoinSessionAsync(manager, secondChannel, turn.SessionId, ChannelFilter);
        var reply = await manager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = turn.SessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(10), ct);

        Assert.IsType<CommandAck>(reply);
        var completed = await secondChannel.FishForMessageAsync<TurnCompleted>(
            message => message.Outcome == TurnOutcome.Completed, TimeSpan.FromSeconds(20), cancellationToken: ct);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
    }
}

/// <summary>
/// A tool batch commit clears the selected inputs of the turn, so a later write of the same turn
/// must not look idle. The user still waits for a reply, and the tool has already run.
/// </summary>
public sealed class SessionToolCallRecordedPersistFailureTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<ToolCallRecordedFailingJournal>(output)
{
    [Fact]
    public async Task Failed_tool_result_write_reports_the_error_then_fails_the_turn_then_stops_the_session()
    {
        var turn = await StartToolTurnAsync("persist-failure/tool-call-recorded");

        await AssertErrorThenFailedTurnThenStopAsync(turn);
    }
}

/// <summary>
/// The journal rejects the write instead of failing it. Akka only logs a rejection and the actor
/// keeps running, so the session itself must fail the turn. A turn that never ends would hold the
/// session until the processing watchdog fires. The failed turn must also close its input in the
/// journal, or a replay would run the same input again.
/// </summary>
public sealed class SessionToolBatchPersistRejectionTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<ToolBatchStartedRejectingJournal>(output)
{
    [Fact]
    public async Task Rejected_tool_batch_write_fails_the_turn_closes_its_input_and_the_same_session_accepts_the_next_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = CreateTestProbe("persist-failure-rejected-journal");
        Sys.EventStream.Subscribe(journal.Ref, typeof(JournalEventWritten));
        var turn = await StartToolTurnAsync("persist-failure/tool-batch-rejected");

        await turn.Channel.FishForMessageAsync<ErrorOutput>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
        var failed = await turn.Channel.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
        Assert.Equal(TurnOutcome.Failed, failed.Outcome);

        // The close lands before the failed turn completion, so the journal signal is already queued.
        await journal.FishForMessageAsync<JournalEventWritten>(
            written => written.EventName == nameof(InputClosed), TimeSpan.FromSeconds(10), cancellationToken: ct);

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var reply = await manager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = turn.SessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(10), ct);
        Assert.IsType<CommandAck>(reply);
        var completed = await turn.Channel.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(20), cancellationToken: ct);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // A restarted or replaced session would have a new incarnation, which does not equal the old reference.
        Assert.Equal(turn.Session, await ResolveSessionAsync(turn.SessionId));
    }
}

/// <summary>
/// The journal rejects the tool batch write, and then it rejects the write that closes the input.
/// The session must fail the turn once. A second close attempt would reject again and loop.
/// </summary>
public sealed class SessionInputClosedPersistRejectionTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<ToolBatchAndInputClosedRejectingJournal>(output)
{
    [Fact]
    public async Task Rejected_input_close_write_fails_the_turn_once_and_does_not_retry_the_close()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = CreateTestProbe("persist-failure-close-journal");
        Sys.EventStream.Subscribe(journal.Ref, typeof(JournalEventWritten));
        Sys.EventStream.Subscribe(journal.Ref, typeof(JournalWriteFailed));
        var turn = await StartToolTurnAsync("persist-failure/input-closed-rejected");

        await turn.Channel.FishForMessageAsync<ErrorOutput>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
        var failed = await turn.Channel.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
        Assert.Equal(TurnOutcome.Failed, failed.Outcome);

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var reply = await manager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = turn.SessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(10), ct);
        Assert.IsType<CommandAck>(reply);

        // A duplicate failed completion of the first turn arrives before this one and fails the assertion.
        var next = await turn.Channel.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(20), cancellationToken: ct);
        Assert.Equal(TurnOutcome.Completed, next.Outcome);

        // The journal signals arrive in write order. The turn record of the second turn ends the first turn's writes.
        var signals = new List<object>();
        while (true)
        {
            var signal = await journal.ExpectMsgAsync<object>(TimeSpan.FromSeconds(10), cancellationToken: ct);
            signals.Add(signal);
            if (signal is JournalEventWritten { EventName: nameof(TurnRecorded) })
                break;
        }

        Assert.Equal(1, signals.OfType<JournalWriteFailed>().Count(failure => failure.EventName == nameof(InputClosed)));
        Assert.Equal(1, signals.OfType<JournalWriteFailed>().Count(failure => failure.EventName == nameof(ToolBatchStarted)));
    }
}

/// <summary>
/// No turn is active when the title write fails. The failure is a log event. An error output
/// would show the user a failure of work that the user never asked for.
/// </summary>
public sealed class SessionIdlePersistFailureTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<SessionTitleSetFailingJournal>(output)
{
    [Fact]
    public async Task Failed_title_write_while_no_turn_is_active_sends_no_error_to_the_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = new SessionId("persist-failure/idle-title");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var channel = CreateTestProbe("persist-failure-idle-channel");
        var journalFailures = CreateTestProbe("persist-failure-idle-journal");
        Sys.EventStream.Subscribe(journalFailures.Ref, typeof(JournalWriteFailed));
        await JoinSessionAsync(manager, channel, sessionId, ChannelFilter);
        var session = await ResolveSessionAsync(sessionId);

        session.Tell(new TitleGenerationCompleted { Title = "A generated title" });

        // The journal signal proves the write ran and failed. The expect-no-message window then
        // covers the time that the actor needs to handle the failure.
        await journalFailures.ExpectMsgAsync<JournalWriteFailed>(TimeSpan.FromSeconds(10), cancellationToken: ct);
        await channel.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), ct);
    }
}

/// <summary>
/// Guards the existing contract for an input event. The caller of an admission gets a Nack, and
/// the session starts no model call. The channel gets no error output, because the Nack already
/// tells the caller.
/// </summary>
public sealed class SessionInputPersistFailureTests(ITestOutputHelper output)
    : SessionPersistFailureTestBase<InputAdmittedFailingJournal>(output)
{
    [Fact]
    public async Task Failed_input_admission_write_nacks_the_sender_and_starts_no_model_call()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var sessionId = new SessionId("persist-failure/input");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var channel = CreateTestProbe("persist-failure-input-channel");
        await JoinSessionAsync(manager, channel, sessionId, ChannelFilter);

        // Act
        var reply = await manager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello"
        }, TimeSpan.FromSeconds(10), ct);

        // Assert
        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Contains("journal", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ModelCallCount);
        await channel.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), ct);
    }
}

public abstract class SessionPersistFailureTestBase<TJournal>(ITestOutputHelper output) : LlmSessionTestBase(output)
    where TJournal : MemoryJournal
{
    protected const OutputFilter ChannelFilter = OutputFilter.Text | OutputFilter.Files | OutputFilter.ProcessingState;

    private readonly FakeChatClient _client = new();

    protected const string UnsafeToRepeatWording = "may already have run";

    protected int ModelCallCount => _client.CallCount;

    /// <summary>A joined session that the test watches.</summary>
    protected sealed record ToolTurn(
        SessionId SessionId, Akka.TestKit.TestProbe Channel, IActorRef Session, Akka.TestKit.TestProbe Watcher);

    /// <summary>
    /// Joins a session, watches it, and sends a message that makes the model call a tool.
    /// The turn writes a <see cref="ToolBatchStarted"/> event.
    /// </summary>
    protected async Task<ToolTurn> StartToolTurnAsync(string sessionName)
    {
        var ct = TestContext.Current.CancellationToken;
        ScriptSearchToolCallOnFirstModelCall();
        var sessionId = new SessionId(sessionName);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var channel = CreateTestProbe("persist-failure-channel");
        await JoinSessionAsync(manager, channel, sessionId, ChannelFilter);
        var session = await ResolveSessionAsync(sessionId);
        var watcher = CreateTestProbe("persist-failure-watcher");
        watcher.Watch(session);

        var reply = await manager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a tool"
        }, TimeSpan.FromSeconds(5), ct);
        Assert.IsType<CommandAck>(reply);

        return new ToolTurn(sessionId, channel, session, watcher);
    }

    /// <summary>
    /// The stop path must tell the channel first: an error that warns about actions that already ran,
    /// then a failed turn completion. Only then does the actor stop.
    /// </summary>
    protected static async Task AssertErrorThenFailedTurnThenStopAsync(ToolTurn turn)
    {
        var ct = TestContext.Current.CancellationToken;
        var error = await turn.Channel.FishForMessageAsync<ErrorOutput>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
        Assert.Contains(UnsafeToRepeatWording, error.Message, StringComparison.Ordinal);

        // The fish skips all earlier messages. A TurnCompleted before the error would be skipped here,
        // and the next wait would time out. Thus this wait proves the order: error first.
        var completed = await turn.Channel.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);

        await turn.Watcher.ExpectTerminatedAsync(turn.Session, TimeSpan.FromSeconds(10), cancellationToken: ct);
    }

    protected void ScriptSearchToolCallOnFirstModelCall()
        => _client.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "search_tools", new Dictionary<string, object?>
            {
                ["_rationale"] = "Read the current tool list."
            })
        ];

    protected override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore();
        builder.AddHocon($"""
            akka.persistence.journal.inmem.class = "{typeof(TJournal).AssemblyQualifiedName}"
            """, HoconAddMode.Prepend);
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { SnapshotInterval = 0, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Test assistant."));
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "same result", "search_tools"), "builtin");
        services.AddSingleton(registry);
    }

    protected async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
        => await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
}

/// <summary>The journal publishes this event to the event stream before it fails or rejects a write of the named event.</summary>
public sealed record JournalWriteFailed(string EventName);

/// <summary>The journal publishes this event to the event stream after it stores an event, in write order.</summary>
public sealed record JournalEventWritten(string EventName);

/// <summary>
/// A journal that fails the write of one event type. By default it throws a
/// <see cref="TimeoutException"/>, as the circuit breaker does when a write waits on a SQLite lock.
/// A derived journal can throw another exception, or reject the write.
/// </summary>
public abstract class FailingEventJournal : MemoryJournal
{
    private readonly EventStream _eventStream = Context.System.EventStream;

    protected abstract IReadOnlyList<Type> FailingEventTypes { get; }

    /// <summary>
    /// When true, the journal returns the exception in the result list. Akka reports a
    /// <c>WriteMessageRejected</c> to the persistent actor. When false, the journal throws.
    /// </summary>
    protected virtual bool RejectInsteadOfThrow => false;

    protected virtual Exception CreateFailure(string eventName)
        => new TimeoutException($"The test journal failed the write of {eventName}.");

    protected override async Task<IImmutableList<Exception>> WriteMessagesAsync(
        IEnumerable<AtomicWrite> messages, CancellationToken cancellationToken)
    {
        var writes = messages.ToArray();
        if (!writes.Any(write => FailingEventName(write) is not null))
        {
            var stored = await base.WriteMessagesAsync(writes, cancellationToken);
            PublishWritten(writes);
            return stored;
        }

        if (!RejectInsteadOfThrow)
        {
            var failingName = writes.Select(FailingEventName).First(name => name is not null)!;
            _eventStream.Publish(new JournalWriteFailed(failingName));
            throw CreateFailure(failingName);
        }

        // One result per atomic write, in order. A null result is a success, a non-null result a rejection.
        var results = new List<Exception>();
        foreach (var write in writes)
        {
            if (FailingEventName(write) is { } name)
            {
                _eventStream.Publish(new JournalWriteFailed(name));
                results.Add(CreateFailure(name));
                continue;
            }

            var written = await base.WriteMessagesAsync([write], cancellationToken);
            PublishWritten([write]);
            results.Add(written?.FirstOrDefault()!);
        }

        return results.ToImmutableList();
    }

    private void PublishWritten(IEnumerable<AtomicWrite> writes)
    {
        foreach (var write in writes)
        {
            foreach (var item in (IEnumerable<IPersistentRepresentation>)write.Payload)
                _eventStream.Publish(new JournalEventWritten(item.Payload.GetType().Name));
        }
    }

    private string? FailingEventName(AtomicWrite write)
        => ((IEnumerable<IPersistentRepresentation>)write.Payload)
            .Select(item => FailingEventTypes.FirstOrDefault(type => type.IsInstanceOfType(item.Payload))?.Name)
            .FirstOrDefault(name => name is not null);
}

/// <summary>Models the error that Microsoft.Data.Sqlite raises when a lock wait ends (SQLITE_BUSY).</summary>
public sealed class ToolBatchStartedFailingJournal : FailingEventJournal
{
    protected override IReadOnlyList<Type> FailingEventTypes => [typeof(ToolBatchStarted)];

    protected override Exception CreateFailure(string eventName)
        => new SqliteException("database is locked", 5);
}

/// <summary>Fails the tool result write. The tool has already run and the tool batch write has committed.</summary>
public sealed class ToolCallRecordedFailingJournal : FailingEventJournal
{
    protected override IReadOnlyList<Type> FailingEventTypes => [typeof(ToolCallRecorded)];
}

public sealed class ToolBatchStartedRejectingJournal : FailingEventJournal
{
    protected override IReadOnlyList<Type> FailingEventTypes => [typeof(ToolBatchStarted)];

    protected override bool RejectInsteadOfThrow => true;

    protected override Exception CreateFailure(string eventName)
        => new InvalidOperationException($"The test journal rejected the write of {eventName}.");
}

/// <summary>Rejects the tool batch write and then the write that closes the input of the failed turn.</summary>
public sealed class ToolBatchAndInputClosedRejectingJournal : FailingEventJournal
{
    protected override IReadOnlyList<Type> FailingEventTypes => [typeof(ToolBatchStarted), typeof(InputClosed)];

    protected override bool RejectInsteadOfThrow => true;

    protected override Exception CreateFailure(string eventName)
        => new InvalidOperationException($"The test journal rejected the write of {eventName}.");
}

public sealed class SessionTitleSetFailingJournal : FailingEventJournal
{
    protected override IReadOnlyList<Type> FailingEventTypes => [typeof(SessionTitleSet)];
}

public sealed class InputAdmittedFailingJournal : FailingEventJournal
{
    protected override IReadOnlyList<Type> FailingEventTypes => [typeof(InputAdmitted)];
}
