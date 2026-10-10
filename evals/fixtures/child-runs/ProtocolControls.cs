// -----------------------------------------------------------------------
// <copyright file="ProtocolControls.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Security.Cryptography;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Evals.ChildRuns;

internal static class ProtocolControls
{
    private const string Session = "signalr/observer-control";
    private const string Body = "{\"run_id\":\"owner-run\",\"scope_id\":\"owner-scope\",\"state\":\"Accepted\",\"control_tool\":\"check_agent_run\"}";

    public static int Run(string? transcriptPath)
    {
        var count = 0;
        object? replay = null;
        if (transcriptPath is not null)
        {
            var bytes = File.ReadAllBytes(transcriptPath);
            var outputs = File.ReadLines(transcriptPath).Select(line =>
            {
                using var row = JsonDocument.Parse(line);
                var output = JsonSerializer.Deserialize<SessionOutputDto>(row.RootElement.GetProperty("output").GetRawText())
                    ?? throw new InvalidDataException("The replay contains a null output.");
                return (Output: output, ObservedNs: row.RootElement.GetProperty("observed_ns").GetInt64());
            }).ToArray();
            var protocol = new HeldChildProtocol(outputs.First().Output.SessionId!);
            var statuses = new List<JsonElement>();
            var pending = new Dictionary<string, SessionObserver.ObservedCall>(StringComparer.Ordinal);
            var calls = new List<SessionObserver.ObservedCall>();
            var corrections = 0;
            var rejectedStarts = 0;
            foreach (var row in outputs)
            {
                var output = row.Output;
                protocol.Observe(output);
                if (output.Type == SessionOutputTypes.ToolCall)
                {
                    using var arguments = JsonDocument.Parse(output.ArgumentsJson ?? "{}");
                    var call = new SessionObserver.ObservedCall(output.CallId!, output.ToolName!, arguments.RootElement.Clone(),
                        protocol.CompletedTurns + 1, row.ObservedNs, calls.Count + 1);
                    Check(pending.TryAdd(output.CallId!, call));
                    calls.Add(call);
                }
                if (output.Type != SessionOutputTypes.ToolResult)
                    continue;
                Check(pending.Remove(output.CallId!, out var observed));
                var status = observed!.CompleteResult(output);
                if (HeldChildProtocol.IsUnexecutedRationaleRejection(output))
                {
                    Check(!observed.Success && observed.FailureCode == "invalid_rationale"
                        && observed.Result == HeldChildProtocol.RequiredRationaleError);
                    rejectedStarts++;
                }
                if (output.ToolName != "check_agent_run")
                    continue;
                Check(output.ToolFailureCode is null);
                Console.Error.WriteLine($"Replay status result {statuses.Count + corrections + 1}: {output.CallId}");
                Check(observed.Result == output.Result && observed.Success == (status is not null));
                if (status is { } body)
                {
                    statuses.Add(body);
                    Console.Error.WriteLine($"Replay status parsed: {body.GetProperty("state").GetString()}");
                }
                else
                    corrections++;
            }
            if (rejectedStarts > 0)
                Check(rejectedStarts == 1 && statuses.Count == 0 && corrections == 0 && protocol.Acceptance is null);
            else
            {
                Check(statuses.Count == 2 && statuses.All(status => status.GetProperty("state").GetString() == "Running"));
                Check(corrections == 1);
            }
            Check(!protocol.ChildHeld && !protocol.Released && protocol.CompletedTurns == 0);
            replay = new { source_sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                status_results = statuses.Count, corrections, rejected_starts = rejectedStarts, completed_turns = protocol.CompletedTurns,
                child_held = protocol.ChildHeld, released = protocol.Released,
                calls = calls.Where(call => call.Name is "check_agent_run" or "spawn_agent").Select(call => new
                    { id = call.Id, name = call.Name, occurrence = call.Occurrence, observed_ns = call.ObservedNs,
                        result = call.Result, success = call.Success, failure_code = call.FailureCode }) };
        }
        foreach (var malformed in new[]
                 {
                     "{}", "[" + Body + "]", "prose " + Body, "```json\n" + Body + "\n```",
                     Change("\"owner-run\"", "null"), Change("owner-scope", " "),
                     Change("Accepted", "Completed"), Change("check_agent_run", "check_background_job"),
                     Change("\"run_id\":\"owner-run\"", "\"run_id\":\"owner-run\",\"run_id\":\"other\"")
                 })
            Reject(() => HeldChildProtocol.ParseAcceptance(malformed));
        var accepted = HeldChildProtocol.ParseAcceptance(" \n" + Body + "\n");
        Check(accepted == new AcceptedRun("owner-run", "owner-scope"));
        var valid = New();
        using var barrier = JsonDocument.Parse("{\"nonce\":\"trial\",\"binding\":{\"accepted\":{\"run_id\":\"owner-run\",\"scope_id\":\"owner-scope\"},\"request_id\":1}}");
        AddAcceptance(valid);
        valid.ConfirmHeld(barrier.RootElement, "trial", "child");
        AddTurn(valid, 1, "The child was accepted.");
        AddTurn(valid, 2, "probe-answer");
        valid.ConfirmRelease("probe-answer");
        Check(valid.Released && valid.CompletedTurns == 2);
        Reject(() => New().ConfirmRelease("probe-answer"));
        var early = New(); AddAcceptance(early); early.ConfirmHeld(barrier.RootElement, "trial", "child");
        AddTurn(early, 1, "probe-answer");
        Reject(() => early.ConfirmRelease("probe-answer"));
        var wrong = New(); AddAcceptance(wrong); wrong.ConfirmHeld(barrier.RootElement, "trial", "child");
        AddTurn(wrong, 1, "accepted"); AddTurn(wrong, 2, "different");
        Reject(() => wrong.ConfirmRelease("probe-answer"));
        Reject(() => New().ConfirmHeld(barrier.RootElement, "foreign", "child"));
        foreach (var malformedBinding in new[]
                 {
                     "{\"nonce\":\"trial\",\"binding\":{\"accepted\":{\"run_id\":\"foreign\",\"scope_id\":\"owner-scope\"},\"request_id\":1}}",
                     "{\"nonce\":\"trial\",\"binding\":{\"accepted\":{\"run_id\":\"owner-run\",\"scope_id\":\"foreign\"},\"request_id\":1}}",
                     "{\"nonce\":\"trial\",\"binding\":{\"accepted\":{\"run_id\":\"owner-run\",\"scope_id\":\"owner-scope\"},\"request_id\":0}}"
                 })
        {
            var invalid = New();
            AddAcceptance(invalid);
            using var snapshot = JsonDocument.Parse(malformedBinding);
            Reject(() => invalid.ConfirmHeld(snapshot.RootElement, "trial", "child"));
        }
        Reject(() => New().Observe(Result()));
        var duplicate = New(); AddAcceptance(duplicate);
        Reject(() => duplicate.Observe(Result()));
        Reject(() => New().Observe(Dto(new TextOutput("foreign") { SessionId = new SessionId("foreign") })));
        Reject(() => valid.Observe(Dto(new TurnCompleted
            { SessionId = new SessionId(Session), TurnNumber = new TurnNumber(2) })));
        Reject(() => New().Observe(Dto(new TurnCompleted
            { SessionId = new SessionId(Session), TurnNumber = new TurnNumber(1), Outcome = TurnOutcome.Failed })));
        var mismatch = New(); mismatch.Observe(Call());
        Reject(() => mismatch.Observe(Result() with { ToolName = "skill_load" }));
        var deltas = New();
        deltas.Observe(Dto(new TextDeltaOutput("one") { SessionId = new SessionId(Session) }));
        deltas.Observe(Dto(new TextOutput("one") { SessionId = new SessionId(Session) }));
        deltas.Observe(Dto(new TurnCompleted { SessionId = new SessionId(Session), TurnNumber = new TurnNumber(1) }));
        Check(deltas.LastReply == "one");
        var reused = New();
        var readCall = Call() with { ToolName = "file_read" };
        var readResult = Result() with { ToolName = "file_read", Result = "neutral read result" };
        reused.Observe(readCall);
        Reject(() => reused.Observe(readCall));
        reused.Observe(readResult);
        AddTurn(reused, 1, "Initial acknowledgement with REQUIRED-MARKER");
        reused.Observe(readCall);
        reused.Observe(readResult);
        AddTurn(reused, 2, "Actual final response");
        Check(reused.CompletedTurns == 2 && reused.LastReply == "Actual final response"
            && !reused.LastReply.Contains("REQUIRED-MARKER", StringComparison.Ordinal));
        Reject(() => reused.Observe(readResult));
        const string running = "{\"run_id\":\"owner-run\",\"scope_id\":\"owner-scope\",\"state\":\"Running\"}";
        Check(SessionObserver.ParseControlStatus(running) is { } live && live.GetProperty("state").GetString() == "Running");
        const string correction = "Netclaw stopped this tool call because it would continue a repeated action-and-outcome cycle. "
            + "The same action completed twice without a changed result. This call did not execute.\n"
            + "Next action: choose a different action, load a missing tool, or finish the task.";
        Check(SessionObserver.ParseControlStatus(correction) is null);
        foreach (var invalid in new[] { "Unknown prose.", "{", "```json\n" + running + "\n```", correction + " Unexpected suffix." })
            Reject(() => SessionObserver.ParseControlStatus(invalid));
        Check(SessionObserver.ParseControlStatus(running) is { } later && later.GetProperty("run_id").GetString() == "owner-run");
        using var noArguments = JsonDocument.Parse("{}");
        var refused = new SessionObserver.ObservedCall("status", "check_agent_run", noArguments.RootElement.Clone(), 1, 10, 1);
        var refusedResult = Result() with { CallId = "status", ToolName = "check_agent_run", Result = correction };
        Check(refused.CompleteResult(refusedResult) is null && !refused.Success && refused.Result == correction
            && refused.Id == "status" && refused.Name == "check_agent_run" && refused.Occurrence == 1 && refused.ObservedNs == 10);
        var next = new SessionObserver.ObservedCall("status", "check_agent_run", noArguments.RootElement.Clone(), 2, 20, 2);
        Check(next.CompleteResult(refusedResult with { Result = running }) is { } nextStatus
            && next.Success && nextStatus.GetProperty("state").GetString() == "Running" && !refused.Success
            && refused.Result == correction);
        Reject(() => next.CompleteResult(refusedResult with { CallId = "foreign" }));
        Reject(() => next.CompleteResult(refusedResult with { ToolName = "spawn_agent" }));
        var ordinary = new SessionObserver.ObservedCall("status", "file_read", noArguments.RootElement.Clone(), 1, 10, 1);
        Check(ordinary.CompleteResult(refusedResult with { ToolName = "file_read" }) is null && ordinary.Success);
        var rejectedResult = Result() with { ToolFailureCode = "invalid_rationale", Result = HeldChildProtocol.RequiredRationaleError };
        var repaired = New();
        repaired.Observe(Call());
        repaired.Observe(rejectedResult);
        Check(repaired.Acceptance is null && repaired.CompletedTurns == 0 && !repaired.ChildHeld && !repaired.Released);
        var failedCall = new SessionObserver.ObservedCall("start", "spawn_agent", noArguments.RootElement.Clone(), 1, 10, 1);
        Check(failedCall.CompleteResult(rejectedResult) is null && !failedCall.Success
            && failedCall.FailureCode == "invalid_rationale" && failedCall.Result == HeldChildProtocol.RequiredRationaleError);
        repaired.Observe(Call() with { CallId = "repaired-start" });
        repaired.Observe(Result() with { CallId = "repaired-start" });
        AddTurn(repaired, 1, "The child was accepted after correction.");
        repaired.RequireHeldAcceptance(1);
        repaired.ConfirmHeld(barrier.RootElement, "trial", "child");
        AddTurn(repaired, 2, "probe-answer");
        repaired.ConfirmRelease("probe-answer");
        Check(repaired.Released && repaired.Acceptance == accepted && !failedCall.Success);
        foreach (var invalid in new[] { rejectedResult with { ToolFailureCode = null },
                     rejectedResult with { ToolFailureCode = "unknown_agent" },
                     rejectedResult with { Result = HeldChildProtocol.RequiredRationaleError + " suffix" } })
        {
            var invalidStart = New(); invalidStart.Observe(Call());
            Reject(() => invalidStart.Observe(invalid));
        }
        Reject(() => New().Observe(rejectedResult));
        var foreignRejection = New(); foreignRejection.Observe(Call());
        Reject(() => foreignRejection.Observe(rejectedResult with { SessionId = "foreign" }));
        var wrongRejection = New(); wrongRejection.Observe(Call());
        Reject(() => wrongRejection.Observe(rejectedResult with { CallId = "foreign" }));
        var mismatchedRejection = New(); mismatchedRejection.Observe(Call());
        Reject(() => mismatchedRejection.Observe(rejectedResult with { ToolName = "file_read" }));
        var repeatedRejection = New(); repeatedRejection.Observe(Call()); repeatedRejection.Observe(rejectedResult);
        Reject(() => repeatedRejection.Observe(rejectedResult));
        var noAcceptance = New(); noAcceptance.Observe(Call()); noAcceptance.Observe(rejectedResult);
        AddTurn(noAcceptance, 1, "No child was accepted.");
        Reject(() => noAcceptance.RequireHeldAcceptance(0));
        var multiple = New(); AddAcceptance(multiple);
        multiple.Observe(Call() with { CallId = "second-start" });
        multiple.Observe(Result() with { CallId = "second-start", Result = Body.Replace("owner-run", "second-run", StringComparison.Ordinal) });
        AddTurn(multiple, 1, "Two children were accepted.");
        Check(multiple.CompletedTurns == 1 && multiple.Acceptance == accepted);
        Reject(() => multiple.RequireHeldAcceptance(2));
        Console.WriteLine(JsonSerializer.Serialize(new { passed = count, failed = 0, replay,
            scope = "protocol/barrier controls; no model or daemon proof" }));
        return 0;

        void Check(bool condition)
        {
            if (!condition) throw new InvalidDataException("A positive protocol control failed.");
            count++;
        }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { count++; return; }
            throw new InvalidDataException("A negative protocol control accepted unsafe evidence.");
        }
    }

    private static string Change(string before, string after) => Body.Replace(before, after, StringComparison.Ordinal);
    private static HeldChildProtocol New() => new(Session);
    private static SessionOutputDto Dto(SessionOutput output) =>
        SessionOutputDtoMapper.ToDto(SessionOutputDtoMapper.FromDto(SessionOutputDtoMapper.ToDto(output)));
    private static SessionOutputDto Call() => Dto(new ToolCallOutput
        { SessionId = new SessionId(Session), CallId = new ToolCallId("start"), ToolName = new ToolName("spawn_agent") });
    private static SessionOutputDto Result() => Dto(new ToolResultOutput
        { SessionId = new SessionId(Session), CallId = new ToolCallId("start"), ToolName = new ToolName("spawn_agent"), Result = Body });
    private static void AddAcceptance(HeldChildProtocol protocol) { protocol.Observe(Call()); protocol.Observe(Result()); }
    private static void AddTurn(HeldChildProtocol protocol, int number, string text)
    {
        protocol.Observe(Dto(new TextOutput(text) { SessionId = new SessionId(Session) }));
        protocol.Observe(Dto(new TurnCompleted { SessionId = new SessionId(Session), TurnNumber = new TurnNumber(number) }));
    }
}
