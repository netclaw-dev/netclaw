// -----------------------------------------------------------------------
// <copyright file="LateToolReplyMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Dispatch.MessageQueues;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.MutationTests;

public sealed class LateToolReplyMutationTests : IAsyncLifetime
{
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(2);
    private readonly NetclawPaths _paths = new(Path.Combine(Path.GetTempPath(), $"netclaw-late-reply-mutations-{Guid.NewGuid():N}"));
    private readonly ReplyClient _client = new();
    private readonly Channel<object> _outputs = Channel.CreateUnbounded<object>();
    private readonly SessionId _session = new("mutation/late-reply");
    private IHost _host = null!;
    private IActorRef _owner = null!;
    private IActorRef _observer = null!;
    private ActorSystem _system = null!;
    private HeldExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        _executor = new HeldExecutor(Path.Combine(_paths.BasePath, "confirmed-effects.txt"));
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create((int step) => $"Step {step}", "search_tools"), "builtin");
        var storage = SessionStoragePaths.CreateLegacy(Path.Combine(_paths.BasePath, "session"), Path.Combine(_paths.BasePath, "logs"), _session.Value);
        var services = new SessionServices(new SingleClientProvider(_client), new StaticSystemPromptProvider("Execute the supplied task."),
            [], new SnapshotProvider(), TimeProvider.System, _paths, new StorageResolver(storage));
        var tools = new SessionToolServices(_executor, registry, null, null, null);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAkka($"late-reply-mutations-{Guid.NewGuid():N}", akka => akka
            .AddHocon($"reply-capture-mailbox {{ mailbox-type = \"{typeof(CaptureMailbox).AssemblyQualifiedName}\" }}", HoconAddMode.Prepend)
            .WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization()
            .WithActors((system, _) =>
            {
                _system = system;
                _observer = system.ActorOf(Props.Create(() => new OutputObserver(_outputs.Writer)));
                _owner = system.ActorOf(Props.Create(() => new LlmSessionActor(_session.Value,
                    new ModelCapabilities { ModelId = "fake", ContextWindowTokens = 2_000_000 },
                    new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0, CompactionThreshold = 0.99 } },
                    services, tools)).WithMailbox("reply-capture-mailbox"));
            }));
        _host = builder.Build();
        await _host.StartAsync();
        await _owner.Ask<SessionJoined>(new JoinSession(_observer) { SessionId = _session,
            Filter = OutputFilter.Full | OutputFilter.ProcessingState }, FaultCeiling);
        await ReadUntilAsync(message => message is SessionJoined);
    }

    [Fact]
    public async Task A_captured_prior_result_cannot_complete_a_new_dispatch_with_the_same_provider_id()
    {
        await _owner.Ask<CommandAck>(new SendUserMessage { SessionId = _session, Content = "Execute the first task.", Source = Source("operator-old") }, FaultCeiling);
        var first = await ReadUntilAsync(message => message is ProcessingStateOutput { IsProcessing: false });
        Assert.Single(first.OfType<TurnCompleted>());
        Assert.Equal(2, File.ReadAllLines(_executor.EffectPath).Length);
        Assert.Equal(4, _client.Count);
        var prior = CaptureMailbox.Captures.GetOrCreateValue(_system).First(envelope =>
            envelope.Message is ToolExecutionSingleCompleted { Result.Receipt: ToolInvocationReceipt.Succeeded });
        var priorResult = Assert.IsType<ToolExecutionSingleCompleted>(prior.Message).Result;
        Assert.Equal("request-1-repeat", priorResult.Message.ToolCallId?.Value);

        await _owner.Ask<CommandAck>(new SendUserMessage { SessionId = _session, Content = "Execute a fresh task.", Source = Source("operator-new") }, FaultCeiling);
        var current = await _executor.Entered.Task.WaitAsync(FaultCeiling);
        Assert.Equal("request-1-repeat", current.CallId);
        Assert.NotEqual(priorResult.AuthorizationAttemptId, current.AuthorizationAttemptId);
        Assert.Equal("operator-new", current.Requester);
        _owner.Tell(prior.Message, prior.Sender);
        _owner.Tell(new JoinSession(_observer) { SessionId = _session,
            Filter = (OutputFilter.Full | OutputFilter.ProcessingState) & ~OutputFilter.TextStreaming }, prior.Sender);
        var observed = await ReadUntilAsync(message => message is SessionJoined);
        Assert.DoesNotContain(observed, message => message is ToolResultOutput or TurnCompleted or ToolInteractionRequest);
        Assert.False(current.Token.IsCancellationRequested);
        Assert.Equal(2, File.ReadAllLines(_executor.EffectPath).Length);
        Assert.Equal(5, _client.Count);

        _executor.Release.TrySetResult();
        var completed = await ReadUntilAsync(message => message is ProcessingStateOutput { IsProcessing: false });
        Assert.Single(completed.OfType<TurnCompleted>());
        Assert.Single(completed.OfType<ToolResultOutput>());
        Assert.Equal(3, File.ReadAllLines(_executor.EffectPath).Length);
        Assert.Equal(6, _client.Count);
        var latest = _client.Requests.Last();
        var batch = Array.FindLastIndex(latest, message => message.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == current.CallId));
        Assert.True(batch >= 0);
        Assert.Single(latest.Skip(batch + 1).SelectMany(message => message.Contents.OfType<FunctionResultContent>()), result => result.CallId == current.CallId);
    }

    private async Task<IReadOnlyList<object>> ReadUntilAsync(Func<object, bool> condition)
    {
        using var ceiling = new CancellationTokenSource(FaultCeiling);
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

    private static MessageSource Source(string sender) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(sender), Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", sender, sender)
    };

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

    private sealed class HeldExecutor(string path) : IToolExecutor
    {
        public sealed record Execution(string CallId, AuthorizationAttemptId AuthorizationAttemptId, string? Requester, CancellationToken Token);
        private int _calls;
        public string EffectPath => path;
        public TaskCompletionSource<Execution> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 3)
            {
                Entered.TrySetResult(new Execution(call.CallId, context.Approval.AuthorizationAttemptId, context.RunScope.DefaultDeliveryTarget?.DestinationId, ct));
                await Release.Task.WaitAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{call.CallId} {context.Approval.AuthorizationAttemptId.Value}\n");
            context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
            return "verified effect 1";
        }
    }

    private sealed class ReplyClient : IChatClient
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public ConcurrentQueue<ChatMessage[]> Requests { get; } = new();
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(messages.ToArray());
            var number = Interlocked.Increment(ref _count);
            var contents = number == 6 || options?.Tools is not { Count: > 0 }
                ? new List<AIContent> { new TextContent("The fresh task is complete.") }
                : new List<AIContent> { new FunctionCallContent(number == 5 ? "request-1-repeat" : $"request-{number}-repeat", "search_tools",
                    new Dictionary<string, object?> { ["step"] = 1, ["_rationale"] = "Execute the specified effect." }) };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = response.Messages[0].Contents };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    public sealed class CaptureMailbox(Settings settings, Config config) : MailboxType(settings, config), IProducesMessageQueue<CaptureQueue>
    {
        internal static readonly ConditionalWeakTable<ActorSystem, ConcurrentQueue<Envelope>> Captures = new();
        public override IMessageQueue Create(IActorRef owner, ActorSystem system) => new CaptureQueue(Captures.GetOrCreateValue(system));
    }

    public sealed class CaptureQueue(ConcurrentQueue<Envelope> captured) : IMessageQueue, IUnboundedDequeBasedMessageQueueSemantics
    {
        private readonly UnboundedDequeMessageQueue _inner = new();
        public int Count => _inner.Count;
        public bool HasMessages => _inner.HasMessages;
        public void Enqueue(IActorRef receiver, Envelope envelope)
        {
            if (envelope.Message is ToolExecutionSingleCompleted) captured.Enqueue(envelope);
            _inner.Enqueue(receiver, envelope);
        }
        public void EnqueueFirst(Envelope envelope) => _inner.EnqueueFirst(envelope);
        public bool TryDequeue(out Envelope envelope) => _inner.TryDequeue(out envelope);
        public void CleanUp(IActorRef owner, IMessageQueue deadletters) => _inner.CleanUp(owner, deadletters);
    }
}
