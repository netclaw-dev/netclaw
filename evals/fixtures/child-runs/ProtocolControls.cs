// -----------------------------------------------------------------------
// <copyright file="ProtocolControls.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Evals.ChildRuns;

internal static class ProtocolControls
{
    private const string Session = "signalr/observer-control";
    private const string Body = "{\"run_id\":\"owner-run\",\"scope_id\":\"owner-scope\",\"state\":\"Accepted\",\"control_tool\":\"check_agent_run\"}";

    public static int Run()
    {
        var count = 0;
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
        Console.WriteLine(JsonSerializer.Serialize(new { passed = count, failed = 0, scope = "protocol/barrier controls; no model or daemon proof" }));
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
