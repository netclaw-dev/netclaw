// -----------------------------------------------------------------------
// <copyright file="ChildNoticeTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Shared setup for specs about a child result that reaches the parent as an admitted notice.
/// The parent model is scripted from the content of each request, so a spec states what the model sees and not
/// how many calls came before. A notice is an admitted input whose text names the run and the path of its
/// <c>result.md</c> file. The notice has no type that the specs could name, so <see cref="IsNoticeFor"/> reads that text.
/// <para>
/// This base reuses the journal probe, the clients, the preparation barrier, and the approval tool of
/// <see cref="ChildRunActorBoundaryTests"/>. It adds the real <c>file_read</c> tool, so a spec proves that the
/// model reads the result file through a real tool call.
/// </para>
/// </summary>
public abstract class ChildNoticeTestBase : LlmSessionTestBase
{
    protected const string Alice = "alice";
    protected const string Bob = "bob";
    protected const string ChildOutput = "CHILD-OUTPUT-SENTINEL-4471";
    protected const string Brief = "Summarize the quarterly numbers.";
    protected static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static readonly Regex ResultPathPattern = new(@"(?:[A-Za-z]:)?[\\/][^\s""'`<>()\[\]]*result\.md", RegexOptions.Compiled);

    private protected readonly ChildRunActorBoundaryTests.BoundaryClients Clients = new();
    private protected readonly ChildRunActorBoundaryTests.PreparationBarrier Preparation = new();
    private protected readonly ChildRunActorBoundaryTests.AuthorityTool ApprovalTool = new();
    // A spec arms this gate for one parent model request that matches a predicate. A request that does not match
    // passes. This keeps a gate from stopping a call of another turn, for instance the second call of a notice turn.
    private protected readonly RequestGate ParentGate = new();
    private IActorRef? _phaseTracker;
    private AcceptanceFault? _journal;

    protected ChildNoticeTestBase(ITestOutputHelper output) : base(output)
    {
    }

    protected void Note(string line) => Output?.WriteLine(line);

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // The audience profile of the requester. A spec can remove file_read from it.
    protected virtual void ConfigurePersonalProfile(ToolAudienceProfile profile) { }

    // A spec can add a tool for the parent model, for instance a tool that holds its call open.
    protected virtual void RegisterParentTools(ToolRegistry registry) { }

    // A session saves a snapshot only when this interval divides the sequence number. Zero turns snapshots off.
    protected virtual int SnapshotInterval => 0;

    // A child with structured findings yields one finding from its text output.
    protected virtual bool ChildEmitsFindings => false;

    protected override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore();
        builder.AddHocon($"""
            akka.persistence.journal.inmem.class = "{typeof(AcceptanceBoundaryJournal).AssemblyQualifiedName}"
            """, HoconAddMode.Prepend);
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        var registry = new ToolRegistry();
        var profiles = new SubAgentDefinitionRegistry();
        foreach (var (name, role) in new[] { ("child-a", ModelRole.Fallback), ("child-b", ModelRole.Compaction) })
        {
            profiles.Register(new SubAgentProfile
            {
                Name = name, Description = name, SystemPrompt = name, ModelRole = role,
                ToolNames = ["authority_probe", ChangeProbeTool.ToolName], EmitStructuredFindings = ChildEmitsFindings,
                Visibility = SubAgentVisibility.UserFacing
            });
        }

