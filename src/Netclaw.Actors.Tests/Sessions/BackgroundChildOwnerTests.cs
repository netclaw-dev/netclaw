// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerTests(ITestOutputHelper output)
    : PersistenceTestKit(output: output), IAsyncDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);
    private readonly FakeChatClient _main = new();
    private readonly FakeChatClient _child = new();
    private readonly TaskCompletionSource _childRelease = NewSignal();
    private TestSessionTempDirectory? _directory;
    private StartExecutor? _start;
    private int _approvedEffects;
    private readonly FindingCheckpointSink _findingCheckpoint = new();

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
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(SecurityPolicyDefaults.Resolve(null));
        services.AddSingleton<BackgroundJobDefinitionStore>();
        services.AddSingleton(new SchedulingConfig());
        services.AddSingleton<ReminderDefinitionStore>();
        services.AddSingleton<ReminderHistoryStore>();
        services.AddSingleton<IOperationalNotificationSink>(NullNotificationSink.Instance);
        services.AddSingleton<IReminderChannelNotifier>(NullReminderChannelNotifier.Instance);
        services.AddSingleton<SessionPipeline>();
        services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());
        services.AddSingleton<IMemoryCheckpointSink>(_findingCheckpoint);
        var clients = new RoleProvider(_main, _child);
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
        registry.RegisterCore(new FakeNetclawTool("file_read", "The neutral fixture is available.", "file",
            invocation => invocation.TryComplete(new ToolInvocationReceipt.Succeeded([], null))));
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "control_probe"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "live_log_read"), "file");
        registry.RegisterCore(new FakeNetclawTool("approval_probe", "The approved fixture returned its result.", "builtin", invocation =>
        {
            Interlocked.Increment(ref _approvedEffects);
            invocation.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
        }));
        services.AddSingleton(registry);
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            { ["approval_probe"] = ToolApprovalMode.Approval }
        };
        var policy = new ToolAccessPolicy(_directory.Paths, toolConfig,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var snapshots = new WorkingContextSnapshotProvider(new GitWorkingContextInspector(TimeProvider.System),
            NullLogger<WorkingContextSnapshotProvider>.Instance);
        var spawner = new SubAgentSpawner(clients, registry, policy, null, prompt, snapshots,
            NullLogger<SubAgentSpawner>.Instance);
        _start = new StartExecutor(spawner, new FileReadTool(toolConfig, _directory.Paths, new ToolPathPolicy([])));
        services.AddSingleton<IToolExecutor>(_start);
        services.AddLlmSessionCompositeRecords();
        _main.ToolCallsOnFirstCall = [new FunctionCallContent("start-1", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate the assigned task." })];
        _child.NextResponseGate = _childRelease;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_commit_precedes_child_provider_and_parent_result(bool failCommit)
    {
        _child.ToolCallsOnFirstCall = [new FunctionCallContent("child-read-1", "file_read",
            new Dictionary<string, object?> { ["_rationale"] = "Read the assigned neutral fixture." })];
        ChildRunEvent.Checkpointed? checkpoint = null;
        var entered = NewSignal();
        var release = NewSignal();
        ChildRunAccepted? accepted = null;
        (ChildRunEvent.TerminalRecorded Event, long SequenceNr)? terminal = null;
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is ChildRunEvent.Checkpointed childCheckpoint)
                checkpoint = childCheckpoint;
            if (record.Payload is ChildRunEvent.TerminalRecorded childTerminal)
                terminal = (childTerminal, record.SequenceNr);
            if (record.Payload is not ChildRunAccepted admission) return false;
            accepted = admission;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return failCommit;
        });
        var session = new SessionId("signalr/background-owner");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
            .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        async Task ActAsync()
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = session, Content = "Delegate the neutral task.", Source = new MessageSource
                {
                    ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"),
                    TurnId = new TurnId("original"), MessageId = "original", Audience = TrustAudience.Personal,
                    Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
                    Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
                }
            }, Ceiling, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.NotNull(accepted);
            Assert.Equal("original", accepted.Run.OriginalContext.TurnId);
            Assert.False(accepted.Run.StartBatchSettled);
            Assert.Equal(0, _child.CallCount);
            Assert.Equal(1, _main.CallCount);
            release.TrySetResult();
            if (failCommit)
            {
                await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(0, _child.CallCount);
                Assert.Equal(1, _main.CallCount);
                return;
            }
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
            Assert.False(_childRelease.Task.IsCompleted);
            var result = Assert.Single(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal("start-1", result.CallId);
            using var body = JsonDocument.Parse(result.Result!.ToString()!);
            Assert.Equal("Accepted", body.RootElement.GetProperty("state").GetString());
            Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
            Assert.Equal("check_agent_run", body.RootElement.GetProperty("control_tool").GetString());
            Assert.DoesNotContain(_main.ReceivedMessages[^1], message => message.Text?.Contains("child final", StringComparison.Ordinal) == true);
            Assert.NotNull(_start!.Prepared);
            var retry = new StartBackgroundChildRun(_start.Prepared, accepted.Run.StartKey,
                accepted.Run.OriginalContext, accepted.Run.SourceOperation, _start.Token);
            var recorded = await owner.Ask<ChildStartReply>(retry, Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(accepted.Run.RunId, Assert.IsType<ChildStartReply.Accepted>(recorded).Run.RunId);
            Assert.Equal(1, _child.CallCount);
            var conflict = retry with { Prepared = _start.Prepared with { ArgumentsDigest = new string('b', 64) } };
            Assert.IsType<ChildStartReply.Conflict>(await owner.Ask<ChildStartReply>(conflict, Ceiling,
                TestContext.Current.CancellationToken));
            var foreign = retry with { InvocationContext = retry.InvocationContext with { RequesterSenderId = new SenderId("operator-b") } };
            await Assert.ThrowsAsync<InvalidDataException>(() => owner.Ask<ChildStartReply>(foreign, Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(1, _child.CallCount);
            _childRelease.TrySetResult();
            await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(3, _main.CallCount);
            Assert.Equal(2, _child.CallCount);
            Assert.NotNull(checkpoint);
            Assert.Equal(1, checkpoint.Checkpoint.CompletedRound);
            Assert.Contains("The neutral fixture is available.", checkpoint.Checkpoint.Summary, StringComparison.Ordinal);
            Assert.NotNull(terminal);
            Assert.Equal(terminal.Value.SequenceNr, terminal.Value.Event.TerminalSequenceNr);
            var delivered = _main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                .Where(item => item.CallId != "start-1").ToArray();
            var childResult = Assert.Single(delivered);
            using var childBody = JsonDocument.Parse(childResult.Result!.ToString()!);
            Assert.Equal(accepted.Run.RunId.Value, childBody.RootElement.GetProperty("run_id").GetString());
            var childCall = Assert.Single(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionCallContent>()),
                call => call.CallId == childResult.CallId);
            Assert.Equal("start_probe", childCall.Name);
            Assert.Single(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
                item => item.CallId == "start-1");
        }
        try
        {
            if (failCommit)
                await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(ActAsync,
                    cancellationToken: TestContext.Current.CancellationToken);
            else
                await ActAsync();
        }
        finally
        {
            release.TrySetResult();
            _childRelease.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_keeps_failed_outcome_and_confines_the_atomic_report(bool linkedReport)
    {
        var session = new SessionId("signalr/background-cancel");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        MessageSource Source(string turn) => new()
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"),
            TurnId = new TurnId(turn), MessageId = turn, Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        };
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Delegate the neutral task.", Source = Source("original")
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        var prepared = Assert.IsType<PreparedChildRun>(_start!.Prepared);
        var storage = Assert.IsType<ToolSessionScope.Bound>(prepared.Execution.Scope.Authority.Session).Storage;
        var artifact = storage.ArtifactDirectory.Value;
        var report = Path.Combine(artifact, "cancelled-results.json");
        var outside = Path.Combine(_directory!.Path, "outside-marker");
        await File.WriteAllTextAsync(outside, "outside unchanged", TestContext.Current.CancellationToken);
        if (linkedReport)
        {
            Directory.CreateDirectory(artifact);
            File.CreateSymbolicLink(report, outside);
        }
        _main.PlannedResponses.Enqueue([new FunctionCallContent("cancel-1", "control_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Cancel the accepted child.", ["run_id"] = prepared.RunId.Value })]);
        _main.PlannedResponses.Enqueue([new TextContent("The cancellation was admitted.")]);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Cancel that child.", Source = Source("cancel-turn")
        }, Ceiling, TestContext.Current.CancellationToken);
        FunctionResultContent? delivered = null;
        await AwaitAssertAsync(() =>
        {
            delivered = _main.ReceivedMessages.SelectMany(messages => messages)
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                .LastOrDefault(result => result.CallId.StartsWith("child-result-", StringComparison.Ordinal));
            Assert.NotNull(delivered);
        }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(delivered!.Result!.ToString()!);
        Assert.Equal("Cancelled", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("Failed", body.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(SubAgentOutcomeReason.CancelledByParent.Value, body.RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, _child.CallCount);
        Assert.True(_childRelease.Task.IsCanceled);
        Assert.Equal("outside unchanged", await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        if (linkedReport)
            Assert.Contains("No report path is confirmed", body.RootElement.GetProperty("warning").GetString(), StringComparison.Ordinal);
        else
        {
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken));
            Assert.Equal("Cancelled", saved.RootElement.GetProperty("state").GetString());
            Assert.Contains(report, body.RootElement.GetProperty("output").GetString(), StringComparison.Ordinal);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StartExecutor(SubAgentSpawner spawner, FileReadTool fileRead) : IToolExecutor
    {
        public PreparedChildRun? Prepared { get; private set; }
        public CancellationToken Token { get; private set; }
        public ChildControlReply? LastControlReply { get; private set; }
        public bool EmitStructuredFindings { get; set; }
        public string? LastControlBody { get; private set; }
        public string? LastLogRead { get; private set; }
        public ToolInvocationReceipt? LastLogReceipt { get; private set; }
        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            var factory = context.Invocation.SpawnChildActor
                ?? throw new InvalidOperationException("The start test requires the owner factory.");
            if (call.Name == "live_log_read")
            {
                LastLogRead = await fileRead.ExecuteAsync(call.Arguments!, context, ct);
                LastLogReceipt = context.Outputs.Receipt;
                return LastLogRead;
            }
            if (call.Name == "control_probe")
            {
                var controlScope = context.RunScope with { SpawnChildActor = async (payload, name, token) =>
                {
                    var reply = Assert.IsType<ChildControlReply>(await factory(payload, name, token));
                    LastControlReply = reply;
                    return reply;
                }};
                LastControlBody = await new CheckAgentRunTool().ExecuteAsync(new Dictionary<string, object?>
                {
                    ["runId"] = call.Arguments!["run_id"],
                    ["cancel"] = !call.Arguments.TryGetValue("cancel", out var cancel) || cancel is true
                }, new ToolExecutionContext(controlScope, context.ExecutionTimeout, context.Outputs), ct);
                return LastControlBody;
            }
            var scope = context.RunScope with { SpawnChildActor = async (payload, name, token) =>
            {
                Prepared = Assert.IsType<PreparedChildRun>(payload);
                Token = token;
                return await factory(payload, name, token);
            }};
            return await spawner.StartRunAsync(new SubAgentProfile
            {
                Name = "worker", Description = "Complete the neutral task.", SystemPrompt = "Complete the assigned task.",
                ModelRole = ModelRole.Compaction, ToolNames = ["file_read", "approval_probe"], EmitStructuredFindings = EmitStructuredFindings
            }, "Inspect the neutral fixture.", null,
                new ToolInvocationContext(scope, context.ExecutionTimeout, context.Outputs), ct, null);
        }
    }

    private sealed class RoleProvider(IChatClient main, IChatClient child) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => role == ModelRole.Main ? main : child;
    }

    private sealed class FindingCheckpointSink : IMemoryCheckpointSink
    {
        public TaskCompletionSource<MemoryCheckpointRequest> Finding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MemoryCheckpointEnqueueResult> EnqueueAsync(MemoryCheckpointRequest request, CancellationToken ct = default)
        {
            if (request.TriggerType == CheckpointTriggerType.SubagentFindings)
                Finding.TrySetResult(request);
            return Task.FromResult(new MemoryCheckpointEnqueueResult("fixture-checkpoint", 0));
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        _childRelease.TrySetResult();
        try { await base.DisposeAsync(); }
        finally { if (_directory is not null) await _directory.DisposeAsync(); }
    }
}
