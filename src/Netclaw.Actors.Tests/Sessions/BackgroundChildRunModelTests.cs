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
    public bool TerminalSeen { get; set; }
    public bool ResultReady { get; set; }
    public int PairCommitCount { get; set; }
    public bool ContinuationClosed { get; set; }
    public int OrdinaryUserCount { get; set; }
}

public sealed class BackgroundChildRunModelTests(ITestOutputHelper output)
{
    private const string InitialSampleSeed = "CoordChildModel1";
    private static readonly SessionId Owner = new("test/child-run-model");

    private enum Action { RecordTerminal, Prepare, Consume, Close, StaleClose }

    // This model records public obligations. It does not duplicate lifecycle enum states.
    private sealed record Contract(bool Accepted, bool TerminalSeen, bool ResultReady,
        int PairCommitCount, bool ContinuationClosed, int OrdinaryUserCount);
    private sealed record Command(Action Action);
    private sealed record Scenario(Command[] Commands, int RunId, int CallId, int UserId);

    [Fact]
    public void Generated_child_actions_preserve_acceptance_result_and_single_pair_commit()
    {
        var command = Gen.OneOfConst(Action.RecordTerminal, Action.Prepare, Action.Consume, Action.Close, Action.StaleClose)
            .Select(action => new Command(action));
        var scenarios = Gen.Select(command.Array[1, 12], Gen.Int[0, 999], Gen.Int[0, 999], Gen.Int[0, 999],
            (commands, runId, callId, userId) => new Scenario(commands, runId, callId, userId));
        var spec = CreateSpec();
        var explored = new HashSet<Action>();
        var cases = 0;
        var operations = 0;

        scenarios.Sample(scenario =>
        {
            var run = CreateRun(scenario.RunId);
            var pair = CreatePair(scenario.CallId);
            var terminal = CreateTerminal(run);
            var state = SessionState.Empty.Apply(new ChildRunAccepted
            {
                SessionId = Owner,
                Run = run,
                RecordedAtMs = 1
            });
            var profile = new StateProfile(new ChildRunContractState { Accepted = true });

            foreach (var command in scenario.Commands)
            {
                explored.Add(command.Action);
                var current = Model((ChildRunContractState)profile.SingleState());
                var expected = Advance(current, command.Action);
                if (expected is null)
                {
                    Assert.Throws<InvalidOperationException>(() => ApplyActual(
                        state, run, terminal, pair, command.Action, scenario.UserId));
                    Assert.Equal(current, Observe(state, run, pair, scenario.UserId));
                    continue;
                }

                state = ApplyActual(state, run, terminal, pair, command.Action, scenario.UserId);
                var actual = Observe(state, run, pair, scenario.UserId);
                var operation = spec.GetOperation<Command, Contract>("Apply");
                var (valid, message, updated) = spec.Allows(operation, command, actual, profile);
                Assert.True(valid, $"Accordant rejected {command.Action}: {message}");
                profile = updated;
                Assert.Equal(expected, actual);
                operations++;
            }

            cases++;
        }, iter: 1_000, seed: InitialSampleSeed, threads: 1,
            print: scenario => $"run={scenario.RunId}; call={scenario.CallId}; "
                + string.Join(" -> ", scenario.Commands.Select(item => item.Action)));

        output.WriteLine($"seed={InitialSampleSeed}; cases={cases}; valid-operations={operations}; actions={string.Join(',', explored)}");
        Assert.True(cases >= 1_000, $"CsCheck ran only {cases} cases.");
        Assert.True(operations > cases, $"Only {operations} valid operations ran in {cases} cases.");
        Assert.Equal(Enum.GetValues<Action>().Length, explored.Count);
    }

    private static AccordantSpec CreateSpec()
    {
        var spec = new AccordantSpec();
        spec.Operation<Command, Contract>("Apply", (command, state) =>
        {
            var expected = Advance(Model(state), command.Action)
                ?? throw new InvalidOperationException("The scenario applied an illegal child action to the contract.");
            return Expect.That<Contract>(actual => actual == expected
                    ? ValidationResult.Valid()
                    : ValidationResult.Invalid($"Expected {expected}, observed {actual}."))
                .ThenState<ChildRunContractState>(next => SetModel(next, expected));
        });
        return spec;
    }

    private static Contract Model(ChildRunContractState state)
        => new(state.Accepted, state.TerminalSeen, state.ResultReady, state.PairCommitCount,
            state.ContinuationClosed, state.OrdinaryUserCount);

    private static void SetModel(ChildRunContractState state, Contract value)
    {
        state.Accepted = value.Accepted;
        state.TerminalSeen = value.TerminalSeen;
        state.ResultReady = value.ResultReady;
        state.PairCommitCount = value.PairCommitCount;
        state.ContinuationClosed = value.ContinuationClosed;
        state.OrdinaryUserCount = value.OrdinaryUserCount;
    }

