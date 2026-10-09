// -----------------------------------------------------------------------
// <copyright file="ToolTaskRecoveryAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Netclaw.Security;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;
using Xunit;
using ModelRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolTaskRecoveryAdversarialTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(20);
    private const string RestartInstruction = "Resume the work that was interrupted by the daemon restart.";
    private readonly RecoveryClient _client = new();
    private readonly RecoveryExecutor _executor = new();
    protected override bool VerifySerialization => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0 } });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Continue the admitted task."));
        services.AddSingleton<IToolExecutor>(_executor);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "same", "search_tools"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create((string command) => command, "shell_execute"), "builtin");
        services.AddSingleton(registry);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Actual_recovery_keeps_the_closed_inputs_task_and_its_completed_result(bool missingReceipt, bool invalidQueuedResult)
    {
        var session = new SessionId("signalr/closed-input-recovery");
        var input = Input(session, "original-input", "operator-a");
        var batch = Prepare("durable-call");
        var tracker = new TurnStateTracker();
        var admission = Admission(batch, input.TurnContext.TurnId);
        var events = new List<ISessionEvent>
        {
            input, Adoption(input, [input.InputId]),
            new ToolBatchStarted
            {
                SessionId = session, UserMessage = input.UserMessage,
                AssistantMessage = Assistant("durable-call"), ConsumedInputIds = [input.InputId],
                LoopAdmission = admission, LoopDelta = tracker.CaptureDelta(input.TurnContext.TurnId, new ToolLoopCheckpoint())
            },
            new ToolCallRecorded
            {
                SessionId = session,
                ToolResult = new SerializableChatMessage
                { Role = Netclaw.Actors.Protocol.ChatRole.Tool, Name = "search_tools", ToolCallId = new ToolCallId("durable-call"), Content = "same" },
                LoopObservation = ToolCycleSignatureFactory.CreateObservation("durable-call",
                    missingReceipt ? null : new ToolInvocationReceipt.Succeeded([], null), "same", false)
            }
        };
        if (invalidQueuedResult)
        {
            var source = JobSource("orphan", new BackgroundJobOrigin(new TurnId("uncommitted"), new ToolCallId("launch")));
            events.Add(new InputAdmitted
            {
                SessionId = session, InputId = new InputId("orphan-input"), SourceMessageId = source.MessageId,
                SourceBackgroundJobId = source.BackgroundJobId, BackgroundJobOrigin = source.BackgroundJobOrigin,
                BackgroundJobLineageVersion = source.BackgroundJobLineageVersion,
                TurnContext = TurnContext.FromMessageSource(session, source.TurnId!.Value, source).ToRecord(),
                UserMessage = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = "Never adopt this orphan result." }
            });
        }
        await SeedAsync(session, events);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(missingReceipt ? 0 : 3, _client.Count);
        Assert.Equal(missingReceipt ? 0 : 1, _executor.Count);
        if (!missingReceipt)
        {
            var first = _client.Requests[0];
            Assert.Contains(first.SelectMany(message => message.Contents.OfType<FunctionResultContent>()), result => result.CallId == "durable-call");
            Assert.DoesNotContain(first, message => message.Text == RestartInstruction);
            Assert.All(_executor.Targets, target => Assert.Equal("operator-a", target));
            Assert.All(_client.Requests, request => Assert.DoesNotContain(request,
                message => message.Role == ModelRole.User && message.Text == "Never adopt this orphan result."));
        }
    }

    [Fact]
    public async Task A_completed_task_does_not_reopen_from_its_retained_checkpoint()
    {
        var session = new SessionId("signalr/completed-recovery");
        var input = Input(session, "completed-input", "operator-a");
        await SeedAsync(session,
        [
            input, Adoption(input, [input.InputId]),
            new TurnRecorded
            {
                SessionId = session, ConsumedInputIds = [input.InputId], UserMessage = input.UserMessage,
                AssistantReply = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.Assistant, Content = "The task is complete." }
            }
        ]);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session);
        await manager.Ask<CommandNack>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        Assert.Equal(0, _client.Count);
        Assert.Equal(0, _executor.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_requesters_make_progress_in_arrival_order_with_the_same_prefix_after_restart(bool restart)
    {
        var session = new SessionId("signalr/mixed-requesters");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        _client.CompleteEachTask = true;
        _client.PauseFirst = true;
        _client.PauseSecond = restart;
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = "old", Source = Source("old", "operator-a") }, FaultCeiling, TestContext.Current.CancellationToken);
        await _client.FirstEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        foreach (var (id, requester) in new[] { ("peer-first", "operator-b"), ("peer-last", "operator-b"), ("other", "operator-c") })
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = session, Content = id, Source = Source(id, requester) }, FaultCeiling, TestContext.Current.CancellationToken);
        _client.ReleaseFirst.TrySetResult();
        if (restart)
        {
            await _client.SecondEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            var actor = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
                .ResolveOne(FaultCeiling, TestContext.Current.CancellationToken);
            var watcher = CreateTestProbe();
            watcher.Watch(actor);
            var ack = await manager.Ask<DaemonRestartPrepared>(new PrepareForDaemonRestart(session, "daemon-stop"),
                FaultCeiling, TestContext.Current.CancellationToken);
            Assert.NotNull(ack.RestartReminder);
            await watcher.ExpectTerminatedAsync(actor, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
            subscriber = CreateTestProbe();
            await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
            await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        }
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted && _executor.Count == 3,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["operator-a", "operator-b", "operator-c"], _executor.Targets);
        var prefixRequests = _client.Requests.Where(request => request.LastOrDefault(message => message.Role == ModelRole.User)?.Text == "peer-last").ToArray();
        Assert.NotEmpty(prefixRequests);
        Assert.All(prefixRequests, request =>
        {
            Assert.Contains(request, message => message.Role == ModelRole.User && message.Text == "peer-first");
            Assert.DoesNotContain(request, message => message.Role == ModelRole.User && message.Text == "other");
        });
    }

    [Fact]
    public void Closure_of_an_unrelated_queued_input_keeps_the_active_task_authority()
    {
        var session = new SessionId("adversarial/unrelated-close");
        var active = Input(session, "active", "operator-a");
        var queued = Input(session, "queued", "operator-b");
        var state = SessionState.Empty.Apply(active).Apply(Adoption(active, [active.InputId])).Apply(queued);
        var next = state.Apply(new InputClosed { SessionId = session, InputIds = [queued.InputId] });
        Assert.Equal(active.TurnContext, next.AdoptedTaskContext);
        Assert.Equal(active.TurnContext.TurnId, next.LoopCheckpoint.TaskId);
        Assert.Equal(active.InputId, Assert.Single(next.PendingInputs).InputId);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("kind")]
    public void Adoption_cannot_replace_the_latest_admitted_source_representation(string field)
    {
        var session = new SessionId("adversarial/source-fields");
        var input = Input(session, "input", "operator-a");
        var evt = Adoption(input, [input.InputId]);
        evt = evt with { TurnContext = field == "scope"
            ? evt.TurnContext with { SourceScope = "foreign-scope" }
            : evt.TurnContext with { SourceKind = "foreign-kind" } };
        Assert.Throws<InvalidDataException>(() => SessionState.Empty.Apply(input).Apply(evt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_job_result_without_the_parent_receipt_never_requests_the_model(bool crashAfterInput)
    {
        var session = new SessionId("signalr/orphan-job-result");
        var source = JobSource("orphan", new BackgroundJobOrigin(new TurnId("old-task"), new ToolCallId("launch")));
        const string data = "The process wrote this known partial result.";
        if (crashAfterInput)
            await SeedAsync(session, [new InputAdmitted
            {
                SessionId = session, InputId = new InputId("orphan-input"), SourceMessageId = source.MessageId,
                SourceBackgroundJobId = source.BackgroundJobId, BackgroundJobOrigin = source.BackgroundJobOrigin,
                BackgroundJobLineageVersion = source.BackgroundJobLineageVersion,
                TurnContext = TurnContext.FromMessageSource(session, source.TurnId!.Value, source).ToRecord(),
                UserMessage = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = data }
            }]);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        if (crashAfterInput)
            await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        else
            await manager.Ask<CommandAck>(new SendUserMessage { SessionId = session, Content = data, Source = source },
                FaultCeiling, TestContext.Current.CancellationToken);
        var report = await subscriber.FishForMessageAsync<object>(message => message is TextOutput text && text.Text.Contains(data),
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("committed task lineage is missing or invalid", ((TextOutput)report).Text);
        Assert.Equal(0, _client.Count);
        Assert.Equal(0, _executor.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_job_result_restores_its_original_correction_after_parent_receipt_recovery(bool freshTask)
    {
        var session = new SessionId("signalr/job-evidence-recovery");
        var input = Input(session, "original-task", "operator-a");
        var repeat = Prepare("old-probe");
        var tracker = new TurnStateTracker();
        var completed = ToolCycleSignatureFactory.Complete(repeat,
            new Dictionary<string, ToolCycleResult> { ["old-probe"] = new(ToolInvocationOutcomeCategory.Success, "same") });
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(repeat).Kind);
        var origin = new BackgroundJobOrigin(new TurnId(input.TurnContext.TurnId), new ToolCallId("launch"));
        var launch = ToolCycleSignatureFactory.Prepare([new FunctionCallContent("launch", "shell_execute",
            new Dictionary<string, object?> { ["command"] = "read-value" })], new FakeToolExecutor());
        var events = new List<ISessionEvent>
        {
            input, Adoption(input, [input.InputId]),
            new ToolBatchStarted
            {
                SessionId = session, UserMessage = input.UserMessage, ConsumedInputIds = [input.InputId],
                AssistantMessage = new SerializableChatMessage
                {
                    Role = Netclaw.Actors.Protocol.ChatRole.Assistant,
                    ToolCalls = [new SerializableToolCall { CallId = origin.CallId, Name = new ToolName("shell_execute"), ArgumentsJson = "{\"command\":\"read-value\"}" }]
                },
                LoopAdmission = Admission(launch, input.TurnContext.TurnId),
                LoopDelta = tracker.CaptureDelta(input.TurnContext.TurnId, new ToolLoopCheckpoint())
            },
            new ToolCallRecorded
            {
                SessionId = session,
                ToolResult = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.Tool,
                    ToolCallId = origin.CallId, Name = "shell_execute", Content = "The process started." },
                LoopObservation = ToolCycleSignatureFactory.CreateObservation("launch", new ToolInvocationReceipt.Succeeded([], null), "The process started.", false),
                StartedBackgroundJob = new ActiveJobInfo
                {
                    JobId = new BackgroundJobId("known"), Command = "read-value", Rationale = "Read the value.", StartedAtMs = 0,
                    Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, LineageVersion = 1, Origin = origin
                }
            },
            new TurnRecorded { SessionId = session, UserMessage = input.UserMessage,
                AssistantReply = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.Assistant, Content = "The process started." } }
        };
        if (freshTask)
        {
            var fresh = Input(session, "fresh-task", "operator-b");
            events.Add(fresh);
            events.Add(Adoption(fresh, [fresh.InputId]));
            events.Add(new TurnRecorded { SessionId = session, UserMessage = fresh.UserMessage, ConsumedInputIds = [fresh.InputId],
                AssistantReply = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.Assistant, Content = "The other task is complete." } });
        }
        await SeedAsync(session, events);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = "The process returned its result.", Source = JobSource("known", origin) },
            FaultCeiling, TestContext.Current.CancellationToken);
        var complete = await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(complete);
        Assert.Equal(1, _client.Count);
        Assert.Equal(0, _executor.Count);
        Assert.Contains(_client.Requests.Single(), message => message.Text == "The process returned its result.");
    }

    [Fact]
    public async Task The_last_invalid_buffered_job_result_does_not_request_an_extra_model_response()
    {
        var session = new SessionId("signalr/buffered-orphan-job");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full | OutputFilter.ProcessingState);
        _client.CompleteFirst = true;
        _client.PauseFirst = true;
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = "old", Source = Source("old", "operator-a") }, FaultCeiling, TestContext.Current.CancellationToken);
        await _client.FirstEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        const string data = "The orphan process returned its known result.";
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = data, Source = JobSource("orphan", new BackgroundJobOrigin(new TurnId("old"), new ToolCallId("launch"))) },
            FaultCeiling, TestContext.Current.CancellationToken);
        _client.ReleaseFirst.TrySetResult();
        var completedBeforeRejection = false;
        await subscriber.FishForMessageAsync<object>(message =>
        {
            if (message is TurnCompleted)
                completedBeforeRejection = true;
            return message is TextOutput text && text.Text.Contains(data);
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(completedBeforeRejection);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, _client.Count);
        Assert.Equal(0, _executor.Count);
    }

    [Fact]
    public async Task A_fast_job_result_waits_for_the_parent_receipt_before_it_adopts_the_original_task()
    {
        var session = new SessionId("signalr/fast-job-result");
        var jobManager = CreateTestProbe();
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManager.Ref, overwrite: true);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        _client.LaunchFirst = true;
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = "Launch the process.", Source = Source("original-task", "operator-a") },
            FaultCeiling, TestContext.Current.CancellationToken);
        var launch = await jobManager.ExpectMsgAsync<StartBackgroundJob>(FaultCeiling,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new BackgroundJobOrigin(new TurnId("original-task"), new ToolCallId("launch")), launch.Origin);
        const string result = "The fast process returned its known output.";
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = result, Source = JobSource("fast", launch.Origin) },
            FaultCeiling, TestContext.Current.CancellationToken);
        Assert.Equal(1, _client.Count);
        Assert.Equal(0, _executor.Count);
        jobManager.Reply(new BackgroundJobStarted(new BackgroundJobId("fast"), "output.log"));
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted && _client.Count == 5,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, _client.Count);
        Assert.Equal(2, _executor.Count);
        Assert.All(_executor.Targets, target => Assert.Null(target));
        var resumed = _client.Requests[1];
        Assert.Contains(resumed, message => message.Text == result);
        Assert.Contains(resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()), item => item.CallId == "launch");
    }

    [Fact]
    public async Task A_legacy_approval_prompt_commits_the_job_origin_without_reexecution_of_completed_siblings()
    {
        var session = new SessionId("signalr/legacy-approval-job");
        var input = Input(session, "legacy-task", "operator-a");
        var origin = new BackgroundJobOrigin(new TurnId(input.TurnContext.TurnId), new ToolCallId("launch"));
        const string siblingResult = "The legacy sibling returned this exact result.";
        await SeedAsync(session,
        [
            new ToolBatchStarted
            {
                SessionId = session, UserMessage = input.UserMessage,
                AssistantMessage = new SerializableChatMessage
                {
                    Role = Netclaw.Actors.Protocol.ChatRole.Assistant,
                    ToolCalls =
                    [
                        new SerializableToolCall { CallId = origin.CallId, Name = new ToolName("shell_execute"),
                            ArgumentsJson = "{\"command\":\"read-value\",\"_background\":true,\"_rationale\":\"Read the value.\"}" },
                        new SerializableToolCall { CallId = new ToolCallId("completed-sibling"), Name = new ToolName("search_tools"), ArgumentsJson = "{}" }
                    ]
                }
            },
            new ToolCallRecorded
            {
                SessionId = session, ToolResult = new SerializableChatMessage
                { Role = Netclaw.Actors.Protocol.ChatRole.Tool, ToolCallId = new ToolCallId("completed-sibling"), Name = "search_tools", Content = siblingResult }
            },
            new ToolApprovalRequested
            {
                SessionId = session, CallId = "launch", AuthorizationAttemptId = LegacyAuthorizationAttempt(), ToolName = "shell_execute",
                Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, ChannelType = "signalr",
                RequesterSenderId = new SenderId("operator-a"), RequesterPrincipal = PrincipalClassification.Operator,
                SupportsInteractiveApproval = true, TurnContext = input.TurnContext with { SupportsInteractiveApproval = true },
                OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny]
            }
        ]);
        var jobManager = CreateTestProbe();
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManager.Ref, overwrite: true);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        await manager.Ask<CommandAck>(new ToolInteractionResponse
        { SessionId = session, CallId = origin.CallId, SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce), SenderId = new SenderId("operator-a") },
            FaultCeiling, TestContext.Current.CancellationToken);
        var launch = await jobManager.ExpectMsgAsync<StartBackgroundJob>(FaultCeiling,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(origin, launch.Origin);
        Assert.Equal(new SenderId("operator-a"), launch.SenderId);
        Assert.Equal(0, _client.Count);
        Assert.Equal(0, _executor.Count);
        jobManager.Reply(new BackgroundJobStarted(new BackgroundJobId("legacy"), "output.log"));
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted && _client.Count == 4,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _executor.Count);
        Assert.All(_executor.Targets, target => Assert.Equal("operator-a", target));
        Assert.Contains(_client.Requests[0].SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            result => result.CallId == "completed-sibling" && result.Result?.ToString() == siblingResult);
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = "The legacy job returned.", Source = JobSource("legacy", origin) },
            FaultCeiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted && _client.Count == 5,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, _client.Count);
        Assert.Equal(2, _executor.Count);
    }

    [Fact]
    public async Task A_duplicate_legacy_approval_command_does_not_add_a_feedback_round_or_effect()
    {
        var session = new SessionId("signalr/legacy-duplicate-approval");
        var input = Input(session, "legacy-task", "operator-a");
        await SeedAsync(session,
        [
            new ToolBatchStarted { SessionId = session, UserMessage = input.UserMessage, AssistantMessage = Assistant("legacy-probe") },
            LegacyApproval(input, "legacy-probe")
        ]);
        _executor.HoldFirst = true;
        _client.PauseFirst = true;
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        var approve = new ToolInteractionResponse
        { SessionId = session, CallId = new ToolCallId("legacy-probe"), SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce), SenderId = new SenderId("operator-a") };
        await manager.Ask<CommandAck>(approve, FaultCeiling, TestContext.Current.CancellationToken);
        await _executor.FirstEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        await manager.Ask<CommandNack>(approve, FaultCeiling, TestContext.Current.CancellationToken);
        Assert.Equal(1, _executor.Count);
        Assert.Equal(0, _client.Count);
        _executor.ReleaseFirst.TrySetResult();
        await _client.FirstEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var firstResult = Assert.Single(_client.Requests[0].SelectMany(message => message.Contents.OfType<FunctionResultContent>()), result => result.CallId == "legacy-probe");
        var exactText = Assert.IsType<string>(firstResult.Result);
        Assert.StartsWith("same", exactText);
        _executor.ResultText = exactText;
        _client.ReleaseFirst.TrySetResult();
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var secondResult = Assert.Single(_client.Requests[1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()), result => result.CallId == "resumed-1");
        Assert.Equal(exactText, secondResult.Result?.ToString());
        Assert.Equal(2, _executor.Count);
        Assert.Equal(3, _client.Count);
        Assert.All(_executor.Targets, target => Assert.Equal("operator-a", target));
    }

    [Fact]
    public async Task A_post_baseline_snapshot_without_approval_state_keeps_the_first_receipt_and_original_task_authority()
    {
        var session = new SessionId("signalr/legacy-snapshot-recovery");
        var input = Input(session, "legacy-task", "operator-a");
        var batch = Prepare("legacy-probe");
        const string actualResult = "same\n[approval: once]";
        _executor.ResultText = actualResult;
        var metadata = new ToolBatchStarted
        {
            SessionId = session, MetadataOnly = true, LegacyTaskContext = input.TurnContext,
            LoopAdmission = Admission(batch, input.TurnContext.TurnId),
            LoopDelta = new ToolLoopDelta { TaskId = input.TurnContext.TurnId, Reset = true }
        };
        var result = new ToolCallRecorded
        {
            SessionId = session, ToolResult = new SerializableChatMessage
            { Role = Netclaw.Actors.Protocol.ChatRole.Tool, ToolCallId = new ToolCallId("legacy-probe"), Name = "search_tools", Content = actualResult },
            LoopObservation = ToolCycleSignatureFactory.CreateObservation("legacy-probe", new ToolInvocationReceipt.Succeeded([], null), actualResult, false)
        };
        var state = (SessionState.Empty with { History = SessionState.Empty.History.Add(input.UserMessage).Add(Assistant("legacy-probe")) })
            .ApplyLoopAdmission(metadata).ApplyLoopObservation(result);
        state = state with { History = state.History.Add(result.ToolResult) };
        Assert.Equal(1, Assert.Single(state.LoopCheckpoint.Entries).EqualRounds);
        var seed = Sys.ActorOf(Props.Create(() => new EventSeeder($"session-{session.Value}")));
        foreach (var evt in new ISessionEvent[]
        {
            new ToolBatchStarted { SessionId = session, UserMessage = input.UserMessage, AssistantMessage = Assistant("legacy-probe") },
            LegacyApproval(input, "legacy-probe"),
            new ToolApprovalResolved { SessionId = session, CallId = "legacy-probe", AuthorizationAttemptId = LegacyAuthorizationAttempt(), Decision = ConsentAnswerCodec.ToJournalText(ConsentAnswer.Once.Instance) },
            metadata, result
        })
            await seed.Ask<Done>(evt, FaultCeiling, TestContext.Current.CancellationToken);
        await seed.Ask<Done>(new SeedSnapshot(state.ToSnapshot()), FaultCeiling, TestContext.Current.CancellationToken);
        var watcher = CreateTestProbe();
        watcher.Watch(seed);
        Sys.Stop(seed);
        await watcher.ExpectTerminatedAsync(seed, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, _executor.Count);
        Assert.Equal(3, _client.Count);
        Assert.All(_executor.Targets, target => Assert.Equal("operator-a", target));
        Assert.Contains(_client.Requests[0].SelectMany(message => message.Contents.OfType<FunctionResultContent>()), result => result.CallId == "legacy-probe");
    }

    private static ToolApprovalRequested LegacyApproval(InputAdmitted input, string callId) => new()
    {
        SessionId = input.SessionId, CallId = callId, AuthorizationAttemptId = LegacyAuthorizationAttempt(), ToolName = "search_tools",
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, ChannelType = "signalr",
        RequesterSenderId = new SenderId("operator-a"), RequesterPrincipal = PrincipalClassification.Operator,
        SupportsInteractiveApproval = true, TurnContext = input.TurnContext,
        OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny]
    };

    private static string LegacyAuthorizationAttempt()
    {
        const string value = "auth-11111111111111111111111111111111";
        Assert.True(AuthorizationAttemptId.TryParse(value, out var attempt));
        Assert.Equal(value, attempt.Value);
        return attempt.Value;
    }

    private sealed record SeedSnapshot(SessionSnapshot Snapshot) : INoSerializationVerificationNeeded;

    private static MessageSource JobSource(string id, BackgroundJobOrigin origin)
    {
        var key = $"bg-job:{id}";
        return new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId("background-job-system"), MessageId = key,
            TurnId = new TurnId(key), BackgroundJobId = new BackgroundJobId(key), BackgroundJobLineageVersion = 1,
            BackgroundJobOrigin = origin, Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            { SourceKind = new SourceKind(BackgroundJobManagerActor.SourceKind) }
        };
    }

    private async Task SeedAsync(SessionId session, IReadOnlyList<ISessionEvent> events)
    {
        var seed = Sys.ActorOf(Props.Create(() => new EventSeeder($"session-{session.Value}")));
        foreach (var evt in events)
            await seed.Ask<Done>(evt, FaultCeiling, TestContext.Current.CancellationToken);
        Watch(seed);
        Sys.Stop(seed);
        await ExpectTerminatedAsync(seed, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static PreparedToolCycleBatch Prepare(string id) => ToolCycleSignatureFactory.Prepare([Call(id)], new FakeToolExecutor());
    private static FunctionCallContent Call(string id) => new(id, "search_tools", new Dictionary<string, object?> { ["_rationale"] = "Read the same value." });
    private static SerializableChatMessage Assistant(string id) => new()
    {
        Role = Netclaw.Actors.Protocol.ChatRole.Assistant,
        ToolCalls = [new SerializableToolCall { CallId = new ToolCallId(id), Name = new ToolName("search_tools"), ArgumentsJson = "{}" }]
    };
    private static ToolLoopAdmission Admission(PreparedToolCycleBatch batch, string task) => new()
    {
        TaskId = task, ActionHash = batch.Action.Value,
        Calls = batch.Calls.Select(call => new ToolLoopPreparedCall(call.CallId.Value, call.ToolName.Value, call.ArgumentsHash, call.AllowsPendingJob)).ToArray()
    };
    private static InputAdmitted Input(SessionId session, string id, string requester) => new()
    {
        SessionId = session, InputId = new InputId(id),
        UserMessage = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = id },
        TurnContext = TurnContext.FromMessageSource(session, new TurnId(id), Source(id, requester)).ToRecord()
    };
    private static MessageSource Source(string id, string requester) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester), MessageId = id, TurnId = new TurnId(id),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", requester, requester)
    };
    private static ToolTaskAdopted Adoption(InputAdmitted input, IReadOnlyList<InputId> ids) => new()
    { SessionId = input.SessionId, TurnContext = input.TurnContext, InputIds = ids };
    private static SendUserMessage Restart(SessionId session) => new()
    {
        SessionId = session, Content = RestartInstruction,
        Source = Source("restart-resume-adversarial:1", "reminder-system") with
        { ReminderId = new ReminderId("restart-resume-adversarial:1"), Principal = PrincipalClassification.VerifiedAutomation }
    };

    private sealed class EventSeeder : ReceivePersistentActor
    {
        public override string PersistenceId { get; }
        public EventSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });
            IActorRef? snapshotReply = null;
            Command<SeedSnapshot>(request =>
            {
                snapshotReply = Sender;
                // The memory store retains objects. Verify the registered serializer before the save.
                var serialization = Context.System.Serialization;
                var serializer = Assert.IsType<NetclawProtobufSerializer>(serialization.FindSerializerFor(request.Snapshot));
                var bytes = serializer.ToBinary(request.Snapshot);
                var manifest = serializer.Manifest(request.Snapshot);
                Assert.NotEmpty(manifest);
                SaveSnapshot((SessionSnapshot)serialization.Deserialize(bytes, serializer.Identifier, manifest));
            });
            Command<SaveSnapshotSuccess>(_ => snapshotReply!.Tell(Done.Instance));
            Command<SaveSnapshotFailure>(failure => snapshotReply!.Tell(new Status.Failure(failure.Cause)));
            Command<ISessionEvent>(evt => { var reply = Sender; Persist(evt, _ => reply.Tell(Done.Instance)); });
        }
    }

    private sealed class RecoveryExecutor : IToolExecutor
    {
        public Task<ShellProcessLaunch> PrepareShellLaunchAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(new ShellProcessLaunch(
                ToolArgumentHelper.GetString(call.Arguments, "Command")!,
                context.ResolveShellCwd(ToolArgumentHelper.GetString(call.Arguments, "WorkingDirectory"))
                    ?? throw new InvalidOperationException("The test launch requires a working directory."),
                context.Invocation, new ShellCommandPolicy(TestShellEnvironment.Current),
                new ToolPathPolicy(TestShellEnvironment.Current, []), static _ => Task.CompletedTask));
        public Task AuthorizeAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
            => Task.CompletedTask;
        public bool HoldFirst { get; set; }
        public string ResultText { get; set; } = "same";
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get; private set; }
        public List<string?> Targets { get; } = [];
        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            Count++;
            Targets.Add(context.RunScope.DefaultDeliveryTarget?.DestinationId);
            if (Count == 1 && HoldFirst)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(ct);
            }
            context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
            return ResultText;
        }
    }

    private sealed class RecoveryClient : IChatClient
    {
        private readonly HashSet<string> _completedTasks = [];
        public int Count { get; private set; }
        public bool CompleteEachTask { get; set; }
        public bool CompleteFirst { get; set; }
        public bool LaunchFirst { get; set; }
        public bool PauseFirst { get; set; }
        public bool PauseSecond { get; set; }
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ChatMessage[]> Requests { get; } = [];
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = ++Count;
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            if (request == 1 && PauseFirst)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            if (request == 2 && PauseSecond)
            {
                SecondEntered.TrySetResult();
                await TestStreamingHelpers.ParkUntilCancelledAsync(cancellationToken);
            }
            if (CompleteFirst && request == 1)
                return new ChatResponse(new ChatMessage(ModelRole.Assistant, "The old task is complete."));
            if (LaunchFirst && request == 1)
                return new ChatResponse(new ChatMessage(ModelRole.Assistant,
                    [new FunctionCallContent("launch", "shell_execute", new Dictionary<string, object?>
                    { ["command"] = "read-value", ["_background"] = true, ["_rationale"] = "Read the value." })]));
            if (CompleteEachTask)
            {
                var task = snapshot.Last(message => message.Role == ModelRole.User).Text;
                if (!_completedTasks.Add(task))
                    return new ChatResponse(new ChatMessage(ModelRole.Assistant, "The task is complete."));
            }
            return new ChatResponse(new ChatMessage(ModelRole.Assistant, [Call($"resumed-{request}")]));
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
