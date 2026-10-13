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

namespace Netclaw.Actors.Tests.Sessions;

[State]
public partial class ToolBatchModelState
{
    public string FirstCallId { get; set; } = string.Empty;
    public string SecondCallId { get; set; } = string.Empty;
    public List<string> CurrentResults { get; set; } = [];
    public List<string> HistoryResults { get; set; } = [];
    public bool ExecutionTaskCompleted { get; set; }
}

public sealed class ActiveToolBatchAccordantTests(ITestOutputHelper output)
{
    private const string ReplaySeed = "0N0XIzNsQ0O2";

    private enum ActionKind { FirstResult, SecondResult, TaskDone, ProbeCompletion }

    private sealed record Command(ActionKind Kind, string Payload);
    private sealed record BatchIds(string First, string Second);
    private sealed record ResultInput(string CallId, string Payload);
    private sealed record BatchView(bool Ready, int ResultCount, string History);

    [Fact]
    public void Generated_batches_match_the_accordant_result_and_completion_model()
    {
        // Arrange
        var spec = CreateSpec();
        var resultOperation = spec.GetOperation<ResultInput, string>("RecordResult");
        var taskOperation = spec.GetOperation<Unit, Unit>("MarkTaskDone");
        var probeOperation = spec.GetOperation<BatchIds, BatchView>("ProbeCompletion");
        var cases = 0;
        var operations = 0;
        var readyCases = 0;
        var pendingCases = 0;
        var duplicateCases = 0;
        var secondBatchCases = 0;
        var resultBeforeTaskCases = 0;
        var taskBeforeResultCases = 0;
        var command = Gen.Select(
            Gen.OneOfConst(ActionKind.FirstResult, ActionKind.SecondResult,
                ActionKind.TaskDone, ActionKind.ProbeCompletion),
            Gen.Int[0, 999],
            (kind, value) => new Command(kind, $"payload-{value}"));
        var generated = Gen.Select(command.Array[1, 10], Gen.Int[0, 999].Array[11],
            (commands, idSuffixes) => (commands, idSuffixes));

        // Act
        generated.Sample(sample =>
        {
            var (commands, idSuffixes) = sample;
            var batchNumber = 0;
            var batch = Ids(batchNumber, idSuffixes[batchNumber]);
            var tracker = new ActiveToolBatchTracker();
            tracker.Start(Calls(batch), preparedCycleBatch: null);
            var session = SessionState.Empty;
            var turn = new TurnStateTracker();
            var profile = new StateProfile(new ToolBatchModelState
            {
                FirstCallId = batch.First,
                SecondCallId = batch.Second
            });
            var trace = new List<string>();
            var seenFirst = false;
            var seenSecond = false;
            var seenTask = false;
            var duplicate = false;
            var resultBeforeTask = false;
            var taskBeforeResult = false;
            var hadReady = false;
            var hadPending = false;

            foreach (var commandInput in commands)
            {
                switch (commandInput.Kind)
                {
                    case ActionKind.FirstResult:
                    case ActionKind.SecondResult:
                    {
                        var first = commandInput.Kind == ActionKind.FirstResult;
                        var callId = first ? batch.First : batch.Second;
                        duplicate |= first ? seenFirst : seenSecond;
                        if (first) seenFirst = true;
                        else seenSecond = true;
                        taskBeforeResult |= seenTask;
                        var request = new ResultInput(callId, commandInput.Payload);
                        trace.Add($"Result({request.CallId}, {request.Payload})");
                        session = tracker.ApplyRecordedResult(session, Result(request));
                        Verify(spec, resultOperation, request, History(session), ref profile, trace);
                        break;
                    }
                    case ActionKind.TaskDone:
                        trace.Add("TaskDone");
                        tracker.MarkExecutionTaskCompleted();
                        resultBeforeTask |= !seenTask && (seenFirst || seenSecond);
                        seenTask = true;
                        Verify(spec, taskOperation, Unit.Value, Unit.Value, ref profile, trace);
                        break;
                    case ActionKind.ProbeCompletion:
                    {
                        var nextBatch = Ids(batchNumber + 1, idSuffixes[batchNumber + 1]);
                        trace.Add($"ProbeCompletion(next={nextBatch.First},{nextBatch.Second})");
                        var completion = tracker.CompleteIfReady(session, turn, 10);
                        var view = completion is BatchCompletion.Ready ready
                            ? new BatchView(true, ready.ResultCount, History(ready.State))
                            : new BatchView(false, 0, History(session));
                        Verify(spec, probeOperation, nextBatch, view, ref profile, trace);
                        if (view.Ready)
                        {
                            hadReady = true;
                            tracker.Clear();
                            batchNumber++;
                            batch = nextBatch;
                            tracker.Start(Calls(batch), preparedCycleBatch: null);
                            seenFirst = false;
                            seenSecond = false;
                            seenTask = false;
                        }
                        else hadPending = true;
                        break;
                    }
                }
            }

            Interlocked.Increment(ref cases);
            Interlocked.Add(ref operations, commands.Length);
            if (hadReady) Interlocked.Increment(ref readyCases);
            if (hadPending) Interlocked.Increment(ref pendingCases);
            if (duplicate) Interlocked.Increment(ref duplicateCases);
            if (batchNumber > 0) Interlocked.Increment(ref secondBatchCases);
            if (resultBeforeTask) Interlocked.Increment(ref resultBeforeTaskCases);
            if (taskBeforeResult) Interlocked.Increment(ref taskBeforeResultCases);
        }, iter: 1_000, seed: ReplaySeed, threads: 1,
            print: sample => string.Join(" -> ", sample.commands.Select(commandInput =>
                $"{commandInput.Kind}({commandInput.Payload})")));

        // Assert
        output.WriteLine($"seed={ReplaySeed}; cases={cases}; operations={operations}; "
            + $"ready={readyCases}; pending={pendingCases}; duplicate={duplicateCases}; "
            + $"second-batch={secondBatchCases}; result-before-task={resultBeforeTaskCases}; "
            + $"task-before-result={taskBeforeResultCases}");
        Assert.True(cases >= 1_000, $"CsCheck ran only {cases} cases.");
        Assert.True(operations > cases, $"Only {operations} operations ran in {cases} cases.");
        Assert.True(readyCases > 0, "No generated case completed a batch.");
        Assert.True(pendingCases > 0, "No generated case observed a pending batch.");
        Assert.True(duplicateCases > 0, "No generated case repeated a result in one batch.");
        Assert.True(secondBatchCases > 0, "No generated case cleared and started a second batch.");
        Assert.True(resultBeforeTaskCases > 0, "No result preceded task completion.");
        Assert.True(taskBeforeResultCases > 0, "No task completion preceded a result.");
    }