    private static Contract? Advance(Contract contract, Action action) => action switch
    {
        Action.RecordTerminal when contract.Accepted && !contract.TerminalSeen
            => contract with { TerminalSeen = true },
        Action.Prepare when contract.TerminalSeen && !contract.ResultReady
            => contract with { ResultReady = true },
        Action.Consume when contract.ResultReady
            && (!contract.ContinuationClosed || contract.PairCommitCount == 1)
            => contract with { PairCommitCount = Math.Max(1, contract.PairCommitCount) },
        Action.Close when contract.ResultReady || contract.PairCommitCount == 1
            => contract with { PairCommitCount = 1, ContinuationClosed = true },
        _ => null
    };

    private static SessionState ApplyActual(
        SessionState state,
        BackgroundChildRun run,
        ChildRunTerminal terminal,
        (SerializableChatMessage Call, SerializableChatMessage Result, ToolCallId CallId) pair,
        Action action,
        int userId) => action switch
    {
        Action.RecordTerminal => state.Apply(new ChildRunEvent.TerminalRecorded(terminal)
        {
            SessionId = Owner,
            RunId = run.RunId,
            RecordedAtMs = 3
        }),
        Action.Prepare => state.Apply(new ChildRunEvent.ResultPrepared(pair.Call, pair.Result, terminal.Result.WorkingContext)
        {
            SessionId = Owner,
            RunId = run.RunId,
            RecordedAtMs = 4
        }),
        Action.Consume => new SessionToolWorkflow().ApplyBatchStarted(state, new ToolBatchStarted
        {
            SessionId = Owner,
            ChildContinuation = new ChildContinuation(run.RunId, pair.CallId),
            AssistantMessage = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Parent next step." },
            UserMessage = OrdinaryMessage(userId),
            StartedAtMs = 5
        }),
        Action.Close => state.Apply(new TurnRecorded
        {
            SessionId = Owner,
            ChildContinuation = new ChildContinuation(run.RunId, pair.CallId),
            UserMessage = OrdinaryMessage(userId),
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = $"Parent final {userId}." },
            RecordedAtMs = 6
        }),
        Action.StaleClose => state.Apply(new InputClosed
        {
            SessionId = Owner,
            ChildContinuation = new ChildContinuation(run.RunId, new ToolCallId($"stale-{userId}")),
            ClosedAtMs = 7
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static Contract Observe(
        SessionState state,
        BackgroundChildRun run,
        (SerializableChatMessage Call, SerializableChatMessage Result, ToolCallId CallId) pair,
        int userId)
    {
        var lifecycle = state.ChildRuns[run.RunId].Lifecycle;
        var callCount = state.History.Sum(message => message.ToolCalls.Count(call => call.CallId == pair.CallId));
        var resultCount = state.History.Count(message => message.ToolCallId == pair.CallId);
        Assert.Equal(callCount, resultCount);
        return new Contract(
            Accepted: state.ChildRuns.ContainsKey(run.RunId),
            TerminalSeen: lifecycle is ChildRunLifecycle.Terminal or ChildRunLifecycle.Prepared
                or ChildRunLifecycle.Consumed or ChildRunLifecycle.Closed,
            ResultReady: lifecycle is ChildRunLifecycle.Prepared or ChildRunLifecycle.Consumed
                or ChildRunLifecycle.Closed,
            PairCommitCount: callCount,
            ContinuationClosed: lifecycle is ChildRunLifecycle.Closed,
            OrdinaryUserCount: state.History.Count(message => message == OrdinaryMessage(userId)));
    }

    private static BackgroundChildRun CreateRun(int suffix)
    {
        var context = new TurnContextRecord
        {
            SessionId = Owner,
            TurnId = "turn-1",
            Boundary = TrustBoundary.Public,
            RequesterPrincipal = PrincipalClassification.UntrustedExternal
        };
        return new BackgroundChildRun
        {
            RunId = new SubAgentRunId($"model-run-{suffix}"),
            ScopeId = new SubAgentScopeId($"model-scope-{suffix}"),
            StartKey = new ChildRunStartKey.Slash(Owner, "turn-1", new InputId($"model-input-{suffix}")),
            AgentName = new AgentName("worker"),
            SourceOperation = "slash",
            ArgumentsDigest = $"model-digest-{suffix}",
            OriginalContext = context,
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty,
                Git = new GitWorkingContextInspection.Skipped()
            },
            AcceptedAtMs = 1,
            Lifecycle = new ChildRunLifecycle.Accepted(null)
        };
    }

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

    private static SerializableChatMessage OrdinaryMessage(int suffix)
        => new() { Role = ChatRole.User, Content = $"Ordinary-{suffix}" };
}
