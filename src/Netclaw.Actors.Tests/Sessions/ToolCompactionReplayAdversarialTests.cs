// -----------------------------------------------------------------------
// <copyright file="ToolCompactionReplayAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using ModelRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolCompactionReplayAdversarialTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private const string First = "First admitted input.";
    private const string Second = "Second admitted input.";
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(10);
    private readonly OverflowClient _client = new();
    private readonly ProbeExecutor _executor = new();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 1000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { TitleGenerationInterval = 0, KeepRecentMessages = 0, SnapshotInterval = 1000 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Process the admitted inputs."));
        services.AddSingleton<IToolExecutor>(_executor);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create((string label) => label, "input_probe"), "builtin");
        services.AddSingleton(registry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overflow_replay_and_later_input_keep_their_order_authority_and_once_only_consumption(bool differentRequester)
    {
        var session = new SessionId("signalr/overflow-replay-prefix");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        { SessionId = session, Filter = OutputFilter.Full | OutputFilter.ProcessingState }, FaultCeiling, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = First, Source = Source("first", "operator-a") }, FaultCeiling, TestContext.Current.CancellationToken);
        await _client.FirstEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = Second, Source = Source("second", differentRequester ? "operator-b" : "operator-a") },
            FaultCeiling, TestContext.Current.CancellationToken);
        _client.ReleaseOverflow.TrySetResult();

        await subscriber.FishForMessageAsync<object>(message => message is CompactionOutput,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var completions = new List<TurnCompleted>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            if (message is TurnCompleted completed) completions.Add(completed);
            return message is ProcessingStateOutput { IsProcessing: false } && completions.Count == 1 && _executor.Effects.Count == 2;
        },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(completions);

        Assert.Equal([First, Second], _executor.Effects.Select(effect => effect.Label));
        Assert.Equal("operator-a", _executor.Effects[0].Requester);
        Assert.Equal(differentRequester ? "operator-b" : "operator-a", _executor.Effects[1].Requester);
        Assert.All(_executor.Effects, effect =>
        {
            Assert.Equal(TrustAudience.Personal, effect.Audience);
            Assert.Equal(TrustBoundary.Personal, effect.Boundary);
        });
        var last = _client.Requests[^1];
        var actualInputs = last.Where(message => message.Role == ModelRole.User)
            .Select(message => message.Text).Where(text => text is First or Second).ToArray();
        Assert.Equal([First, Second], actualInputs);
        var pairs = last.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
        Assert.Equal(["first-effect", "second-effect"], pairs.Select(pair => pair.CallId));
        Assert.Equal([First, Second], pairs.Select(pair => pair.Result?.ToString()));
        Assert.All(_client.Requests.Skip(1), messages =>
        {
            var inputs = messages.Where(message => message.Role == ModelRole.User)
                .Select(message => message.Text).Where(text => text is First or Second).ToArray();
            Assert.Equal(inputs.Distinct().Count(), inputs.Length);
            if (inputs.Contains(Second)) Assert.Equal([First, Second], inputs);
        });

        var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
            .ResolveOne(FaultCeiling, TestContext.Current.CancellationToken);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        Sys.Stop(owner);
        await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = new TaskCompletionSource<JournalEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        Sys.ActorOf(Props.Create(() => new EvidenceReader($"session-{session.Value}", recovered)));
        var evidence = await recovered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var admitted = evidence.Inputs.Where(input => input.UserMessage.Content is First or Second).ToArray();
        Assert.Equal([First, Second], admitted.Select(input => input.UserMessage.Content));
        Assert.Equal(admitted.Select(input => input.InputId).OrderBy(id => id.Value), evidence.Consumed.OrderBy(id => id.Value));
        Assert.Equal(2, evidence.Consumed.Distinct().Count());
        Assert.Equal(["first", "second"], evidence.BatchTasks);
        Assert.Equal(["first", "second"], evidence.AdoptedContexts.Select(context => context.TurnId));
        Assert.Equal("operator-a", evidence.AdoptedContexts[0].RequesterSenderId?.Value);
        Assert.Equal(differentRequester ? "operator-b" : "operator-a", evidence.AdoptedContexts[1].RequesterSenderId?.Value);
        Assert.Contains("result:first-effect", evidence.EventOrder);
        Assert.Contains("adopt:second", evidence.EventOrder);
        Assert.True(evidence.EventOrder.IndexOf("result:first-effect") < evidence.EventOrder.IndexOf("adopt:second"));
    }

    private static MessageSource Source(string turn, string requester) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester), MessageId = turn, TurnId = new TurnId(turn),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", requester, requester)
    };

    private sealed record Effect(string Label, string? Requester, TrustAudience Audience, TrustBoundary? Boundary);
    private sealed record JournalEvidence(InputAdmitted[] Inputs, InputId[] Consumed, string[] BatchTasks, TurnContextRecord[] AdoptedContexts, List<string> EventOrder);
    private sealed class EvidenceReader : ReceivePersistentActor
    {
        public EvidenceReader(string persistenceId, TaskCompletionSource<JournalEvidence> completed)
        {
            PersistenceId = persistenceId;
            var inputs = new List<InputAdmitted>();
            var consumed = new List<InputId>();
            var tasks = new List<string>();
            var contexts = new List<TurnContextRecord>();
            var eventOrder = new List<string>();
            Recover<SnapshotOffer>(offer =>
            {
                var snapshot = Assert.IsType<SessionSnapshot>(offer.Snapshot);
                inputs.AddRange(snapshot.PendingInputs);
                if (snapshot.AdoptedTaskContext is { } context) contexts.Add(context);
            });
            Recover<InputAdmitted>(input => inputs.Add(input));
            Recover<ToolTaskAdopted>(adopted =>
            {
                contexts.Add(adopted.TurnContext);
                eventOrder.Add($"adopt:{adopted.TurnContext.TurnId}");
            });
            Recover<ToolCallRecorded>(result => eventOrder.Add($"result:{result.ToolResult.ToolCallId?.Value}"));
            Recover<ToolBatchStarted>(batch =>
            {
                consumed.AddRange(batch.ConsumedInputIds);
                if (batch.LoopAdmission is { } admission) tasks.Add(admission.TaskId);
            });
            Recover<TurnRecorded>(turn => consumed.AddRange(turn.ConsumedInputIds));
            Recover<RecoveryCompleted>(_ => completed.TrySetResult(new JournalEvidence(inputs.ToArray(), consumed.ToArray(), tasks.ToArray(), contexts.ToArray(), eventOrder)));
            RecoverAny(_ => { });
        }
        public override string PersistenceId { get; }
    }

    private sealed class ProbeExecutor : IToolExecutor
    {
        public List<Effect> Effects { get; } = [];
        public Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            var label = ToolArgumentHelper.GetString(call.Arguments, "label")!;
            Effects.Add(new Effect(label, context.RunScope.DefaultDeliveryTarget?.DestinationId, context.RunScope.Audience, context.RunScope.Boundary));
            context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
            return Task.FromResult(label);
        }
    }

    private sealed class OverflowClient : IChatClient
    {
        private readonly HashSet<string> _issued = [];
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOverflow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ChatMessage[]> Requests { get; } = [];
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            if (Requests.Count == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseOverflow.Task.WaitAsync(cancellationToken);
                throw new ProviderException("maximum context length exceeded", "HTTP 400: maximum context length exceeded", statusCode: 400);
            }
            var calls = snapshot.Where(message => message.Role == ModelRole.User)
                .Select(message => message.Text).Where(text => text is First or Second).Where(text => _issued.Add(text!))
                .Select(text => (AIContent)new FunctionCallContent(text == First ? "first-effect" : "second-effect", "input_probe",
                    new Dictionary<string, object?> { ["label"] = text })).ToArray();
            return new ChatResponse(calls.Length > 0 ? new ChatMessage(ModelRole.Assistant, calls) : new ChatMessage(ModelRole.Assistant, "The admitted inputs are complete."));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