        var toolConfig = new ToolConfig();
        // Every probe call asks for consent, so a parent or child call opens an approval prompt.
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode> { ["authority_probe"] = ToolApprovalMode.Approval }
        };
        ConfigurePersonalProfile(toolConfig.AudienceProfiles.Personal);
        var policy = new ToolAccessPolicy(TestPaths, toolConfig,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var prompt = new StaticSystemPromptProvider("Test parent.");
        var spawner = new SubAgentSpawner(Clients, registry, policy, null, prompt,
            Preparation, NullLogger<SubAgentSpawner>.Instance, new SessionConfig());
        registry.RegisterCore(new SpawnAgentTool(profiles, spawner, TestPaths));
        registry.RegisterCore(new CheckAgentRunTool());
        registry.RegisterCore(new FileReadTool(toolConfig, TestPaths, new ToolPathPolicy([])));
        registry.RegisterCore(ApprovalTool);
        registry.RegisterCore(new ChangeProbeTool());
        RegisterParentTools(registry);
        services.AddSingleton<IChatClientProvider>(new GatedParentClients(Clients, ParentGate));
        services.AddSingleton(new ModelCapabilities { ModelId = "notice-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            // A spec that needs passivation sends ReceiveTimeout itself. A timer would race the spec.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning { SnapshotInterval = SnapshotInterval, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(prompt);
        services.AddSingleton(registry);
        services.AddSingleton(profiles);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(registry, policy, null,
            NullLogger<DispatchingToolExecutor>.Instance));
    }

    // The test journal records each committed event. A spec reads this list and never the actor state.
    private protected async Task<AcceptanceFault> JournalAsync()
    {
        TrackPhases();
        if (_journal is not null)
            return _journal;
        var control = new AcceptanceFault(rejectAcceptance: false, holdAcceptance: false);
        await AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Ask<Done>(control, Deadline, Cancellation);
        return _journal = control;
    }

    protected IActorRef SessionManager => ActorRegistry.Get<SessionManagerActorKey>();

    protected async Task<Akka.TestKit.TestProbe> JoinAsync(SessionId id)
    {
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(SessionManager, subscriber, id, OutputFilter.Full);
        return subscriber;
    }

    protected Task<IActorRef> ResolveSessionAsync(SessionId id) =>
        Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(id.Value)}").ResolveOne(Deadline, Cancellation);

    protected async Task StopAsync(IActorRef session)
    {
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        Sys.Stop(session);
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
    }

    // A crash: the actor stops with no drain, the live children stop with it, and the next join recovers from the journal.
    protected async Task<Akka.TestKit.TestProbe> RestartSessionAsync(SessionId id)
    {
        await StopAsync(await ResolveSessionAsync(id));
        return await JoinAsync(id);
    }

    // The ask timeout runs on the actor system scheduler. A spec on the virtual scheduler would never time out, so the
    // wait on the task carries the real deadline.
    protected Task<T> AskManagerAsync<T>(object message)
        => SessionManager.Ask<T>(message, TimeSpan.FromHours(1), Cancellation).WaitAsync(Deadline, Cancellation);

    protected async Task<CommandAck> SendAsync(SessionId id, string content, MessageSource source)
    {
        var response = await AskManagerAsync<ISessionResponse>(
            new SendUserMessage { SessionId = id, Content = content, Source = source });
        if (response is CommandNack nack)
            Assert.Fail($"The session refused \"{content}\": {nack.Reason}");
        return Assert.IsAssignableFrom<CommandAck>(response);
    }

    // After a daemon restart the reminder system sends the restart reminder to the recovered session.
    private protected Task<ISessionResponse> SendRestartReminderAsync(SessionId id, Netclaw.Actors.Reminders.ReminderDefinition reminder)
    {
        var fire = reminder.Schedule.FireAt ?? throw new InvalidOperationException("The restart reminder has no fire time.");
        return AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = id,
            Content = reminder.Instructions,
            Source = Reminder($"{reminder.Id.Value}:{fire.ToUnixTimeMilliseconds()}")
        });
    }

    // Ends Alice's child and holds the notice turn in its first model call. Then the actor stops without a drain.
    // The journal holds the admitted notice and nothing that consumed it.
    private protected async Task<(AcceptanceFault Journal, ChildRunAccepted Accepted, InputAdmitted Notice)> CrashWhileTheNoticeTurnWaitsAsync(SessionId sessionId)
    {
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        var hold = NewGate();
        Clients.Parent.NextResponseGate = hold;
        gate.TrySetResult();
        await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(() => Assert.Null(Clients.Parent.NextResponseGate), Deadline, cancellationToken: Cancellation);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.Equal(0, ConsumptionCount(journal, notice.InputId));

        await StopAsync(await ResolveSessionAsync(sessionId));
        hold.TrySetResult();
        return (journal, accepted, notice);
    }

    protected Task<ISessionResponse> AnswerAsync(SessionId id, ToolCallId callId, string sender) =>
        AskManagerAsync<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = id, CallId = callId,
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(sender)
        });

    // A refused answer names its reason, so a spec failure shows why the session did not take the answer. A restart
    // reminder that resumes nothing gets RestartResumeNotNeeded, which is an acknowledgement too.
    protected static void AssertAck(ISessionResponse response)
    {
        if (response is CommandNack nack)
            Assert.Fail($"The session refused the command: {nack.Reason}");
        Assert.IsAssignableFrom<CommandAck>(response);
    }

    protected ValueTask<ToolInteractionRequest> FishApprovalAsync(Akka.TestKit.TestProbe subscriber, string callId) =>
        subscriber.FishForMessageAsync<ToolInteractionRequest>(item => item.CallId.Value == callId, Deadline,
            cancellationToken: Cancellation);

    protected async Task ExpectTurnsAsync(Akka.TestKit.TestProbe subscriber, int count)
    {
        for (var turn = 0; turn < count; turn++)
        {
            await subscriber.FishForMessageAsync(
                message => message is TurnCompleted { Outcome: TurnOutcome.Completed }, Deadline,
                cancellationToken: Cancellation);
        }
    }

    // The folder that holds task.md and result.md. It sits beside the artifacts folder in both storage layouts.
    protected string RunFolder(SessionId sessionId, SubAgentRunId runId, SubAgentScopeId scopeId)
    {
        var resolver = Host.Services.GetRequiredService<ISessionStorageResolver>();
        var artifacts = resolver.Resolve(sessionId).ForChild(runId, scopeId).ArtifactDirectory.Value;
        return Path.GetDirectoryName(artifacts)
            ?? throw new InvalidOperationException("The artifact folder has no parent.");
    }

    protected string RunFolder(SessionId sessionId, ChildRunAccepted accepted)
        => RunFolder(sessionId, accepted.Run.RunId, accepted.Run.ScopeId);

    protected string ResultPath(SessionId sessionId, ChildRunAccepted accepted)
        => Path.Combine(RunFolder(sessionId, accepted), "result.md");

    protected string TaskPath(SessionId sessionId, ChildRunAccepted accepted)
        => Path.Combine(RunFolder(sessionId, accepted), "task.md");

    // A notice is an admitted user input that names the run and its result file.
    protected static bool IsNoticeFor(InputAdmitted admitted, SubAgentRunId runId)
        => admitted.UserMessage.Role == Netclaw.Actors.Protocol.ChatRole.User
           && admitted.UserMessage.Content is { } text
           && text.Contains(runId.Value, StringComparison.Ordinal)
           && text.Contains("result.md", StringComparison.Ordinal);

    private protected static IReadOnlyList<InputAdmitted> NoticesFor(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<InputAdmitted>().Where(admitted => IsNoticeFor(admitted, runId)).ToArray();

    private protected static IReadOnlyList<ChildRunAccepted> Accepted(AcceptanceFault journal)
        => journal.Committed.OfType<ChildRunAccepted>().ToArray();

    // The journal probe records every session of the test system. A spec with several sessions reads one by its id.
    private protected static IReadOnlyList<ChildRunAccepted> Accepted(AcceptanceFault journal, SessionId sessionId)
        => journal.Committed.OfType<ChildRunAccepted>().Where(accepted => accepted.SessionId == sessionId).ToArray();

    // The result of a run is ready for the parent when the journal holds it in the form of the day. Before S2 that
    // is the ResultPrepared event. After S2 it is the notice. A spec that must not depend on the form waits here.
    // The check reads the type name, so this file compiles after S2 removes the live writer of ResultPrepared.
    private protected static bool IsResultReady(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<ChildRunEvent>().Any(evt => evt.RunId == runId && evt.GetType().Name == "ResultPrepared")
           || NoticesFor(journal, runId).Count > 0;

    // A model request carries the child result in either form: a notice, or the synthetic pair that S2 deletes.
    // A spec about the order of inputs uses this predicate so that it fails for the order and not for the form.
    protected static bool CarriesChildResult(IReadOnlyList<ChatMessage> request, SubAgentRunId runId)
        => CarriesNoticeFor(request, runId)
           || Calls(request).Any(call => call.Name == "child_run_result"
                                         && call.Arguments?["run_id"]?.ToString() == runId.Value);

    // The model request that carries a notice for the run. The text of the request names the run and its result file.
    protected static bool CarriesNoticeFor(IReadOnlyList<ChatMessage> request, SubAgentRunId runId)
        => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                                  && message.Text.Contains(runId.Value, StringComparison.Ordinal)
                                  && message.Text.Contains("result.md", StringComparison.Ordinal)
                                  && !message.Text.Contains("[working-context]", StringComparison.Ordinal));

    /// <summary>
    /// Starts a child for the requester and holds it at its first model call. The caller releases the gate to end the child.
    /// The child output is <see cref="ChildOutput"/> and the label.
    /// </summary>
    private protected async Task<(ChildRunAccepted Accepted, TaskCompletionSource Gate)> StartChildAsync(
        SessionId sessionId, string requester, string label, string agent = "child-a", bool adopted = false,
        string? changedFile = null)
    {
        var journal = await JournalAsync();
        var client = agent == "child-a" ? Clients.A : Clients.B;
        var gate = NewGate();
        client.NextResponseGate = gate;
        // A child that changes a file first calls change_probe, and then ends with its text.
        if (changedFile is not null)
            client.PlannedResponses.Enqueue([ChangeFile($"change-{label}", changedFile)]);
        client.PlannedResponses.Enqueue([new TextContent($"{ChildOutput} {label}")]);
        var before = Accepted(journal, sessionId).Count;
        var calls = client.CallCount;
        await SendAsync(sessionId, $"spawn:{agent}:{label}", Operator(requester, adopted));
        await AwaitAssertAsync(() =>
        {
            Assert.Equal(before + 1, Accepted(journal, sessionId).Count);
            Assert.Equal(calls + 1, client.CallCount);
        }, Deadline, cancellationToken: Cancellation);
        return (Accepted(journal, sessionId)[before], gate);
    }

    // The position in the committed journal of the first event that consumed the input. -1 when no event did.
    private protected static int ConsumedAt(AcceptanceFault journal, InputId inputId)
    {
        var events = journal.Committed.ToArray();
        for (var index = 0; index < events.Length; index++)
        {
            var consumed = events[index] switch
            {
                ToolBatchStarted batch => batch.ConsumedInputIds,
                TurnRecorded turn => turn.ConsumedInputIds,
                InputClosed closed => closed.InputIds,
                _ => []
            };
            if (consumed.Contains(inputId))
                return index;
        }

        return -1;
    }

    // How many committed events consumed the input. A turn that runs a notice twice is a double delivery.
    private protected static int ConsumptionCount(AcceptanceFault journal, InputId inputId)
        => journal.Committed.ToArray().Count(evt => evt switch
        {
            ToolBatchStarted batch => batch.ConsumedInputIds.Contains(inputId),
            TurnRecorded turn => turn.ConsumedInputIds.Contains(inputId),
            InputClosed closed => closed.InputIds.Contains(inputId),
            _ => false
        });

    protected static bool HasCall(IReadOnlyList<ChatMessage> request, string callId)
        => Calls(request).Any(call => call.CallId == callId);

    protected static bool HasResult(IReadOnlyList<ChatMessage> request, string callId)
        => Results(request).Any(result => result.CallId == callId);

    protected static IEnumerable<FunctionCallContent> Calls(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>());

    protected static IEnumerable<FunctionResultContent> Results(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>());

    protected static int IndexOf(IReadOnlyList<IReadOnlyList<ChatMessage>> requests, Func<IReadOnlyList<ChatMessage>, bool> match)
    {
        for (var index = 0; index < requests.Count; index++)
        {
            if (match(requests[index]))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// Scripts the parent model from the content of each request. Only the messages after the last assistant message
    /// are new work. A new user message that starts with <c>spawn:{agent}:{label}</c> starts a child, and
    /// <c>probe:{label}</c> opens an approval prompt, and <c>probetext:{label}</c> adds preamble text before the prompt. A new notice makes the model read the result file with
    /// <c>file_read</c>. Any other request gets the default reply.
    /// </summary>
    protected void ScriptParent()
        => Clients.Parent.ResponseContentsSelector = static request => ScriptedParentReply(request);

    protected static IReadOnlyList<AIContent>? ScriptedParentReply(IReadOnlyList<ChatMessage> request)
    {
        var fresh = FreshUserTexts(request);
        var calls = new List<AIContent>();
        foreach (var text in fresh)
        {
            if (text.StartsWith("spawn:", StringComparison.Ordinal))
            {
                var parts = text.Split(':');
                calls.Add(Spawn($"spawn-{parts[2]}", parts[1], $"{Brief} [{parts[2]}]"));
            }
            else if (text.StartsWith("probe:", StringComparison.Ordinal))
            {
                calls.Add(Probe($"probe-{text["probe:".Length..]}"));
            }
            else if (text.StartsWith("probetext:", StringComparison.Ordinal))
            {
                // Preamble text, and then an approval prompt in the same assistant message.
                calls.Add(new TextContent("Checking this now."));
                calls.Add(Probe($"probe-{text["probetext:".Length..]}"));
            }
            else if (text.StartsWith("probe2:", StringComparison.Ordinal))
            {
                calls.Add(Probe($"probe-{text["probe2:".Length..]}"));
            }
            else if (text.StartsWith("read:", StringComparison.Ordinal))
            {
                // A read of a project file. The call id is stable, so a repeated request reads the file once.
                var path = text["read:".Length..];
                calls.Add(ReadFile($"read-project-{Math.Abs(path.GetHashCode(StringComparison.Ordinal))}", path));
            }
            else if (ResultPathPattern.Match(text) is { Success: true } match)
            {
                // One read per result file. The call id is stable, so a repeated request reads the file once.
                calls.Add(ReadFile($"read-{Math.Abs(match.Value.GetHashCode(StringComparison.Ordinal))}", match.Value));
            }
        }

        // A "probe2:{label}" message asks for a second approval in the same turn, after the first approval ran.
        foreach (var text in request.Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User).Select(message => message.Text))
        {
            if (!text.StartsWith("probe2:", StringComparison.Ordinal))
                continue;
            var first = $"probe-{text["probe2:".Length..]}";
            if (HasResult(request, first) && !HasCall(request, $"{first}-b"))
                calls.Add(Probe($"{first}-b"));
        }

        return calls.Count == 0 ? null : calls;
    }

    // The user messages after the last assistant message. They are the new work of the request.
    protected static string[] FreshUserTexts(IReadOnlyList<ChatMessage> request)
        => FreshTail(request)
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Select(message => message.Text)
            .ToArray();

    // The messages after the last assistant message. They are the new content of the request.
    protected static IReadOnlyList<ChatMessage> FreshTail(IReadOnlyList<ChatMessage> request)
    {
        var lastAssistant = -1;
        for (var index = 0; index < request.Count; index++)
        {
            if (request[index].Role == Microsoft.Extensions.AI.ChatRole.Assistant)
                lastAssistant = index;
        }

        return request.Skip(lastAssistant + 1).ToArray();
    }

    // The absolute path of result.md in the text of a notice. The notice contract is that it names this path.
    protected static string ResultPathIn(string noticeText)
        => ResultPathPattern.Match(noticeText) is { Success: true } match
            ? match.Value
            : throw new InvalidOperationException($"The notice names no result.md path: {noticeText}");

    // The session phase that the session actor logged last. The phase is Ready when the turn has ended.
    // The log line is the only signal outside the actor, because a phase change does not reach the journal.
    // The tracker is an actor, so the question goes through its mailbox. Every phase line that the session
    // published before the question is in that mailbox ahead of the question.
    protected async Task<string?> PhaseOfAsync(SessionId sessionId)
    {
        var tracker = _phaseTracker ?? throw new InvalidOperationException("Call JournalAsync before the phase is read.");
        var reply = await tracker.Ask<PhaseTracker.Phase>(
            new PhaseTracker.QueryPhase($"/session-manager/{Uri.EscapeDataString(sessionId.Value)}"), Deadline, Cancellation);
        return reply.Value;
    }

    // A session is idle when no turn runs and none starts. A passivating or stopped session is idle too.
    protected async Task<bool> IsIdleAsync(SessionId sessionId) => await PhaseOfAsync(sessionId) is null or "Ready" or "Passivating";

    private void TrackPhases()
    {
        if (_phaseTracker is not null)
            return;
        _phaseTracker = Sys.ActorOf(Props.Create(() => new PhaseTracker()), "phase-tracker");
        Sys.EventStream.Subscribe(_phaseTracker, typeof(Akka.Event.Info));
    }

    // The journal sequence number of the admission of an input.
    private protected async Task<long> SequenceOfAsync(SessionId sessionId, InputId inputId)
    {
        var probe = CreateTestProbe();
        AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new Akka.Persistence.Journal.MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, probe.Ref));
        var persistenceId = $"session-{sessionId.Value}";
        long found = 0;
        while (true)
        {
            switch (await probe.ExpectMsgAsync<object>(Deadline, cancellationToken: Cancellation))
            {
                case Akka.Persistence.Journal.MemoryJournal.ReplayedEvent { Persistent: { Payload: InputAdmitted admitted } persistent }
                    when persistent.PersistenceId == persistenceId && admitted.InputId == inputId:
                    found = persistent.SequenceNr;
                    break;
                case Akka.Persistence.Journal.MemoryJournal.EventReplaySuccess:
                    return found > 0 ? found : throw new InvalidOperationException("The input admission is not in the journal.");
                case Akka.Persistence.Journal.MemoryJournal.EventReplayFailure failure:
                    throw new InvalidOperationException("The journal replay failed.", failure.Cause);
            }
        }
    }

    // The input ids that one committed event consumed. An event that consumes no input gives an empty list.
    private protected static IReadOnlyList<InputId> ConsumedBy(object evt) => evt switch
    {
        ToolBatchStarted batch => batch.ConsumedInputIds,
        TurnRecorded turn => turn.ConsumedInputIds,
        InputClosed closed => closed.InputIds,
        _ => []
    };

    // The admission of the user input with this exact text.
    private protected async Task<InputAdmitted> AwaitInputAdmittedAsync(AcceptanceFault journal, string text)
    {
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<InputAdmitted>(),
            admitted => admitted.UserMessage.Content == text), Deadline, cancellationToken: Cancellation);
        return journal.Committed.OfType<InputAdmitted>().Single(admitted => admitted.UserMessage.Content == text);
    }

    // The sequence number of the newest snapshot of the session. Zero means that the store holds none.
    private protected async Task<long> LatestSnapshotSequenceNrAsync(SessionId sessionId)
    {
        var probe = CreateTestProbe();
        AkkaPersistence.Instance.Apply(Sys).SnapshotStoreFor(null).Tell(
            new Akka.Persistence.LoadSnapshot($"session-{sessionId.Value}", Akka.Persistence.SnapshotSelectionCriteria.Latest, long.MaxValue),
            probe.Ref);
        var result = await probe.ExpectMsgAsync<Akka.Persistence.LoadSnapshotResult>(Deadline, cancellationToken: Cancellation);
        return result.Snapshot?.Metadata.SequenceNr ?? 0;
    }

    protected static FunctionCallContent ChangeFile(string callId, string path) => new(callId, ChangeProbeTool.ToolName,
        new Dictionary<string, object?> { ["path"] = path, ["_rationale"] = "Change a file." });

    protected static FunctionCallContent Spawn(string callId, string agent = "child-a", string task = Brief) => new(callId, "spawn_agent",
        new Dictionary<string, object?> { ["agent"] = agent, ["task"] = task, ["_rationale"] = "Start a child run." });

    protected static FunctionCallContent Probe(string callId) => new(callId, "authority_probe",
        new Dictionary<string, object?> { ["_rationale"] = "Open an approval prompt." });

    protected static FunctionCallContent ReadFile(string callId, string path) => new(callId, "file_read",
        new Dictionary<string, object?> { ["path"] = path, ["_rationale"] = "Read the child result." });

    protected static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected static MessageSource Operator(string sender, bool adopted = false) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(sender),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        ReceivedAt = TimeProvider.System.GetUtcNow(),
        AdoptedSpeakerIds = adopted ? [sender] : []
    };

    // Mirrors the Mode B source that ReminderExecutionActor builds for a reminder in an existing session.
    protected static MessageSource Reminder(string key) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId("reminder-system"),
        MessageId = key, TurnId = new TurnId(key), ReminderId = new ReminderId(key),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("reminder")
        },
        ReceivedAt = TimeProvider.System.GetUtcNow()
    };
}

