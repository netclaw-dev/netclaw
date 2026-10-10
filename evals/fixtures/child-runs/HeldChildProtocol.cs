// -----------------------------------------------------------------------
// <copyright file="HeldChildProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using Netclaw.Actors.Protocol;

namespace Netclaw.Evals.ChildRuns;

internal sealed record AcceptedRun(string RunId, string ScopeId);

internal sealed class HeldChildProtocol(string sessionId)
{
    // ToolCallMetaExtractor returns this exact pre-dispatch rejection. It is not child acceptance.
    internal const string RequiredRationaleError =
        "Error: Required meta argument '_rationale' must be a non-empty string. " +
        "Supply one sentence that states the tool call intent. The tool was NOT executed.";
    private readonly Dictionary<string, string> _calls = new(StringComparer.Ordinal);
    private readonly StringBuilder _text = new();
    private bool _hasDelta;
    private int _lastTurn;

    public AcceptedRun? Acceptance { get; private set; }
    public int CompletedTurns { get; private set; }
    public bool ChildHeld { get; private set; }
    public bool Released { get; private set; }
    public string LastReply { get; private set; } = "";

    public void Observe(SessionOutputDto output)
    {
        Require(output.SessionId == sessionId, "A foreign session output reached the observer.");
        switch (output.Type)
        {
            case SessionOutputTypes.ToolCall:
                Require(!string.IsNullOrWhiteSpace(output.CallId) && !string.IsNullOrWhiteSpace(output.ToolName),
                    "A tool call lacks its canonical identifiers.");
                Require(_calls.TryAdd(output.CallId!, output.ToolName!), "A tool call identifier was reused.");
                break;
            case SessionOutputTypes.ToolResult:
                Require(output.CallId is not null && _calls.Remove(output.CallId, out var name)
                    && name == output.ToolName,
                    "A tool result lacks one matching call or repeats a result.");
                if (output.ToolName == "spawn_agent")
                {
                    if (IsUnexecutedRationaleRejection(output))
                        break;
                    Require(output.ToolFailureCode is null, "The child start failed.");
                    Acceptance ??= ParseAcceptance(output.Result ?? "");
                }
                break;
            case SessionOutputTypes.TextDelta:
                _hasDelta = true;
                _text.Append(output.Text);
                break;
            case SessionOutputTypes.Text:
                // The canonical headless consumer also excludes final text after stream deltas.
                if (!_hasDelta)
                    _text.Append(output.Text);
                break;
            case SessionOutputTypes.TurnCompleted:
                Require(output.TurnNumber is { } number && number.Value > _lastTurn,
                    "A completed turn lacks a fresh ordinal.");
                Require(output.TurnOutcome == "completed", "The parent turn did not complete normally.");
                _lastTurn = output.TurnNumber!.Value.Value;
                LastReply = _text.ToString();
                _text.Clear();
                _hasDelta = false;
                CompletedTurns++;
                break;
            case SessionOutputTypes.Error:
                throw new InvalidDataException("The daemon emitted an error: " + output.ErrorMessage);
        }
    }

    internal static bool IsUnexecutedRationaleRejection(SessionOutputDto output) =>
        output.ToolName == "spawn_agent" && output.ToolFailureCode == "invalid_rationale"
        && output.Result == RequiredRationaleError;

    public void RequireHeldAcceptance(int acceptedRunCount) =>
        Require(CompletedTurns == 1 && acceptedRunCount == 1 && Acceptance is not null
            && !string.IsNullOrWhiteSpace(LastReply),
            "The initial parent turn requires exactly one accepted child and a visible reply.");

    public void ConfirmHeld(JsonElement snapshot, string nonce, string slot)
    {
        Require(!Released && !ChildHeld, "The held-child barrier was repeated or followed release.");
        Require(Acceptance is not null, "The child binding precedes acceptance.");
        Require(slot == "child" && snapshot.GetProperty("nonce").GetString() == nonce,
            "The fixture acknowledged a foreign trial.");
        var binding = snapshot.GetProperty("binding");
        Require(binding.GetProperty("accepted").GetProperty("run_id").GetString() == Acceptance!.RunId
            && binding.GetProperty("accepted").GetProperty("scope_id").GetString() == Acceptance.ScopeId
            && binding.GetProperty("request_id").GetInt32() > 0,
            "The fixture did not bind the exact accepted child request.");
        ChildHeld = true;
    }

    public void ConfirmRelease(string probeMarker)
    {
        Require(ChildHeld && !Released && Acceptance is not null && CompletedTurns == 2,
            "Child release requires acceptance and two parent turn boundaries under the barrier.");
        Require(!string.IsNullOrWhiteSpace(probeMarker)
            && LastReply.Contains(probeMarker, StringComparison.Ordinal),
            "The second parent reply lacks the independent probe marker.");
        Released = true;
    }

    public static AcceptedRun ParseAcceptance(string body)
    {
        using var document = JsonDocument.Parse(body);
        Require(document.RootElement.ValueKind == JsonValueKind.Object, "Acceptance is not one JSON object.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.EnumerateObject())
        {
            Require(field.Value.ValueKind == JsonValueKind.String && fields.TryAdd(field.Name, field.Value.GetString()!),
                "Acceptance contains a duplicate or non-string field.");
        }
        foreach (var key in new[] { "run_id", "scope_id", "state", "control_tool" })
            Require(fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value),
                "Acceptance lacks a nonempty required field: " + key);
        Require(fields["state"] == "Accepted" && fields["control_tool"] == "check_agent_run",
            "Acceptance has the wrong initial state or control tool.");
        return new AcceptedRun(fields["run_id"], fields["scope_id"]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
