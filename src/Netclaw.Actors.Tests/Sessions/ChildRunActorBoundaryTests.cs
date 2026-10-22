// -----------------------------------------------------------------------
// <copyright file="ChildRunActorBoundaryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Persistence.Journal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ChildRunActorBoundaryTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly BoundaryClients _clients = new();
    private readonly PreparationBarrier _preparation = new();
    private readonly AuthorityTool _tool = new();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

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
            profiles.Register(new SubAgentProfile
            {
                Name = name, Description = name, SystemPrompt = name, ModelRole = role,
                ToolNames = ["authority_probe"], EmitStructuredFindings = false,
                Visibility = SubAgentVisibility.UserFacing
            });
        var toolConfig = new ToolConfig();
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
        registry.RegisterCore(_tool);
        services.AddSingleton<IChatClientProvider>(_clients);
        services.AddSingleton(new ModelCapabilities { ModelId = "boundary-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { SnapshotInterval = 0, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(prompt);
        services.AddSingleton(registry);
        services.AddSingleton(profiles);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(registry, policy, null,
            NullLogger<DispatchingToolExecutor>.Instance));
    }

    [Fact]
    public async Task Acceptance_write_failure_starts_no_child_and_returns_no_accepted_receipt()
    {
        // Arrange
        var sessionId = new SessionId("boundary/reject-acceptance");
        var control = await ConfigureJournalAsync(rejectAcceptance: true, holdAcceptance: false);
        _clients.Parent.ToolCallsOnFirstCall = [Spawn("accept-a", "child-a")];
        _clients.A.ToolCallsOnFirstCall = [Probe("forbidden-child-effect")];
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, sessionId, OutputFilter.Full);
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);

        // Act
        await SendAsync(sessionId, "Start child A.", "original-owner");
        var attempted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        subscriber.Ref.Tell(Done.Instance);
        var outputs = new List<SessionOutput>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            if (message is SessionOutput emitted)
                outputs.Add(emitted);
            return message is Done;
        }, Deadline, cancellationToken: Cancellation);

        // Assert
        Assert.Equal("accept-a", Assert.IsType<ChildRunStartKey.Tool>(attempted.Run.StartKey).CallId.Value);
        Assert.Equal(sessionId, attempted.SessionId);
        Assert.DoesNotContain(control.Committed, evt => evt is ChildRunAccepted);
        Assert.Single(_clients.Parent.ReceivedMessages);
        Assert.Empty(_clients.Parent.ReceivedMessages.SelectMany(Results));
        Assert.Equal(0, _clients.A.CallCount);
        Assert.Equal(0, _clients.B.CallCount);
        Assert.Empty(_tool.Contexts);
        Assert.DoesNotContain(outputs.OfType<ToolResultOutput>(), result =>
            result.CallId.Value == "accept-a" && result.Result == attempted.AcceptanceResult!.Content);
        Assert.DoesNotContain(outputs.OfType<SubAgentOutput>(), result => result.Phase == SubAgentPhase.Started);
    }

    [Fact]
    public async Task Recovery_after_acceptance_commit_keeps_the_exact_receipt_without_child_relaunch()
    {
        // Arrange
        var sessionId = new SessionId("boundary/accepted-before-callback");
        var control = await ConfigureJournalAsync(rejectAcceptance: false, holdAcceptance: true);
        _clients.Parent.ToolCallsOnFirstCall = [Spawn("original-acceptance-call", "child-a")];
        _clients.A.ToolCallsOnFirstCall = [Probe("forbidden-relaunch-effect")];
        var session = await JoinAsync(sessionId);
        var recoveredGate = NewGate();

        try
        {
            // Act
            await SendAsync(sessionId, "Start child A.", "original-owner");
            var accepted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
            Assert.Same(accepted, Assert.Single(control.Committed.OfType<ChildRunAccepted>()));
            Assert.False(control.ReleaseAcceptance.Task.IsCompleted);
            Assert.Equal(0, _clients.A.CallCount);
            Assert.Single(_clients.Parent.ReceivedMessages);
            await StopAsync(session);
            control.ReleaseAcceptance.TrySetResult();
            await control.WriteReturned.Task.WaitAsync(Deadline, Cancellation);
            _clients.Parent.NextResponseGate = recoveredGate;
            await JoinAsync(sessionId);
            await AwaitAssertAsync(() => Assert.True(_clients.Parent.CallCount >= 2), Deadline,
                cancellationToken: Cancellation);

            // Assert
            var resumed = _clients.Parent.ReceivedMessages[1];
            var receipt = Assert.Single(Results(resumed), result => result.CallId == "original-acceptance-call");
            Assert.Equal(accepted.AcceptanceResult!.Content, Assert.IsType<string>(receipt.Result));
            var acceptanceCall = Assert.Single(Calls(resumed), call => call.CallId == "original-acceptance-call");
            Assert.Equal("spawn_agent", acceptanceCall.Name);
            using var body = JsonDocument.Parse(Assert.IsType<string>(receipt.Result));
            Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(accepted.Run.ScopeId.Value, body.RootElement.GetProperty("scope_id").GetString());
            Assert.Equal("accepted", body.RootElement.GetProperty("state").GetString());
            Assert.Equal(0, _clients.A.CallCount);
            Assert.Equal(0, _clients.B.CallCount);
            Assert.Empty(_tool.Contexts);
            recoveredGate.TrySetResult();
            await AwaitAssertAsync(() => Assert.Contains(control.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
                evt => evt.RunId == accepted.Run.RunId && evt.Terminal.Lost && !evt.Terminal.Result.Success),
                Deadline, cancellationToken: Cancellation);
            await AwaitAssertAsync(() => Assert.Contains(_clients.Parent.ReceivedMessages,
                request => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                    && message.Text.Contains(accepted.Run.RunId.Value, StringComparison.Ordinal)
                    && message.Text.Contains("result.md", StringComparison.Ordinal))),
                Deadline, cancellationToken: Cancellation);
            var notice = Assert.Single(control.Committed.OfType<InputAdmitted>(), evt => evt.ChildNotice?.RunId == accepted.Run.RunId);
            Assert.Contains("reason=actor_stopped", notice.UserMessage.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(_clients.Parent.ReceivedMessages, request => Calls(request).Any(call => call.Name == "child_run_result"));
            await AwaitAssertAsync(() => Assert.Single(control.Committed.OfType<TurnRecorded>(),
                evt => evt.ConsumedInputIds.Contains(notice.InputId)), Deadline, cancellationToken: Cancellation);
            await StopAsync(await ResolveSessionAsync(sessionId));
            Assert.All(_clients.Parent.ReceivedMessages.Skip(1), request =>
                Assert.Single(Results(request), result => result.CallId == "original-acceptance-call"));
            Assert.Single(control.Committed.OfType<ChildRunAccepted>());
            Assert.Equal(0, _clients.A.CallCount);
            Assert.Empty(_tool.Contexts);
        }
        finally
        {
            control.ReleaseAcceptance.TrySetResult();
            recoveredGate.TrySetResult();
        }
    }

    [Fact]
    public async Task Active_child_approval_count_changes_only_after_the_owner_resolves_approval()
    {
        // Arrange
        var sessionId = new SessionId("boundary/active-child-approval");
        var control = await ConfigureJournalAsync(rejectAcceptance: false, holdAcceptance: false);
        var childCompletion = NewGate();
        _clients.Parent.ToolCallsOnFirstCall = [Spawn("approval-child", "child-a")];
        _clients.A.ToolCallsOnFirstCall = [Probe("active-child-authority")];
        _clients.A.AfterToolCallResponse = _ => _clients.A.NextResponseGate = childCompletion;
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, sessionId, OutputFilter.Full);
        var session = await ResolveSessionAsync(sessionId);

        try
        {
            // Act
            await SendAsync(sessionId, "Start the child with an approval.", "original-owner");
            var approval = await subscriber.FishForMessageAsync<ToolInteractionRequest>(
                item => item.ToolName.Value == "authority_probe", Deadline, cancellationToken: Cancellation);
            var accepted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
            var ownerStatus = await session.Ask<string>(new ChildRunControl(
                accepted.Run.RunId, false, accepted.Run.OriginalContext), Deadline, Cancellation);
            var foreignStatus = await session.Ask<string>(new ChildRunControl(
                accepted.Run.RunId, false,
                accepted.Run.OriginalContext with { RequesterSenderId = new SenderId("other-requester") }), Deadline, Cancellation);

            // Assert
            Assert.Equal("original-owner", approval.RequesterSenderId?.Value);
            Assert.Empty(_tool.Contexts);
            using var pending = JsonDocument.Parse(ownerStatus);
            Assert.Equal(accepted.Run.RunId.Value, pending.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(1, pending.RootElement.GetProperty("pending_approval_count").GetInt32());
            Assert.False(pending.RootElement.GetProperty("terminal").GetBoolean());
            Assert.Equal("Error: This child run is not available to the requester.", foreignStatus);
            Assert.DoesNotContain(control.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
            var reply = await ActorRegistry.Get<SessionManagerActorKey>().Ask<ISessionResponse>(new ToolInteractionResponse
            {
                SessionId = sessionId, CallId = approval.CallId,
                SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
                SenderId = new SenderId("original-owner")
            }, Deadline, Cancellation);
            Assert.IsType<CommandAck>(reply);
            await AwaitAssertAsync(() =>
            {
                Assert.Single(_tool.Contexts);
                Assert.Equal(2, _clients.A.CallCount);
                Assert.Null(_clients.A.NextResponseGate);
            }, Deadline, cancellationToken: Cancellation);
            var resolvedStatus = await session.Ask<string>(new ChildRunControl(
                accepted.Run.RunId, false, accepted.Run.OriginalContext), Deadline, Cancellation);
            using var resolved = JsonDocument.Parse(resolvedStatus);
            Assert.Equal(accepted.Run.RunId.Value, resolved.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(0, resolved.RootElement.GetProperty("pending_approval_count").GetInt32());
            Assert.False(resolved.RootElement.GetProperty("terminal").GetBoolean());
            Assert.False(childCompletion.Task.IsCompleted);
            Assert.DoesNotContain(control.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
            childCompletion.TrySetResult();
            await AwaitAssertAsync(() => Assert.Contains(control.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
                evt => evt.RunId == accepted.Run.RunId && evt.Terminal.Result.Success), Deadline, cancellationToken: Cancellation);
        }
        finally
        {
            childCompletion.TrySetResult();
        }
    }

    [Fact]
    public async Task A_cancelled_child_approval_is_closed_for_replay_and_logs_nothing_after_a_restart()
    {
        // Arrange: the child waits on an approval that nobody answers. The parent cancels the child.
        var sessionId = new SessionId("boundary/cancel-open-child-approval");
        var control = await ConfigureJournalAsync(rejectAcceptance: false, holdAcceptance: false);
        _clients.Parent.ToolCallsOnFirstCall = [Spawn("cancel-approval-child", "child-a")];
        _clients.A.ToolCallsOnFirstCall = [Probe("forbidden-after-cancel")];
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, sessionId, OutputFilter.Full);
        var session = await ResolveSessionAsync(sessionId);
        await SendAsync(sessionId, "Start the child with an approval.", "original-owner");
        await subscriber.FishForMessageAsync<ToolInteractionRequest>(
            item => item.ToolName.Value == "authority_probe", Deadline, cancellationToken: Cancellation);
        var accepted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
        using var cancelled = JsonDocument.Parse(await session.Ask<string>(
            new ChildRunControl(accepted.Run.RunId, true, accepted.Run.OriginalContext), Deadline, Cancellation));
        await AwaitAssertAsync(() => Assert.Contains(control.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
            evt => evt.RunId == accepted.Run.RunId), Deadline, cancellationToken: Cancellation);
        var live = await session.Ask<string>(new ChildRunControl(
            accepted.Run.RunId, false, accepted.Run.OriginalContext), Deadline, Cancellation);
        using (var liveJson = JsonDocument.Parse(live))
            Assert.Equal(0, liveJson.RootElement.GetProperty("pending_approval_count").GetInt32());
        await StopAsync(session);

        // Act + Assert: replay of the cancel and terminal records closes the entry. The journal still holds the
        // approval request, and replay must not log it as a request.
        await EventFilter.Info(contains: "Tool approval requested").ExpectAsync(0, async () =>
        {
            await JoinAsync(sessionId);
        }, Deadline, Cancellation);
        Assert.Empty(_tool.Contexts);
    }

    [Theory]
    [InlineData("original-owner", true)]
    [InlineData("other-requester", false)]
    public async Task Child_control_keeps_requester_authority_and_acknowledges_cancellation_after_the_journal(
        string requester, bool authorized)
    {
        // Arrange
        var sessionId = new SessionId("boundary/child-control");
        var control = await ConfigureJournalAsync(rejectAcceptance: false, holdAcceptance: false);
        control.HoldCancellation = true;
        var childGate = NewGate();
        _clients.A.NextResponseGate = childGate;
        _clients.A.ToolCallsOnFirstCall = [Probe("forbidden-after-cancel")];
        _clients.Parent.ToolCallsOnFirstCall = [Spawn("controlled-child", "child-a")];
        var session = await JoinAsync(sessionId);

        try
        {
            await SendAsync(sessionId, "Start the child.", "original-owner");
            await _clients.A.FirstCallEntered.Task.WaitAsync(Deadline, Cancellation);
            var accepted = await control.Acceptance.Task.WaitAsync(Deadline, Cancellation);
            var candidate = accepted.Run.OriginalContext with { RequesterSenderId = new SenderId(requester) };

            // Act
            var status = await session.Ask<string>(new ChildRunControl(accepted.Run.RunId, false, candidate), Deadline, Cancellation);
            var cancel = session.Ask<string>(new ChildRunControl(accepted.Run.RunId, true, candidate), Deadline, Cancellation);

            // Assert
            if (authorized)
            {
                using var json = JsonDocument.Parse(status);
                Assert.Equal(accepted.Run.RunId.Value, json.RootElement.GetProperty("run_id").GetString());
                Assert.Equal(accepted.Run.ScopeId.Value, json.RootElement.GetProperty("scope_id").GetString());
                Assert.Equal("accepted", json.RootElement.GetProperty("state").GetString());
                Assert.Equal(0, json.RootElement.GetProperty("pending_approval_count").GetInt32());
                Assert.False(json.RootElement.GetProperty("terminal").GetBoolean());
            }
            else
            {
                Assert.Equal("Error: This child run is not available to the requester.", status);
                Assert.Equal(status, await cancel.WaitAsync(Deadline, Cancellation));
                Assert.DoesNotContain(control.Committed, evt => evt is ChildRunEvent.CancellationRequested);
                Assert.False(childGate.Task.IsCompleted);
                var ownerStatus = await session.Ask<string>(new ChildRunControl(
                    accepted.Run.RunId, false, accepted.Run.OriginalContext), Deadline, Cancellation);
                using var json = JsonDocument.Parse(ownerStatus);
                Assert.Equal("accepted", json.RootElement.GetProperty("state").GetString());
                cancel = session.Ask<string>(new ChildRunControl(
                    accepted.Run.RunId, true, accepted.Run.OriginalContext), Deadline, Cancellation);
            }

            var intent = await control.Cancellation.Task.WaitAsync(Deadline, Cancellation);
            Assert.Equal(accepted.Run.RunId, intent.RunId);
            Assert.Same(intent, Assert.Single(control.Committed.OfType<ChildRunEvent.CancellationRequested>()));
            Assert.False(cancel.IsCompleted);
            Assert.False(childGate.Task.IsCompleted);
            control.ReleaseCancellation.TrySetResult();
            using var acknowledged = JsonDocument.Parse(await cancel.WaitAsync(Deadline, Cancellation));
            Assert.Equal("cancelling", acknowledged.RootElement.GetProperty("state").GetString());
            Assert.False(acknowledged.RootElement.GetProperty("terminal").GetBoolean());
            await AwaitAssertAsync(() => Assert.Contains(control.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
                evt => evt.RunId == accepted.Run.RunId && !evt.Terminal.Result.Success),
                Deadline, cancellationToken: Cancellation);
            await StopAsync(session);
            Assert.True(childGate.Task.IsCanceled);
            Assert.Equal(1, _clients.A.CallCount);
            Assert.Empty(_tool.Contexts);
            Assert.DoesNotContain(control.Committed.OfType<ToolCallRecorded>(),
                evt => evt.ToolResult.ToolCallId?.Value == "forbidden-after-cancel");
        }
        finally
        {
            control.ReleaseCancellation.TrySetResult();
            childGate.TrySetResult();
        }
    }

    private async Task<AcceptanceFault> ConfigureJournalAsync(bool rejectAcceptance, bool holdAcceptance)
    {
        var control = new AcceptanceFault(rejectAcceptance, holdAcceptance);
        await AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Ask<Done>(control, Deadline, Cancellation);
        return control;
    }

    private async Task<IActorRef> JoinAsync(SessionId id)
    {
        await JoinSessionAsync(ActorRegistry.Get<SessionManagerActorKey>(), CreateTestProbe(), id, OutputFilter.Full);
        return await ResolveSessionAsync(id);
    }

    private Task<IActorRef> ResolveSessionAsync(SessionId id) =>
        Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(id.Value)}").ResolveOne(Deadline, Cancellation);

    private async Task StopAsync(IActorRef session)
    {
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        Sys.Stop(session);
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
    }

    private Task<CommandAck> SendAsync(SessionId id, string content, string sender) =>
        ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = id, Content = content,
            Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR, SenderId = new SenderId(sender),
                Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
                Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
                ReceivedAt = TimeProvider.System.GetUtcNow()
            }
        }, Deadline, Cancellation);

    private static FunctionCallContent Spawn(string callId, string agent) => new(callId, "spawn_agent",
        new Dictionary<string, object?> { ["agent"] = agent, ["task"] = $"Complete {agent}.", ["_rationale"] = "Test the child boundary." });
    private static FunctionCallContent Probe(string callId) => new(callId, "authority_probe",
        new Dictionary<string, object?> { ["_rationale"] = "Check original authority." });
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IEnumerable<FunctionCallContent> Calls(IEnumerable<ChatMessage> messages) => messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>());
    private static IEnumerable<FunctionResultContent> Results(IEnumerable<ChatMessage> messages) => messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>());

    internal sealed class BoundaryClients : IChatClientProvider
    {
        public FakeChatClient Parent { get; } = new();
        public FakeChatClient A { get; } = new();
        public FakeChatClient B { get; } = new();
        public IChatClient GetClient(ModelRole role) => role switch
        {
            ModelRole.Main => Parent, ModelRole.Fallback => A, ModelRole.Compaction => B,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
    }

    internal sealed class PreparationBarrier : IWorkingContextSnapshotProvider
    {
        private int _holdNext;
        public TaskCompletionSource Entered { get; } = NewGate();
        public TaskCompletionSource Release { get; } = NewGate();
        public void HoldNext() => Interlocked.Exchange(ref _holdNext, 1);
        public async Task<WorkingContextSnapshot> CreateAsync(WorkingContext context, TrustAudience audience, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _holdNext, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new WorkingContextSnapshot { WorkingContext = context, Git = new GitWorkingContextInspection.Skipped() };
        }
    }

    internal sealed class AuthorityTool : INetclawTool
    {
        public ConcurrentQueue<ToolInvocationContext> Contexts { get; } = new();
        public string Name => "authority_probe";
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Observe original child authority.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => "confirmed", Name, Description);
        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");
        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
        {
            Contexts.Enqueue(context);
            return Task.FromResult("confirmed");
        }
    }
}

