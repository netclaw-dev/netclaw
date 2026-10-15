// -----------------------------------------------------------------------
// <copyright file="ChildNoticeTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    private AcceptanceFault? _journal;

    protected ChildNoticeTestBase(ITestOutputHelper output) : base(output)
    {
    }

    protected void Note(string line) => Output?.WriteLine(line);

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // The audience profile of the requester. A spec can remove file_read from it.
    protected virtual void ConfigurePersonalProfile(ToolAudienceProfile profile) { }

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
                ToolNames = ["authority_probe"], EmitStructuredFindings = false,
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
            Preparation, NullLogger<SubAgentSpawner>.Instance);
        registry.RegisterCore(new SpawnAgentTool(profiles, spawner, TestPaths));
        registry.RegisterCore(new CheckAgentRunTool());
        registry.RegisterCore(new FileReadTool(toolConfig, TestPaths, new ToolPathPolicy([])));
        registry.RegisterCore(ApprovalTool);
        services.AddSingleton<IChatClientProvider>(Clients);
        services.AddSingleton(new ModelCapabilities { ModelId = "notice-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            // A spec that needs passivation sends ReceiveTimeout itself. A timer would race the spec.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning { SnapshotInterval = 0, TitleGenerationInterval = 0 }
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

    protected Task<ISessionResponse> AnswerAsync(SessionId id, ToolCallId callId, string sender) =>
        AskManagerAsync<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = id, CallId = callId,
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(sender)
        });

    // A refused answer names its reason, so a spec failure shows why the session did not take the answer.
    protected static void AssertAck(ISessionResponse response)
    {
        if (response is CommandNack nack)
            Assert.Fail($"The session refused the command: {nack.Reason}");
        Assert.IsType<CommandAck>(response);
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
                                  && message.Text.Contains("result.md", StringComparison.Ordinal));

    /// <summary>
    /// Starts a child for the requester and holds it at its first model call. The caller releases the gate to end the child.
    /// The child output is <see cref="ChildOutput"/> and the label.
    /// </summary>
    private protected async Task<(ChildRunAccepted Accepted, TaskCompletionSource Gate)> StartChildAsync(
        SessionId sessionId, string requester, string label, string agent = "child-a", bool adopted = false)
    {
        var journal = await JournalAsync();
        var client = agent == "child-a" ? Clients.A : Clients.B;
        var gate = NewGate();
        client.NextResponseGate = gate;
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
    /// <c>probe:{label}</c> opens an approval prompt. A new notice makes the model read the result file with
    /// <c>file_read</c>. Any other request gets the default reply.
    /// </summary>
    protected void ScriptParent()
        => Clients.Parent.ResponseContentsSelector = static request => ScriptedParentReply(request);

    protected static IReadOnlyList<AIContent>? ScriptedParentReply(IReadOnlyList<ChatMessage> request)
    {
        var lastAssistant = -1;
        for (var index = 0; index < request.Count; index++)
        {
            if (request[index].Role == Microsoft.Extensions.AI.ChatRole.Assistant)
                lastAssistant = index;
        }

        var fresh = request.Skip(lastAssistant + 1)
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Select(message => message.Text)
            .ToArray();
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
            else if (text.StartsWith("probe2:", StringComparison.Ordinal))
            {
                calls.Add(Probe($"probe-{text["probe2:".Length..]}"));
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
