// -----------------------------------------------------------------------
// <copyright file="ChildOwnerActorSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
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
/// Executable specifications for child ownership and for the answers to a child approval prompt.
/// Each test states one contract and records the observed values when the contract fails.
/// </summary>
public sealed class ChildOwnerActorSpecs(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private const string NotAvailable = "Error: This child run is not available to the requester.";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private readonly ChildRunActorBoundaryTests.BoundaryClients _clients = new();
    private readonly ChildRunActorBoundaryTests.PreparationBarrier _preparation = new();
    private readonly ChildRunActorBoundaryTests.AuthorityTool _tool = new();
    private readonly ITestOutputHelper _output = output;

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
        profiles.Register(new SubAgentProfile
        {
            Name = "child-a", Description = "child-a", SystemPrompt = "child-a", ModelRole = ModelRole.Fallback,
            ToolNames = ["authority_probe"], EmitStructuredFindings = false,
            Visibility = SubAgentVisibility.UserFacing
        });
        var toolConfig = new ToolConfig();
        // Every probe call asks for consent, so a child call opens an approval prompt.
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode> { ["authority_probe"] = ToolApprovalMode.Approval }
        };
        var policy = new ToolAccessPolicy(TestPaths, toolConfig,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var prompt = new StaticSystemPromptProvider("Test parent.");
        var spawner = new SubAgentSpawner(_clients, registry, policy, null, prompt,
            _preparation, NullLogger<SubAgentSpawner>.Instance, new SessionConfig());
        registry.RegisterCore(new SpawnAgentTool(profiles, spawner, TestPaths));
        // The runtime refuses a spawn when the requester cannot read the result file.
        registry.RegisterCore(new FileReadTool(toolConfig, TestPaths, new ToolPathPolicy([])));
        registry.RegisterCore(new CheckAgentRunTool());
        registry.RegisterCore(_tool);
        services.AddSingleton<IChatClientProvider>(_clients);
        services.AddSingleton(new ModelCapabilities { ModelId = "owner-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
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

    /// <summary>
    /// Owner lock-out (actor half of S5b). The owner of a child can read its status and cancel it
    /// from any later turn of the same requester. Another requester cannot.
    /// Source: background-subagent-runs "Status enforces run ownership" ("the owner can observe recorded
    /// progress"; "another requester ... the system denies that status request") and
    /// "Child execution keeps the parent's authority" (owner cancellation).
    /// The spawn turn has adopted context. The later turn has none, as with a fresh operator message.
    /// </summary>
    [Theory]
    [InlineData("alice", true)]
    [InlineData("bob", false)]
    public async Task Child_owner_controls_its_run_from_a_later_turn_and_another_requester_cannot(
        string requester, bool isOwner)
    {
        // Arrange
        var sessionId = new SessionId($"child-owner/owner-{requester}");
        var control = await ConfigureJournalAsync();
        var childGate = NewGate();
        _clients.A.NextResponseGate = childGate;
        _clients.Parent.PlannedResponses.Enqueue([Spawn("owner-spawn")]);
        _clients.Parent.PlannedResponses.Enqueue([new TextContent("Child started.")]);
        await JoinAsync(sessionId);

        try
        {
            await SendAsync(sessionId, "Start a child.", Operator("alice", adopted: true));
            var accepted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
            await _clients.A.FirstCallEntered.Task.WaitAsync(Deadline, Cancellation);
            await AwaitAssertAsync(() => Assert.Single(control.Committed.OfType<TurnRecorded>()),
                Deadline, cancellationToken: Cancellation);
            var runId = accepted.Run.RunId.Value;
            _clients.Parent.PlannedResponses.Enqueue([CheckRun("status-call", runId, cancel: false)]);
            _clients.Parent.PlannedResponses.Enqueue([CheckRun("cancel-call", runId, cancel: true)]);
            _clients.Parent.PlannedResponses.Enqueue([new TextContent("Checked.")]);

            // Act
            await SendAsync(sessionId, "Check my child, then cancel it.", Operator(requester));
            await AwaitAssertAsync(() => Assert.Contains(control.Committed.OfType<ToolCallRecorded>(),
                evt => evt.ToolResult.ToolCallId?.Value == "cancel-call"), Deadline, cancellationToken: Cancellation);

            // Assert
            var status = ToolResult(control, "status-call");
            var cancel = ToolResult(control, "cancel-call");
            _output.WriteLine($"requester={requester} status={status}");
            _output.WriteLine($"requester={requester} cancel={cancel}");
            if (isOwner)
            {
                Assert.DoesNotContain(NotAvailable, status, StringComparison.Ordinal);
                Assert.Contains($"\"run_id\":\"{runId}\"", status, StringComparison.Ordinal);
                Assert.DoesNotContain(NotAvailable, cancel, StringComparison.Ordinal);
                Assert.Contains(control.Committed.OfType<ChildRunEvent.CancellationRequested>(),
                    evt => evt.RunId == accepted.Run.RunId);
            }
            else
            {
                Assert.Contains(NotAvailable, status, StringComparison.Ordinal);
                Assert.Contains(NotAvailable, cancel, StringComparison.Ordinal);
                Assert.DoesNotContain(control.Committed, evt => evt is ChildRunEvent.CancellationRequested);
            }
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    /// <summary>
    /// Issue #2524. A reminder turn and any other turn of one session manage each other's child runs, and two
    /// turns that are not reminder turns keep the six-fact rule. The fire has the source shape of
    /// ReminderExecutionActor.InitializeCurrentSessionAsync (sender reminder-system, principal VerifiedAutomation,
    /// source kind reminder).
    /// Source: openspec/changes/reminder-turn-manages-own-children, background-subagent-runs
    /// "A reminder turn shares child runs with the other turns of its own session".
    /// Row 1 is the swap-1 exercise (a person started the run). Rows 3 and 4 follow the owner answer of
    /// 18 October 2026: a person, also another sender, controls a run that a reminder turn started.
    /// Rows 5 and 6: the delivery turn of a background job is not a person, and the rule still holds.
    /// Rows 7 and 8: a webhook turn can carry the source kind "reminder" because the webhook sender sets it.
    /// Such a turn is not a reminder turn, so it cannot reach the run of a person, and a person cannot reach its run.
    /// Rows 7 and 8 need a reminder test that also requires the authenticity LocalProcess.
    /// </summary>
    [Theory]
    [InlineData("person", "reminder", true)]
    [InlineData("reminder", "reminder", true)]
    [InlineData("reminder", "person", true)]
    [InlineData("reminder", "other-person", true)]
    [InlineData("reminder", "background-job", true)]
    [InlineData("background-job", "reminder", true)]
    [InlineData("person", "webhook-reminder", false)]
    [InlineData("webhook-reminder", "person", false)]
    public async Task Child_control_that_involves_a_reminder_turn_follows_the_rule_in_its_own_session(
        string starter, string controller, bool allowed)
    {
        // Arrange
        var sessionId = new SessionId($"child-owner/reminder-{starter}-{controller}");
        var control = await ConfigureJournalAsync();
        var childGate = NewGate();
        _clients.A.NextResponseGate = childGate;
        _clients.Parent.PlannedResponses.Enqueue([Spawn("reminder-spawn")]);
        _clients.Parent.PlannedResponses.Enqueue([new TextContent("Child started.")]);
        await JoinAsync(sessionId);

        try
        {
            await SendAsync(sessionId, "Start a child.", SourceOf(starter, "child-reminder:1000"));
            var accepted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
            await _clients.A.FirstCallEntered.Task.WaitAsync(Deadline, Cancellation);
            await AwaitAssertAsync(() => Assert.Single(control.Committed.OfType<TurnRecorded>()),
                Deadline, cancellationToken: Cancellation);
            var runId = accepted.Run.RunId.Value;
            _clients.Parent.PlannedResponses.Enqueue([CheckRun("status-call", runId, cancel: false)]);
            _clients.Parent.PlannedResponses.Enqueue([CheckRun("cancel-call", runId, cancel: true)]);
            _clients.Parent.PlannedResponses.Enqueue([new TextContent("Checked.")]);

            // Act
            await SendAsync(sessionId, "Check the child, then cancel it.", SourceOf(controller, "child-reminder:2000"));
            await AwaitAssertAsync(() => Assert.Contains(control.Committed.OfType<ToolCallRecorded>(),
                evt => evt.ToolResult.ToolCallId?.Value == "cancel-call"), Deadline, cancellationToken: Cancellation);

            // Assert
            var status = ToolResult(control, "status-call");
            var cancel = ToolResult(control, "cancel-call");
            _output.WriteLine($"starter={starter} controller={controller} status={status}");
            _output.WriteLine($"starter={starter} controller={controller} cancel={cancel}");
            if (allowed)
            {
                Assert.DoesNotContain(NotAvailable, status, StringComparison.Ordinal);
                Assert.Contains($"\"run_id\":\"{runId}\"", status, StringComparison.Ordinal);
                Assert.DoesNotContain(NotAvailable, cancel, StringComparison.Ordinal);
                Assert.Contains(control.Committed.OfType<ChildRunEvent.CancellationRequested>(),
                    evt => evt.RunId == accepted.Run.RunId);
            }
            else
            {
                Assert.Contains(NotAvailable, status, StringComparison.Ordinal);
                Assert.Contains(NotAvailable, cancel, StringComparison.Ordinal);
                Assert.DoesNotContain(control.Committed, evt => evt is ChildRunEvent.CancellationRequested);
            }
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    /// <summary>
    /// A turn in session B cannot reach a child run of session A when one side is a reminder turn. These tests pass
    /// today and must keep passing after the fix.
    /// Scope: session B does not hold the run, so the lookup in ControlChild fails before the comparison. These
    /// tests prove that structural isolation. They do not prove the session compare in
    /// TurnContext.CanControlChildRunOf, because a run id is random and session B cannot hold the run of session A.
    /// <c>TurnContextChildControlTests</c> and <c>ChildOwnerRelationPropertyTests</c> cover that compare.
    /// </summary>
    [Theory]
    [InlineData("person", "reminder")]
    [InlineData("reminder", "person")]
    public async Task Child_control_that_involves_a_reminder_turn_cannot_cross_sessions(string starter, string controller)
    {
        // Arrange
        var sessionA = new SessionId($"child-owner/cross-{starter}-a");
        var sessionB = new SessionId($"child-owner/cross-{starter}-b");
        var control = await ConfigureJournalAsync();
        var childGate = NewGate();
        _clients.A.NextResponseGate = childGate;
        _clients.Parent.PlannedResponses.Enqueue([Spawn("cross-spawn")]);
        _clients.Parent.PlannedResponses.Enqueue([new TextContent("Child started.")]);
        await JoinAsync(sessionA);
        await JoinAsync(sessionB);

        try
        {
            await SendAsync(sessionA, "Start a child.", SourceOf(starter, "child-reminder:1000"));
            var accepted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
            await _clients.A.FirstCallEntered.Task.WaitAsync(Deadline, Cancellation);
            await AwaitAssertAsync(() => Assert.Single(control.Committed.OfType<TurnRecorded>()),
                Deadline, cancellationToken: Cancellation);
            var runId = accepted.Run.RunId.Value;
            _clients.Parent.PlannedResponses.Enqueue([CheckRun("cross-status", runId, cancel: false)]);
            _clients.Parent.PlannedResponses.Enqueue([CheckRun("cross-cancel", runId, cancel: true)]);
            _clients.Parent.PlannedResponses.Enqueue([new TextContent("Checked.")]);

            // Act
            await SendAsync(sessionB, "Check the child of the other session.", SourceOf(controller, "child-reminder:3000"));
            await AwaitAssertAsync(() => Assert.Contains(control.Committed.OfType<ToolCallRecorded>(),
                evt => evt.ToolResult.ToolCallId?.Value == "cross-cancel"), Deadline, cancellationToken: Cancellation);

            // Assert
            Assert.Contains(NotAvailable, ToolResult(control, "cross-status"), StringComparison.Ordinal);
            Assert.Contains(NotAvailable, ToolResult(control, "cross-cancel"), StringComparison.Ordinal);
            Assert.DoesNotContain(control.Committed, evt => evt is ChildRunEvent.CancellationRequested);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    /// <summary>
    /// A second answer to a child approval prompt that is already answered. The first answer denies the call.
    /// The second answer tries to approve it. The second answer must not run the tool, and it must not tell
    /// the operator that the prompt "expired" or that the session "moved on or restarted".
    /// Source: tool-authorization TA-11 (a stale answer grants nothing); the first answer worked, so the
    /// "expired" notice is false. Evidence: Slack session D0AC6CKBK5K/1791982382.056659, 13:56:30 and 13:56:35.
    /// </summary>
    [Fact]
    public async Task Second_answer_to_an_answered_child_prompt_grants_nothing_and_gives_no_expired_notice()
    {
        // Arrange
        var sessionId = new SessionId("child-owner/second-answer");
        var childCompletion = NewGate();
        _clients.Parent.ToolCallsOnFirstCall = [Spawn("second-answer-spawn")];
        _clients.A.ToolCallsOnFirstCall = [Probe("second-answer-probe")];
        _clients.A.AfterToolCallResponse = _ => _clients.A.NextResponseGate = childCompletion;
        var subscriber = await JoinAsync(sessionId);

        try
        {
            await SendAsync(sessionId, "Start the child with an approval.", Operator("alice"));
            var approval = await FishApprovalAsync(subscriber, "authority_probe");
            Assert.IsType<CommandAck>(await AnswerAsync(sessionId, approval.CallId, ApprovalOptionKeys.Deny, "alice"));

            // Act
            var second = await AnswerAsync(sessionId, approval.CallId, ApprovalOptionKeys.ApproveOnce, "alice");
            var outputs = await DrainAsync(subscriber);

            // Assert
            var notices = outputs.OfType<TextOutput>().Select(item => item.Text).ToArray();
            _output.WriteLine($"second answer reply={second.GetType().Name}; text outputs=[{string.Join(" | ", notices)}]");
            Assert.IsType<CommandNack>(second);
            Assert.DoesNotContain(notices, text => text.Contains("expired", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(_tool.Contexts);
        }
        finally
        {
            childCompletion.TrySetResult();
        }
    }

    /// <summary>
    /// Control for the test above. An answer for a call that never existed still tells the operator that the
    /// prompt expired, and it grants nothing. A real prompt stays open, so the session is in a live wait.
    /// </summary>
    [Fact]
    public async Task Answer_for_a_call_that_never_existed_still_gets_the_expired_notice()
    {
        // Arrange
        var sessionId = new SessionId("child-owner/unknown-answer");
        var childCompletion = NewGate();
        _clients.Parent.ToolCallsOnFirstCall = [Spawn("unknown-answer-spawn")];
        _clients.A.ToolCallsOnFirstCall = [Probe("unknown-answer-probe")];
        _clients.A.AfterToolCallResponse = _ => _clients.A.NextResponseGate = childCompletion;
        var subscriber = await JoinAsync(sessionId);

        try
        {
            await SendAsync(sessionId, "Start the child with an approval.", Operator("alice"));
            var approval = await FishApprovalAsync(subscriber, "authority_probe");

            // Act
            var reply = await AnswerAsync(sessionId, new ToolCallId("never-existed"), ApprovalOptionKeys.ApproveOnce, "alice");
            var outputs = await DrainAsync(subscriber);

            // Assert
            var notices = outputs.OfType<TextOutput>().Select(item => item.Text).ToArray();
            _output.WriteLine($"unknown call reply={reply.GetType().Name}; text outputs=[{string.Join(" | ", notices)}]");
            Assert.IsType<CommandNack>(reply);
            Assert.Contains(notices, text => text.Contains("approval prompt has expired", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(_tool.Contexts);
            Assert.IsType<CommandAck>(await AnswerAsync(sessionId, approval.CallId, ApprovalOptionKeys.Deny, "alice"));
        }
        finally
        {
            childCompletion.TrySetResult();
        }
    }

    private async Task<AcceptanceFault> ConfigureJournalAsync()
    {
        var control = new AcceptanceFault(rejectAcceptance: false, holdAcceptance: false);
        await AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Ask<Done>(control, Deadline, Cancellation);
        return control;
    }

    private async Task<Akka.TestKit.TestProbe> JoinAsync(SessionId id)
    {
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, id, OutputFilter.Full);
        return subscriber;
    }

    private ValueTask<ToolInteractionRequest> FishApprovalAsync(Akka.TestKit.TestProbe subscriber, string toolName) =>
        subscriber.FishForMessageAsync<ToolInteractionRequest>(item => item.ToolName.Value == toolName, Deadline,
            cancellationToken: Cancellation);

    // The session emits output to subscribers before it replies to the sender. A marker sent after the reply
    // therefore arrives behind every output that the answer caused.
    private async Task<List<SessionOutput>> DrainAsync(Akka.TestKit.TestProbe subscriber)
    {
        var outputs = new List<SessionOutput>();
        subscriber.Ref.Tell(Done.Instance);
        await subscriber.FishForMessageAsync<object>(message =>
        {
            if (message is SessionOutput emitted)
                outputs.Add(emitted);
            return message is Done;
        }, Deadline, cancellationToken: Cancellation);
        return outputs;
    }

    private Task<ISessionResponse> AnswerAsync(SessionId id, ToolCallId callId, string optionKey, string sender) =>
        ActorRegistry.Get<SessionManagerActorKey>().Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = id, CallId = callId,
            SelectedKey = new ApprovalOptionKey(optionKey),
            SenderId = new SenderId(sender)
        }, Deadline, Cancellation);

    private Task<CommandAck> SendAsync(SessionId id, string content, MessageSource source) =>
        ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = id, Content = content, Source = source
        }, Deadline, Cancellation);

    private static string ToolResult(AcceptanceFault control, string callId) =>
        control.Committed.OfType<ToolCallRecorded>().Single(evt => evt.ToolResult.ToolCallId?.Value == callId).ToolResult.Content ?? string.Empty;

    private static MessageSource Operator(string sender, bool adopted = false) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(sender),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        ReceivedAt = TimeProvider.System.GetUtcNow(),
        AdoptedSpeakerIds = adopted ? [sender] : []
    };

    private static MessageSource SourceOf(string kind, string fireKey) =>
        kind switch
        {
            "reminder" => ReminderOf(fireKey),
            "background-job" => BackgroundJobOf(),
            "webhook-reminder" => WebhookReminderOf(),
            "other-person" => Operator("bob"),
            _ => Operator("local")
        };

    // The source shape of ReminderExecutionActor.InitializeCurrentSessionAsync for a TUI-style origin.
    private static MessageSource ReminderOf(string fireKey) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId("reminder-system"),
        MessageId = fireKey, TurnId = new TurnId(fireKey), ReminderId = new ReminderId(fireKey),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        { SourceKind = new SourceKind("reminder") },
        ReceivedAt = TimeProvider.System.GetUtcNow()
    };

    // The source shape of BackgroundJobManagerActor.DeliverResultToSession for a TUI-style origin.
    private static MessageSource BackgroundJobOf() => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId("background-job-system"),
        MessageId = "bg-job:test-1", TurnId = new TurnId("bg-job:test-1"), BackgroundJobId = new BackgroundJobId("bg-job:test-1"),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        { SourceKind = new SourceKind("background-job") },
        ReceivedAt = TimeProvider.System.GetUtcNow()
    };

    // The source shape of WebhookExecutionActor, with the event header "reminder". The webhook sender sets the
    // source kind. The audience, boundary, and channel match the session, so only the reminder test can deny it.
    private static MessageSource WebhookReminderOf() => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId("webhook:github"),
        MessageId = "github-delivery-1", TurnId = new TurnId("github-delivery-1"),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        { SourceKind = new SourceKind("reminder") },
        ReceivedAt = TimeProvider.System.GetUtcNow()
    };

    private static FunctionCallContent Spawn(string callId) => new(callId, "spawn_agent",
        new Dictionary<string, object?> { ["agent"] = "child-a", ["task"] = "Complete child-a.", ["_rationale"] = "Test child ownership." });
    private static FunctionCallContent Probe(string callId) => new(callId, "authority_probe",
        new Dictionary<string, object?> { ["_rationale"] = "Open an approval prompt." });
    private static FunctionCallContent CheckRun(string callId, string runId, bool cancel) => new(callId, "check_agent_run",
        new Dictionary<string, object?> { ["runId"] = runId, ["cancel"] = cancel, ["_rationale"] = "Control the owned child." });
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