/// <summary>
/// Remembers the last phase that each session actor logged. The log source is the path of the actor.
/// </summary>
internal sealed class PhaseTracker : ReceiveActor
{
    private const string Marker = "session_phase_transition from=";

    public sealed record QueryPhase(string PathSuffix);

    public sealed record Phase(string? Value);

    public PhaseTracker()
    {
        var phases = new Dictionary<string, string>();
        Receive<Akka.Event.Info>(info =>
        {
            var text = info.Message?.ToString() ?? string.Empty;
            if (!text.StartsWith(Marker, StringComparison.Ordinal))
                return;
            var toIndex = text.IndexOf(" to=", StringComparison.Ordinal);
            if (toIndex >= 0)
                phases[info.LogSource] = text[(toIndex + " to=".Length)..].Trim();
        });
        Receive<QueryPhase>(query => Sender.Tell(new Phase(
            phases.FirstOrDefault(entry => entry.Key.EndsWith(query.PathSuffix, StringComparison.Ordinal)).Value)));
    }
}

/// <summary>
/// Holds one parent model request that matches a predicate until a spec releases it. A request that does not match
/// passes at once. The gate takes the first match only, and then it is empty again.
/// </summary>
internal sealed class RequestGate
{
    private readonly object _lock = new();
    private Func<IReadOnlyList<ChatMessage>, bool>? _match;
    private TaskCompletionSource? _release;
    private TaskCompletionSource? _entered;