internal sealed class AcceptanceFault(bool rejectAcceptance, bool holdAcceptance)
{
    public bool RejectAcceptance { get; } = rejectAcceptance;
    public bool HoldAcceptance { get; } = holdAcceptance;
    public bool HoldCancellation { get; set; }
    public TaskCompletionSource<ChildRunEvent.CancellationRequested> Cancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<object> Committed { get; } = new();
    public TaskCompletionSource<ChildRunAccepted> Acceptance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseAcceptance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WriteReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The journal rejects this many later writes that admit an approval notice (source kind child-consent). Akka then
    // reports a WriteMessageRejected for each event of that atomic write. Other writes are stored as usual.
    private int _consentNoticeRejections;

    public TaskCompletionSource<InputAdmitted> ConsentNoticeRejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RejectNextConsentNotice() => Interlocked.Exchange(ref _consentNoticeRejections, 1);

    public bool TryTakeConsentNoticeRejection() => Interlocked.CompareExchange(ref _consentNoticeRejections, 0, 1) == 1;

    // The journal stores the next matching admission with a damaged authority record (no turn ID), so that
    // TurnContext.TryFromRecord fails for it after a replay. The live actor keeps the event that it wrote.
    private Func<InputAdmitted, bool>? _damageMatch;

    public TaskCompletionSource<InputAdmitted> AdmissionDamaged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void DamageNextAdmission(Func<InputAdmitted, bool> match) => Interlocked.Exchange(ref _damageMatch, match);

