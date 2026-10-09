// -----------------------------------------------------------------------
// <copyright file="ToolRecurrenceAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Akka.Actor;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolRecurrenceAdversarialTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private const string ProbeTool = "search_tools";
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(30);
    private readonly AdversarialChatClient _client = new();
    private readonly EffectLedger _ledger = new();
    private IActorRef? _childActor;
    private SubAgentResult? _childResult;
    private readonly List<CompactionOutput> _compactions = [];
    private readonly List<string> _admittedCallIds = [];

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 2_000_000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                CompactionThreshold = 0.99,
                KeepRecentMessages = 2,
                KeepRecentToolResults = 1000,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Use the supplied task contract."));
        services.AddSingleton<IToolExecutor>(new EffectExecutor(_ledger));
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create((int step) => $"Step {step}", ProbeTool), "builtin");
        services.AddSingleton(registry);
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(false, 65)]
    [InlineData(true, 10)]
    [InlineData(true, 35)]
    public async Task Useful_distinct_effects_complete_beyond_former_iteration_limits(bool child, int steps)
    {
        ConfigureEffects();
        _client.Frames = Enumerable.Range(1, steps)
            .Select(step => new[] { Call($"effect-{step}", step) }).ToArray();

        var result = await RunToCompletionAsync(child);

        // The independent disk oracle rejects a final success claim without all effects.
        Assert.Equal(steps, _ledger.Effects.Count);
        for (var step = 1; step <= steps; step++)
            Assert.Equal($"verified effect {step}", await File.ReadAllTextAsync(
                _ledger.PathFor(step), TestContext.Current.CancellationToken));
        Assert.Empty(_client.PairingErrors);
        Assert.Contains("All requested effects complete.", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parallel_calls_receive_exactly_one_result_each_before_the_next_model_request(bool child)
    {
        ConfigureEffects();
        _client.Frames =
        [
            [Call("parallel-b", 2), Call("parallel-a", 1)],
            [Call("serial-c", 3)]
        ];

        await RunToCompletionAsync(child);

        Assert.Empty(_client.PairingErrors);
        Assert.Equal(new[] { 1, 2, 3 }, _ledger.Effects.Order().ToArray());
        Assert.Equal(3, _client.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_cycle_settles_without_a_final_model_response(bool child)
    {
        ConfigureEffects();
        _client.Repeat = true;
        _client.RefuseToolFreeResponse = true;
        _client.RejectRequestsAfter = 4;
        _client.Frames = [[Call("repeat", 1)]];

        Task<string> completion = RunToCompletionAsync(child);
        var winner = await Task.WhenAny(completion, _client.ToolFreeRequest.Task, _client.UnexpectedRequest.Task)
            .WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);

        // A no-tools provider request is the named faulty baseline behavior.
        Assert.Same(completion, winner);
        var result = await completion;
        Assert.NotEmpty(result);
        Assert.Empty(_client.PairingErrors);
        Assert.False(_client.ToolFreeRequest.Task.IsCompleted);
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(2, _ledger.Attempts.Count);
        if (!child)
            Assert.DoesNotContain("request-4-repeat", _admittedCallIds);
        if (child)
        {
            Assert.NotNull(_childResult);
            Assert.Equal(SubAgentRunOutcome.Partial, _childResult.Outcome);
            Assert.Equal(SubAgentOutcomeReason.ToolCycleStopped, _childResult.OutcomeReason);
        }
    }

    [Fact]
    public async Task Parent_compaction_preserves_the_unresolved_correction()
    {
        ConfigureEffects();
        // A completed prior turn gives the reducer a safe boundary before the active task.
        await RunToCompletionAsync(false);
        _client.Repeat = true;
        _client.Frames = [[Call("repeat-through-compaction", 1)]];
        _client.CompactAfterRequest = 4;
        _client.RejectRequestsAfter = 5;
        _client.RefuseToolFreeResponse = true;

        var completion = RunToCompletionAsync(false);
        var winner = await Task.WhenAny(completion, _client.ToolFreeRequest.Task, _client.UnexpectedRequest.Task)
            .WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);

        Assert.Same(completion, winner);
        await completion;
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(1, _client.CompactionCount);
        var compaction = Assert.Single(_compactions);
        Assert.True(compaction.Summarized);
        Assert.True(compaction.MessagesAfter < compaction.MessagesBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_work_survives_correction_but_no_member_dispatches_after_stop(bool child)
    {
        ConfigureEffects();
        _client.Frames =
        [
            [Call("repeat-a", 1)], [Call("diagnostic-a", 2)],
            [Call("repeat-b", 1)], [Call("diagnostic-b", 3)],
            [Call("refused", 1), Call("eligible", 4)],
            [Call("diagnostic-c", 5)],
            [Call("terminal", 1), Call("must-not-dispatch", 6)]
        ];

        await RunToCompletionAsync(child);

        Assert.Equal(new[] { 1, 1, 2, 3, 4, 5 }, _ledger.Effects.Order().ToArray());
        Assert.Equal(new[] { 1, 1, 2, 3, 4, 5 }, _ledger.Attempts.Order().ToArray());
        if (!child)
        {
            Assert.DoesNotContain("request-7-terminal", _admittedCallIds);
            Assert.DoesNotContain("request-7-must-not-dispatch", _admittedCallIds);
        }
        Assert.Empty(_client.PairingErrors);
        Assert.Equal(7, _client.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_count_changes_neither_inflate_nor_reset_completed_rounds(bool child)
    {
        ConfigureEffects();
        _client.Frames =
        [
            Enumerable.Range(1, 5).Select(index => Call($"first-{index}", 1)).ToArray(),
            [Call("second", 1)],
            [Call("third-a", 1), Call("third-b", 1), Call("eligible", 2)],
            [Call("terminal", 1), Call("must-not-dispatch", 3)]
        ];

        await RunToCompletionAsync(child);

        Assert.Equal(6, _ledger.Effects.Count(step => step == 1));
        Assert.Equal(1, _ledger.Effects.Count(step => step == 2));
        Assert.DoesNotContain(3, _ledger.Effects);
        Assert.Empty(_client.PairingErrors);
        Assert.Equal(4, _client.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Distinct_sibling_outcomes_reset_only_their_actual_episode(bool child)
    {
        ConfigureEffects();
        _ledger.ReceiptForOccurrence = (_, occurrence) => occurrence == 2
            ? new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure)
            : new ToolInvocationReceipt.Succeeded([], null);
        _client.Frames =
        [
            [Call("mixed-a", 1), Call("mixed-b", 1)],
            [Call("success-a", 1)], [Call("success-b", 1)],
            [Call("refused", 1), Call("eligible", 2)],
            [Call("terminal", 1), Call("must-not-dispatch", 3)]
        ];

        await RunToCompletionAsync(child);

        Assert.Equal(4, _ledger.Effects.Count(step => step == 1));
        Assert.Equal(1, _ledger.Effects.Count(step => step == 2));
        Assert.DoesNotContain(3, _ledger.Effects);
        Assert.Empty(_client.PairingErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_actual_result_begins_a_new_episode_before_the_next_correction(bool child)
    {
        ConfigureEffects();
        _ledger.ResultForOccurrence = (step, occurrence) => step == 1 && occurrence == 1
            ? "old external state" : $"new external state {step}";
        _client.Frames =
        [
            [Call("old", 1)], [Call("new-a", 1)], [Call("new-b", 1)],
            [Call("refused", 1), Call("eligible", 2)],
            [Call("terminal", 1), Call("must-not-dispatch", 3)]
        ];

        await RunToCompletionAsync(child);

        Assert.Equal(3, _ledger.Effects.Count(step => step == 1));
        Assert.Equal(1, _ledger.Effects.Count(step => step == 2));
        Assert.DoesNotContain(3, _ledger.Effects);
        Assert.Empty(_client.PairingErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_supplied_break_code_is_actual_evidence_not_an_actor_refusal(bool child)
    {
        ConfigureEffects();
        _ledger.ReceiptForOccurrence = (_, _) => new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle);
        _client.Repeat = true;
        _client.Frames = [[Call("actual-tool-advice", 1)]];
        _client.RefuseToolFreeResponse = true;
        _client.RejectRequestsAfter = 4;

        var completion = RunToCompletionAsync(child);
        var winner = await Task.WhenAny(completion, _client.ToolFreeRequest.Task, _client.UnexpectedRequest.Task)
            .WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);

        Assert.Same(completion, winner);
        await completion;
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Empty(_client.PairingErrors);
    }

    [Fact]
    public async Task Missing_final_parent_receipt_settles_without_success_substitution_or_another_request()
    {
        ConfigureEffects();
        _ledger.EmitReceipt = false;
        _client.Frames = [[Call("missing-receipt", 1)]];
        _client.RejectRequestsAfter = 1;

        var completion = RunToCompletionAsync(false);
        var winner = await Task.WhenAny(completion, _client.UnexpectedRequest.Task)
            .WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);

        Assert.Same(completion, winner);
        Assert.DoesNotContain("All requested effects complete.", await completion, StringComparison.Ordinal);
        Assert.Single(_ledger.Effects);
        Assert.Equal(1, _client.RequestCount);
    }

    [Fact]
    public async Task Missing_final_child_receipt_settles_at_the_actor_boundary_without_another_request()
    {
        ConfigureEffects();
        _ledger.HoldBeforeApply = true;
        _client.Frames = [[Call("missing-receipt", 1)]];
        _client.RejectRequestsAfter = 1;

        var completion = RunToCompletionAsync(true);
        await _ledger.ApplicationEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        Assert.NotNull(_childActor);
        // Inject a defective completed pipeline message, not an ordinary pending wait.
        _childActor.Tell(new ToolExecutionCompleted
        {
            ToolResults = [new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.Tool,
                Name = ProbeTool,
                ToolCallId = new ToolCallId("request-1-missing-receipt"),
                Content = "The final pipeline result deliberately has no receipt."
            }]
        });
        var winner = await Task.WhenAny(completion, _client.UnexpectedRequest.Task)
            .WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);

        Assert.Same(completion, winner);
        await completion;
        Assert.NotNull(_childResult);
        Assert.NotEqual(SubAgentRunOutcome.Completed, _childResult.Outcome);
        Assert.Empty(_ledger.Effects);
        Assert.Equal(1, _client.RequestCount);
    }

    private void ConfigureEffects()
        => _ledger.Directory = Path.Combine(TestPaths.BasePath, "adversarial-effects");

    private async Task<string> RunToCompletionAsync(bool child)
    {
        if (child)
        {
            var definition = new SubAgentDefinition
            {
                Name = new AgentName("adversarial-worker"),
                SystemPrompt = "Execute each distinct requested effect. Report partial work truthfully.",
                Tools = [new EffectTool(_ledger)],
                EmitStructuredFindings = false
            };
            var policy = new ToolAccessPolicy(
                TestPaths, new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
                new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
                new ShellCommandPolicy(), new ToolPathPolicy([]));
            var actor = Sys.ActorOf(SubAgentActor.CreateProps(definition, _client, policy));
            _childActor = actor;
            var scope = SubAgentTestScope.Create(sessionDirectory: Path.Combine(TestPaths.BasePath, "child-workspace"));
            var storage = SessionStoragePaths.CreateLegacy(
                Path.Combine(TestPaths.BasePath, "child-workspace"),
                Path.Combine(TestPaths.BasePath, "child-logs"), "adversarial-child");
            scope = scope with { Authority = scope.Authority with { Session = new ToolSessionScope.Bound("adversarial-child", storage) } };
            var result = await actor.Ask<SubAgentResult>(new RunSubAgent
            {
                Scope = scope,
                Task = "Execute the requested effect sequence.",
                Timeout = FaultCeiling
            }, FaultCeiling, TestContext.Current.CancellationToken);
            _childResult = result;
            return result.Output;
        }

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        var sessionId = new SessionId("adversarial/effects");
        await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Execute the requested effect sequence."
        }, FaultCeiling, TestContext.Current.CancellationToken);
        var outputs = new List<string>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            if (message is TextOutput text)
                outputs.Add(text.Text);
            if (message is CompactionOutput compaction)
                _compactions.Add(compaction);
            if (message is ToolCallOutput admitted)
                _admittedCallIds.Add(admitted.CallId.Value);
            return message is TurnCompleted;
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        return string.Join("\n", outputs);
    }

    private static FunctionCallContent Call(string id, int step) => new(id, ProbeTool,
        new Dictionary<string, object?>
        {
            ["step"] = step,
            ["_rationale"] = "Execute the next specified effect."
        });

    private sealed class AdversarialChatClient : IChatClient
    {
        private readonly List<FunctionCallContent> _previousCalls = [];
        private int _requests;
        private int _compactions;
        public IReadOnlyList<FunctionCallContent[]> Frames { get; set; } = [];
        public bool Repeat { get; set; }
        public bool RefuseToolFreeResponse { get; set; }
        public int RejectRequestsAfter { get; set; } = int.MaxValue;
        public int CompactAfterRequest { get; set; } = int.MaxValue;
        public int CompactionCount => Volatile.Read(ref _compactions);
        public int RequestCount => Volatile.Read(ref _requests);
        public ConcurrentQueue<string> PairingErrors { get; } = new();
        public TaskCompletionSource ToolFreeRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UnexpectedRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            if (snapshot.Any(message => message.Role == ChatRole.System
                && message.Text.Contains("You are a session summarizer", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref _compactions);
                return Text("The effect task remains incomplete. Preserve the recorded tool evidence.");
            }
            var request = Interlocked.Increment(ref _requests);
            if (request > RejectRequestsAfter)
            {
                UnexpectedRequest.TrySetResult();
                await TestStreamingHelpers.ParkUntilCancelledAsync(cancellationToken);
            }
            var results = snapshot.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
            foreach (var previous in _previousCalls)
            {
                var count = results.Count(result => result.CallId == previous.CallId);
                if (count != 1)
                    PairingErrors.Enqueue($"Call {previous.CallId} received {count} results before request {request}.");
            }

            if (options?.Tools is not { Count: > 0 })
            {
                if (RefuseToolFreeResponse)
                {
                    ToolFreeRequest.TrySetResult();
                    await TestStreamingHelpers.ParkUntilCancelledAsync(cancellationToken);
                }
                return Text("Premature final response; requested effects are incomplete.");
            }

            if (!Repeat && request > Frames.Count)
                return Text("All requested effects complete.");

            var frame = Frames[Repeat ? 0 : request - 1];
            var calls = frame.Select(call => new FunctionCallContent(
                $"request-{request}-{call.CallId}", call.Name, call.Arguments)).ToArray();
            _previousCalls.Clear();
            _previousCalls.AddRange(calls);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList()))
            {
                Usage = request == CompactAfterRequest ? new UsageDetails { InputTokenCount = 1_990_000 } : null
            };
        }

        private static ChatResponse Text(string text)
            => new(new ChatMessage(ChatRole.Assistant, text));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class EffectLedger
    {
        public string Directory { get; set; } = string.Empty;
        public ConcurrentBag<int> Effects { get; } = [];
        public ConcurrentBag<int> Attempts { get; } = [];
        private readonly ConcurrentDictionary<int, int> _occurrences = new();
        private readonly object _effectGate = new();
        public bool EmitReceipt { get; set; } = true;
        public bool HoldBeforeApply { get; set; }
        public TaskCompletionSource ApplicationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<int, int, ToolInvocationReceipt> ReceiptForOccurrence { get; set; }
            = (_, _) => new ToolInvocationReceipt.Succeeded([], null);
        public Func<int, int, string> ResultForOccurrence { get; set; }
            = (step, _) => $"verified effect {step}";
        public string PathFor(int step) => Path.Combine(Directory, $"effect-{step}.txt");

        public async Task<(string Output, ToolInvocationReceipt Receipt)> ApplyAsync(IDictionary<string, object?> arguments, CancellationToken ct)
        {
            var value = arguments["step"];
            var step = value is JsonElement element
                ? element.GetInt32() : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            Attempts.Add(step);
            if (HoldBeforeApply)
            {
                ApplicationEntered.TrySetResult();
                await TestStreamingHelpers.ParkUntilCancelledAsync(ct);
            }
            lock (_effectGate)
            {
                ct.ThrowIfCancellationRequested();
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(PathFor(step), $"verified effect {step}");
                Effects.Add(step);
                var occurrence = _occurrences.AddOrUpdate(step, 1, (_, value) => value + 1);
                return (ResultForOccurrence(step, occurrence), ReceiptForOccurrence(step, occurrence));
            }
        }
    }

    private sealed class EffectExecutor(EffectLedger ledger) : IToolExecutor
    {
        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            var (output, receipt) = await ledger.ApplyAsync(call.Arguments!, ct);
            if (ledger.EmitReceipt)
                context.Outputs.TryComplete(receipt);
            return output;
        }
    }

    private sealed class EffectTool(EffectLedger ledger) : INetclawTool
    {
        public string Name => ProbeTool;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Execute one independently verified local effect.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { step = new { type = "integer" } }, required = new[] { "step" }
        });
        public AITool ToAITool() => AIFunctionFactory.Create((int step) => $"Step {step}", Name);
        public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => (await ledger.ApplyAsync(arguments!, ct)).Output;
        public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
        {
            var (output, receipt) = await ledger.ApplyAsync(arguments!, ct);
            if (ledger.EmitReceipt)
                context.Outputs.TryComplete(receipt);
            return output;
        }
    }
}
