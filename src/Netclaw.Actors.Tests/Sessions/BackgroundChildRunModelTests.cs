// -----------------------------------------------------------------------
// <copyright file="BackgroundChildRunModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using CsCheck;
using Microsoft.Accordant;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using AccordantSpec = Microsoft.Accordant.Spec<Netclaw.Actors.Tests.Sessions.ChildRunContractState>;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

[State]
public partial class ChildRunContractState
{
    public bool Accepted { get; set; }
    public bool CancellationPending { get; set; }
    public bool TerminalSeen { get; set; }
    public bool ResultReady { get; set; }
    public int PairCommitCount { get; set; }
    public bool ContinuationClosed { get; set; }
    public string OrdinaryTurns { get; set; } = "";
}

public sealed class BackgroundChildRunModelTests(ITestOutputHelper output)
{
    private const string InitialSampleSeed = "CoordChildModel1";
    private static readonly SessionId Owner = new("test/child-run-model");

    private enum Action
    {
        Accept, Cancel, RecordTerminal, Prepare, Consume, Close, StaleClose, ParentTurn, RestoreSnapshot
    }

    private enum Outcome
    {
        AcceptAfterParentTurn, CancelBeforeTerminal, RepeatCancellation, ParentProgressDuringChild,
        ParentProgressAfterPreparation, RestoreBeforePreparation, RestoreAfterPreparation,
        RestoreAfterConsumption, RepeatDelivery, RejectStaleClosure
    }

    // The oracle describes public obligations, independent of the production lifecycle types.
    private sealed record Contract(bool Accepted, bool CancellationPending, bool TerminalSeen, bool ResultReady,
        int PairCommitCount, bool ContinuationClosed, string OrdinaryTurns);
    private sealed record Command(Action Action, int UserValue);
    private sealed record Scenario(Command[] Commands, int RunId, int ScopeId, int AcceptanceCallId,
        int DeliveryCallId, int UserId);

