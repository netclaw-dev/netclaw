// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Persistence;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests(ITestOutputHelper output)
    : PersistenceTestKit(output: output), IAsyncDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);
    private readonly FakeChatClient _main = new();
    private readonly FakeChatClient _child = new();
    private readonly FakeChatClient _secondChild = new();
    private readonly TaskCompletionSource _secondChildRelease = NewSignal();
    private readonly FakeTimeProvider _time = new();
    private readonly ControllableWorkingContextSnapshotProvider _snapshots = new();
    private readonly TaskCompletionSource _childRelease = NewSignal();
    private TestSessionTempDirectory? _directory;
    private StartExecutor? _start;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        base.ConfigureAkka(builder, provider);
        builder.WithNetclawSerialization()
            .WithNetclawActors(provider.GetRequiredService<ShellExecutionEnvironment>());
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        _directory = TestSessionTempDirectory.Create("netclaw-child-owner-");
        services.AddSingleton(_directory);
        services.AddSingleton(_directory.Paths);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IWorkingContextSnapshotProvider>(_snapshots);
        services.AddSingleton(SecurityPolicyDefaults.Resolve(null));
        services.AddSingleton<BackgroundJobDefinitionStore>();
        services.AddSingleton(new SchedulingConfig());
        services.AddSingleton<ReminderDefinitionStore>();
        services.AddSingleton<ReminderHistoryStore>();
        services.AddSingleton<IOperationalNotificationSink>(NullNotificationSink.Instance);
        services.AddSingleton<IReminderChannelNotifier>(NullReminderChannelNotifier.Instance);
        services.AddSingleton<SessionPipeline>();
        services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());
        var clients = new RoleProvider(_main, _child, _secondChild);
        services.AddSingleton<IChatClientProvider>(clients);
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { SnapshotInterval = 1000, TitleGenerationInterval = 0 }
        });
        var prompt = new StaticSystemPromptProvider("Use the assigned tools.");
        services.AddSingleton<ISystemPromptProvider>(prompt);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "start_probe"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "spawn_agent"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "skill_load"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "control_probe"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "status_json_probe"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "file_read"), "file");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "neutral_probe"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "missing_receipt_probe"), "builtin");
        services.AddSingleton(registry);
        var policy = new ToolAccessPolicy(_directory.Paths, new ToolConfig(),
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var spawner = new SubAgentSpawner(clients, registry, policy, null, prompt, _snapshots,
            NullLogger<SubAgentSpawner>.Instance);
        _start = new StartExecutor(spawner, registry, policy);
        services.AddSingleton<IToolExecutor>(_start);
        services.AddLlmSessionCompositeRecords();
        _main.ToolCallsOnFirstCall = [new FunctionCallContent("start-1", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate the assigned task." })];
        _child.NextResponseGate = _childRelease;
        _secondChild.NextResponseGate = _secondChildRelease;
    }

    [Theory]
    [InlineData("acceptance")]
    [InlineData("started")]
    public async Task Neither_durable_start_boundary_can_dispatch_the_child_before_its_write_completes(string boundary)
    {
        var entered = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            if (boundary == "acceptance" ? record.Payload is not ChildRunAccepted : record.Payload is not ChildRunEvent.Started)
                return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        try
        {
            await SendOriginalAsync(manager);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var childIdentity = await Sys.ActorSelection($"{owner.Path}/child-run-{_start!.Prepared!.RunId.Value}")
                .Ask<ActorIdentity>(new Identify("held-start-boundary"), Ceiling, TestContext.Current.CancellationToken);
            if (boundary == "acceptance")
                Assert.Null(childIdentity.Subject);
            else
                Assert.NotNull(childIdentity.Subject);
            Assert.Equal(0, _child.CallCount);
            Assert.Equal(1, _main.CallCount);
            release.TrySetResult();
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            var events = await ReadJournalAsync();
            var admission = Assert.Single(events.OfType<ChildRunAccepted>());
            var started = Assert.Single(events.OfType<ChildRunEvent.Started>());
            Assert.True(Array.IndexOf(events, admission) < Array.IndexOf(events, started));
            Assert.Equal(admission.Run.RunId, started.RunId);
            Assert.Equal("original", admission.Run.OriginalContext.TurnId);
            Assert.Equal(new SenderId("operator-a"), admission.Run.OriginalContext.RequesterSenderId);
            Assert.Single(admission.Run.OriginInputIds);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(2, _main.CallCount);
            Assert.False(_childRelease.Task.IsCompleted);
            var result = Assert.Single(events.OfType<ToolCallRecorded>());
            Assert.Equal(new ToolCallId("start-1"), result.ToolResult.ToolCallId);
            Assert.False(result.LoopObservation!.Synthetic);
            using var body = JsonDocument.Parse(result.ToolResult.Content);
            Assert.Equal("Accepted", body.RootElement.GetProperty("state").GetString());
            Assert.Equal(admission.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
            Assert.Equal("check_agent_run", body.RootElement.GetProperty("control_tool").GetString());
            var paired = Assert.Single(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal("start-1", paired.CallId);
            Assert.Equal(result.ToolResult.Content, paired.Result!.ToString());
            Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(Retry(admission), Ceiling,
                TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task A_failed_acceptance_has_no_durable_child_or_child_provider_request()
    {
        var entered = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not ChildRunAccepted) return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        });
        var (owner, manager, _) = await CreateOwnerAsync();
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(async () =>
            {
                await SendOriginalAsync(manager);
                await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                Assert.Equal(0, _child.CallCount);
                release.TrySetResult();
                await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);
            var events = await ReadJournalAsync();
            Assert.Single(events.OfType<ToolBatchStarted>());
            Assert.Empty(events.OfType<ChildRunAccepted>());
            Assert.Empty(events.OfType<ChildRunEvent.Started>());
            Assert.Empty(events.OfType<ToolCallRecorded>());
            Assert.Equal(0, _child.CallCount);
            Assert.Equal(1, _main.CallCount);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task A_recorded_retry_survives_expired_dispatch_but_cannot_change_arguments_or_original_authority()
    {
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await SendOriginalAsync(manager);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var before = await ReadJournalAsync();
        var admission = Assert.Single(before.OfType<ChildRunAccepted>());
        var retry = Retry(admission);
        var staleNew = retry with
        {
            StartKey = new ChildRunStartKey.Tool(new ToolCallId("unadmitted-new-start"))
            { SessionId = Session, TurnId = new TurnId("original") }
        };
        var stale = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.Ask<ChildStartReply>(staleNew, Ceiling,
            TestContext.Current.CancellationToken));
        Assert.Contains("no longer owns", stale.Message, StringComparison.Ordinal);
        var repeated = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(retry, Ceiling,
            TestContext.Current.CancellationToken));
        Assert.Equal(admission.Run.RunId, repeated.Run.RunId);
        Assert.Equal(admission.Run.StartKey, repeated.Run.StartKey);
        Assert.Equal(admission.Run.ArgumentsDigest, repeated.Run.ArgumentsDigest);
        Assert.True(SessionState.SameCanonicalContext(admission.Run.OriginalContext, repeated.Run.OriginalContext));
        Assert.Equal(BackgroundChildState.Running, repeated.State);
        var conflict = retry with { Prepared = retry.Prepared with { ArgumentsDigest = new string('b', 64) } };
        Assert.IsType<ChildStartReply.Conflict>(await owner.Ask<ChildStartReply>(conflict, Ceiling,
            TestContext.Current.CancellationToken));
        var foreign = retry with
        {
            InvocationContext = retry.InvocationContext with { RequesterSenderId = new SenderId("operator-b") }
        };
        var denial = await Assert.ThrowsAsync<InvalidDataException>(() => owner.Ask<ChildStartReply>(foreign, Ceiling,
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(admission.Run.RunId.Value, denial.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(admission.Run.ScopeId.Value, denial.Message, StringComparison.Ordinal);
        Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(retry, Ceiling,
            TestContext.Current.CancellationToken));
        var after = await ReadJournalAsync();
        Assert.Equal(before.Length, after.Length);
        Assert.Single(after.OfType<ChildRunAccepted>());
        Assert.Single(after.OfType<ChildRunEvent.Started>());
        Assert.Equal(1, _child.CallCount);
        Assert.Equal(2, _main.CallCount);
    }

    [Fact]
    public async Task Cold_recovery_of_accepted_but_unstarted_child_returns_the_recorded_run_without_relaunch()
    {
        var entered = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not ChildRunEvent.Started) return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        });
        var (owner, manager, _) = await CreateOwnerAsync();
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await SendOriginalAsync(manager);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(0, _child.CallCount);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            release.TrySetResult();
            await Journal.OnWrite.Pass();
            var before = await ReadJournalAsync();
            var admission = Assert.Single(before.OfType<ChildRunAccepted>());
            Assert.Empty(before.OfType<ChildRunEvent.Started>());
            Assert.Empty(before.OfType<ToolCallRecorded>());
            var recoveredSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(recoveredSubscriber) { SessionId = Session, Filter = OutputFilter.Full }, recoveredSubscriber.Ref);
            await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var recovered = await OwnerAsync();
            Assert.NotEqual(owner, recovered);
            var repeated = Assert.IsType<ChildStartReply.Accepted>(await recovered.Ask<ChildStartReply>(Retry(admission), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(admission.Run.RunId, repeated.Run.RunId);
            Assert.Equal(BackgroundChildState.Lost, repeated.State);
            Assert.Equal(admission.Run.StartKey, repeated.Run.StartKey);
            Assert.Equal(admission.Run.ArgumentsDigest, repeated.Run.ArgumentsDigest);
            var lost = Assert.IsType<ChildRunTerminal>(repeated.Run.Terminal);
            Assert.True(lost.Lost);
            Assert.Equal(SubAgentRunOutcome.Failed, lost.Result.Outcome);
            Assert.Equal(SubAgentOutcomeReason.OwnerRestartLost, lost.Result.OutcomeReason);
            Assert.True(SessionState.SameCanonicalContext(admission.Run.OriginalContext, repeated.Run.OriginalContext));
            Assert.Equal(admission.Run.OriginInputIds, repeated.Run.OriginInputIds);
            var after = await ReadJournalPositionsAsync();
            Assert.Single(after.Select(record => record.Event).OfType<ChildRunAccepted>());
            Assert.Empty(after.Select(record => record.Event).OfType<ChildRunEvent.Started>());
            var terminalRow = Assert.Single(after, record => record.Event is ChildRunEvent.TerminalRecorded);
            var terminal = Assert.IsType<ChildRunEvent.TerminalRecorded>(terminalRow.Event);
            Assert.Equal(terminalRow.SequenceNr, terminal.TerminalSequenceNr);
            Assert.Equal(admission.Run.RunId, terminal.RunId);
            Assert.True(terminal.Terminal.Lost);
            Assert.Equal(SubAgentOutcomeReason.OwnerRestartLost, terminal.Terminal.Result.OutcomeReason);
            await CompletedAsync(recoveredSubscriber);
            var settled = await ReadJournalAsync();
            Assert.Single(settled.OfType<ToolBatchAbandoned>());
            var delivery = Assert.Single(settled.OfType<ChildRunEvent.DeliveryAdmitted>());
            Assert.Equal(admission.Run.RunId, delivery.RunId);
            var adoption = Assert.Single(settled.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
            Assert.True(SessionState.SameCanonicalContext(admission.Run.OriginalContext, adoption.TurnContext));
            var pairs = _main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>());
            Assert.Equal(delivery.Input.UserMessage.Content, Assert.IsType<string>(Assert.Single(pairs,
                result => result.CallId == delivery.Input.UserMessage.ToolCallId!.Value.Value).Result));
            Assert.Equal(0, _child.CallCount);
            Assert.Equal(2, _main.CallCount);
        }
        finally { release.TrySetResult(); }
    }

    private static readonly SessionId Session = new("signalr/background-owner-adversarial");
    private StartBackgroundChildRun Retry(ChildRunAccepted admission)
        => new(_start!.Prepared!, admission.Run.StartKey, admission.Run.OriginalContext,
            admission.Run.SourceOperation, _start.Token);
    private async Task<(IActorRef Owner, IActorRef Manager, Akka.TestKit.TestProbe Subscriber)> CreateOwnerAsync()
    {
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = Session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        return (await OwnerAsync(), manager, subscriber);
    }
    private Task<IActorRef> OwnerAsync() => Sys.ActorSelection(
        $"/user/session-manager/{Uri.EscapeDataString(Session.Value)}").ResolveOne(Ceiling, TestContext.Current.CancellationToken);
    private static Task<CommandAck> SendOriginalAsync(IActorRef manager) => manager.Ask<CommandAck>(new SendUserMessage
    {
        SessionId = Session, Content = "Delegate the neutral task.", Source = new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"), TurnId = new TurnId("original"),
            MessageId = "original", Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        }
    }, Ceiling, TestContext.Current.CancellationToken);
    private static ValueTask<object> CompletedAsync(Akka.TestKit.TestProbe subscriber)
        => subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
    private async Task<ISessionEvent[]> ReadJournalAsync()
        => (await ReadJournalPositionsAsync()).Select(record => record.Event).ToArray();
    private async Task<(ISessionEvent Event, long SequenceNr)[]> ReadJournalPositionsAsync()
    {
        var done = new TaskCompletionSource<(ISessionEvent Event, long SequenceNr)[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Sys.ActorOf(Props.Create(() => new ChildJournalReader($"session-{Session.Value}", done)));
        var watcher = CreateTestProbe();
        watcher.Watch(reader);
        var records = await done.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        Sys.Stop(reader);
        await watcher.ExpectTerminatedAsync(reader, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        return records;
    }
    private sealed class ChildJournalReader : ReceivePersistentActor
    {
        public ChildJournalReader(string id, TaskCompletionSource<(ISessionEvent Event, long SequenceNr)[]> done)
        {
            PersistenceId = id;
            var events = new List<(ISessionEvent Event, long SequenceNr)>();
            Recover<ISessionEvent>(evt => events.Add((evt, LastSequenceNr)));
            Recover<RecoveryCompleted>(_ => done.TrySetResult(events.ToArray()));
        }
        public override string PersistenceId { get; }
        public override Recovery Recovery => new(SnapshotSelectionCriteria.None);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StartExecutor(SubAgentSpawner spawner, ToolRegistry registry, ToolAccessPolicy policy) : IToolExecutor
    {
        public PreparedChildRun? Prepared { get; private set; }
        public SubAgentSpawner Spawner => spawner;
        public INetclawTool? StartTool { get; set; }
        public ToolInvocationContext? StartInvocation { get; private set; }
        public List<string?> ProbeRequesters { get; } = [];
        public List<string> ProbeCallIds { get; } = [];
        public CancellationToken Token { get; private set; }
        public CancellationToken? StartInvocationCancellation { get; set; }
        public CancellationToken? StartReplyWaitCancellation { get; set; }
        public TaskCompletionSource StartReplyWaitCancelled { get; } = NewSignal();
        public ToolInvocationContext? ControlInvocation { get; private set; }
        public CancellationToken ControlToken { get; private set; }
        public TaskCompletionSource? ControlCompletionGate { get; set; }
        public string? ChildMailbox { get; set; }
        public TaskCompletionSource<ChildControlReply> ControlReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            if (call.Name is "neutral_probe" or "missing_receipt_probe")
            {
                ProbeRequesters.Add(context.RunScope.DefaultDeliveryTarget?.DestinationId);
                ProbeCallIds.Add(call.CallId);
                if (call.Name == "neutral_probe")
                    context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
                return "The neutral result is unchanged.";
            }
            var factory = context.Invocation.SpawnChildActor
                ?? throw new InvalidOperationException("The start test requires the owner factory.");
            if (call.Name == "status_json_probe")
                return await new CheckAgentRunTool().ExecuteAsync(new Dictionary<string, object?>
                {
                    ["RunId"] = call.Arguments!["run_id"]!.ToString(),
                    ["Cancel"] = JsonSerializer.SerializeToElement(call.Arguments).GetProperty("cancel").GetBoolean()
                }, context.Invocation, ct);
            if (call.Name == "control_probe")
            {
                ControlInvocation = context.Invocation;
                ControlToken = ct;
                var reply = Assert.IsType<ChildControlReply>(await factory(new ChildControlRequest(
                    new SubAgentRunId(call.Arguments!["run_id"]!.ToString()!), true), "control", ct));
                ControlReply.TrySetResult(reply);
                if (ControlCompletionGate is { } completion) await completion.Task.WaitAsync(ct);
                context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
                return JsonSerializer.Serialize(new { found = reply.Run is not null, state = reply.Run?.State.ToString(),
                    dispatch_closed = reply.Run?.DispatchClosedAtMs is not null });
            }
            var scope = context.RunScope with { SpawnChildActor = async (payload, name, token) =>
            {
                Prepared = Assert.IsType<PreparedChildRun>(payload);
                if (ChildMailbox is { } mailbox) Prepared = Prepared with { Props = Prepared.Props.WithMailbox(mailbox) };
                Token = token;
                try { return await factory(Prepared, name, StartReplyWaitCancellation ?? token); }
                catch (OperationCanceledException) when (StartReplyWaitCancellation?.IsCancellationRequested == true
                    || StartInvocationCancellation?.IsCancellationRequested == true)
                {
                    StartReplyWaitCancelled.TrySetResult();
                    throw;
                }
            }};
            StartInvocation = new ToolInvocationContext(scope, context.ExecutionTimeout, context.Outputs);
            if (StartTool is SkillLoadTool routedTool)
            {
                registry.ReplaceCore(routedTool);
                var dispatchContext = new ToolExecutionContext(scope, context.ExecutionTimeout, context.Outputs);
                dispatchContext.Approval.RestoreAuthorizationAttemptId(context.Approval.AuthorizationAttemptId);
                return await new DispatchingToolExecutor(registry, policy).ExecuteAsync(
                    call, dispatchContext, StartInvocationCancellation ?? ct);
            }
            if (StartTool is { } startTool)
                return await startTool.ExecuteAsync(call.Arguments, StartInvocation, StartInvocationCancellation ?? ct);
            return await spawner.StartRunAsync(new SubAgentProfile
            {
                Name = "worker", Description = "Complete the neutral task.", SystemPrompt = "Complete the assigned task.",
                ModelRole = call.CallId == "start-2" ? ModelRole.Fallback : ModelRole.Compaction, ToolNames = ["file_read"], EmitStructuredFindings = false
            }, "Inspect the neutral fixture.", null,
                new ToolInvocationContext(scope, context.ExecutionTimeout, context.Outputs), StartInvocationCancellation ?? ct, null);
        }
    }

    private sealed class RoleProvider(IChatClient main, IChatClient child, IChatClient secondChild) : IChatClientProvider
    {
        public IChatClient Child { get; set; } = child;
        public IChatClient GetClient(ModelRole role) => role switch
        {
            ModelRole.Main => main,
            ModelRole.Fallback => secondChild,
            _ => Child
        };
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        _childRelease.TrySetResult();
        _secondChildRelease.TrySetResult();
        try { await base.DisposeAsync(); }
        finally { if (_directory is not null) await _directory.DisposeAsync(); }
    }
}
