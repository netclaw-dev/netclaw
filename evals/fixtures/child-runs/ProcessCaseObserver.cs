// -----------------------------------------------------------------------
// <copyright file="ProcessCaseObserver.cs" company="Petabridge, LLC">
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

internal static class ProcessCaseObserver
{
    internal const string ExpiredNotice = "That approval prompt has expired — the session moved on or restarted. "
        + "Please re-issue the request and I'll ask again if approval is needed.";

    internal static bool Supports(string mode) => mode is
        "process-routed" or "process-approval" or "process-approval-cancel" or "process-recovery";

    internal static async Task<int> RunAsync(ObserverInput input)
    {
        Require(Supports(input.Mode) && input.TimeoutSeconds > 0, "The process case or deadline is invalid.");
        foreach (var endpoint in new[] { input.DaemonEndpoint, input.FixtureEndpoint })
            Require(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback,
                "The process observer requires owned loopback endpoints.");
        Require(!string.IsNullOrWhiteSpace(input.Nonce) && !string.IsNullOrWhiteSpace(input.InitialPrompt)
            && !string.IsNullOrWhiteSpace(input.ProbeMarker) && input.ProbePrompt.Contains(input.ProbeMarker, StringComparison.Ordinal)
            && string.IsNullOrEmpty(input.SessionId), "The process trial requires one fresh session and an exact probe.");
        var root = input.EvidenceDirectory;
        Directory.CreateDirectory(root);
        var receiptPath = Path.Combine(root, "observer-receipt.json");
        Require(!File.Exists(receiptPath), "The process receipt already exists.");
        await using var events = NewLog("session-output.jsonl");
        await using var actions = NewLog("observer-actions.jsonl");
        await using var connections = NewLog("connection-events.jsonl");
        await using var client = new DaemonClient(input.DaemonEndpoint);
        var outputs = Channel.CreateUnbounded<(SessionOutput Output, long ObservedNs)>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false });
        using var subscription = client.SessionOutput.Subscribe(output => outputs.Writer.TryWrite((output, Now())));
        using var connectionSubscription = client.ConnectionEvents.Subscribe(value =>
        {
            lock (connections)
                connections.WriteLine(JsonSerializer.Serialize(new { observed_ns = Now(), connection = value }));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(input.TimeoutSeconds));
        using var http = new HttpClient();
        var token = deadline.Token;
        var channel = input.Mode is "process-approval" or "process-approval-cancel" ? ChannelType.Tui : ChannelType.Headless;
        var operation = input.Mode == "process-routed" ? "skill_load" : "spawn_agent";
        var calls = new List<SessionObserver.ObservedCall>();
        var pending = new Dictionary<string, SessionObserver.ObservedCall>(StringComparer.Ordinal);
        var replies = new List<string>();
        string sessionId = "";
        HeldChildProtocol? protocol = null;
        AcceptedRun? accepted = null;
        SessionObserver.ObservedCall? start = null;
        SessionOutputDto? prompt = null;
        var sequence = 0;
        var actionSequence = 0;
        var userInputs = 0;
        var promptSequence = 0;
        var joined = false;
        var bound = false;
        var expiredSequences = new List<int>();
        long firstTurnNs = 0, secondTurnNs = 0, releaseNs = 0;
        var consumption = JsonSerializer.SerializeToElement(new { complete = false, deliveries = Array.Empty<object>() });
        try
        {
            await client.ConnectAsync(token);
            sessionId = await client.CreateSessionAsync(channel, token);
            protocol = new HeldChildProtocol(sessionId);
            await Send(input.InitialPrompt);
            await ReadThrough(() => protocol.CompletedTurns == 1);
            Require(accepted is not null && start is not null && bound, "The initial reply lacks one bound accepted child.");
            using var held = await Control("child-wait", new { });
            if (input.Mode is "process-approval" or "process-approval-cancel")
            {
                await Release();
                await ReadThrough(() => prompt is not null);
                using var opened = await Control("approval-open", new { prompt_sequence = promptSequence, prompt });
                await Action("prompt_observed", new { prompt_sequence = promptSequence, call_id = prompt!.CallId });
                await Send(input.ProbePrompt.Replace("{{RUN_ID}}", accepted!.RunId, StringComparison.Ordinal));
                if (input.Mode == "process-approval")
                {
                    await ReadThrough(() => protocol.CompletedTurns == 2);
                    Require(protocol.LastReply.Contains(input.ProbeMarker, StringComparison.Ordinal), "The independent parent probe did not reply.");
                    await Answer("approval-answer-ready");
                    await ReadThrough(() => consumption.GetProperty("complete").GetBoolean());
                }
                else
                {
                    await ReadThrough(() => consumption.GetProperty("complete").GetBoolean() && expiredSequences.Count > 0);
                    var priorNotice = expiredSequences[^1];
                    await Action("cancellation_notice_consumed", new { notice_sequence = priorNotice });
                    await Answer("stale-answer-ready");
                    await ReadThrough(() => expiredSequences.Any(value => value > priorNotice));
                }
            }
            else
            {
                await Send(input.ProbePrompt.Replace("{{RUN_ID}}", accepted!.RunId, StringComparison.Ordinal));
                await ReadThrough(() => protocol.CompletedTurns == 2);
                Require(protocol.LastReply.Contains(input.ProbeMarker, StringComparison.Ordinal), "The independent parent probe did not reply.");
                if (input.Mode == "process-recovery")
                {
                    using var recovered = await Control("daemon-crash", new { session_id = sessionId, accepted = Body(accepted!) });
                    joined = false;
                    await Action("resume_started", new { session_id = sessionId });
                    var resumed = await client.ResumeSessionAsync(sessionId, channel, token);
                    Require(resumed == sessionId, "Recovery replaced the original session.");
                    await ReadThrough(() => joined);
                    await Action("resume_completed", new { session_id = sessionId });
                    using var released = await Control("recovery-release", new { session_id = sessionId, joined_sequence = sequence });
                }
                else
                    await Release();
                await ReadThrough(() => consumption.GetProperty("complete").GetBoolean());
            }
            await WriteReceipt("observed", null);
            Console.Write(replies.Last());
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await WriteReceipt("incomplete", error.ToString());
            throw;
        }

        StreamWriter NewLog(string filename) => new(new FileStream(Path.Combine(root, filename), FileMode.CreateNew, FileAccess.Write))
            { AutoFlush = true };

        async Task Send(string text)
        {
            userInputs++;
            await client.SendAsync(text, token);
        }

        async Task Release()
        {
            using var released = await Control("child-release", new { });
            releaseNs = Now();
        }

        async Task Action(string kind, object data) => await actions.WriteLineAsync(JsonSerializer.Serialize(new
            { sequence = ++actionSequence, observed_ns = Now(), kind, after_event_sequence = sequence, data }));

        async Task Answer(string barrier)
        {
            var current = prompt ?? throw new InvalidDataException("The approval prompt is absent.");
            var options = current.InteractionOptions ?? throw new InvalidDataException("The approval prompt lacks actual options.");
            var option = options.Single(value => value.Key.Value == ApprovalOptionKeys.ApproveOnce);
            using var ready = await Control(barrier, new { call_id = current.CallId, prompt_sequence = promptSequence });
            await Action("answer_started", new { call_id = current.CallId, selected_key = option.Key.Value, prompt_sequence = promptSequence });
            await client.RespondToInteractionAsync(current.CallId!, option.Key.Value, token);
            await Action("answer_completed", new { call_id = current.CallId, selected_key = option.Key.Value, prompt_sequence = promptSequence });
        }

        async Task<JsonDocument> Control(string action, object data)
        {
            using var content = JsonContent.Create(data);
            await content.LoadIntoBufferAsync(token);
            using var response = await http.PostAsync(input.FixtureEndpoint.TrimEnd('/') + "/control/" + action, content, token);
            var body = await response.Content.ReadAsStringAsync(token);
            Require(response.IsSuccessStatusCode, "The process fixture rejected the control: " + body);
            return JsonDocument.Parse(body);
        }

        async Task ReadThrough(Func<bool> complete)
        {
            while (!complete())
            {
                var item = await outputs.Reader.ReadAsync(token);
                var dto = SessionOutputDtoMapper.ToDto(item.Output);
                await events.WriteLineAsync(JsonSerializer.Serialize(new { sequence = ++sequence, observed_ns = item.ObservedNs, output = dto }));
                protocol!.Observe(dto);
                if (dto.Type == SessionOutputTypes.ToolCall)
                {
                    using var arguments = JsonDocument.Parse(dto.ArgumentsJson ?? "{}");
                    var call = new SessionObserver.ObservedCall(dto.CallId!, dto.ToolName!, arguments.RootElement.Clone(),
                        protocol.CompletedTurns + 1, item.ObservedNs, calls.Count + 1);
                    Require(pending.TryAdd(call.Id, call), "An unresolved process call ID repeats.");
                    calls.Add(call);
                }
                else if (dto.Type == SessionOutputTypes.ToolResult)
                {
                    Require(pending.Remove(dto.CallId!, out var call), "A process result lacks its pending occurrence.");
                    var status = call!.CompleteResult(dto);
                    if (call.Name == operation && call.Success)
                    {
                        Require(accepted is null, "The process trial accepted a second child.");
                        accepted = HeldChildProtocol.ParseAcceptance(call.Result);
                        start = call;
                    }
                    if (!bound && call.Name == "check_agent_run" && status is { } live)
                    {
                        Require(accepted is not null, "The status precedes child acceptance.");
                        using var binding = await Control("child-bind", new { accepted = Body(accepted!), status = live });
                        Require(binding.RootElement.GetProperty("binding").GetProperty("accepted").GetProperty("run_id").GetString() == accepted!.RunId,
                            "The process fixture bound a foreign child.");
                        bound = true;
                    }
                }
                else if (dto.Type == SessionOutputTypes.ToolInteraction)
                {
                    Require(prompt is null && dto.ToolName == "shell_execute", "The process trial received an extra or foreign approval prompt.");
                    prompt = dto;
                    promptSequence = sequence;
                }
                else if (dto.Type == SessionOutputTypes.SessionJoined)
                    joined = true;
                else if (dto.Type == SessionOutputTypes.Text && dto.Text == ExpiredNotice)
                    expiredSequences.Add(sequence);
                else if (dto.Type == SessionOutputTypes.TurnCompleted)
                {
                    if (protocol.CompletedTurns == 1) firstTurnNs = item.ObservedNs;
                    if (protocol.CompletedTurns == 2) secondTurnNs = item.ObservedNs;
                    replies.Add(protocol.LastReply);
                    using var value = await Control("child-consumed", new
                    {
                        expected = accepted is null ? Array.Empty<object>() : new object[]
                            { new { accepted = Body(accepted), call_id = start!.Id, source_operation = operation } },
                        parent_boundary_ns = item.ObservedNs, observed_calls = calls.Select(SessionObserver.CallBody)
                    });
                    consumption = value.RootElement.Clone();
                }
            }
        }

        async Task WriteReceipt(string status, string? error) => await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
        {
            status, error, session_id = sessionId, prompt_nonce = input.Nonce, observer_mode = input.Mode,
            initial_prompt_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.InitialPrompt))).ToLowerInvariant(),
            accepted_run = accepted is null ? null : Body(accepted), accepted_runs = accepted is null ? Array.Empty<object>() : new[] { Body(accepted) },
            completed_turns = protocol?.CompletedTurns, user_inputs = userInputs, first_turn_ns = firstTurnNs, second_turn_ns = secondTurnNs,
            release_ns = releaseNs, last_reply = replies.LastOrDefault(), all_replies = replies, delivery_observations = consumption,
            calls = calls.Select(SessionObserver.CallBody), prompt_sequence = promptSequence, expired_notice_sequences = expiredSequences,
            limit = "The separate process oracle must verify actual journal, provider, effects, and owner process evidence."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Body(AcceptedRun run) => new { run_id = run.RunId, scope_id = run.ScopeId, state = "Accepted", control_tool = "check_agent_run" };
    private static long Now() => (long)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
