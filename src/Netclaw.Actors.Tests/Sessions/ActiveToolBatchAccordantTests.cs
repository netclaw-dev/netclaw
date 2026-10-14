// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchAccordantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using CsCheck;
using Microsoft.Accordant;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Tools;
using Xunit;
using AccordantSpec = Microsoft.Accordant.Spec<Netclaw.Actors.Tests.Sessions.ToolBatchModelState>;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;
using HistoryResult = (string CallId, string Content);

namespace Netclaw.Actors.Tests.Sessions;

[State]
public partial class ToolBatchModelState
{
    public string FirstCallId { get; set; } = string.Empty;
    public string SecondCallId { get; set; } = string.Empty;
    public List<HistoryResult> History { get; set; } = [];
    public bool ExecutionTaskCompleted { get; set; }

    public bool HasResult(string callId) => History.Any(result => result.CallId == callId);
}

public sealed class ActiveToolBatchAccordantTests(ITestOutputHelper output)
{
    // CsCheck replays this first case, then explores new random sequences. Failures report their own replay seed.
    private const string InitialSampleSeed = "0N0XIzNsQ0O2";

    private enum ActionKind { FirstResult, SecondResult, TaskDone, ProbeCompletion }
    private enum Explored { Ready, Pending, DuplicateResult, NewBatch, ResultBeforeTask, TaskBeforeResult }

    private sealed record Command(ActionKind Kind, string Payload);
    private sealed record BatchIds(string First, string Second);
    private sealed record ResultInput(string CallId, string Payload);
    private sealed record BatchObservation(bool Ready, int ResultCount, HistoryResult[] History);
    private sealed record GeneratedSequence(Command[] Commands, int[] IdSuffixes)
    {
        public BatchIds Batch(int number)
            => new($"batch-{number}-A-{IdSuffixes[number]}", $"batch-{number}-B-{IdSuffixes[number]}");

        public override string ToString()
            => $"IDs=[{string.Join(',', IdSuffixes)}]; "
                + string.Join(" -> ", Commands.Select(command => $"{command.Kind}({command.Payload})"));
    }

    [Fact]
    public void Generated_batches_retain_first_results_and_wait_for_results_and_task()
    {
        // Arrange
        var spec = CreateSpec();
        var sequences = GenerateSequences();
        var explored = new HashSet<Explored>();
        var cases = 0;
        var operations = 0;

        // Act: CsCheck chooses operation order and inputs. Accordant checks each observed response.
        sequences.Sample(sequence =>
        {
            explored.UnionWith(new BatchScenario(spec, sequence).Run());
            cases++;
            operations += sequence.Commands.Length;
        }, iter: 1_000, seed: InitialSampleSeed, threads: 1, print: sequence => sequence.ToString());

        // Assert: These counts check exploration. The model rules below check behavior.
        output.WriteLine($"first-case-seed={InitialSampleSeed}; cases={cases}; operations={operations}; explored={string.Join(',', explored)}");
        Assert.True(cases >= 1_000, $"CsCheck ran only {cases} cases.");
        Assert.True(operations > cases, $"Only {operations} operations ran in {cases} cases.");
        foreach (var required in Enum.GetValues<Explored>())
            Assert.Contains(required, explored);
    }

    private static Gen<GeneratedSequence> GenerateSequences()
    {
        var command = Gen.Select(
            Gen.OneOfConst(ActionKind.FirstResult, ActionKind.SecondResult,
                ActionKind.TaskDone, ActionKind.ProbeCompletion),
            Gen.Int[0, 999],
            (kind, value) => new Command(kind, $"payload-{value}"));
        return Gen.Select(command.Array[1, 10], Gen.Int[0, 999].Array[11],
            (commands, idSuffixes) => new GeneratedSequence(commands, idSuffixes));
    }

    private static AccordantSpec CreateSpec()
    {
        var spec = new AccordantSpec();
        spec.Operation<ResultInput, HistoryResult[]>("RecordResult", RetainFirstResultInArrivalOrder);
        spec.Operation<Unit, Unit>("MarkTaskDone", (_, _) =>
            Expect.That<Unit>(_ => true)
                .ThenState<ToolBatchModelState>(nextState => nextState.ExecutionTaskCompleted = true));
        spec.Operation<BatchIds, BatchObservation>("ProbeCompletion", CompleteOnlyAfterBothResultsAndTask);
        return spec;
    }

    private static ExpectedOutcomes RetainFirstResultInArrivalOrder(ResultInput request, ToolBatchModelState state)
    {
        HistoryResult[] expected = state.HasResult(request.CallId)
            ? [.. state.History]
            : [.. state.History, (request.CallId, $"result {request.Payload}")];
        return Expect.That<HistoryResult[]>(actual => ValidateHistory(expected, actual))
            .ThenState<ToolBatchModelState>(nextState => nextState.History = [.. expected]);
    }

