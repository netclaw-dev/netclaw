// -----------------------------------------------------------------------
// <copyright file="BackgroundChildApprovalGrantAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Dispatch.MessageQueues;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildApprovalGrantAdversarialTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);
    private static readonly SessionId Session = new("signalr/child-approval-grant-adversarial");
    private readonly FakeChatClient _main = new();
    private readonly FakeChatClient _child = new();
    private readonly TaskCompletionSource _firstChildResponse = NewSignal();
    private readonly TaskCompletionSource _nextChildResponse = NewSignal();
    private readonly ControllableWorkingContextSnapshotProvider _snapshots = new();
    private ToolApprovalStore _store = null!;
    private int _approvedEffects;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        var clients = new RoleProvider(_main, _child);
        services.AddSingleton<IChatClientProvider>(clients);
        services.AddSingleton(new ModelCapabilities { ModelId = "fake", ContextWindowTokens = 2_000_000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { SnapshotInterval = 1000, TitleGenerationInterval = 0, CompactionThreshold = 0.99 }
        });
        var prompt = new StaticSystemPromptProvider("Use the assigned tools.");
        services.AddSingleton<ISystemPromptProvider>(prompt);
        services.AddSingleton<IWorkingContextSnapshotProvider>(_snapshots);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "start_probe"), "builtin");
        registry.RegisterCore(new FakeNetclawTool("approval_probe", "The actual approved result.", "builtin", invocation =>
        {
            Interlocked.Increment(ref _approvedEffects);
            invocation.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
        }));
        services.AddSingleton(registry);
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            { ["approval_probe"] = ToolApprovalMode.Approval }
        };
        var policy = new ToolAccessPolicy(TestPaths, config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        services.AddSingleton(policy);
        _store = new ToolApprovalStore(TestPaths.ToolApprovalsPath, TimeProvider.System);
        services.AddSingleton(_store);
        services.AddSingleton<IToolApprovalService, AkkaToolApprovalService>();
        services.AddSingleton<IToolExecutor>(provider => new StartExecutor(new SubAgentSpawner(
            clients, registry, policy, provider.GetRequiredService<IToolApprovalService>(), prompt, _snapshots,
            NullLogger<SubAgentSpawner>.Instance)));
        _main.ToolCallsOnFirstCall = [new FunctionCallContent("start-1", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate the neutral task." })];
        _child.ToolCallsOnFirstCall = [new FunctionCallContent("child-approval-1", "approval_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Request the neutral operation." })];
        _child.NextResponseGate = _firstChildResponse;
    }

    [Fact]
    public async Task A_live_original_requester_grants_one_reusable_approval_and_one_exact_retry()
    {
        var gate = InstallWaitReplyGate();
        try
        {
            var (manager, _, prompt) = await StartPromptAsync();
            var denied = Assert.IsType<CommandNack>(await manager.Ask<ISessionResponse>(Answer(prompt, "operator-b"), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(ApprovalNackReasons.WrongRequester, denied.Reason);
            Assert.Empty(_store.Load().Audiences);
            Assert.Equal(0, Volatile.Read(ref _approvedEffects));
            Assert.IsType<CommandAck>(await manager.Ask<ISessionResponse>(Answer(prompt, "operator-a"), Ceiling,
                TestContext.Current.CancellationToken));
            await AwaitAssertAsync(() => Assert.Equal(2, _child.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref _approvedEffects));
            var stored = _store.Load();
            var audience = Assert.Single(stored.Audiences);
            Assert.Equal("personal", audience.Key);
            var tool = Assert.Single(audience.Value);
            Assert.Equal("approval_probe", tool.Key);
            Assert.Equal("approval_probe", Assert.Single(tool.Value).Verb);
            var committed = await ReadJournalAsync();
            var requested = Assert.Single(committed.OfType<ToolApprovalRequested>());
            var resolved = Assert.Single(committed.OfType<ToolApprovalResolved>());
            Assert.Equal(requested.SourceChildRunId, resolved.SourceChildRunId);
            Assert.Equal(requested.AuthorizationAttemptId, resolved.AuthorizationAttemptId);
            Assert.Equal("ApprovedEverywhere", resolved.Decision);
            Assert.Equal(new SenderId("operator-a"), requested.RequesterSenderId);
            Assert.Equal(new ToolCallId("child-approval-1"), requested.OriginalChildCallId);
            var retry = Assert.Single(_child.ReceivedMessages[^1]
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal("child-approval-1", retry.CallId);
            Assert.Equal("The actual approved result.\n[approval: always anywhere]", Assert.IsType<string>(retry.Result));
            var beforeDuplicate = await File.ReadAllBytesAsync(TestPaths.ToolApprovalsPath, TestContext.Current.CancellationToken);
            var stale = Assert.IsType<CommandNack>(await manager.Ask<ISessionResponse>(Answer(prompt, "operator-a"), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(ApprovalNackReasons.PromptExpired, stale.Reason);
            Assert.Equal(beforeDuplicate, await File.ReadAllBytesAsync(TestPaths.ToolApprovalsPath, TestContext.Current.CancellationToken));
            Assert.Equal(1, Volatile.Read(ref _approvedEffects));
            Assert.Equal(2, _child.CallCount);
            Assert.Equal(2, _main.CallCount);
            Assert.False(_nextChildResponse.Task.IsCompleted);
            Assert.Empty((await ReadJournalAsync()).OfType<ChildRunEvent.TerminalRecorded>());
        }
        finally { ReleaseOwnedSignals(gate); }
    }

    [Fact]
    public async Task A_reusable_answer_cannot_grant_after_the_real_live_wait_is_claimed_and_denied()
    {
        var gate = InstallWaitReplyGate();
        try
        {
            var (manager, _, prompt) = await StartPromptAsync();
            gate.Arm();
            Directory.CreateDirectory(TestPaths.ToolApprovalsPath);
            await EventFilter.Error(contains: "Child approval decision failed").ExpectOneAsync(async () =>
            {
                var failed = Assert.IsType<CommandNack>(await manager.Ask<ISessionResponse>(Answer(prompt, "operator-a"), Ceiling,
                    TestContext.Current.CancellationToken));
                Assert.Equal(ApprovalNackReasons.PersistFailed, failed.Reason);
            }, cancellationToken: TestContext.Current.CancellationToken);
            var captured = await gate.Held.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal("ChildApprovalWaitFinished", captured.Envelope.Message.GetType().Name);
            Assert.True(Directory.Exists(TestPaths.ToolApprovalsPath));
            await AwaitAssertAsync(() => Assert.Equal(2, _child.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var pending = await ReadJournalAsync();
            var requested = Assert.Single(pending.OfType<ToolApprovalRequested>());
            Assert.Equal(prompt.CallId.Value, requested.CallId);
            Assert.Equal(prompt.AuthorizationAttemptId, requested.AuthorizationAttemptId);
            Assert.Equal(new SenderId("operator-a"), requested.RequesterSenderId);
            Assert.Equal(new ToolCallId("child-approval-1"), requested.OriginalChildCallId);
            Assert.Empty(pending.OfType<ToolApprovalResolved>());
            Assert.Empty(pending.OfType<ChildRunEvent.CancellationRequested>());
            Assert.Empty(pending.OfType<ChildRunEvent.DispatchClosed>());
            Assert.Empty(pending.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Single(pending.OfType<ChildRunEvent.Checkpointed>());
            var deniedResult = Assert.Single(_child.ReceivedMessages[^1]
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal("child-approval-1", deniedResult.CallId);
            Assert.Equal("Tool access denied: approval_denied_by_user", Assert.IsType<string>(deniedResult.Result));
            Assert.Equal(0, Volatile.Read(ref _approvedEffects));
            Directory.Delete(TestPaths.ToolApprovalsPath);
            Assert.Empty(_store.Load().Audiences);
            var expired = Assert.IsType<CommandNack>(await manager.Ask<ISessionResponse>(Answer(prompt, "operator-a"), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(ApprovalNackReasons.PromptExpired, expired.Reason);
            Assert.Empty(_store.Load().Audiences);
            Assert.False(File.Exists(TestPaths.ToolApprovalsPath));
            Assert.Equal(0, Volatile.Read(ref _approvedEffects));
            Assert.Equal(2, _child.CallCount);
            Assert.Equal(2, _main.CallCount);
            Assert.False(_nextChildResponse.Task.IsCompleted);
            Assert.Empty((await ReadJournalAsync()).OfType<ToolApprovalResolved>());
            gate.Release();
            await AwaitAssertAsync(async () =>
            {
                var resolved = Assert.Single((await ReadJournalAsync()).OfType<ToolApprovalResolved>());
                Assert.Equal("Denied", resolved.Decision);
                Assert.Equal(requested.SourceChildRunId, resolved.SourceChildRunId);
                Assert.Equal(requested.AuthorizationAttemptId, resolved.AuthorizationAttemptId);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(_store.Load().Audiences);
            Assert.Equal(0, Volatile.Read(ref _approvedEffects));
        }
        finally
        {
            try { if (Directory.Exists(TestPaths.ToolApprovalsPath)) Directory.Delete(TestPaths.ToolApprovalsPath); }
            finally { ReleaseOwnedSignals(gate); }
        }
    }

    private async Task<(IActorRef Manager, Akka.TestKit.TestProbe Subscriber, ToolInteractionRequest Prompt)> StartPromptAsync()
    {
        Assert.Empty(_store.Load().Audiences);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = Session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Start the neutral child task.", Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"), TurnId = new TurnId("original"),
                MessageId = "original", Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
                Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
            }
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        _child.NextResponseGate = _nextChildResponse;
        _firstChildResponse.TrySetResult();
        var prompt = Assert.IsType<ToolInteractionRequest>(await subscriber.FishForMessageAsync<object>(
            message => message is ToolInteractionRequest, Ceiling, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new SenderId("operator-a"), prompt.RequesterSenderId);
        Assert.Contains(prompt.Options, option => option.Key == ApprovalOptionKeys.ApproveEverywhereKey);
        Assert.Equal(0, Volatile.Read(ref _approvedEffects));
        var requested = Assert.Single((await ReadJournalAsync()).OfType<ToolApprovalRequested>());
        Assert.NotNull(requested.SourceChildRunId);
        Assert.Equal(prompt.CallId.Value, requested.CallId);
        return (manager, subscriber, prompt);
    }

    private static ToolInteractionResponse Answer(ToolInteractionRequest prompt, string sender) => new()
    {
        SessionId = Session, CallId = prompt.CallId, SenderId = new SenderId(sender), SelectedKey = ApprovalOptionKeys.ApproveEverywhereKey
    };

    private void ReleaseOwnedSignals(WaitReplyGate gate)
    {
        gate.Release();
        _firstChildResponse.TrySetResult();
        _nextChildResponse.TrySetResult();
    }

    private WaitReplyGate InstallWaitReplyGate()
    {
        Sys.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            $"child-approval-reply-mailbox {{ mailbox-type = \"{typeof(WaitReplyMailbox).AssemblyQualifiedName}\" }}"));
        ((ExtendedActorSystem)Sys).Provider.Deployer.SetDeploy(new Deploy(
            $"/session-manager/{Uri.EscapeDataString(Session.Value)}", Config.Empty, NoRouter.Instance, LocalScope.Instance,
            Deploy.NoDispatcherGiven, "child-approval-reply-mailbox"));
        return WaitReplyMailbox.Gates.GetOrCreateValue(Sys);
    }

    private async Task<ISessionEvent[]> ReadJournalAsync()
    {
        var done = new TaskCompletionSource<ISessionEvent[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Sys.ActorOf(Props.Create(() => new JournalReader($"session-{Session.Value}", done)));
        var watcher = CreateTestProbe();
        watcher.Watch(reader);
        try { return await done.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken); }
        finally
        {
            Sys.Stop(reader);
            await watcher.ExpectTerminatedAsync(reader, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private sealed class JournalReader : ReceivePersistentActor
    {
        public JournalReader(string id, TaskCompletionSource<ISessionEvent[]> done)
        {
            PersistenceId = id;
            var events = new List<ISessionEvent>();
            Recover<ISessionEvent>(events.Add);
            Recover<RecoveryCompleted>(_ => done.TrySetResult(events.ToArray()));
        }
        public override string PersistenceId { get; }
        public override Recovery Recovery => new(SnapshotSelectionCriteria.None);
    }

    private sealed class RoleProvider(IChatClient main, IChatClient child) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => role == ModelRole.Main ? main : child;
    }

    private sealed class StartExecutor(SubAgentSpawner spawner) : IToolExecutor
    {
        public Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
            => spawner.StartRunAsync(new SubAgentProfile
            {
                Name = "worker", Description = "Complete the neutral task.", SystemPrompt = "Complete the assigned task.",
                ModelRole = ModelRole.Compaction, ToolNames = ["approval_probe"], EmitStructuredFindings = false
            }, "Request the neutral operation.", null,
                new ToolInvocationContext(context.RunScope, context.ExecutionTimeout, context.Outputs), ct, null);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class WaitReplyGate
    {
        private readonly object _sync = new();
        private bool _armed;
        public string MessageName { get; set; } = "ChildApprovalWaitFinished";
        public sealed record Captured(IActorRef Receiver, Envelope Envelope);
        public TaskCompletionSource<Captured> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() { lock (_sync) _armed = true; }
        public bool TryHold(IActorRef receiver, Envelope envelope)
        {
            lock (_sync)
            {
                return _armed && Held.TrySetResult(new Captured(receiver, envelope));
            }
        }
        public void Release()
        {
            Captured? captured;
            lock (_sync)
            {
                if (!_armed) return;
                _armed = false;
                captured = Held.Task.IsCompletedSuccessfully ? Held.Task.Result : null;
            }
            captured?.Receiver.Tell(captured.Envelope.Message, captured.Envelope.Sender);
        }
    }

    public sealed class WaitReplyMailbox(Settings settings, Config config) : MailboxType(settings, config), IProducesMessageQueue<WaitReplyQueue>
    {
        internal static readonly ConditionalWeakTable<ActorSystem, WaitReplyGate> Gates = new();
        public override IMessageQueue Create(IActorRef owner, ActorSystem system) => new WaitReplyQueue(Gates.GetOrCreateValue(system));
    }

    public sealed class WaitReplyQueue(WaitReplyGate gate) : IMessageQueue, IUnboundedDequeBasedMessageQueueSemantics
    {
        private readonly UnboundedDequeMessageQueue _inner = new();
        public int Count => _inner.Count;
        public bool HasMessages => _inner.HasMessages;
        public void Enqueue(IActorRef receiver, Envelope envelope)
        {
            if (envelope.Message.GetType().Name == gate.MessageName && gate.TryHold(receiver, envelope)) return;
            _inner.Enqueue(receiver, envelope);
        }
        public void EnqueueFirst(Envelope envelope) => _inner.EnqueueFirst(envelope);
        public bool TryDequeue(out Envelope envelope) => _inner.TryDequeue(out envelope);
        public void CleanUp(IActorRef owner, IMessageQueue deadletters) => _inner.CleanUp(owner, deadletters);
    }
}
