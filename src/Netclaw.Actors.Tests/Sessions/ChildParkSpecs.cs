// -----------------------------------------------------------------------
// <copyright file="ChildParkSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// PR 1 of durable-child-consent-wait (issue #2572), S1 to S4 of the cut plan. Each row runs one child run of Alice to
/// its end and then checks the shared run invariants (<see cref="ChildParkTestBase.AssertRunInvariantsAsync"/>).
/// Each row that parks first checks the park precondition. Today a child waits in memory, so each row fails there
/// or at the invariant that the park changes.
/// Source: background-subagent-runs, tool-authorization TA-12, design D1 to D4 and D7.
/// </summary>
public sealed class ChildParkSpecs(ITestOutputHelper output) : ChildParkTestBase(output)
{
    private const string BobTurn = "Bob keeps a turn open.";
    private ToolAudienceProfile? _personal;

    protected override void ConfigurePersonalProfile(ToolAudienceProfile profile) => _personal = profile;

    protected override void RegisterParentTools(ToolRegistry registry)
    {
        base.RegisterParentTools(registry);
        registry.RegisterCore(new ProjectTool());
    }

    /// <summary>
    /// S1, park and resume. Rows: a call and a sibling; two calls; a new call after the resume; a deny rule after the
    /// park; a parent that moved its project directory; a sibling that sets the child directory; a sibling that changes
    /// a file. Bob's turn is open while Alice answers. Bob answers first and gets the wrong-requester nack. A second
    /// answer after the end gets "already answered".
    /// Planted bug: the resume runs the batch again, resumes on the first of two answers, keeps the consent for a new
    /// call, skips the gate, takes the authority or the directory of the current turn, or loses the directory or the
    /// file activity that a finished sibling of the parked batch made (design D2, batch effects).
    /// </summary>
    [Theory]
    [InlineData("sibling")]
    [InlineData("two")]
    [InlineData("newcall")]
    [InlineData("deny")]
    [InlineData("moved")]
    [InlineData("cwd")]
    [InlineData("files")]
    public async Task A_parked_run_resumes_once_under_the_run_authority(string row)
    {
        // Arrange
        var id = new SessionId($"park/resume-{row}");
        var journal = await JournalAsync();
        ScriptParentWithProjects();
        var recorded = Directory.CreateDirectory(Path.Combine(TestPaths.WorkspacesDirectory, "recorded")).FullName;
        var moved = Directory.CreateDirectory(Path.Combine(TestPaths.WorkspacesDirectory, "moved")).FullName;
        var changed = Path.Combine(recorded, "changed.txt");
        IReadOnlyList<AIContent>[] plan = row switch
        {
            "sibling" => [[Probe("p1"), SiblingCall("s1")]],
            "two" => [[Probe("p1"), Probe("p2")]],
            "newcall" => [[Probe("p1")], [Probe("p2")]],
            "cwd" => [[Probe("p1"), ProjectCall("c1", moved)], [Probe("p2")]],
            "files" => [[Probe("p1"), ChangeFile("f1", changed)]],
            _ => [[Probe("p1")]]
        };
        var (parked, prompts, probeRuns) = row switch
        {
            "two" => (2, 2, 2),
            "newcall" or "cwd" => (1, 2, 2),
            "deny" => (1, 1, 0),
            _ => (1, 1, 1)
        };
        PlanChild($"park-{row}", plan);
        await JoinRecorderAsync(id);
        if (row == "moved")
            await DeclareProjectAsync(journal, id, recorded);
        var run = await StartAsync(id, $"park-{row}");
        var first = await AwaitPromptsAsync(run.Run.RunId, parked);
        await AssertParkedAsync(journal, id, run, parked);
        if (row == "deny")
            _personal!.ApprovalPolicy!.ToolOverrides["authority_probe"] = ToolApprovalMode.Deny;
        if (row == "moved")
            await DeclareProjectAsync(journal, id, moved);
        var bobTurn = await HoldTurnAsync(journal, id, BobTurn, OperatorWithTargets(Bob, BobTarget));

        // Act
        Assert.Equal(ApprovalNackReasons.WrongRequester,
            Assert.IsType<CommandNack>(await AnswerAsync(id, first[0].CallId, Bob)).Reason);
        for (var answered = 0; answered < prompts; answered++)
        {
            var prompt = (await AwaitPromptsAsync(run.Run.RunId, answered + 1))[answered];
            AssertAck(await AnswerAsync(id, prompt.CallId, Alice));
            if (answered + 1 < parked)
            {
                await BarrierAsync(id, run);
                Assert.Empty(ApprovalTool.Contexts);
            }
        }

        bobTurn.TrySetResult();

        // Assert
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns);
        Assert.True(terminal.Result.Success, terminal.Result.Output);
        var resumed = Clients.A.ReceivedMessages.First(request => HasResult(request, "p1"));
        Assert.Single(Results(resumed), result => result.CallId == "p1");
        if (row == "sibling")
            Assert.Single(Results(resumed), result => result.CallId == "s1");
        if (row == "deny")
            Assert.NotEqual("confirmed", Results(resumed).Single(result => result.CallId == "p1").Result?.ToString());
        if (row == "moved")
            Assert.Equal(recorded, Assert.Single(ApprovalTool.Contexts).ProjectDirectory);
        if (row == "cwd")
            Assert.Equal(moved, ApprovalTool.Contexts.Last().ProjectDirectory);
        if (row == "files")
            Assert.Contains(changed, terminal.Result.WorkingContext?.ConfirmedChangedFiles ?? []);
        Assert.Equal(ApprovalNackReasons.AlreadyAnswered,
            Assert.IsType<CommandNack>(await AnswerAsync(id, first[0].CallId, Alice)).Reason);
    }

    /// <summary>
    /// S2, refusals. Rows: the journal rejects both requests of a park during a parent turn; the journal rejects one
    /// write of a two-request park; the agent profile is deleted before the answer; the model call of the resumed child
    /// fails; the journal rejects the answer. Each run ends failed once, and no parent turn fails.
    /// Planted bug: a rejected child request fails the parent turn (today), one park writes its requests in more than
    /// one atomic write, a missing profile resumes with a stale profile, a failed resume records two ends, or a
    /// rejected answer leaves the run open.
    /// </summary>
    [Theory]
    [InlineData("rejected")]
    [InlineData("atomic")]
    [InlineData("profile")]
    [InlineData("model")]
    [InlineData("answer")]
    public async Task A_refused_park_or_resume_ends_the_run_failed_once(string row)
    {
        // Arrange
        var id = new SessionId($"park/refused-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        var twoRequests = row is "rejected" or "atomic";
        PlanChild($"park-{row}", twoRequests ? [[Probe("p1"), Probe("p2")]] : [[Probe("p1")]]);
        var profiles = Host.Services.GetRequiredService<SubAgentDefinitionRegistry>();
        profiles.ReplaceFileProfiles([new SubAgentProfile
        {
            Name = "child-c", Description = "child-c", SystemPrompt = "child-c", ModelRole = ModelRole.Fallback,
            ToolNames = ["authority_probe"], Visibility = SubAgentVisibility.UserFacing
        }]);
        await JoinRecorderAsync(id);
        TaskCompletionSource? aliceTurn = null;
        if (twoRequests)
            journal.RejectWritesOf(static evt => evt is ToolApprovalRequested { SourceChildRunId: not null },
                writes: row == "rejected" ? 2 : 1);

        // Act
        var run = await StartAsync(id, $"park-{row}", row == "profile" ? "child-c" : "child-a",
            row == "rejected" ? async () => aliceTurn = await HoldTurnAsync(journal, id, "Alice keeps a turn open.", AliceSource) : null);
        Task<ISessionResponse>? rejectedAnswer = null;
        if (twoRequests)
        {
            await journal.FirstEventRejected.Task.WaitAsync(Deadline, Cancellation);
            await BarrierAsync(id, run);
            aliceTurn?.TrySetResult();
            // One park writes all its requests in one atomic write, so a rejected write leaves no request of the park.
            Assert.Empty(RequestsOf(journal, run.Run.RunId));
        }
        else
        {
            var prompt = (await AwaitPromptsAsync(run.Run.RunId, 1))[0];
            await AssertParkedAsync(journal, id, run, 1);
            if (row == "profile")
                profiles.ReplaceFileProfiles([]);
            else if (row == "model")
                Clients.A.PlannedExceptions.Enqueue(new InvalidOperationException("The model is down."));
            else
                journal.RejectWritesOf(static evt => evt is ToolApprovalResolved);
            if (row == "answer")
                rejectedAnswer = AnswerAsync(id, prompt.CallId, Alice);
            else
                AssertAck(await AnswerAsync(id, prompt.CallId, Alice));
        }

        // Assert
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: row == "model" ? 1 : 0);
        Assert.Equal(SubAgentRunOutcome.Failed, terminal.Result.Outcome);
        if (rejectedAnswer is not null)
            Assert.IsAssignableFrom<ISessionResponse>(await rejectedAnswer);
    }

    /// <summary>
    /// S3, cancel (design D7). Rows: a cancel while the run is parked; a cancel while the batch runs. The run ends
    /// cancelled with one terminal record, and the parked call never runs. A cancel during the batch leaves no request.
    /// Planted bug: the cancel of a parked run waits for an actor that does not exist, or a late park shows a prompt.
    /// </summary>
    [Theory]
    [InlineData("parked")]
    [InlineData("batch")]
    public async Task A_cancelled_run_ends_cancelled_once(string row)
    {
        // Arrange
        var id = new SessionId($"park/cancel-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild($"park-{row}", [[Probe("p1"), SiblingCall("s1")]]);
        await JoinRecorderAsync(id);
        var siblingStarted = row == "batch" ? Sibling.HoldNext() : Task.CompletedTask;
        var run = await StartAsync(id, $"park-{row}");
        if (row == "parked")
        {
            await AwaitPromptsAsync(run.Run.RunId, 1);
            await AssertParkedAsync(journal, id, run, 1);
        }
        else
            await siblingStarted.WaitAsync(Deadline, Cancellation);

        // Act
        var session = await ResolveSessionAsync(id);
        await session.Ask<string>(new ChildRunControl(run.Run.RunId, true, run.Run.OriginalContext), Deadline, Cancellation);
        Sibling.Release();

        // Assert
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: 0);
        Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Result.Completion);
        if (row == "batch")
            Assert.Empty(RequestsOf(journal, run.Run.RunId));
    }

    /// <summary>
    /// S4, restart (PR 1 rules; PR 2 changes both rows on purpose). Rows: a restart while parked, after an idle
    /// timeout that must not passivate the session; an answer during a restart drain, which is recorded and does not
    /// resume, and then the restart. Both runs end lost, and the parked call never runs.
    /// Planted bug: an in-memory park lets the session passivate, or an answer during the drain runs the call.
    /// </summary>
    [Theory]
    [InlineData("parked")]
    [InlineData("drain")]
    public async Task A_restart_loses_an_in_memory_park(string row)
    {
        // Arrange
        var id = new SessionId($"park/restart-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild($"park-{row}", [[Probe("p1")]]);
        await JoinRecorderAsync(id);
        var run = await StartAsync(id, $"park-{row}");
        var prompt = (await AwaitPromptsAsync(run.Run.RunId, 1))[0];
        await AssertParkedAsync(journal, id, run, 1);
        var session = await ResolveSessionAsync(id);

        // Act
        if (row == "parked")
        {
            session.Tell(ReceiveTimeout.Instance);
            await BarrierAsync(id, run);
            Assert.NotEqual("Passivating", await PhaseOfAsync(id));
            await RestartSessionAsync(id);
        }
        else
        {
            var childCalls = Clients.A.CallCount;
            var turn = await HoldTurnAsync(journal, id, "Alice keeps the drain waiting.", AliceSource);
            var drain = AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(id, "config-reload"));
            AssertAck(await AnswerAsync(id, prompt.CallId, Alice));
            await BarrierAsync(id, run);
            Assert.Empty(ApprovalTool.Contexts);
            Assert.Equal(childCalls, Clients.A.CallCount);
            turn.TrySetResult();
            // The turn ended, so the drain needs no model grace timer. The reply wait has the spec deadline. The next
            // join must reach a new session, so the spec waits until the drained session stops.
            var stopped = CreateTestProbe();
            stopped.Watch(session);
            await drain;
            await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        }

        await JoinRecorderAsync(id);

        // Assert
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: 0);
        Assert.True(terminal.Lost);
        Assert.IsType<CommandNack>(await AnswerAsync(id, prompt.CallId, Alice));
    }

    // Holds the next parent model request that carries the text, so a turn stays open. The caller releases it.
    private async Task<TaskCompletionSource> HoldTurnAsync(AcceptanceFault journal, SessionId id, string text, MessageSource source)
    {
        await SettleNoticesAsync(journal, id);
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains(text), hold);
        await SendAsync(id, text, source);
        await entered.WaitAsync(Deadline, Cancellation);
        return hold;
    }

    // The parent declares a project directory with a "cd:{path}" message, and the session settles.
    private async Task DeclareProjectAsync(AcceptanceFault journal, SessionId id, string path)
    {
        await SendAsync(id, $"cd:{path}", AliceSource);
        await AwaitAssertAsync(() => Assert.Contains(Clients.Parent.ReceivedMessages, request => Results(request)
            .Any(result => result.Result?.ToString() == $"project {path}")), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
    }

    private void ScriptParentWithProjects() => Clients.Parent.ResponseContentsSelector = static request =>
        FreshUserTexts(request).FirstOrDefault(static text => text.StartsWith("cd:", StringComparison.Ordinal)) is { } cd
            ? HasResult(request, cd) ? null : [ProjectCall(cd, cd[3..])]
            : ScriptedParentReply(request);

    private static FunctionCallContent ProjectCall(string callId, string path) => new(callId, ProjectTool.ToolName,
        new Dictionary<string, object?> { ["Path"] = path, ["_rationale"] = "Declare the project." });

    // A set_working_directory double for the parent and the child. It declares the directory of its argument with no
    // path policy.
    private sealed class ProjectTool : INetclawTool
    {
        public const string ToolName = SetWorkingDirectoryTool.ToolName;
        public string Name => ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Declare the project directory.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create((string Path) => Path, Name, Description);
        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context,
            CancellationToken ct = default)
        {
            var path = arguments?["Path"]?.ToString() ?? throw new InvalidOperationException("The path is required.");
            return Task.FromResult(context.SuccessProject($"project {path}", path));
        }
    }
}