    private static ExpectedOutcomes CompleteOnlyAfterBothResultsAndTask(BatchIds nextBatch, ToolBatchModelState state)
    {
        var bothResultsArrived = state.HasResult(state.FirstCallId) && state.HasResult(state.SecondCallId);
        var expectedReady = bothResultsArrived && state.ExecutionTaskCompleted;
        var expectation = Expect.That<BatchObservation>(actual =>
        {
            if (actual.Ready != expectedReady)
                return ValidationResult.Invalid($"Completion requires both results and the task. Expected ready={expectedReady}, actual={actual.Ready}.");
            var expectedCount = expectedReady ? 2 : 0;
            if (actual.ResultCount != expectedCount)
                return ValidationResult.Invalid($"Expected result count={expectedCount}, actual={actual.ResultCount}.");
            return ValidateHistory(state.History, actual.History);
        });
        if (!expectedReady)
            return expectation.SameState();
        return expectation.ThenState<ToolBatchModelState>(nextState =>
        {
            nextState.FirstCallId = nextBatch.First;
            nextState.SecondCallId = nextBatch.Second;
            nextState.ExecutionTaskCompleted = false;
        });
    }

    private static ValidationResult ValidateHistory(IEnumerable<HistoryResult> expected, IEnumerable<HistoryResult> actual)
        => expected.SequenceEqual(actual)
            ? ValidationResult.Valid()
            : ValidationResult.Invalid($"History must retain the first result per call in arrival order. "
                + $"Expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");

    // This adapter invokes the real tracker. Only the Accordant model predicts its responses.
    private sealed class BatchScenario
    {
        private readonly AccordantSpec _spec;
        private readonly GeneratedSequence _sequence;
        private readonly ActiveToolBatchTracker _tracker = new();
        private readonly TurnStateTracker _turnState = new();
        private readonly List<string> _trace = [];
        private readonly HashSet<Explored> _explored = [];
        private SessionState _session = SessionState.Empty;
        private StateProfile _profile;
        private int _batchNumber;

        public BatchScenario(AccordantSpec spec, GeneratedSequence sequence)
        {
            _spec = spec;
            _sequence = sequence;
            var batch = sequence.Batch(0);
            _profile = new StateProfile(new ToolBatchModelState
            {
                FirstCallId = batch.First,
                SecondCallId = batch.Second
            });
            Start(batch);
        }

        public IReadOnlySet<Explored> Run()
        {
            foreach (var command in _sequence.Commands)
            {
                switch (command.Kind)
                {
                    case ActionKind.FirstResult:
                        RecordResult(_sequence.Batch(_batchNumber).First, command.Payload);
                        break;
                    case ActionKind.SecondResult:
                        RecordResult(_sequence.Batch(_batchNumber).Second, command.Payload);
                        break;
                    case ActionKind.TaskDone:
                        MarkTaskDone();
                        break;
                    case ActionKind.ProbeCompletion:
                        ProbeCompletion();
                        break;
                }
            }
            return _explored;
        }

        private void RecordResult(string callId, string payload)
        {
            var model = (ToolBatchModelState)_profile.SingleState();
            if (model.HasResult(callId))
                _explored.Add(Explored.DuplicateResult);
            if (model.ExecutionTaskCompleted)
                _explored.Add(Explored.TaskBeforeResult);
            var request = new ResultInput(callId, payload);
            _trace.Add($"Result({callId}, {payload})");
            _session = _tracker.ApplyRecordedResult(_session, new SerializableChatMessage
            {
                Role = ChatRole.Tool,
                Name = "file_read",
                ToolCallId = new ToolCallId(callId),
                Content = $"result {payload}"
            });
            Verify("RecordResult", request, History(_session));
        }

        private void MarkTaskDone()
        {
            var model = (ToolBatchModelState)_profile.SingleState();
            var anyResultArrived = model.HasResult(model.FirstCallId) || model.HasResult(model.SecondCallId);
            if (!model.ExecutionTaskCompleted && anyResultArrived)
                _explored.Add(Explored.ResultBeforeTask);
            _trace.Add("TaskDone");
            _tracker.MarkExecutionTaskCompleted();
            Verify("MarkTaskDone", Unit.Value, Unit.Value);
        }

        private void ProbeCompletion()
        {
            var nextBatch = _sequence.Batch(_batchNumber + 1);
            _trace.Add($"ProbeCompletion(next={nextBatch.First},{nextBatch.Second})");
            var completion = _tracker.CompleteIfReady(_session, _turnState);
            var observation = completion is BatchCompletion.Ready ready
                ? new BatchObservation(true, ready.ResultCount, History(ready.State))
                : new BatchObservation(false, 0, History(_session));
            Verify("ProbeCompletion", nextBatch, observation);
            _explored.Add(observation.Ready ? Explored.Ready : Explored.Pending);
            if (!observation.Ready)
                return;
            _tracker.Clear();
            _batchNumber++;
            Start(nextBatch);
            _explored.Add(Explored.NewBatch);
        }

        private void Start(BatchIds ids) => _tracker.Start(
            [new FunctionCallContent(ids.First, "file_read"), new FunctionCallContent(ids.Second, "file_read")],
            preparedCycleBatch: null);

        private void Verify<TRequest, TResponse>(string name, TRequest request, TResponse response)
        {
            var operation = _spec.GetOperation<TRequest, TResponse>(name);
            var (valid, message, updated) = _spec.Allows(operation, request, response, _profile);
            Assert.True(valid, $"Accordant rejected {string.Join(" -> ", _trace)}: {message}");
            _profile = updated;
        }

        private static HistoryResult[] History(SessionState session)
            => session.History
                .Where(message => message.Role == ChatRole.Tool && message.ToolCallId is not null)
                .Select(message => (message.ToolCallId!.Value.Value, message.Content!))
                .ToArray();
    }
}
