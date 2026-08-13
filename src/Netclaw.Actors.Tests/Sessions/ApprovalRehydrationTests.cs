// -----------------------------------------------------------------------
// <copyright file="ApprovalRehydrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration tests for persisted tool-approval interactions: a tool batch
/// parks on a human-approval gate, the session passivates / cold-respawns, and
/// the approval click that arrives afterward must re-drive the parked batch
/// rather than being silently dropped.
/// </summary>
public sealed class ApprovalRehydrationTests : LlmSessionTestBase
{
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly ApprovalGateToolExecutor _toolExecutor = new();

    public ApprovalRehydrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            // Disable idle-timeout passivation — tests drive passivation
            // explicitly by stopping the child actor.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_toolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        registry.Register(
            AIFunctionFactory.Create((string path) => $"read {path}", "read_file"),
            "read_file");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Passivated_session_resumes_tool_batch_when_approval_arrives()
    {
        const string callId = "call-shell-1";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/resume-after-passivation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The tool batch is announced (ToolCallOutput) then parks on the
        // approval gate (ToolInteractionRequest).
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(callId, request.CallId.Value);
        Assert.Equal("shell_execute", request.ToolName.Value);

        // First tool attempt threw the approval-required exception — the tool
        // has not actually executed yet.
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);

        // Force a cold respawn: stop the session actor. The journaled approval
        // request carries the pending interaction.
        await ColdRespawnAsync(sessionId);

        // Re-join to cold-respawn the session — it recovers the pending
        // interaction from journaled events and lands in Ready.
        var subscriberB = CreateTestProbe("resume-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // The approval click arrives after recovery.
        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.ApproveOnce);

        // The parked batch re-drives: the tool executes successfully (the
        // ApprovedOnce pre-seed bypassed the gate without a duplicate prompt)
        // and the follow-up LLM call produces a final text response.
        await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);

        // No duplicate approval prompt was emitted for the re-driven call.
        await subscriberB.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Idle_passivation_proceeds_with_pending_approval_and_response_resumes()
    {
        const string callId = "call-shell-passivate";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/passivate-with-pending-approval");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("passivate-pending-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Cold-respawn so the session recovers the journaled pending approval
        // and lands in Ready — the state the old rule kept pinned in memory.
        await ColdRespawnAsync(sessionId);
        var rejoinProbe = CreateTestProbe("passivate-pending-rejoin");
        await sessionManager.Ask<SessionJoined>(new JoinSession(rejoinProbe)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await rejoinProbe.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Idle timeout with the recovered approval still pending: the session
        // passivates (approval state is journaled) instead of deferring forever.
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);
        child.Tell(new LeaveSession(rejoinProbe) { SessionId = sessionId });
        child.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(
            child, TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

        // The approval click rehydrates the session and re-drives the parked batch.
        var subscriberB = CreateTestProbe("passivate-pending-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.ApproveOnce);

        await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Pending_approval_rejects_option_that_was_not_offered()
    {
        const string callId = "call-shell-invalid-option";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/invalid-approval-option");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("invalid-option-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(callId, request.CallId.Value);
        Assert.Equal([ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny], request.Options.Select(o => o.Key.Value).ToArray());

        var invalidReply = await sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveAlways),
            SenderId = new SenderId("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var nack = Assert.IsType<CommandNack>(invalidReply);
        Assert.Equal(ApprovalNackReasons.OptionUnavailable, nack.Reason);

        var warning = await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("not available", warning.Text, StringComparison.OrdinalIgnoreCase);

        var validReply = await sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.IsType<CommandAck>(validReply);
        await ExpectApprovalOutcomeAsync(subscriber, callId, ApprovalOptionKeys.ApproveOnce);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Passivated_session_resumes_tool_batch_when_cold_text_approval_arrives()
    {
        const string callId = "call-shell-text-cold-1";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/cold-text-after-passivation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("cold-text-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(callId, request.CallId.Value);
        Assert.Equal([ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny], request.Options.Select(o => o.Key.Value).ToArray());

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("cold-text-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var reply = await sessionManager.Ask<ISessionResponse>(new ToolInteractionTextResponse
        {
            SessionId = sessionId,
            Text = "A",
            SenderId = new SenderId("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.IsType<CommandAck>(reply);
        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.ApproveOnce);
        await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Recovered_batch_does_not_reexecute_completed_sibling_tool_call()
    {
        const string readCallId = "call-read-1";
        const string shellCallId = "call-shell-2";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(readCallId, "read_file",
                new Dictionary<string, object?> { ["path"] = "README.md" }),
            new FunctionCallContent(shellCallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/no-duplicate-sibling");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("no-duplicate-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Read and then run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        ToolInteractionRequest? request = null;
        ToolResultOutput? readResult = null;
        await AwaitAssertAsync(async () =>
        {
            while (request is null || readResult is null)
            {
                var msg = await subscriber.FishForMessageAsync<object>(m =>
                    m is ToolInteractionRequest or ToolResultOutput,
                    TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
                if (msg is ToolInteractionRequest r)
                    request = r;
                if (msg is ToolResultOutput tr && tr.CallId.Value == readCallId)
                    readResult = tr;
            }
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(shellCallId, request!.CallId.Value);
        Assert.Equal(1, _toolExecutor.ExecutionsFor("read_file"));
        Assert.Equal(0, _toolExecutor.ExecutionsFor("shell_execute"));

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("no-duplicate-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(shellCallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        });

        await ExpectApprovalOutcomeAsync(subscriberB, shellCallId, ApprovalOptionKeys.ApproveOnce);

        var shellResult = await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(shellCallId, shellResult.CallId.Value);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _toolExecutor.ExecutionsFor("read_file"));
        Assert.Equal(1, _toolExecutor.ExecutionsFor("shell_execute"));
    }

    [Fact]
    public async Task Recovered_batch_waits_for_all_pending_sibling_approvals_before_redrive()
    {
        const string readCallId = "call-read-pending";
        const string shellCallId = "call-shell-pending";
        _toolExecutor.GatedTools.Add("read_file");
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(readCallId, "read_file",
                new Dictionary<string, object?> { ["path"] = "README.md" }),
            new FunctionCallContent(shellCallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/multi-pending-approval");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("multi-pending-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Read the file and run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var approvalCallIds = new HashSet<string>(StringComparer.Ordinal);
        await AwaitAssertAsync(async () =>
        {
            while (approvalCallIds.Count < 2)
            {
                var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
                    TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
                approvalCallIds.Add(request.CallId.Value);
            }
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(readCallId, approvalCallIds);
        Assert.Contains(shellCallId, approvalCallIds);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("multi-pending-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(readCallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        });

        await ExpectApprovalOutcomeAsync(subscriberB, readCallId, ApprovalOptionKeys.ApproveOnce);

        // One sibling approval is still pending, so the recovered session must
        // not advance the LLM with a half-closed assistant tool-call batch.
        await subscriberB.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(shellCallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        });

        await ExpectApprovalOutcomeAsync(subscriberB, shellCallId, ApprovalOptionKeys.ApproveOnce);

        var resultCallIds = new HashSet<string>(StringComparer.Ordinal);
        await AwaitAssertAsync(async () =>
        {
            while (resultCallIds.Count < 2)
            {
                var result = await subscriberB.ExpectMsgAsync<ToolResultOutput>(
                    TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
                resultCallIds.Add(result.CallId.Value);
            }
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Contains(readCallId, resultCallIds);
        Assert.Contains(shellCallId, resultCallIds);
        Assert.Equal(1, _toolExecutor.ExecutionsFor("read_file"));
        Assert.Equal(1, _toolExecutor.ExecutionsFor("shell_execute"));
    }

    [Fact]
    public async Task Recovered_session_abandons_batch_when_crash_happens_after_approval_resolved_before_tool_result()
    {
        const string callId = "call-shell-crash-after-resolve";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/crash-after-approval-resolved");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("crash-resolve-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("crash-resolve-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        _toolExecutor.BlockNextSuccessfulExecution("shell_execute");
        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.ApproveOnce);

        await _toolExecutor.BlockedExecutionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);
        _toolExecutor.FailBlockedExecution();

        var subscriberC = CreateTestProbe("crash-resolve-sub-c");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberC)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberC.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await subscriberC.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Never mind, just say hello"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriberC.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberC.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);

        var lastCall = _fakeChatClient.ReceivedMessages[^1];
        var healed = lastCall.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.Tool
            && m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == callId));
        Assert.True(healed, "resolved-but-interrupted tool_use should be closed with a synthetic tool result");
    }

    [Fact]
    public async Task Recovered_resolved_batch_heals_history_when_next_message_arrives_without_join()
    {
        const string callId = "call-shell-crash-no-join";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/crash-after-resolve-no-join");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("crash-no-join-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("crash-no-join-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        _toolExecutor.BlockNextSuccessfulExecution("shell_execute");
        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.ApproveOnce);

        await _toolExecutor.BlockedExecutionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Never mind, just say hello"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            Assert.True(_fakeChatClient.ReceivedMessages.Count >= 2);
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);

        var lastCall = _fakeChatClient.ReceivedMessages[^1];
        var healed = lastCall.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.Tool
            && m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == callId));
        Assert.True(healed, "resolved-but-interrupted tool_use should be closed before direct user ingress");
    }

    [Fact]
    public async Task Join_during_live_approved_tool_execution_does_not_abandon_batch()
    {
        const string callId = "call-shell-live-join";
        _toolExecutor.GatedTools.Add("shell_execute");
        _toolExecutor.BlockNextSuccessfulExecution("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/live-approved-join");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("live-join-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriber, callId, ApprovalOptionKeys.ApproveOnce);

        await _toolExecutor.BlockedExecutionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var subscriberB = CreateTestProbe("live-join-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        _toolExecutor.ReleaseBlockedExecution();

        await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var lastCall = _fakeChatClient.ReceivedMessages[^1];
        var toolResult = lastCall
            .Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == callId)
            .Result?.ToString();
        Assert.NotNull(toolResult);
        Assert.Contains("[executed shell_execute]", toolResult);
        Assert.DoesNotContain("session restarted", toolResult!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Idle_session_with_pending_interaction_redrives_when_approval_arrives()
    {
        const string callId = "call-shell-idle";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "ls" })
        ];

        var sessionId = new SessionId("test-channel/idle-redrive");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("idle-redrive-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "List the directory",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(callId, request.CallId.Value);

        // Without stopping the actor, send the approval response. The session
        // is in Ready (the parked tool-loop task is the only thing in flight,
        // there is no live Processing turn for an idle deferred-passivation
        // session) — the Ready handler re-drives the batch from history.
        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        });

        await ExpectApprovalOutcomeAsync(subscriber, callId, ApprovalOptionKeys.ApproveOnce);

        await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Unknown_call_id_fails_loud_with_expired_prompt_message()
    {
        // A session with history but no pending interaction. Send a response
        // for a call id that was never parked.
        var sessionId = new SessionId("test-channel/expired-prompt");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("expired-prompt-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Complete a normal text turn so the session is in Ready with history.
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId("call-never-existed"),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        var notice = await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("expired", notice.Text, StringComparison.OrdinalIgnoreCase);

        // No LLM call or tool dispatch was triggered.
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Non_requester_response_is_rejected_after_recovery()
    {
        const string callId = "call-shell-auth";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/auth-parity");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("auth-parity-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Drive the turn with an explicit requester so the pending record
        // carries a concrete requester sender id.
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("U-requester")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // A different user clicks approve — must be rejected.
        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("U-imposter")
        }, ActorRefs.Nobody);

        var rejection = await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("only the requesting user", rejection.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Cold_recovered_redrive_runs_at_original_turn_audience()
    {
        const string callId = "call-shell-aud";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/redrive-audience");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("redrive-aud-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Drive the turn at Team audience — the pending interaction persists it.
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("U-requester")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Cold respawn: the transient _currentTurnSource is lost; only the
        // journaled approval request carries the turn's trust context.
        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("redrive-aud-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("U-requester")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.ApproveOnce);

        await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // The re-drive ran at the original turn's Team audience — not the
        // fail-closed Public a null cold-recovery source would have forced.
        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);
        Assert.Equal(TrustAudience.Team, _toolExecutor.LastExecutionAudience);
    }

    [Fact]
    public async Task Cold_recovered_continuation_tool_calls_run_at_original_turn_audience()
    {
        // Regression for the 0.21 cold-recovery audience bug (session
        // D0AC6CKBK5K/1779897736.065949): the parked batch and any continuation
        // tool calls must execute from the same persisted turn context. A null
        // recovered source used to fall through to TrustAudience.Public,
        // silently blocking tools the audience profile doesn't list for Public.
        const string parkedCallId = "call-shell-parked";
        const string continuationCallId = "call-read-continuation";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(parkedCallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        // After the redriven shell_execute returns its result, the LLM emits a
        // continuation tool call (read_file). read_file is NOT gated, so it
        // runs immediately — and its execution context is what we use to pin
        // whether the rehydrated _currentTurnSource carried the audience.
        _fakeChatClient.PlannedResponses.Enqueue(new AIContent[]
        {
            new FunctionCallContent(continuationCallId, "read_file",
                new Dictionary<string, object?> { ["path"] = "/tmp/example.txt" })
        });

        var sessionId = new SessionId("test-channel/redrive-continuation-audience");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("redrive-cont-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status then read a file",
            Source = RequesterSource("U-requester")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("redrive-cont-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(parkedCallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("U-requester")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, parkedCallId, ApprovalOptionKeys.ApproveOnce);

        // Drain through the redriven shell_execute result, the LLM continuation
        // call that produces the read_file batch, and the read_file result.
        // TurnCompleted comes last when the model returns a plain text reply.
        var completed = await subscriberB.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // Both tool calls executed: the redriven shell_execute AND the
        // continuation read_file.
        Assert.Equal(2, _toolExecutor.SuccessfulExecutions);
        Assert.Equal(1, _toolExecutor.ExecutionsFor("shell_execute"));
        Assert.Equal(1, _toolExecutor.ExecutionsFor("read_file"));

        // The CONTINUATION call's execution context carries the original turn's
        // Team audience. Without the synthesized _currentTurnSource fix this
        // would be Public — the broken path that blocks shell_execute and other
        // tools the audience profile doesn't list for Public.
        Assert.Equal(TrustAudience.Team, _toolExecutor.LastExecutionAudience);
        Assert.Equal(TrustBoundary.Team, _toolExecutor.LastExecutionBoundary);
        Assert.Equal(ChannelType.Slack.ToWireValue(), _toolExecutor.LastExecutionChannelType);
        Assert.True(_toolExecutor.LastSupportsInteractiveApproval);
    }

    [Fact]
    public async Task Cold_recovered_continuation_approval_preserves_third_party_adopted_context()
    {
        const string parkedCallId = "call-shell-adopted-parked";
        const string continuationCallId = "call-shell-adopted-continuation";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(parkedCallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];
        _fakeChatClient.PlannedResponses.Enqueue(new AIContent[]
        {
            new FunctionCallContent(continuationCallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git diff" })
        });

        var sessionId = new SessionId("test-channel/redrive-adopted-context");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("redrive-adopted-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status then git diff",
            Source = RequesterSourceWithThirdPartyAdoptedContext("U-requester", "U-observer")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var liveRequest = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(liveRequest.HasThirdPartyAdoptedContext);
        Assert.Equal(["U-observer"], liveRequest.AdoptedSpeakerIds);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("redrive-adopted-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(parkedCallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("U-requester")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, parkedCallId, ApprovalOptionKeys.ApproveOnce);

        await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var recoveredContinuationRequest = await subscriberB.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(continuationCallId, recoveredContinuationRequest.CallId.Value);
        Assert.True(recoveredContinuationRequest.HasThirdPartyAdoptedContext);
        Assert.Equal(["U-observer"], recoveredContinuationRequest.AdoptedSpeakerIds);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(continuationCallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.Deny),
            SenderId = new SenderId("U-requester")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, continuationCallId, ApprovalOptionKeys.Deny);

        await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cold_recovered_denied_approval_returns_denial_result_without_reprompting()
    {
        const string callId = "call-shell-denied";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/denied-redrive");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("denied-redrive-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("denied-redrive-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.Deny),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.Deny);

        var toolResult = await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Contains("approval_denied_by_user", toolResult.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);
        await subscriberB.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cold_recovered_denied_scratch_retry_preserves_session_directory_hint()
    {
        const string callId = "call-shell-scratch-denied";
        const string scratchDirectory = "/home/user/.netclaw/sessions/example";
        _toolExecutor.GatedTools.Add("shell_execute");
        _toolExecutor.SessionScratchRetryTools["shell_execute"] = scratchDirectory;

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/scratch-denied-redrive");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("scratch-denied-redrive-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("scratch-denied-redrive-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.Deny),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.Deny);

        var toolResult = await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("approval_denied_by_user", toolResult.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scratchDirectory, toolResult.Result, StringComparison.Ordinal);
        Assert.DoesNotContain("set_working_directory", toolResult.Result, StringComparison.Ordinal);
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Cold_recovered_redrive_restores_boundary_and_channel_support_flags()
    {
        const string callId = "call-shell-trust-context";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/redrive-trust-context");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("redrive-trust-context-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("U-requester")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("redrive-trust-context-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("U-requester")
        }, ActorRefs.Nobody);

        await ExpectApprovalOutcomeAsync(subscriberB, callId, ApprovalOptionKeys.ApproveOnce);

        await subscriberB.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrustAudience.Team, _toolExecutor.LastExecutionAudience);
        Assert.Equal(TrustBoundary.Team, _toolExecutor.LastExecutionBoundary);
        Assert.Equal(ChannelType.Slack.ToWireValue(), _toolExecutor.LastExecutionChannelType);
        Assert.True(_toolExecutor.LastSupportsInteractiveApproval);
    }

    [Fact]
    public async Task Recovered_session_with_parked_approval_heals_history_when_user_sends_a_message()
    {
        const string callId = "call-shell-abandon";
        _toolExecutor.GatedTools.Add("shell_execute");

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(callId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionId = new SessionId("test-channel/abandon-parked-batch");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("abandon-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run git status",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Cold respawn — the journal carries the parked interaction and the
        // assistant tool_use with no matching tool_result.
        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("abandon-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // The user abandons the approval by sending a new message instead of
        // clicking. The parked tool_use must be closed with a synthetic result
        // so the new turn's LLM call ships well-formed history.
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Never mind — just say hello"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriberB.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // The gated tool never ran — it was abandoned, not re-driven.
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);

        // The post-recovery LLM call's history carries a synthetic tool result
        // for the parked call — no orphaned assistant tool_use.
        var lastCall = _fakeChatClient.ReceivedMessages[^1];
        var healed = lastCall.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.Tool
            && m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == callId));
        Assert.True(healed, "parked tool_use should be closed with a synthetic tool result");

        // A late click on the abandoned prompt is now treated as expired.
        sessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);
        var notice = await subscriberB.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("expired", notice.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tool_interaction_response_during_passivation_aborts_shutdown()
    {
        var sessionId = new SessionId("test-channel/passivation-approval");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("passivation-approval-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);

        // Drop the subscriber and force the idle timeout so the session enters
        // Passivating (the ReceiveTimeout handler defers while subscribers exist).
        child.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        child.Tell(ReceiveTimeout.Instance);

        // An approval response arriving during Passivating must abort the
        // shutdown rather than dead-letter into a stopping actor.
        child.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId("call-never-parked"),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId("local-user")
        }, ActorRefs.Nobody);

        // The actor does not stop — passivation was aborted. (The expired-prompt
        // notice has no subscriber to land on here; Passivating only happens
        // with zero subscribers. The point under test is that the shutdown is
        // aborted, not completed.)
        await ExpectNoMsgAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Cold-respawns a session: resolves its actor, stops it, and waits for
    /// termination. The next JoinSession/SendUserMessage re-creates it through
    /// the session manager, recovering from the journal and snapshot.
    /// </summary>
    private async Task ColdRespawnAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task ExpectApprovalOutcomeAsync(
        Akka.TestKit.TestProbe subscriber,
        string callId,
        string selectedKey)
    {
        var outcome = await subscriber.ExpectMsgAsync<ApprovalOutcomeOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(callId, outcome.CallId.Value);
        Assert.Equal(selectedKey, outcome.SelectedKey.Value);
    }

    private MessageSource RequesterSource(string senderId) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId(senderId),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(
            TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = FixedReceivedAt,
    };

    private MessageSource RequesterSourceWithThirdPartyAdoptedContext(string senderId, params string[] adoptedSpeakerIds)
        => RequesterSource(senderId) with
        {
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = adoptedSpeakerIds
        };
}

/// <summary>
/// Fake <see cref="IToolExecutor"/> that mimics the production approval gate:
/// a tool in <see cref="GatedTools"/> throws <see cref="ToolApprovalRequiredException"/>
/// on its first attempt unless the execution context carries a one-time
/// approval grant for that tool (<see cref="Netclaw.Tools.ToolExecutionContext.OneTimeApprovedToolName"/>).
/// This is exactly the bypass <c>DispatchingToolExecutor.IsOneTimeApprovalSatisfied</c>
/// applies, so a re-driven <c>ApprovedOnce</c> batch that pre-seeds the context
/// passes the gate here without a second prompt.
/// </summary>
internal sealed class ApprovalGateToolExecutor : IToolExecutor
{
    private int _successfulExecutions;
    private string? _blockedToolName;
    private TaskCompletionSource<object?>? _blockedExecutionRelease;

    public int SuccessfulExecutions => _successfulExecutions;

    public TaskCompletionSource<object?> BlockedExecutionStarted { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Tool names that require interactive approval before execution.</summary>
    public HashSet<string> GatedTools { get; } = [];

    public Dictionary<string, string> SessionScratchRetryTools { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Audience on the execution context of the most recent successful
    /// execution. A cold-recovered re-drive must run at the original turn's
    /// audience — not the fail-closed <see cref="TrustAudience.Public"/> a null
    /// source would force — so a persisted-scope grant still matches the gate.
    /// </summary>
    public TrustAudience? LastExecutionAudience { get; private set; }

    public TrustBoundary? LastExecutionBoundary { get; private set; }

    public string? LastExecutionChannelType { get; private set; }

    public bool? LastSupportsInteractiveApproval { get; private set; }

    public int ExecutionsFor(string toolName) => _executionsByTool.GetValueOrDefault(toolName);

    private readonly Dictionary<string, int> _executionsByTool = new(StringComparer.Ordinal);

    public Task AuthorizeAsync(
        FunctionCallContent toolCall,
        Netclaw.Tools.ToolExecutionContext? context = null,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task<string> ExecuteAsync(
        FunctionCallContent toolCall,
        Netclaw.Tools.ToolExecutionContext? context = null,
        CancellationToken ct = default)
    {
        if (GatedTools.Contains(toolCall.Name))
        {
            var hasOneTimeGrant = context is not null
                && string.Equals(context.OneTimeApprovedToolName, toolCall.Name, StringComparison.Ordinal);

            if (!hasOneTimeGrant)
            {
                var approvalContext = new ToolApprovalContext(
                    toolCall.Name,
                    $"Tool {toolCall.Name} requires approval",
                    Patterns: [toolCall.Name],
                    CandidateVerbs: [toolCall.Name],
                    Options:
                    [
                        new ToolApprovalOption(
                            new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce), "Approve once"),
                        new ToolApprovalOption(
                            new ApprovalOptionKey(ApprovalOptionKeys.Deny), "Deny")
                    ],
                    Cwd: null,
                    IsMessy: false,
                    Candidates: [new Netclaw.Security.ApprovalCandidate(toolCall.Name, Directory: null)]);
                if (SessionScratchRetryTools.TryGetValue(toolCall.Name, out var scratchDirectory))
                {
                    approvalContext = approvalContext with
                    {
                        IsSessionScratchRetry = true,
                        SessionScratchDirectory = scratchDirectory
                    };
                }

                throw new ToolApprovalRequiredException(approvalContext);
            }
        }

        if (string.Equals(_blockedToolName, toolCall.Name, StringComparison.Ordinal))
        {
            _blockedToolName = null;
            var release = _blockedExecutionRelease!;
            BlockedExecutionStarted.TrySetResult(null);
            await release.Task.WaitAsync(ct);
        }

        LastExecutionAudience = context?.Audience;
        LastExecutionBoundary = context?.Boundary;
        LastExecutionChannelType = context?.ChannelType;
        LastSupportsInteractiveApproval = context is not null
            && context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Available;
        _executionsByTool[toolCall.Name] = _executionsByTool.GetValueOrDefault(toolCall.Name) + 1;
        Interlocked.Increment(ref _successfulExecutions);
        return $"[executed {toolCall.Name}]";
    }

    public void BlockNextSuccessfulExecution(string toolName)
    {
        _blockedToolName = toolName;
        _blockedExecutionRelease = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockedExecutionStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void FailBlockedExecution()
    {
        _blockedExecutionRelease?.TrySetException(new InvalidOperationException("Simulated actor crash during tool execution."));
        _blockedExecutionRelease = null;
    }

    public void ReleaseBlockedExecution()
    {
        _blockedExecutionRelease?.TrySetResult(null);
        _blockedExecutionRelease = null;
    }
}