    private static AccordantSpec CreateSpec()
    {
        var spec = new AccordantSpec();
        spec.Operation<ResultInput, string>("RecordResult", (request, state) =>
        {
            var recorded = $"{request.CallId}:result {request.Payload}";
            List<string> expected = state.HistoryResults.Any(value =>
                    value.StartsWith($"{request.CallId}:", StringComparison.Ordinal))
                ? state.HistoryResults
                : [.. state.HistoryResults, recorded];
            return Expect.That<string>(value => value == string.Join('|', expected),
                    $"History must retain one payload per call: {string.Join('|', expected)}")
                .ThenState<ToolBatchModelState>(updated =>
                {
                    if (!updated.CurrentResults.Contains(request.CallId, StringComparer.Ordinal))
                        updated.CurrentResults.Add(request.CallId);
                    if (!updated.HistoryResults.Any(value =>
                            value.StartsWith($"{request.CallId}:", StringComparison.Ordinal)))
                        updated.HistoryResults.Add(recorded);
                });
        });
        spec.Operation<Unit, Unit>("MarkTaskDone", (_, _) =>
            Expect.That<Unit>(_ => true)
                .ThenState<ToolBatchModelState>(updated => updated.ExecutionTaskCompleted = true));
        spec.Operation<BatchIds, BatchView>("ProbeCompletion", (nextBatch, state) =>
        {
            var expectedReady = state.ExecutionTaskCompleted
                && state.CurrentResults.Contains(state.FirstCallId, StringComparer.Ordinal)
                && state.CurrentResults.Contains(state.SecondCallId, StringComparer.Ordinal);
            var expectedHistory = string.Join('|', state.HistoryResults);
            var expectation = Expect.That<BatchView>(
                value => value.Ready == expectedReady
                    && value.ResultCount == (expectedReady ? 2 : 0)
                    && value.History == expectedHistory,
                $"Ready={expectedReady}, count={(expectedReady ? 2 : 0)}, history={expectedHistory}");
            if (!expectedReady)
                return expectation.SameState();
            return expectation.ThenState<ToolBatchModelState>(updated =>
            {
                updated.FirstCallId = nextBatch.First;
                updated.SecondCallId = nextBatch.Second;
                updated.CurrentResults.Clear();
                updated.ExecutionTaskCompleted = false;
            });
        });
        return spec;
    }

    private static void Verify<TRequest, TResponse>(
        AccordantSpec spec,
        Operation<TRequest, TResponse, ToolBatchModelState> operation,
        TRequest request,
        TResponse response,
        ref StateProfile profile,
        IReadOnlyList<string> trace)
    {
        var (valid, message, updated) = spec.Allows(operation, request, response, profile);
        Assert.True(valid, $"Accordant rejected {string.Join(" -> ", trace)}: {message}");
        profile = updated;
    }

    private static string History(SessionState session)
        => string.Join('|', session.History
            .Where(message => message.Role == ChatRole.Tool && message.ToolCallId is not null)
            .Select(message => $"{message.ToolCallId!.Value.Value}:{message.Content}"));

    private static BatchIds Ids(int batchNumber, int suffix)
        => new($"batch-{batchNumber}-A-{suffix}", $"batch-{batchNumber}-B-{suffix}");

    private static FunctionCallContent[] Calls(BatchIds ids) =>
    [
        new FunctionCallContent(ids.First, "file_read"),
        new FunctionCallContent(ids.Second, "file_read")
    ];

    private static SerializableChatMessage Result(ResultInput input) => new()
    {
        Role = ChatRole.Tool,
        Name = "file_read",
        ToolCallId = new ToolCallId(input.CallId),
        Content = $"result {input.Payload}"
    };
}