    [Fact]
    public void Generated_child_actions_preserve_acceptance_result_and_single_pair_commit()
    {
        // Arrange
        var command = Gen.Select(Gen.OneOfConst(Enum.GetValues<Action>()), Gen.Int[0, 4],
            (action, value) => new Command(action, value));
        var scenarios = Gen.Select(command.Array[1, 48], Gen.Int[0, 999].Array[5],
            (commands, ids) => new Scenario(commands, ids[0], ids[1], ids[2], ids[3], ids[4]));
        var spec = CreateSpec();
        var explored = new HashSet<Outcome>();
        var cases = 0;
        var operations = 0;

        // Act
        scenarios.Sample(scenario =>
        {
            var run = CreateRun(scenario);
            var pair = CreatePair(scenario.DeliveryCallId);
            var terminal = CreateTerminal(run);
            var acceptance = CreateAcceptance(run);
            var initialCall = new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new SerializableToolCall
                {
                    CallId = Assert.IsType<ChildRunStartKey.Tool>(run.StartKey).CallId,
                    Name = new ToolName("spawn_agent"), ArgumentsJson = "{}"
                }]
            };
            var state = SessionState.Empty with { History = [initialCall] };
            var profile = new StateProfile(new ChildRunContractState());

            foreach (var step in scenario.Commands)
            {
                var current = Model((ChildRunContractState)profile.SingleState());
                var expected = Advance(current, step);
                if (expected is null)
                {
                    Assert.Throws<InvalidOperationException>(() => ApplyActual(state, run, terminal, pair, acceptance, step));
                    Assert.Equal(current, Observe(state, run, pair, acceptance));
                    if (step.Action == Action.StaleClose && current.ResultReady)
                        explored.Add(Outcome.RejectStaleClosure);
                    continue;
                }

                state = ApplyActual(state, run, terminal, pair, acceptance, step);
                var actual = Observe(state, run, pair, acceptance);
                var operation = spec.GetOperation<Command, Contract>("Apply");
                var (valid, message, updated) = spec.Allows(operation, step, actual, profile);
                Assert.True(valid, $"Accordant rejected {step}: {message}");
                profile = updated;
                Assert.Equal(expected, actual);
                RecordOutcome(explored, current, step.Action);
                operations++;
            }

            cases++;
        }, iter: 1_000, seed: InitialSampleSeed, threads: 1,
            print: scenario => $"run={scenario.RunId}; scope={scenario.ScopeId}; acceptance={scenario.AcceptanceCallId}; "
                + $"delivery={scenario.DeliveryCallId}; user={scenario.UserId}; "
                + string.Join(" -> ", scenario.Commands.Select(item => $"{item.Action}({item.UserValue})")));

        // Assert
        output.WriteLine($"seed={InitialSampleSeed}; cases={cases}; valid-operations={operations}; outcomes={string.Join(',', explored)}");
        Assert.True(cases >= 1_000, $"CsCheck ran only {cases} cases.");
        Assert.True(operations > cases, $"Only {operations} valid operations ran in {cases} cases.");
        Assert.Equal(Enum.GetValues<Outcome>().Order(), explored.Order());
    }

    private static void RecordOutcome(HashSet<Outcome> explored, Contract before, Action action)
    {
        switch (action)
        {
            case Action.Accept when before.OrdinaryTurns.Length > 0:
                explored.Add(Outcome.AcceptAfterParentTurn);
                break;
            case Action.Cancel:
                explored.Add(before.CancellationPending ? Outcome.RepeatCancellation : Outcome.CancelBeforeTerminal);
                break;
            case Action.ParentTurn when before.Accepted && !before.TerminalSeen:
                explored.Add(Outcome.ParentProgressDuringChild);
                break;
            case Action.ParentTurn when before.ResultReady:
                explored.Add(Outcome.ParentProgressAfterPreparation);
                break;
            case Action.RestoreSnapshot when before.Accepted:
                explored.Add(before.PairCommitCount == 1 ? Outcome.RestoreAfterConsumption
                    : before.ResultReady ? Outcome.RestoreAfterPreparation : Outcome.RestoreBeforePreparation);
                break;
            case Action.Consume or Action.Close when before.PairCommitCount == 1:
                explored.Add(Outcome.RepeatDelivery);
                break;
        }
    }

    private static AccordantSpec CreateSpec()
    {
        var spec = new AccordantSpec();
        spec.Operation<Command, Contract>("Apply", (command, state) =>
        {
            var expected = Advance(Model(state), command)
                ?? throw new InvalidOperationException("The scenario applied an illegal child action to the contract.");
            return Expect.That<Contract>(actual => actual == expected
                    ? ValidationResult.Valid()
                    : ValidationResult.Invalid($"Expected {expected}, observed {actual}."))
                .ThenState<ChildRunContractState>(next => SetModel(next, expected));
        });
        return spec;
    }

    private static Contract Model(ChildRunContractState state)
        => new(state.Accepted, state.CancellationPending, state.TerminalSeen, state.ResultReady,
            state.PairCommitCount, state.ContinuationClosed, state.OrdinaryTurns);

    private static void SetModel(ChildRunContractState state, Contract value)
    {
        state.Accepted = value.Accepted;
        state.CancellationPending = value.CancellationPending;
        state.TerminalSeen = value.TerminalSeen;
        state.ResultReady = value.ResultReady;
        state.PairCommitCount = value.PairCommitCount;
        state.ContinuationClosed = value.ContinuationClosed;
        state.OrdinaryTurns = value.OrdinaryTurns;
    }

    private static Contract? Advance(Contract contract, Command command) => command.Action switch
    {
        Action.Accept when !contract.Accepted => contract with { Accepted = true },
        Action.Cancel when contract.Accepted && !contract.TerminalSeen => contract with { CancellationPending = true },
        Action.RecordTerminal when contract.Accepted && !contract.TerminalSeen
            => contract with { TerminalSeen = true, CancellationPending = false },
        Action.Prepare when contract.TerminalSeen && !contract.ResultReady => contract with { ResultReady = true },
        Action.Consume when contract.ResultReady => contract with { PairCommitCount = 1 },
        Action.Close when contract.ResultReady => contract with { PairCommitCount = 1, ContinuationClosed = true },
        Action.ParentTurn => contract with { OrdinaryTurns = contract.OrdinaryTurns + $"Ordinary-{command.UserValue};" },
        Action.RestoreSnapshot => contract,
        _ => null
    };

    private static SessionState ApplyActual(
        SessionState state,
        BackgroundChildRun run,
        ChildRunTerminal terminal,
        (SerializableChatMessage Call, SerializableChatMessage Result, ToolCallId CallId) pair,
        ChildRunAccepted acceptance,
        Command command) => command.Action switch
    {
        Action.Accept => state.Apply(acceptance),
        Action.Cancel => state.Apply(new ChildRunEvent.CancellationRequested
        {
            SessionId = Owner, RunId = run.RunId, RecordedAtMs = 2
        }),
        Action.RecordTerminal => state.Apply(new ChildRunEvent.TerminalRecorded(terminal)
        {
            SessionId = Owner, RunId = run.RunId, RecordedAtMs = 3
        }),
        Action.Prepare => state.Apply(new ChildRunEvent.ResultPrepared(pair.Call, pair.Result, terminal.Result.WorkingContext)
        {
            SessionId = Owner, RunId = run.RunId, RecordedAtMs = 4
        }),
        Action.Consume => new SessionToolWorkflow().ApplyBatchStarted(state, new ToolBatchStarted
        {
            SessionId = Owner,
            ChildContinuation = new ChildContinuation(run.RunId, pair.CallId),
            AssistantMessage = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Parent next step." },
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Child delivery must not add this user." },
            StartedAtMs = 5
        }),
        Action.Close => state.Apply(new TurnRecorded
        {
            SessionId = Owner,
            ChildContinuation = new ChildContinuation(run.RunId, pair.CallId),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Child closure must not add this user." },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Parent final." },
            RecordedAtMs = 6
        }),
        Action.StaleClose => state.Apply(new InputClosed
        {
            SessionId = Owner,
            ChildContinuation = new ChildContinuation(run.RunId, new ToolCallId($"stale-{command.UserValue}")),
            ClosedAtMs = 7
        }),
        Action.ParentTurn => state.Apply(new TurnRecorded
        {
            SessionId = Owner,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = $"Ordinary-{command.UserValue}" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = $"Reply-{command.UserValue}" },
            RecordedAtMs = 8
        }),
        Action.RestoreSnapshot => SessionState.FromSnapshot(state.ToSnapshot()),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private static Contract Observe(
        SessionState state,
        BackgroundChildRun run,
        (SerializableChatMessage Call, SerializableChatMessage Result, ToolCallId CallId) pair,
        ChildRunAccepted acceptance)
    {
        var accepted = state.ChildRuns.TryGetValue(run.RunId, out var recorded);
        var lifecycle = recorded?.Lifecycle;
        var callCount = state.History.Sum(message => message.ToolCalls.Count(call => call.CallId == pair.CallId));
        var results = state.History.Where(message => message.ToolCallId == pair.CallId).ToArray();
        Assert.Equal(callCount, results.Length);
        Assert.InRange(callCount, 0, 1);
        if (callCount == 1)
        {
            Assert.Equal(pair.Call, Assert.Single(state.History, message => message.ToolCalls.Any(call => call.CallId == pair.CallId)));
            Assert.Equal(pair.Result, Assert.Single(results));
        }
        var receipts = state.History.Where(message => message.ToolCallId == acceptance.AcceptanceResult!.ToolCallId).ToArray();
        Assert.Equal(accepted ? 1 : 0, receipts.Length);
        if (accepted)
        {
            Assert.Single(state.ChildRuns);
            Assert.Equal(run.RunId, recorded!.RunId);
            Assert.Equal(run.ScopeId, recorded.ScopeId);
            Assert.Equal(run.StartKey, recorded.StartKey);
            Assert.Equal(run.OriginalContext, recorded.OriginalContext);
            Assert.Equal(run.ArgumentsDigest, recorded.ArgumentsDigest);
            Assert.Equal(acceptance.AcceptanceResult, Assert.Single(receipts));
        }
        else
        {
            Assert.Empty(state.ChildRuns);
        }
        foreach (var (message, index) in state.History.Select((message, index) => (message, index)))
        {
            if (message.Role != ChatRole.User)
                continue;
            Assert.StartsWith("Ordinary-", message.Content);
            Assert.Equal(ChatRole.Assistant, state.History[index + 1].Role);
            Assert.Equal(message.Content.Replace("Ordinary-", "Reply-", StringComparison.Ordinal), state.History[index + 1].Content);
        }
        return new Contract(
            Accepted: accepted,
            CancellationPending: lifecycle is ChildRunLifecycle.Cancelling,
            TerminalSeen: lifecycle is ChildRunLifecycle.Terminal or ChildRunLifecycle.Prepared
                or ChildRunLifecycle.Consumed or ChildRunLifecycle.Closed,
            ResultReady: lifecycle is ChildRunLifecycle.Prepared or ChildRunLifecycle.Consumed or ChildRunLifecycle.Closed,
            PairCommitCount: callCount,
            ContinuationClosed: lifecycle is ChildRunLifecycle.Closed,
            OrdinaryTurns: string.Concat(state.History.Where(message => message.Role == ChatRole.User)
                .Select(message => message.Content + ";")));
    }

    private static BackgroundChildRun CreateRun(Scenario scenario)
    {
        var context = new TurnContextRecord
        {
            SessionId = Owner,
            TurnId = $"turn-{scenario.UserId}",
            RequesterSenderId = new SenderId($"user-{scenario.UserId}"),
            Boundary = TrustBoundary.Public,
            RequesterPrincipal = PrincipalClassification.UntrustedExternal
        };
        return new BackgroundChildRun
        {
            RunId = new SubAgentRunId($"model-run-{scenario.RunId}"),
            ScopeId = new SubAgentScopeId($"model-scope-{scenario.ScopeId}"),
            StartKey = new ChildRunStartKey.Tool(Owner, context.TurnId, new ToolCallId($"acceptance-{scenario.AcceptanceCallId}")),
            AgentName = new AgentName("worker"),
            SourceOperation = "spawn_agent",
            ArgumentsDigest = $"model-digest-{scenario.RunId}",
            OriginalContext = context,
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty,
                Git = new GitWorkingContextInspection.Skipped()
            },
            AcceptedAtMs = 1,
            Lifecycle = new ChildRunLifecycle.Accepted(null),
            SourceReminderId = null
        };
    }

    private static ChildRunAccepted CreateAcceptance(BackgroundChildRun run) => new()
    {
        SessionId = Owner, Run = run, RecordedAtMs = 1,
        AcceptanceResult = new SerializableChatMessage
        {
            Role = ChatRole.Tool, Name = "spawn_agent",
            ToolCallId = Assert.IsType<ChildRunStartKey.Tool>(run.StartKey).CallId,
            Content = $"{{\"state\":\"accepted\",\"run_id\":\"{run.RunId.Value}\",\"scope_id\":\"{run.ScopeId.Value}\"}}"
        }
    };

    private static ChildRunTerminal CreateTerminal(BackgroundChildRun run)
    {
        var result = new SubAgentProtocol.SubAgentResult
        {
            AgentName = run.AgentName,
            RunId = run.RunId,
            ScopeId = run.ScopeId,
            Output = "Done.",
            Completion = new ChildRunCompletion.Completed(WorkingContextDelta.Empty)
        };
        return new ChildRunTerminal(result, Lost: false, EvidenceWarning: null);
    }

    private static (SerializableChatMessage Call, SerializableChatMessage Result, ToolCallId CallId) CreatePair(int suffix)
    {
        var id = new ToolCallId($"model-delivery-{suffix}");
        return (
            new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = id,
                        Name = new ToolName("child_run_result"),
                        ArgumentsJson = "{}"
                    }
                ]
            },
            new SerializableChatMessage
            {
                Role = ChatRole.Tool,
                Name = "child_run_result",
                ToolCallId = id,
                Content = "Done."
            },
            id);
    }

}
