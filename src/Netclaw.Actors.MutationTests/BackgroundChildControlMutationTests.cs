// -----------------------------------------------------------------------
// <copyright file="BackgroundChildControlMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

public sealed class BackgroundChildControlMutationTests : IAsyncLifetime
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);
    private readonly NetclawPaths _paths = new(Path.Combine(Path.GetTempPath(), $"netclaw-child-control-mutations-{Guid.NewGuid():N}"));
    private readonly SessionId _session = new("mutation/child-control");
    private readonly ParentClient _parent = new();
    private readonly HeldChildClient _child = new();
    private readonly ToolConfig _toolConfig = new();
    private readonly Channel<object> _outputs = Channel.CreateUnbounded<object>();
    private IHost _host = null!;
    private IActorRef _owner = null!;
    private IActorRef _observer = null!;
    private ControlExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        var clients = new RoleProvider(_parent, _child);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "start_probe"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "control_probe"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create(() => "unused", "file_read"), "file");
        var prompt = new StaticSystemPromptProvider("Use the assigned tools.");
        var snapshots = new SnapshotProvider();
        var policy = new ToolAccessPolicy(_paths, _toolConfig,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var spawner = new SubAgentSpawner(clients, registry, policy, null, prompt, snapshots,
            NullLogger<SubAgentSpawner>.Instance);
        _executor = new ControlExecutor(spawner);
        var storage = SessionStoragePaths.CreateLegacy(Path.Combine(_paths.BasePath, "session"),
            Path.Combine(_paths.BasePath, "logs"), _session.Value);
        var services = new SessionServices(clients, prompt, [], snapshots, TimeProvider.System, _paths, new StorageResolver(storage));
        var tools = new SessionToolServices(_executor, registry, policy, null, null);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAkka($"child-control-mutations-{Guid.NewGuid():N}", akka => akka
            .WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization()
            .WithActors((system, _) =>
            {
                _observer = system.ActorOf(Props.Create(() => new OutputObserver(_outputs.Writer)));
                _owner = system.ActorOf(Props.Create(() => new LlmSessionActor(_session.Value,
                    new ModelCapabilities { ModelId = "fake", ContextWindowTokens = 2_000_000 },
                    new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0, CompactionThreshold = 0.99 } },
                    services, tools)));
            }));
        _host = builder.Build();
        await _host.StartAsync();
        _owner.Tell(new JoinSession(_observer)
        { SessionId = _session, Filter = OutputFilter.Full | OutputFilter.ProcessingState }, _observer);
        await ReadUntilAsync(message => message is SessionJoined);
    }

    [Fact]
    public async Task A_foreign_tool_cannot_control_a_child_but_its_original_requester_can()
    {
        _parent.NextCall = new FunctionCallContent("start-1", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate the assigned task." });
        await SendAsync("operator-a", "Start the child.");
        await _child.Entered.Task.WaitAsync(Ceiling);
        var started = await ReadUntilAsync(message => message is ProcessingStateOutput { IsProcessing: false });
        Assert.Single(started.OfType<TurnCompleted>());
        var run = Assert.IsType<PreparedChildRun>(_executor.Prepared);
        var startReceipt = Assert.Single(started.OfType<ToolResultOutput>());
        using var accepted = JsonDocument.Parse(startReceipt.Result);
        Assert.Equal(run.RunId.Value, accepted.RootElement.GetProperty("run_id").GetString());

        _parent.NextCall = ControlCall("foreign-control", run.RunId, true);
        await SendAsync("operator-b", "Cancel the child.");
        var foreign = await ReadUntilAsync(message => message is ProcessingStateOutput { IsProcessing: false });
        Assert.Single(foreign.OfType<TurnCompleted>());
        var denied = Assert.Single(_executor.Controls);
        Assert.Equal("operator-b", denied.Requester);
        Assert.Null(denied.Reply.Run);
        Assert.False(_child.Token.IsCancellationRequested);
        var foreignResult = Assert.Single(foreign.OfType<ToolResultOutput>());
        Assert.Equal("Error: the child run was not found or is not accessible from this session.", foreignResult.Result);
        Assert.DoesNotContain(run.RunId.Value, foreignResult.Result);
        Assert.DoesNotContain(run.Execution.Scope.ScopeId.Value, foreignResult.Result);
        Assert.DoesNotContain("log_path", foreignResult.Result);
        Assert.DoesNotContain("artifact_directory", foreignResult.Result);

        _parent.NextCall = ControlCall("original-control", run.RunId, false);
        await SendAsync("operator-a", "Inspect the child.");
        var original = await ReadUntilAsync(message => message is ProcessingStateOutput { IsProcessing: false });
        Assert.Single(original.OfType<TurnCompleted>());
        Assert.Equal(2, _executor.Controls.Count);
        var allowed = _executor.Controls.Last();
        Assert.Equal("operator-a", allowed.Requester);
        Assert.NotEqual(denied.AuthorizationAttempt, allowed.AuthorizationAttempt);
        var visible = Assert.IsType<BackgroundChildRun>(allowed.Reply.Run);
        Assert.Equal(run.RunId, visible.RunId);
        Assert.Equal(BackgroundChildState.Running, visible.State);
        Assert.Null(visible.CancellationRequestedAtMs);
        Assert.Null(visible.DispatchClosedAtMs);
        Assert.Null(visible.Terminal);
        Assert.False(_child.Token.IsCancellationRequested);
        Assert.Equal(1, _child.Count);
        Assert.Equal(6, _parent.Count);
        var originalResult = Assert.Single(original.OfType<ToolResultOutput>());
        using var status = JsonDocument.Parse(originalResult.Result);
        var storage = Assert.IsType<ToolSessionScope.Bound>(run.Execution.Scope.Authority.Session).Storage;
        Assert.Equal(storage.LogPath.Value, status.RootElement.GetProperty("log_path").GetString());
        Assert.Equal(storage.ArtifactDirectory.Value, status.RootElement.GetProperty("artifact_directory").GetString());
        Assert.DoesNotContain(storage.LogPath.Value, foreignResult.Result);
        Assert.DoesNotContain(storage.ArtifactDirectory.Value, foreignResult.Result);
        Assert.True(File.Exists(storage.LogPath.Value));
        var readOutputs = allowed.Context.Outputs.Fork();
        var readContext = new ToolInvocationContext(allowed.Context.RunScope, allowed.Context.ExecutionTimeout, readOutputs);
        var deniedRead = await new FileReadTool(_toolConfig, _paths, new ToolPathPolicy([storage.LogPath.Value]))
            .ExecuteAsync(new Dictionary<string, object?> { ["Path"] = storage.LogPath.Value }, readContext);
        Assert.IsType<ToolInvocationReceipt.AuthorizationDenied>(readOutputs.Receipt);
        Assert.Contains("Access denied", deniedRead);
        Assert.StartsWith("Error:", deniedRead);
        Assert.Empty(readOutputs.FileAttachments);
        Assert.Empty(readOutputs.ModelInputFiles);
        var pairs = _parent.Requests.Last().SelectMany(message => message.Contents.OfType<FunctionResultContent>());
        Assert.Equal(originalResult.Result,
            Assert.IsType<string>(Assert.Single(pairs, result => result.CallId == "original-control").Result));
    }

    private Task<CommandAck> SendAsync(string requester, string content) => _owner.Ask<CommandAck>(new SendUserMessage
    {
        SessionId = _session, Content = content, Source = new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester), Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", requester, requester)
        }
    }, Ceiling);

    private static FunctionCallContent ControlCall(string id, SubAgentRunId runId, bool cancel) => new(id, "control_probe",
        new Dictionary<string, object?> { ["run_id"] = runId.Value, ["cancel"] = cancel, ["_rationale"] = "Control the recorded child." });

    private async Task<IReadOnlyList<object>> ReadUntilAsync(Func<object, bool> condition)
    {
        using var ceiling = new CancellationTokenSource(Ceiling);
        var items = new List<object>();
        while (true)
        {
            var item = await _outputs.Reader.ReadAsync(ceiling.Token);
            items.Add(item);
            if (condition(item)) return items;
        }
    }

    public async Task DisposeAsync()
    {
        try { if (_host is not null) { await _host.StopAsync(); _host.Dispose(); } }
        finally { if (Directory.Exists(_paths.BasePath)) Directory.Delete(_paths.BasePath, true); }
    }

    private sealed class OutputObserver : ReceiveActor
    {
        public OutputObserver(ChannelWriter<object> writer) => ReceiveAny(message => writer.TryWrite(message));
    }

    private sealed class StorageResolver(SessionStoragePaths storage) : ISessionStorageResolver
    {
        public SessionStoragePaths Resolve(SessionId sessionId) => storage;
    }

    private sealed class SnapshotProvider : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(WorkingContext context, TrustAudience audience, CancellationToken cancellationToken)
            => Task.FromResult(new WorkingContextSnapshot { WorkingContext = context, Git = new GitWorkingContextInspection.Skipped() });
    }

    private sealed class RoleProvider(IChatClient parent, IChatClient child) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => role == ModelRole.Main ? parent : child;
    }

    private sealed class ControlExecutor(SubAgentSpawner spawner) : IToolExecutor
    {
        public sealed record Control(string? Requester, AuthorizationAttemptId AuthorizationAttempt, ChildControlReply Reply, ToolExecutionContext Context);
        public ConcurrentQueue<Control> Controls { get; } = new();
        public PreparedChildRun? Prepared { get; private set; }
        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            var factory = context.Invocation.SpawnChildActor
                ?? throw new InvalidOperationException("The owner factory is required.");
            if (call.Name == "control_probe")
            {
                var controlScope = context.RunScope with { SpawnChildActor = async (payload, name, token) =>
                {
                    Assert.IsType<ChildControlRequest>(payload);
                    var reply = Assert.IsType<ChildControlReply>(await factory(payload, name, token));
                    Controls.Enqueue(new Control(context.RunScope.DefaultDeliveryTarget?.DestinationId, context.Approval.AuthorizationAttemptId, reply, context));
                    return reply;
                }};
                return await new CheckAgentRunTool().ExecuteAsync(new Dictionary<string, object?>
                {
                    ["RunId"] = call.Arguments!["run_id"]!.ToString(),
                    ["Cancel"] = JsonSerializer.SerializeToElement(call.Arguments).GetProperty("cancel").GetBoolean()
                }, new ToolInvocationContext(controlScope, context.ExecutionTimeout, context.Outputs), ct);
            }
            var scope = context.RunScope with { SpawnChildActor = async (payload, name, token) =>
            {
                Prepared = Assert.IsType<PreparedChildRun>(payload);
                return await factory(payload, name, token);
            }};
            return await spawner.StartRunAsync(new SubAgentProfile
            {
                Name = "worker", Description = "Complete the neutral task.", SystemPrompt = "Complete the assigned task.",
                ModelRole = ModelRole.Compaction, ToolNames = ["file_read"], EmitStructuredFindings = false
            }, "Inspect the neutral fixture.", null, new ToolInvocationContext(scope, context.ExecutionTimeout, context.Outputs), ct, null);
        }
    }

    private sealed class ParentClient : IChatClient
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public FunctionCallContent? NextCall { get; set; }
        public ConcurrentQueue<ChatMessage[]> Requests { get; } = new();
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(messages.ToArray());
            Interlocked.Increment(ref _count);
            var call = NextCall;
            NextCall = null;
            return Task.FromResult(new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
                call is null ? [new TextContent("The parent task is complete.")] : [call])));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate { Role = Microsoft.Extensions.AI.ChatRole.Assistant, Contents = response.Messages[0].Contents };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HeldChildClient : IChatClient
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Interlocked.Increment(ref _count);
            Entered.TrySetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("The held child requires cancellation.");
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await GetResponseAsync(messages, options, cancellationToken);
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
