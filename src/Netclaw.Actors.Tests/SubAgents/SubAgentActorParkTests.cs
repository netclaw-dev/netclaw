// -----------------------------------------------------------------------
// <copyright file="SubAgentActorParkTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;
using WireChatRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// PR 1 of durable-child-consent-wait (issue #2572), tasks 3.2 to 3.6 (U1 and U2 of the cut plan). A child call that
/// needs consent does not wait in memory. The child lets the other calls of its batch finish, sends its park to its
/// owner, and waits for <see cref="ChildTerminalCommitted"/>. A new <see cref="SubAgentActor"/> resumes from the park.
/// <para>
/// The owner is a test probe that acknowledges each checkpoint. The child gets a bridge with no consent contract: a
/// PR 1 child only needs to know that consent is available. Today the child cannot ask that bridge, so it fails with
/// tool_execution_failed where the tests expect the park.
/// </para>
/// </summary>
public sealed class SubAgentActorParkTests(ITestOutputHelper output) : SubAgentActorTestBase(output)
{
    private const string ConsentToolName = "approval_probe";
    private const string Overlay = "[Skill Overlay]\nUse the review checklist.";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly SubAgentRunId RunId = new("run");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The SubAgentActor needs no persistence and no hosting.
    }

    /// <summary>
    /// U1, design D1 and D2. The park holds the transcript up to the finished sibling result, the directories that the
    /// batch ran with, the loop guard, and the skill overlay. The child then waits for ChildTerminalCommitted, stops
    /// on it, and sends no ActorStopped result.
    /// Planted bug: the child asks in memory (today), keeps the parked call result, or stops before the commit.
    /// </summary>
    [Fact]
    public async Task The_park_holds_the_child_state_and_the_child_waits_for_the_commit()
    {
        // Arrange
        var start = TestPath("projects", "park-state");
        var looper = new ProgressTool();
        var consent = new CountingTool(ConsentToolName);
        var client = new ScriptedToolCallChatClient(
            [[Loop("first", "a")], [Call("call-park", ConsentToolName), Loop("sibling", "s")]], "unused");
        var owner = CreateTestProbe("park-state-owner");
        var watcher = CreateTestProbe("park-state-watcher");

        // Act
        var child = Launch(owner, Definition(looper, consent) with { SkillOverlay = Overlay }, client, Scope(start), null);
        watcher.Watch(child);
        var (_, park, requests) = await ExpectParkAsync(owner, []);
        var identity = await child.Ask<ActorIdentity>(new Identify("parked"), Deadline, Cancellation);
        owner.Send(child, new ChildTerminalCommitted());
        await watcher.ExpectTerminatedAsync(child, Deadline, cancellationToken: Cancellation);

        // Assert
        Assert.NotNull(identity.Subject);
        Assert.False(owner.HasMessages, "A parked child must not send a result when it stops.");
        Assert.Equal(0, consent.CallCount);
        Assert.Equal("call-park", Assert.Single(requests).CallId.Value);
        Assert.Equal(new ChildParkScopeDirectories(start, start), park.ScopeDirectories);
        Assert.Equal(1, park.LoopGuard?.ToolIterationCount);
        Assert.Equal(Overlay, park.SkillOverlay);
        Assert.Equal(WireChatRole.User, park.Transcript[0].Role);
        var last = park.Transcript.Select((message, index) => (message, index))
            .Last(item => item.message.Role == WireChatRole.Assistant);
        Assert.Equal(["call-park", "sibling"], last.message.ToolCalls.Select(static call => call.CallId.Value));
        var after = Assert.Single(park.Transcript.Skip(last.index + 1));
        Assert.Equal("sibling", after.ToolCallId?.Value);
        Assert.Equal("result:s", after.Content);
    }

    /// <summary>
    /// U2, design D4. A new incarnation resumes from the park. It runs only the parked call: an approved call once,
    /// and a refused call never, with the refusal text of the shared re-drive entry. The sibling does not run again.
    /// The first checkpoint is round N+1 (H1). The loop guard survives, so a repeat that completed twice before the
    /// park is refused (M10). The system message keeps the skill overlay.
    /// Planted bug: the resume starts fresh, runs the whole batch again, resets the round or the loop guard, or drops
    /// the overlay.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resume_runs_only_the_parked_call_and_keeps_the_round_the_guard_and_the_overlay(bool approve)
    {
        // Arrange
        var looper = new ProgressTool();
        var consent = new CountingTool(ConsentToolName);
        var first = new ScriptedToolCallChatClient(
        [
            [Loop("repeat-1", "r")], [Loop("repeat-2", "r")], [Call("call-park", ConsentToolName), Loop("sibling", "s")]
        ], "unused");
        var owner = CreateTestProbe("resume-owner");
        Launch(owner, Definition(looper, consent) with { SkillOverlay = Overlay }, first, Scope(), null);
        var (_, park, requests) = await ExpectParkAsync(owner, []);
        var request = Assert.Single(requests);
        var answer = approve
            ? new ApprovalRedriveEntry(request.AuthorizationAttemptId, OneTimeApprovalKeys.Create(request.Approval), null, null)
            : new ApprovalRedriveEntry(request.AuthorizationAttemptId, null, RefusalKind.Denied, null);

        // Act
        var resumed = new ScriptedToolCallChatClient([[Loop("repeat-3", "r")]], "Done.");
        var resumedOwner = CreateTestProbe("resume-resumed-owner");
        var checkpoints = new List<ChildRunCheckpoint>();
        Launch(resumedOwner, Definition(looper, consent) with { SkillOverlay = park.SkillOverlay }, resumed, Scope(),
            new ChildRunResume(park, new Dictionary<string, ApprovalRedriveEntry> { ["call-park"] = answer }));
        var end = await NextOwnerMessageAsync(resumedOwner, checkpoints);

        // Assert
        Assert.True(end is ChildTerminalReturned { Result.Success: true }, $"The resumed child must end: {Describe(end)}.");
        Assert.Equal(["r", "r", "s"], looper.Queries);
        Assert.Equal(park.LoopGuard!.ToolIterationCount + 1, checkpoints.First().CompletedRound);
        Assert.Contains("same action completed twice", ResultOf(resumed.ReceivedMessagesByCall[^1], "repeat-3"),
            StringComparison.Ordinal);
        Assert.Contains(Overlay, resumed.ReceivedMessagesByCall[0][0].Text, StringComparison.Ordinal);
        Assert.Equal(approve ? 1 : 0, consent.CallCount);
        var expected = answer.TryGetRefusalText(ConsentToolName, out var refusal) ? refusal : "ok";
        Assert.Equal(expected, ResultOf(resumed.ReceivedMessagesByCall[0], "call-park"));
    }

    [Fact]
    public async Task A_park_counts_actual_failures_from_finished_sibling_calls()
    {
        var looper = new ProgressTool();
        var consent = new CountingTool(ConsentToolName);
        var first = new FunctionCallContent("bad-before", "looper", new Dictionary<string, object?> { ["query"] = "a" });
        var sibling = new FunctionCallContent("bad-sibling", "looper", new Dictionary<string, object?> { ["query"] = "b" });
        var client = new ScriptedToolCallChatClient([[first], [Call("call-park", ConsentToolName), sibling]], "unused");
        var owner = CreateTestProbe("progress-park-owner");
        var child = Launch(owner, Definition(looper, consent), client, Scope(), null);

        var (_, park, _) = await ExpectParkAsync(owner, []);
        owner.Send(child, new ChildTerminalCommitted());

        Assert.Equal(2, park.LoopGuard.CallsSinceProgress);
        Assert.Empty(looper.Queries);
        Assert.Equal(0, consent.CallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_resumed_approval_result_updates_progress_before_the_next_model_request(bool approve, bool siblingSucceeded)
    {
        var consent = new CountingTool(ConsentToolName);
        var first = new ScriptedToolCallChatClient([[Call("call-park", ConsentToolName)]], "unused");
        var owner = CreateTestProbe("progress-resume-owner");
        var child = Launch(owner, Definition(consent), first, Scope(), null);
        var (_, park, requests) = await ExpectParkAsync(owner, []);
        owner.Send(child, new ChildTerminalCommitted());
        park = park with { LoopGuard = park.LoopGuard with { CallsSinceProgress = siblingSucceeded ? 0 : 6,
            // The stage drives the nudge, so a park must carry a score and stage that agree with the count:
            // six strong failures are twelve half-units, which is the escalation rung.
            ProgressFailureUnits = siblingSucceeded ? 0 : 12,
            ProgressStage = siblingSucceeded ? RefusedRepeatAction.None : RefusedRepeatAction.EscalatedNudge,
            IncompleteBatchHadSuccess = siblingSucceeded } };
        var request = Assert.Single(requests);
        var answer = approve
            ? new ApprovalRedriveEntry(request.AuthorizationAttemptId, OneTimeApprovalKeys.Create(request.Approval), null, null)
            : new ApprovalRedriveEntry(request.AuthorizationAttemptId, null, RefusalKind.Denied, null);
        var resumed = new ScriptedToolCallChatClient([], "Done.");
        var resumedOwner = CreateTestProbe("progress-resumed-owner");
        Launch(resumedOwner, Definition(consent), resumed, Scope(),
            new ChildRunResume(park, new Dictionary<string, ApprovalRedriveEntry> { ["call-park"] = answer }));
        var end = await NextOwnerMessageAsync(resumedOwner, []);

        Assert.IsType<ChildTerminalReturned>(end);
        var nudges = resumed.ReceivedMessagesByCall[0].Where(message => ToolCycleMessages.IsLoopNudge(message.Text)).ToArray();
        if (approve || siblingSucceeded)
            Assert.Empty(nudges);
        else
            Assert.Contains("7 tool calls failed", Assert.Single(nudges).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_cause_across_tools_receives_a_cause_nudge_with_child_wording()
    {
        // The child shares the same DispatchingToolExecutor front door as the parent, so a call without
        // _rationale fails with invalid_rationale. Different arguments keep action identity blind; only the
        // cause detector sees the repetition.
        var client = new ScriptedToolCallChatClient([
            [CauseCall("cause-1", "looper", "a")],
            [CauseCall("cause-2", "looper", "b")],
            [CauseCall("cause-3", "looper", "c")]
        ], "Done.");
        var owner = CreateTestProbe("cause-child-owner");
        Launch(owner, Definition(new CountingTool("looper")), client, Scope(), null);

        var end = await NextOwnerMessageAsync(owner, []);
        Assert.IsType<ChildTerminalReturned>(end);

        var nudge = client.ReceivedMessagesByCall.SelectMany(static messages => messages)
            .First(message => ToolCycleMessages.IsCauseNudge(message.Text));
        Assert.Contains("invalid_rationale", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("_rationale", nudge.Text, StringComparison.Ordinal);
    }

    private IActorRef Launch(TestProbe owner, SubAgentDefinition definition, IChatClient client, ChildRunScope scope,
        ChildRunResume? resume)
    {
        var execution = new ChildRunExecution(RunId, owner.Ref);
        var child = Sys.ActorOf(SubAgentActor.CreateProps(definition, client, ConsentPolicy()));
        child.Tell(new RunSubAgent
        {
            Scope = scope, Task = "Run the batch.", Timeout = TimeSpan.FromSeconds(30),
            OwnedRun = execution, Cancellation = execution.Token, Resume = resume
        }, owner.Ref);
        return child;
    }

    private static SubAgentDefinition Definition(params INetclawTool[] tools) => CreateDefinition(tools);

    private ChildRunScope Scope(string? projectDirectory = null) => SubAgentTestScope.Create(
        sessionDirectory: TestPath("sessions", "park"), projectDirectory: projectDirectory,
        inheritedCwd: projectDirectory, approvalBridge: new MarkerApprovalBridge());

    // The next owner message that is not a checkpoint. The probe acknowledges each checkpoint, as the session does.
    private static async Task<object> NextOwnerMessageAsync(TestProbe owner, List<ChildRunCheckpoint> checkpoints)
    {
        while (true)
        {
            var message = await owner.ExpectMsgAsync<object>(Deadline, cancellationToken: Cancellation);
            if (message is not ChildCheckpointReached reached)
                return message;
            checkpoints.Add(reached.Checkpoint);
            owner.Reply(SessionProtocol.CommandAck.For(new SessionId("test-session")));
        }
    }

    private static async Task<ChildParkReached> ExpectParkAsync(TestProbe owner, List<ChildRunCheckpoint> checkpoints)
    {
        var message = await NextOwnerMessageAsync(owner, checkpoints);
        Assert.True(message is ChildParkReached, $"The child must send its park, but it sent {Describe(message)}.");
        return (ChildParkReached)message;
    }

    private static string Describe(object message) => message is ChildTerminalReturned terminal
        ? $"ChildTerminalReturned ({terminal.Result.OutcomeReason?.Value}: {terminal.Result.Output})"
        : message.GetType().Name;

    private static string ResultOf(IReadOnlyList<ChatMessage> request, string callId)
        => request.SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Single(result => result.CallId == callId).Result?.ToString() ?? string.Empty;

    private static FunctionCallContent Call(string callId, string name)
        => new(callId, name, new Dictionary<string, object?> { ["_rationale"] = "Test the child park." });

    private static FunctionCallContent Loop(string callId, string query) => new(callId, "looper",
        new Dictionary<string, object?> { ["query"] = query, ["_rationale"] = "Test the child park." });

    // A call with no _rationale, so the shared front door rejects it with invalid_rationale.
    private static FunctionCallContent CauseCall(string callId, string name, string query)
        => new(callId, name, new Dictionary<string, object?> { ["query"] = query });

    private static ToolAccessPolicy ConsentPolicy()
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ConsentToolName] = ToolApprovalMode.Approval
            }
        };
        var environment = TestShellEnvironment.Current;
        return new ToolAccessPolicy(new NetclawPaths(), toolConfig,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(environment), new ToolPathPolicy(environment, []));
    }

    // A bridge with no consent contract. A PR 1 child parks for any available bridge.
    private sealed class MarkerApprovalBridge : IParentApprovalBridge;

    private sealed class CountingTool(string name) : INetclawTool
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);
        public string Name => name;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(name);
        public string Description => "Count the calls.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => "ok", name, Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult("ok");
        }
    }
}
