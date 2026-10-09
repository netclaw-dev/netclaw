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
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Dispatch.MessageQueues;
using Akka.Event;
using Akka.Persistence;
using Akka.Routing;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;
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
    private readonly RecordingGrantService _grants = new();
    private IActorRef? _childActor;
    private SubAgentResult? _childResult;
    private TestProbe? _parentSubscriber;
    private bool _captureReplies;
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
        services.AddSingleton<IToolApprovalService>(_grants);
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
        var executionToken = await _ledger.ApplicationEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        Assert.NotNull(_childActor);
        // Inject a defective completed pipeline message, not an ordinary pending wait.
        _childActor.Tell(new ToolExecutionCompleted
        {
            ExecutionToken = executionToken,
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

    [Theory]
    [InlineData(false, "model")]
    [InlineData(false, "single")]
    [InlineData(true, "model")]
    [InlineData(true, "single")]
    public async Task Settled_parent_rejects_late_replies_before_or_during_a_fresh_task(bool freshTask, string replyKind)
    {
        ConfigureEffects();
        _client.Repeat = true;
        _client.Frames = [[Call("repeat", 1)]];
        _client.RejectRequestsAfter = freshTask ? 5 : 4;
        InstallReplyCaptureMailbox();
        await RunToCompletionAsync(false);
        var subscriber = Assert.IsType<TestProbe>(_parentSubscriber);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var session = new SessionId("adversarial/effects");
        var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
            .ResolveOne(FaultCeiling, TestContext.Current.CancellationToken);
        if (freshTask)
        {
            _client.HoldRequest = 5;
            _client.Repeat = false;
            await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
            { SessionId = session, Content = "This new authorized task must remain separate." },
                FaultCeiling, TestContext.Current.CancellationToken);
            await _client.HeldRequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        }

        var captured = CapturedReply(replyKind);
        // The original sender orders the exact old envelope before the actor acknowledgement.
        owner.Tell(captured.Message, captured.Sender);
        owner.Tell(new JoinSession(subscriber)
        { SessionId = session, Filter = (OutputFilter.Full | OutputFilter.ProcessingState) & ~OutputFilter.TextStreaming }, captured.Sender);
        var observed = new List<object>();
        var barrier = await subscriber.FishForMessageAsync<object>(message =>
        {
            observed.Add(message);
            return message is SessionJoined;
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var state = Assert.IsType<SessionJoined>(barrier);
        Assert.Equal(1, state.TurnCount);
        Assert.DoesNotContain(observed, message => message is TurnCompleted or ToolInteractionRequest or ToolResultOutput);
        Assert.NotNull(state.RecentMessages);
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(freshTask ? 5 : 4, _client.RequestCount);
        Assert.False(_client.UnexpectedRequest.Task.IsCompleted);

        if (freshTask)
        {
            _client.ReleaseHeldRequest.TrySetResult();
            await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
                FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
            await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
                FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, _ledger.Effects.Count);
            Assert.Equal(5, _client.RequestCount);
        }
    }

    [Theory]
    [InlineData("model")]
    [InlineData("aggregate")]
    public async Task Settled_child_stops_and_cannot_accept_a_late_reply(string replyKind)
    {
        ConfigureEffects();
        _client.Repeat = true;
        _client.Frames = [[Call("repeat", 1)]];
        _client.RejectRequestsAfter = 4;
        InstallReplyCaptureMailbox();
        await RunToCompletionAsync(true);
        var child = Assert.IsAssignableFrom<IActorRef>(_childActor);
        var watcher = CreateTestProbe();
        watcher.Watch(child);
        await watcher.ExpectTerminatedAsync(child, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var deadLetters = CreateTestProbe();
        Sys.EventStream.Subscribe(deadLetters, typeof(DeadLetter));
        var captured = CapturedReply(replyKind);
        child.Tell(captured.Message, captured.Sender);
        await deadLetters.FishForMessageAsync<DeadLetter>(message => ReferenceEquals(message.Message, captured.Message) && Equals(message.Recipient, child),
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(4, _client.RequestCount);
        Assert.Equal(SubAgentRunOutcome.Partial, Assert.IsType<SubAgentResult>(_childResult).Outcome);
    }

    [Theory]
    [InlineData("single", false)]
    [InlineData("batch", false)]
    [InlineData("single", true)]
    [InlineData("batch", true)]
    public async Task A_prior_reply_cannot_complete_a_new_batch_that_reuses_its_provider_call_id(string replyKind, bool identicalArguments)
    {
        ConfigureEffects();
        _client.Repeat = true;
        _client.Frames = [[Call("repeat", 1)]];
        InstallReplyCaptureMailbox();
        await RunToCompletionAsync(false);
        var subscriber = Assert.IsType<TestProbe>(_parentSubscriber);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var captured = CapturedReply(replyKind);
        var oldResult = Assert.IsType<ToolExecutionSingleCompleted>(CapturedReply("single").Message).Result;
        var oldId = Assert.IsType<ToolCallId>(oldResult.Message.ToolCallId).Value;
        _client.Repeat = false;
        _client.PreserveCallIdsFromRequest = 5;
        var currentStep = identicalArguments ? 1 : 2;
        _client.Frames = [[], [], [], [], [Call(oldId, currentStep)]];
        _client.RejectRequestsAfter = 6;
        _ledger.HoldStep = currentStep;
        var session = new SessionId("adversarial/effects");
        await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "A new requester authorizes a different effect.",
            Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-new"),
                Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
                Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
                DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", "operator-new", "operator-new")
            }
        }, FaultCeiling, TestContext.Current.CancellationToken);
        await _ledger.ApplicationEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var current = _ledger.Executions.Last();
        Assert.Equal(oldId, current.CallId);
        Assert.NotEqual(oldResult.AuthorizationAttemptId, current.AuthorizationAttemptId);
        Assert.Equal("operator-new", current.Scope.DefaultDeliveryTarget?.DestinationId);
        var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
            .ResolveOne(FaultCeiling, TestContext.Current.CancellationToken);
        owner.Tell(captured.Message, captured.Sender);
        owner.Tell(new JoinSession(subscriber)
        { SessionId = session, Filter = (OutputFilter.Full | OutputFilter.ProcessingState) & ~OutputFilter.TextStreaming }, captured.Sender);
        var observed = new List<object>();
        var barrier = await subscriber.FishForMessageAsync<object>(message =>
        {
            observed.Add(message);
            return message is SessionJoined;
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.IsType<SessionJoined>(barrier).TurnCount);
        Assert.False(current.CancellationToken.IsCancellationRequested);
        Assert.DoesNotContain(observed, message => message is TurnCompleted or ToolInteractionRequest or ToolResultOutput);
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(5, _client.RequestCount);

        _ledger.ReleaseApplication.TrySetResult();
        var currentResults = new List<ToolResultOutput>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            if (message is ToolResultOutput result) currentResults.Add(result);
            return message is TurnCompleted;
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var actual = Assert.Single(currentResults);
        Assert.Equal(oldId, actual.CallId.Value);
        Assert.Equal($"verified effect {currentStep}", actual.Result);
        Assert.Equal(new[] { 1, 1, currentStep }.Order().ToArray(), _ledger.Effects.Order().ToArray());
        Assert.Equal($"verified effect {currentStep}", await File.ReadAllTextAsync(_ledger.PathFor(currentStep), TestContext.Current.CancellationToken));
        Assert.Equal(6, _client.RequestCount);
        Assert.Equal("operator-new", _ledger.Executions.Last().Scope.DefaultDeliveryTarget?.DestinationId);
        var latestRequest = _client.Requests.Last();
        var latestBatch = Array.FindLastIndex(latestRequest, message =>
            message.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == oldId));
        Assert.True(latestBatch >= 0);
        var paired = Assert.Single(latestRequest.Skip(latestBatch + 1)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>()), result => result.CallId == oldId);
        Assert.Equal($"verified effect {currentStep}", paired.Result?.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_closed_approval_callback_cannot_create_a_prompt_or_grant_for_a_fresh_task(bool replayRequest)
    {
        ConfigureEffects();
        _ledger.RequireApproval = true;
        _client.Repeat = true;
        _client.Frames = [[Call("approved-repeat", 1)]];
        InstallReplyCaptureMailbox();
        var session = new SessionId("adversarial/effects");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full | OutputFilter.ProcessingState);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Execute the effect with my explicit approval.",
            Source = ApprovalRequester("operator-old")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        var prompts = new List<ToolInteractionRequest>();
        for (var round = 0; round < 2; round++)
        {
            var request = await subscriber.FishForMessageAsync<ToolInteractionRequest>(_ => true,
                FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
            prompts.Add(request);
            await manager.Ask<CommandAck>(new ToolInteractionResponse
            {
                SessionId = session, CallId = request.CallId,
                SelectedKey = ApprovalOptionKeys.ApproveOnceKey, SenderId = new SenderId("operator-old")
            }, FaultCeiling, TestContext.Current.CancellationToken);
        }
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(4, _client.RequestCount);
        Assert.Empty(_grants.Writes);
        var captured = ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys).First(envelope =>
            envelope.Message is ToolExecutionApprovalRequested);
        var callback = Assert.IsType<ToolExecutionApprovalRequested>(captured.Message);
        Assert.Same(prompts[0], callback.Dispatch.Request);
        Assert.True(callback.ExecutionToken.IsCancellationRequested);
        var before = await ReadSessionEventsAsync(session);
        Assert.Equal(2, before.OfType<ToolApprovalRequested>().Count());
        Assert.Equal(2, before.OfType<ToolApprovalResolved>().Count());
        var confirmed = before.OfType<ToolCallRecorded>()
            .Where(evt => evt.ToolResult.Content.Contains("verified effect 1", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, confirmed.Length);
        Assert.Equal(confirmed[0].ToolResult.Content, confirmed[1].ToolResult.Content);

        _client.Repeat = false;
        _client.HoldRequest = 5;
        _client.RejectRequestsAfter = 5;
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "A fresh task needs no old approval.",
            Source = ApprovalRequester("operator-new")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        await _client.HeldRequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
            .ResolveOne(FaultCeiling, TestContext.Current.CancellationToken);
        if (replayRequest)
            owner.Tell(captured.Message, captured.Sender);
        owner.Tell(new JoinSession(subscriber)
        { SessionId = session, Filter = (OutputFilter.Full | OutputFilter.ProcessingState) & ~OutputFilter.TextStreaming }, captured.Sender);
        var observed = new List<object>();
        var barrier = await subscriber.FishForMessageAsync<object>(message =>
        {
            observed.Add(message);
            return message is SessionJoined;
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.IsType<SessionJoined>(barrier).TurnCount);
        Assert.DoesNotContain(observed, message => message is ToolInteractionRequest or TurnCompleted or ToolResultOutput);
        var after = await ReadSessionEventsAsync(session);
        Assert.Equal(before.OfType<ToolApprovalRequested>().Select(evt => JsonSerializer.Serialize(evt)).ToArray(),
            after.OfType<ToolApprovalRequested>().Select(evt => JsonSerializer.Serialize(evt)).ToArray());
        Assert.Equal(before.OfType<ToolApprovalResolved>().Select(evt => JsonSerializer.Serialize(evt)).ToArray(),
            after.OfType<ToolApprovalResolved>().Select(evt => JsonSerializer.Serialize(evt)).ToArray());
        var nack = await manager.Ask<CommandNack>(new ToolInteractionResponse
        {
            SessionId = session, CallId = prompts[0].CallId,
            SelectedKey = ApprovalOptionKeys.ApproveSessionKey, SenderId = new SenderId("operator-old")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalNackReasons.PromptExpired, nack.Reason);
        Assert.Empty(_grants.Writes);
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(5, _client.RequestCount);
        _client.ReleaseHeldRequest.TrySetResult();
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _ledger.Effects.Count);
        Assert.Equal(5, _client.RequestCount);
        Assert.Empty(_grants.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_prior_pipeline_failure_cannot_cancel_a_fresh_effect(bool replayFailure)
    {
        ConfigureEffects();
        InstallReplyCaptureMailbox();
        var originalFailure = new InvalidOperationException("The execution preflight service failed.");
        _ledger.PreflightFailure = originalFailure;
        _client.PreserveCallIdsFromRequest = 1;
        _client.Frames = [[Call("failed-preflight", 1)]];
        var session = new SessionId("adversarial/effects");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full | OutputFilter.ProcessingState);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Execute the original effect.", Source = ApprovalRequester("operator-old")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        var originalOutputs = new List<object>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            originalOutputs.Add(message);
            return message is ProcessingStateOutput { IsProcessing: false };
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var captured = ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys)
            .Single(envelope => envelope.Message is ToolExecutionFailed);
        var failed = Assert.IsType<ToolExecutionFailed>(captured.Message);
        Assert.Same(originalFailure, failed.Cause);
        Assert.True(failed.ExecutionToken.IsCancellationRequested);
        Assert.Empty(_ledger.Effects);
        Assert.Empty(_ledger.Executions);
        Assert.Single(originalOutputs.OfType<ErrorOutput>());
        var originalEvents = await ReadSessionEventsAsync(session);
        var abandoned = Assert.Single(originalEvents.OfType<ToolBatchAbandoned>());
        var abandonedResult = Assert.Single(abandoned.ToolResults);
        Assert.Equal("failed-preflight", abandonedResult.ToolCallId?.Value);
        Assert.Contains("not completed", abandonedResult.Content, StringComparison.Ordinal);

        _ledger.PreflightFailure = null;
        _ledger.HoldStep = 2;
        _client.Frames = [[], [Call("current-effect", 2)]];
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Execute the fresh effect.", Source = ApprovalRequester("operator-new")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        var currentToken = await _ledger.ApplicationEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var currentExecution = Assert.Single(_ledger.Executions);
        Assert.Equal("current-effect", currentExecution.CallId);
        Assert.Equal(TrustBoundary.Personal, currentExecution.Scope.Boundary);
        Assert.NotEqual(failed.ExecutionToken, currentToken);
        var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
            .ResolveOne(FaultCeiling, TestContext.Current.CancellationToken);
        if (replayFailure)
            owner.Tell(captured.Message, captured.Sender);
        owner.Tell(new JoinSession(subscriber)
        { SessionId = session, Filter = (OutputFilter.Full | OutputFilter.ProcessingState) & ~OutputFilter.TextStreaming }, captured.Sender);
        var observed = new List<object>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            observed.Add(message);
            return message is SessionJoined;
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(observed, message => message is ErrorOutput or TurnCompleted or ToolResultOutput);
        Assert.False(currentToken.IsCancellationRequested);
        Assert.Empty(_ledger.Effects);
        var heldEvents = await ReadSessionEventsAsync(session);
        Assert.Equal("operator-new", heldEvents.OfType<InputAdmitted>().Last().TurnContext.RequesterSenderId?.Value);
        Assert.DoesNotContain(heldEvents.OfType<ToolCallRecorded>(), evt => evt.ToolResult.ToolCallId?.Value == "current-effect");
        _ledger.ReleaseApplication.TrySetResult();
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(_ledger.Effects));
        Assert.Single(_ledger.Executions);
        Assert.Equal("verified effect 2", await File.ReadAllTextAsync(_ledger.PathFor(2), TestContext.Current.CancellationToken));
        Assert.Empty(_client.PairingErrors);
        var finalEvents = await ReadSessionEventsAsync(session);
        var actual = Assert.Single(finalEvents.OfType<ToolCallRecorded>(), evt => evt.ToolResult.ToolCallId?.Value == "current-effect");
        Assert.Equal("verified effect 2", actual.ToolResult.Content);
        var reply = Assert.Single(ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys)
            .Select(envelope => envelope.Message).OfType<ToolExecutionSingleCompleted>(),
            message => message.Result.Message.ToolCallId?.Value == "current-effect");
        Assert.Equal(currentExecution.AuthorizationAttemptId, reply.Result.AuthorizationAttemptId);
        Assert.IsType<ToolInvocationReceipt.Succeeded>(reply.Result.Receipt);
    }

    private static MessageSource ApprovalRequester(string sender) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(sender),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", sender, sender)
    };

    private async Task<IReadOnlyList<ISessionEvent>> ReadSessionEventsAsync(SessionId session)
    {
        var reader = CreateTestProbe();
        AkkaPersistence.Instance.Apply(Sys).JournalFor(string.Empty).Tell(
            new ReplayMessages(1, long.MaxValue, long.MaxValue, $"session-{session.Value}", reader));
        var events = new List<ISessionEvent>();
        await reader.FishForMessageAsync<object>(message =>
        {
            if (message is ReplayedMessage replayed)
                events.Add(Assert.IsAssignableFrom<ISessionEvent>(replayed.Persistent.Payload));
            return message is RecoverySuccess;
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        return events;
    }

    private Envelope CapturedReply(string kind)
    {
        if (kind == "model")
            return ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys).Single(envelope =>
                envelope.Message is LlmResponseReceived { CallId: 4 });
        if (kind == "single")
            return ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys).First(envelope =>
                envelope.Message is ToolExecutionSingleCompleted { Result.Receipt: ToolInvocationReceipt.Succeeded });
        if (kind == "batch")
            return ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys).First(envelope =>
                envelope.Message is ToolExecutionBatchCompleted);
        return ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys).First(envelope =>
            envelope.Message is ToolExecutionCompleted completed
            && completed.ToolReceipts.Values.Any(receipt => receipt is ToolInvocationReceipt.Succeeded));
    }

    private void InstallReplyCaptureMailbox()
    {
        _captureReplies = true;
        Sys.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            $"reply-capture-mailbox {{ mailbox-type = \"{typeof(ReplyCaptureMailbox).AssemblyQualifiedName}\" }}"));
        ((ExtendedActorSystem)Sys).Provider.Deployer.SetDeploy(new Deploy(
            "/session-manager/adversarial%2Feffects", Config.Empty, NoRouter.Instance, LocalScope.Instance,
            Deploy.NoDispatcherGiven, "reply-capture-mailbox"));
    }

    public sealed class ReplyCaptureMailbox(Settings settings, Config config) : MailboxType(settings, config), IProducesMessageQueue<ReplyCaptureQueue>
    {
        internal static readonly ConditionalWeakTable<ActorSystem, ConcurrentQueue<Envelope>> Captures = new();
        public override IMessageQueue Create(IActorRef owner, ActorSystem system)
            => new ReplyCaptureQueue(Captures.GetOrCreateValue(system));
    }

    public sealed class ReplyCaptureQueue(ConcurrentQueue<Envelope> captured) : IMessageQueue, IUnboundedDequeBasedMessageQueueSemantics
    {
        private readonly UnboundedDequeMessageQueue _inner = new();
        public int Count => _inner.Count;
        public bool HasMessages => _inner.HasMessages;
        public void Enqueue(IActorRef receiver, Envelope envelope)
        {
            if (envelope.Message is LlmResponseReceived or ToolExecutionSingleCompleted or ToolExecutionBatchCompleted or ToolExecutionCompleted or ToolExecutionApprovalRequested or ToolExecutionFailed or SpawnChildActorRequest or ToolExecutionSubAgentActivity
                || envelope.Message.GetType().Name is "RoutedSkillSubAgentActivity" or "RoutedSkillExecutionCompleted" or "RoutedSkillExecutionFailed")
                captured.Enqueue(envelope);
            _inner.Enqueue(receiver, envelope);
        }
        public void EnqueueFirst(Envelope envelope) => _inner.EnqueueFirst(envelope);
        public bool TryDequeue(out Envelope envelope) => _inner.TryDequeue(out envelope);
        public void CleanUp(IActorRef owner, IMessageQueue deadletters) => _inner.CleanUp(owner, deadletters);
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
            var props = SubAgentActor.CreateProps(definition, _client, policy);
            var actor = Sys.ActorOf(_captureReplies ? props.WithMailbox("reply-capture-mailbox") : props);
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
        _parentSubscriber = subscriber;
        var sessionId = new SessionId("adversarial/effects");
        await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full | OutputFilter.ProcessingState);
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
        public int HoldRequest { get; set; } = int.MaxValue;
        public int PreserveCallIdsFromRequest { get; set; } = int.MaxValue;
        public ConcurrentQueue<ChatMessage[]> Requests { get; } = new();
        public TaskCompletionSource HeldRequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHeldRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            Requests.Enqueue(snapshot);
            if (request == HoldRequest)
            {
                HeldRequestEntered.TrySetResult();
                await ReleaseHeldRequest.Task.WaitAsync(cancellationToken);
            }
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
                request >= PreserveCallIdsFromRequest ? call.CallId : $"request-{request}-{call.CallId}", call.Name, call.Arguments)).ToArray();
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

    private sealed class RecordingGrantService : IToolApprovalService
    {
        public ConcurrentQueue<IReadOnlyList<ToolApprovalGrant>> Writes { get; } = new();
        public Task<ToolApprovalCheckResult> CheckApprovalAsync(ToolApprovalSessionId? sessionId,
            TrustAudience audience, ToolName toolName, IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd, CancellationToken ct = default)
            => Task.FromResult(new ToolApprovalCheckResult(candidates.Select(candidate => candidate.Verb).ToArray(), []));
        public Task RecordApprovalCandidatesAsync(ToolApprovalSessionId sessionId,
            TrustAudience audience, ToolName toolName, IReadOnlyList<ToolApprovalGrant> grants,
            CancellationToken ct = default)
        {
            Writes.Enqueue(grants.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class EffectLedger
    {
        public sealed record Execution(string CallId, AuthorizationAttemptId AuthorizationAttemptId, ToolRunScope Scope, CancellationToken CancellationToken);
        public ConcurrentQueue<Execution> Executions { get; } = new();
        public string Directory { get; set; } = string.Empty;
        public ConcurrentBag<int> Effects { get; } = [];
        public ConcurrentBag<int> Attempts { get; } = [];
        private readonly ConcurrentDictionary<int, int> _occurrences = new();
        private readonly object _effectGate = new();
        public bool EmitReceipt { get; set; } = true;
        public bool RequireApproval { get; set; }
        public Exception? PreflightFailure { get; set; }
        public bool HoldBeforeApply { get; set; }
        public int HoldStep { get; set; } = int.MaxValue;
        public TaskCompletionSource ReleaseApplication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CancellationToken> ApplicationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                ApplicationEntered.TrySetResult(ct);
                await TestStreamingHelpers.ParkUntilCancelledAsync(ct);
            }
            if (step == HoldStep)
            {
                ApplicationEntered.TrySetResult(ct);
                await ReleaseApplication.Task.WaitAsync(ct);
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
        public ToolCallInterpretation InterpretToolCall(FunctionCallContent call)
        {
            if (ledger.PreflightFailure is { } failure)
                throw failure;
            var (meta, cleaned) = ToolCallMetaExtractor.Extract(call);
            return new ToolCallInterpretation(null, meta, cleaned);
        }

        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            if (ledger.RequireApproval && context.OneTimeApprovedToolName != call.Name)
                throw new ToolApprovalRequiredException(new ToolApprovalContext(
                    call.Name, "The effect needs explicit approval.", [call.Name], [call.Name],
                    [new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, "Approve once"),
                     new ToolApprovalOption(ApprovalOptionKeys.ApproveSessionKey, "This chat"),
                     new ToolApprovalOption(new ApprovalOptionKey(ApprovalOptionKeys.Deny), "Deny")],
                    Candidates: [new ApprovalCandidate(call.Name, null)]));
            ledger.Executions.Enqueue(new EffectLedger.Execution(call.CallId, context.Approval.AuthorizationAttemptId, context.RunScope, ct));
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
