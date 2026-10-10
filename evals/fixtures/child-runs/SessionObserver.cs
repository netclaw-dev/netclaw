// -----------------------------------------------------------------------
// <copyright file="SessionObserver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using R3;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Evals.ChildRuns;

internal sealed record ObserverInput(
    string DaemonEndpoint, string FixtureEndpoint, string Nonce, string InitialPrompt,
    string ProbePrompt, string ProbeMarker, string EvidenceDirectory, int TimeoutSeconds,
    string Mode, string SessionId, string OutputFormat);

internal static class SessionObserver
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--project-process-journal", var database, var session, var output])
            return await ChildProcessJournal.RunAsync(database, session, output);
        if (args is ["--process-projection-controls"])
            return await ProcessJournalControls.RunAsync();
        if (args is ["--protocol-controls"])
            return ProtocolControls.Run(null);
        if (args is ["--protocol-controls", var transcriptPath])
            return ProtocolControls.Run(transcriptPath);
        if (args.Length != 1)
            throw new ArgumentException("Supply one observer input JSON file or --protocol-controls.");
        var input = JsonSerializer.Deserialize<ObserverInput>(await File.ReadAllTextAsync(args[0]))
            ?? throw new InvalidDataException("Observer input is null.");
        if (ProcessCaseObserver.Supports(input.Mode))
            return await ProcessCaseObserver.RunAsync(input);
        Validate(input);
        Directory.CreateDirectory(input.EvidenceDirectory);
        var receiptPath = Path.Combine(input.EvidenceDirectory, "observer-receipt.json");
        if (File.Exists(receiptPath))
            throw new IOException("The observer receipt already exists.");
        await using var events = new StreamWriter(new FileStream(
            Path.Combine(input.EvidenceDirectory, "session-output.jsonl"), FileMode.CreateNew, FileAccess.Write))
            { AutoFlush = true };
        await using var client = new DaemonClient(input.DaemonEndpoint);
        var outputs = Channel.CreateUnbounded<(SessionOutput Output, long ObservedNs)>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false });
        using var subscription = client.SessionOutput.Subscribe(output => outputs.Writer.TryWrite((output, MonotonicNs())));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(input.TimeoutSeconds));
        using var http = new HttpClient();
        var token = deadline.Token;
        var sessionId = "";
        var sequence = 0;
        var userInputs = 0;
        long firstTurnNs = 0, secondTurnNs = 0, releaseNs = 0;
        HeldChildProtocol? protocol = null;
        var pendingCalls = new Dictionary<string, ObservedCall>(StringComparer.Ordinal);
        var calls = new List<ObservedCall>();
        var starts = new Dictionary<string, ObservedCall>(StringComparer.Ordinal);
        var acceptances = new Dictionary<string, AcceptedRun>(StringComparer.Ordinal);
        var transcript = new StringBuilder();
        var replies = new List<string>();
        var deliveryObservations = JsonSerializer.SerializeToElement(new { complete = false, deliveries = Array.Empty<object>() });
        var bound = false;
        var statusBodies = new List<JsonElement>();
        try
        {
            await client.ConnectAsync(token);
            sessionId = string.IsNullOrEmpty(input.SessionId)
                ? await client.CreateSessionAsync(ChannelType.Headless, token)
                : await client.ResumeSessionAsync(input.SessionId, ChannelType.Headless, token);
            protocol = new HeldChildProtocol(sessionId);
            await SendAsync(input.InitialPrompt);
            await ReadThroughAsync(() => protocol.CompletedTurns >= 1);
            if (input.Mode == "collect")
            {
                Require(acceptances.Count > 0, "The initial parent turn lacks an accepted child.");
                await ReadThroughAsync(() => deliveryObservations.GetProperty("complete").GetBoolean());
            }
            else if (input.Mode != "turn")
            {
                protocol.RequireHeldAcceptance(acceptances.Count);
                // This acknowledgement proves candidate arrival only. Status binds ownership below.
                using var candidate = await ControlAsync("child-wait", new { });
                await SendAsync(input.ProbePrompt.Replace("{{RUN_ID}}", protocol.Acceptance!.RunId, StringComparison.Ordinal));
                await ReadThroughAsync(() => protocol.CompletedTurns >= 2);
                Require(bound, "The parent probe lacks an authorized live status for child ownership.");
                protocol.ConfirmRelease(input.ProbeMarker);
                using var released = await ControlAsync("child-release", new { });
                releaseNs = MonotonicNs();
                await ReadThroughAsync(() => deliveryObservations.GetProperty("complete").GetBoolean());
                using var drained = await ControlAsync("child-drained", new { });
            }
            await WriteReceiptAsync("observed", null);
            if (input.OutputFormat == "json")
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    sessionId, response = protocol.LastReply,
                    toolCalls = calls.Select(call => new
                        { callId = call.Id, toolName = call.Name, argumentsJson = call.Arguments.GetRawText() }),
                    evidenceSource = "persistent DaemonClient output; not the headless CLI envelope"
                }));
            else
                Console.Write(transcript.ToString());
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await WriteReceiptAsync("incomplete", error.ToString());
            throw;
        }

        async Task SendAsync(string prompt)
        {
            userInputs++;
            await client.SendAsync(prompt, token);
        }

        async Task<JsonDocument> ControlAsync(string action, object body)
        {
            using var content = JsonContent.Create(body);
            // The bounded fixture parser requires a known Content-Length.
            await content.LoadIntoBufferAsync(token);
            using var response = await http.PostAsync(
                input.FixtureEndpoint.TrimEnd('/') + "/control/" + action, content, token);
            var text = await response.Content.ReadAsStringAsync(token);
            Require(response.IsSuccessStatusCode, "The fixture rejected the control: " + text);
            return JsonDocument.Parse(text);
        }

        async Task ReadThroughAsync(Func<bool> complete)
        {
            var currentProtocol = protocol ?? throw new InvalidDataException("The observer session is not initialized.");
            while (!complete())
            {
                var output = await outputs.Reader.ReadAsync(token);
                var dto = SessionOutputDtoMapper.ToDto(output.Output);
                await events.WriteLineAsync(JsonSerializer.Serialize(new
                    { sequence = ++sequence, observed_ns = output.ObservedNs, output = dto }));
                currentProtocol.Observe(dto);
                if (dto.Type == SessionOutputTypes.ToolCall)
                {
                    using var arguments = JsonDocument.Parse(dto.ArgumentsJson ?? "{}");
                    var call = new ObservedCall(dto.CallId!, dto.ToolName!, arguments.RootElement.Clone(),
                        currentProtocol.CompletedTurns + 1, output.ObservedNs, calls.Count + 1);
                    Require(pendingCalls.TryAdd(dto.CallId!, call), "A pending call identifier repeats within its occurrence.");
                    calls.Add(call);
                    transcript.AppendLine($"[tool:call] {dto.ToolName}({dto.ArgumentsJson})");
                }
                else if (dto.Type == SessionOutputTypes.ToolResult)
                {
                    Require(pendingCalls.Remove(dto.CallId!, out var matched), "The result lacks its pending call occurrence.");
                    var call = matched!;
                    var status = call.CompleteResult(dto);
                    transcript.AppendLine($"[tool:result] {dto.ToolName} → {dto.Result}");
                    if (dto.ToolName == "spawn_agent" && call.Success)
                    {
                        var accepted = HeldChildProtocol.ParseAcceptance(call.Result);
                        Require(acceptances.TryAdd(accepted.RunId, accepted), "The accepted run identifier repeats.");
                        starts.Add(accepted.RunId, call);
                    }
                    if (dto.ToolName == "check_agent_run" && status is { } controlStatus)
                    {
                        statusBodies.Add(controlStatus);
                        if (!bound && (input.Mode is "held" or "cancel")
                            && controlStatus.TryGetProperty("state", out var state)
                            && state.GetString() is "Accepted" or "Running" or "Cancelling")
                        {
                            Require(currentProtocol.Acceptance is not null, "Status precedes the paired start acceptance.");
                            using var held = await ControlAsync("child-bind", new
                            {
                                accepted = AcceptanceBody(currentProtocol.Acceptance!), status = controlStatus
                            });
                            currentProtocol.ConfirmHeld(held.RootElement, input.Nonce, "child");
                            bound = true;
                        }
                    }
                }
                else if (dto.Type == SessionOutputTypes.TurnCompleted)
                {
                    if (firstTurnNs == 0)
                        firstTurnNs = output.ObservedNs;
                    if (currentProtocol.CompletedTurns == 2)
                        secondTurnNs = output.ObservedNs;
                    transcript.AppendLine(currentProtocol.LastReply);
                    replies.Add(currentProtocol.LastReply);
                    using var consumption = await ControlAsync("child-consumed", new
                    {
                        expected = acceptances.Values.Select(accepted => new
                            { accepted = AcceptanceBody(accepted), call_id = starts[accepted.RunId].Id,
                                source_operation = starts[accepted.RunId].Name }),
                        parent_boundary_ns = output.ObservedNs,
                        observed_calls = calls.Select(CallBody)
                    });
                    deliveryObservations = consumption.RootElement.Clone();
                }
            }
        }

        async Task WriteReceiptAsync(string status, string? error)
        {
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
            {
                status, session_id = sessionId, prompt_nonce = input.Nonce, observer_mode = input.Mode,
                initial_prompt_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.InitialPrompt))).ToLowerInvariant(),
                accepted_run = protocol?.Acceptance is { } accepted ? AcceptanceBody(accepted) : null,
                accepted_runs = acceptances.Values.Select(AcceptanceBody),
                completed_turns = protocol?.CompletedTurns, user_inputs = userInputs,
                first_turn_ns = firstTurnNs, second_turn_ns = secondTurnNs, release_ns = releaseNs,
                last_reply = protocol?.LastReply, all_replies = replies, delivery_observations = deliveryObservations,
                status_bodies = statusBodies, error,
                calls = calls.Select(CallBody),
                limit = "Post-commit diagnostics, actual provider history, and files require the separate Python oracle."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal static JsonElement? ParseControlStatus(string result)
    {
        // The DTO omits the internal correction category. Match only the canonical cycle message and presenter action.
        const string cycleCorrection = "Netclaw stopped this tool call because it would continue a repeated action-and-outcome cycle. "
            + "The same action completed twice without a changed result. This call did not execute.\n"
            + "Next action: choose a different action, load a missing tool, or finish the task.";
        if (result == cycleCorrection)
            return null;
        using var status = JsonDocument.Parse(result);
        return status.RootElement.Clone();
    }

    private static object AcceptanceBody(AcceptedRun accepted) => new
        { run_id = accepted.RunId, scope_id = accepted.ScopeId, state = "Accepted", control_tool = "check_agent_run" };
    private static long MonotonicNs() => (long)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Validate(ObserverInput input)
    {
        foreach (var value in new[] { input.DaemonEndpoint, input.FixtureEndpoint, input.Nonce,
                     input.InitialPrompt, input.EvidenceDirectory })
            Require(!string.IsNullOrWhiteSpace(value), "An observer input field is empty.");
        foreach (var endpoint in new[] { input.DaemonEndpoint, input.FixtureEndpoint })
            Require(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback,
                "The observer requires task-owned loopback endpoints.");
        Require(input.TimeoutSeconds > 0 && input.Mode is "held" or "cancel" or "collect" or "turn", "The observer mode or deadline is invalid.");
        Require(input.OutputFormat is "text" or "json", "The observer output format is invalid.");
        if (input.Mode is "held" or "cancel")
            Require(!string.IsNullOrWhiteSpace(input.ProbeMarker)
                && input.ProbePrompt.Contains(input.ProbeMarker, StringComparison.Ordinal), "The probe lacks its reply marker.");
    }

    internal static object CallBody(ObservedCall call) => new
    {
        id = call.Id, occurrence = call.Occurrence, observed_ns = call.ObservedNs,
        name = call.Name, arguments = call.Arguments, turn = call.Turn, success = call.Success,
        failure_code = call.FailureCode, result = call.Result
    };

    internal sealed class ObservedCall(string id, string name, JsonElement arguments, int turn, long observedNs, int occurrence)
    {
        public string Id { get; } = id;
        public int Turn { get; } = turn;
        public long ObservedNs { get; } = observedNs;
        public int Occurrence { get; } = occurrence;
        public string Name { get; } = name;
        public JsonElement Arguments { get; } = arguments;
        public string Result { get; set; } = "";
        public bool Success { get; set; }
        public string? FailureCode { get; private set; }

        public JsonElement? CompleteResult(SessionOutputDto output)
        {
            Require(output.Type == SessionOutputTypes.ToolResult && output.CallId == Id && output.ToolName == Name,
                "The result differs from its attributed call.");
            Result = output.Result ?? "";
            FailureCode = output.ToolFailureCode;
            Success = FailureCode is null;
            if (Name != "check_agent_run" || !Success)
                return null;
            var status = ParseControlStatus(Result);
            Success = status is not null;
            return status;
        }
    }
}