    /// <summary>Arms the gate. The returned task completes when a matching request enters the gate.</summary>
    public Task Arm(Func<IReadOnlyList<ChatMessage>, bool> match, TaskCompletionSource release)
    {
        lock (_lock)
        {
            _match = match;
            _release = release;
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _entered.Task;
        }
    }

    public void Disarm()
    {
        lock (_lock)
        {
            _match = null;
            _release = null;
            _entered = null;
        }
    }

    public TaskCompletionSource? TryTake(IReadOnlyList<ChatMessage> request)
    {
        lock (_lock)
        {
            if (_match is null || _release is null || !_match(request))
                return null;
            var release = _release;
            _entered?.TrySetResult();
            _match = null;
            _release = null;
            _entered = null;
            return release;
        }
    }
}

internal sealed class GatedParentClients(ChildRunActorBoundaryTests.BoundaryClients inner, RequestGate gate) : IChatClientProvider
{
    private readonly IChatClient _parent = new GatedChatClient(inner.Parent, gate);

    public IChatClient GetClient(ModelRole role) => role == ModelRole.Main ? _parent : inner.GetClient(role);
}

internal sealed class GatedChatClient(IChatClient inner, RequestGate gate) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        if (gate.TryTake(request) is { } release)
            await release.Task.WaitAsync(cancellationToken);
        return await base.GetResponseAsync(request, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        if (gate.TryTake(request) is { } release)
            await release.Task.WaitAsync(cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(request, options, cancellationToken).WithCancellation(cancellationToken))
            yield return update;
    }
}

/// <summary>
/// A tool that reports a changed file in its receipt, like a file edit does. It changes no file on disk.
/// </summary>
internal sealed class ChangeProbeTool : INetclawTool
{
    public const string ToolName = "change_probe";

    public string Name => ToolName;
    public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
    public string Description => "Report a changed file.";
    public string GrantCategory => "builtin";
    public JsonElement ParameterSchema => default;
    public AITool ToAITool() => AIFunctionFactory.Create(() => "changed", Name, Description);
    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
        => throw new InvalidOperationException("This test requires invocation authority.");

    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
    {
        var path = arguments?["path"]?.ToString() ?? throw new InvalidOperationException("The path is required.");
        return Task.FromResult(context.SuccessFile($"changed {path}", path, ToolFileActivityKind.Changed));
    }
}