    public InputAdmitted? TryDamage(InputAdmitted admitted)
    {
        var match = Volatile.Read(ref _damageMatch);
        if (match is null || !match(admitted) || Interlocked.CompareExchange(ref _damageMatch, null, match) != match)
            return null;
        var damaged = admitted with { TurnContext = admitted.TurnContext with { TurnId = string.Empty } };
        AdmissionDamaged.TrySetResult(damaged);
        return damaged;
    }
}

public sealed class AcceptanceBoundaryJournal : MemoryJournal
{
    private AcceptanceFault? _fault;
    protected override bool ReceivePluginInternal(object message)
    {
        if (message is AcceptanceFault fault)
        {
            _fault = fault;
            Sender.Tell(Done.Instance);
            return true;
        }
        return base.ReceivePluginInternal(message);
    }

    protected override async Task<IImmutableList<Exception>> WriteMessagesAsync(IEnumerable<AtomicWrite> messages, CancellationToken cancellationToken)
    {
        var writes = messages.ToArray();
        if (_fault is { } damageFault)
            writes = writes.Select(write => Damaged(damageFault, write)).ToArray();
        var events = writes.SelectMany(write => (IEnumerable<IPersistentRepresentation>)write.Payload).Select(item => item.Payload).ToArray();
        if (_fault is { } noticeFault && writes.Any(IsConsentNoticeWrite) && noticeFault.TryTakeConsentNoticeRejection())
            return await RejectConsentNoticeAsync(noticeFault, writes, cancellationToken);
        var accepted = events.OfType<ChildRunAccepted>().SingleOrDefault();
        if (accepted is not null && _fault is { RejectAcceptance: true } rejected)
        {
            rejected.Acceptance.TrySetResult(accepted);
            throw new IOException("The test journal rejected child acceptance.");
        }
        // The base journal stores the event before this barrier withholds the callback acknowledgement.
        var result = await base.WriteMessagesAsync(writes, cancellationToken);
        if (_fault is { } active)
        {
            foreach (var evt in events)
                active.Committed.Enqueue(evt);
            if (events.OfType<ChildRunEvent.CancellationRequested>().SingleOrDefault() is { } cancellation)
            {
                active.Cancellation.TrySetResult(cancellation);
                if (active.HoldCancellation)
                    await active.ReleaseCancellation.Task.WaitAsync(cancellationToken);
            }
            if (accepted is not null)
            {
                active.Acceptance.TrySetResult(accepted);
                if (active.HoldAcceptance)
                    await active.ReleaseAcceptance.Task.WaitAsync(cancellationToken);
                active.WriteReturned.TrySetResult();
            }
        }
        return result;
    }

    // One result for each atomic write, in order. A null result is a success, and a non-null result is a rejection.
    private async Task<IImmutableList<Exception>> RejectConsentNoticeAsync(
        AcceptanceFault fault, IReadOnlyList<AtomicWrite> writes, CancellationToken cancellationToken)
    {
        var results = new List<Exception>();
        foreach (var write in writes)
        {
            if (IsConsentNoticeWrite(write))
            {
                fault.ConsentNoticeRejected.TrySetResult(Payloads(write).OfType<InputAdmitted>().First(IsConsentNotice));
                results.Add(new InvalidOperationException("The test journal rejected the approval notice admission."));
                continue;
            }

            var written = await base.WriteMessagesAsync([write], cancellationToken);
            foreach (var evt in Payloads(write))
                fault.Committed.Enqueue(evt);
            results.Add(written?.FirstOrDefault()!);
        }

        return results.ToImmutableList();
    }

    private static AtomicWrite Damaged(AcceptanceFault fault, AtomicWrite write)
    {
        var items = ((IEnumerable<IPersistentRepresentation>)write.Payload).ToArray();
        var changed = false;
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].Payload is InputAdmitted admitted && fault.TryDamage(admitted) is { } damaged)
            {
                items[index] = items[index].WithPayload(damaged);
                changed = true;
            }
        }

        return changed ? new AtomicWrite(items.ToImmutableList()) : write;
    }

    private static IEnumerable<object> Payloads(AtomicWrite write)
        => ((IEnumerable<IPersistentRepresentation>)write.Payload).Select(static item => item.Payload);

    private static bool IsConsentNoticeWrite(AtomicWrite write) => Payloads(write).OfType<InputAdmitted>().Any(IsConsentNotice);

    private static bool IsConsentNotice(InputAdmitted admitted) => admitted.TurnContext.SourceKind == "child-consent";
}
