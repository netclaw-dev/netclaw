// -----------------------------------------------------------------------
// <copyright file="TurnStateTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns tool-loop control flow decisions: exact cycle detection,
/// empty-response retry logic, and force-no-tools state. The actor asks
/// "what should I do?" and the tracker answers based on accumulated state.
/// </summary>
internal sealed class TurnStateTracker
{
    private const int MaxPreToolEmptyRetries = 5;
    private const int MaxPostToolEmptyRetries = 8;

    // Nudge for a thinking-only response: the model emitted reasoning but no
    // final answer. Generic across providers — no provider-specific payload.
    private const string ThinkingOnlyNudge =
        "Your last response contained only reasoning and no reply to the user. "
        + "Stop thinking and write your answer now as a normal assistant message.";

    // A length-truncated response was cut off mid-output by the provider's token
    // ceiling — it did not refuse to answer, so the "stop thinking" scold is
    // counterproductive. Ask for brevity so the next attempt fits the budget.
    private const string TruncatedResponseNudge =
        "Your previous response was cut off before you finished — it reached the output length limit. "
        + "Give your final answer directly now and keep any reasoning brief.";

    private const string PreToolEmptyNudge =
        "Your previous response was empty. If you need MCP capabilities, call search_tools(\"servers\") to pick a server "
        + "(for example browser, memory, or email), then call search_tools(\"<intent>\", server: \"<server_name>\") to load tools. "
        + "MCP tools are not directly callable until loaded via search_tools.";

    private const string PostToolEmptyNudge =
        "You received tool results but did not respond. "
        + "Continue working or produce your final response.";

    private const string EmptyResponseFailureMessage =
        "I didn't manage to produce a reply. Please try rephrasing or sending your request again.";

    // Keep only six completed action/outcome hash pairs, never raw arguments or results.
    // ObserveCompleted evicts the oldest excess entry after each append.
    // New user input clears this history; compaction and empty replies preserve it.
    private readonly List<CompletedToolCycleIteration> _completedToolCycles = [];
    private ToolActionSignature? _lastBlockedAction;
    private readonly Dictionary<ToolRecurrenceKey, ToolRecurrenceEpisode> _episodes = [];
    private readonly LinkedList<ToolRecurrenceKey> _coldKeys = [];
    internal const int MaximumColdKeys = 256;

    public int ToolCallCount { get; private set; }
    public int ToolIterationCount { get; private set; }
    public bool ForceNoToolsActive { get; set; }

    private int _postToolEmptyResponseCount;
    private int _preToolEmptyResponseCount;

    /// <summary>
    /// Reset all per-turn state. Called at the start of each user turn.
    /// </summary>
    public void ResetForNewTurn()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
        _postToolEmptyResponseCount = 0;
        _preToolEmptyResponseCount = 0;
        ForceNoToolsActive = false;
        _completedToolCycles.Clear();
        _lastBlockedAction = null;
        _episodes.Clear();
        _coldKeys.Clear();
    }

    /// <summary>
    /// Partial reset for mid-turn buffer drain: clears tool counters
    /// but preserves empty-response and force-no-tools state.
    /// </summary>
    public void ResetToolCounters()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
    }

    /// <summary>
    /// Reset empty-response guards when the model starts doing tool work.
    /// Called when a new tool call batch is initiated — the model is clearly
    /// not stuck, so retry counters reset.
    /// </summary>
    public void ResetEmptyResponseGuards()
    {
        _postToolEmptyResponseCount = 0;
        _preToolEmptyResponseCount = 0;
    }

    public int CompletedCycleHistoryCount => _completedToolCycles.Count;

    /// <summary>
    /// Detect two equal, adjacent copies of a completed cycle before its next action executes.
    /// </summary>
    /// <remarks>
    /// A cycle contains one to three batches. Each completed batch includes its action and outcome hashes.
    /// For example, A B A B followed by candidate A gets one correction if both completed copies match.
    /// A changed result in either copy prevents that match. The candidate's future result is not known.
    /// A repeat of the last corrected action stops the turn unless a different action completes first.
    /// Synthetic corrections do not enter completed history. New user input resets both intervention states.
    /// </remarks>
    public ToolCycleDecision EvaluateAdjacentBeforeDispatch(ToolActionSignature candidate)
    {
        if (_lastBlockedAction == candidate)
            return new ToolCycleDecision(ToolCycleDecisionKind.Stop);

        for (var period = 1; period <= ToolCycleSignatureFactory.MaximumPeriod; period++)
        {
            var required = period * 2;
            if (_completedToolCycles.Count < required)
                continue;

            var start = _completedToolCycles.Count - required;
            if (!HasEqualCycleCopies(start, period)
                || candidate != _completedToolCycles[start].Action)
            {
                continue;
            }

            _lastBlockedAction = candidate;
            return new ToolCycleDecision(
                ToolCycleDecisionKind.Correct,
                Period: period,
                Repetitions: 2);
        }

        return new ToolCycleDecision(ToolCycleDecisionKind.Execute);
    }

    public ToolCycleBatchDecision EvaluateBeforeDispatch(PreparedToolCycleBatch batch)
    {
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var exempt = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in batch.Calls.GroupBy(static call => new ToolRecurrenceKey(call.ToolName, call.ArgumentsHash)))
        {
            if (!_episodes.TryGetValue(group.Key, out var episode))
                continue;

            if (episode.PendingJob)
            {
                foreach (var call in group)
                    exempt.Add(call.CallId.Value);
                continue;
            }

            if (episode.Corrected)
                return new ToolCycleBatchDecision(ToolCycleDecisionKind.Stop, new HashSet<string>(StringComparer.Ordinal), exempt);

            if (episode.EqualRounds >= 2)
            {
                foreach (var call in group)
                    refused.Add(call.CallId.Value);
            }
        }

        if (exempt.Count == 0)
        {
            var adjacent = EvaluateAdjacentBeforeDispatch(batch.Action);
            if (adjacent.Kind == ToolCycleDecisionKind.Stop)
                return new ToolCycleBatchDecision(ToolCycleDecisionKind.Stop, new HashSet<string>(StringComparer.Ordinal), exempt);
            if (adjacent.Kind == ToolCycleDecisionKind.Correct)
            {
                foreach (var call in batch.Calls)
                    refused.Add(call.CallId.Value);
            }
        }

        foreach (var group in batch.Calls.Where(call => refused.Contains(call.CallId.Value))
                     .GroupBy(static call => new ToolRecurrenceKey(call.ToolName, call.ArgumentsHash)))
        {
            if (_episodes.TryGetValue(group.Key, out var episode))
                _episodes[group.Key] = episode with { Corrected = true };
        }

        return new ToolCycleBatchDecision(refused.Count == 0 ? ToolCycleDecisionKind.Execute : ToolCycleDecisionKind.Correct,
            refused, exempt);
    }

    private void ObserveGroup(CompletedToolRecurrenceGroup group)
    {
        _episodes.TryGetValue(group.Key, out var previous);
        var equal = previous is not null && previous.Outcome == group.Outcome;
        var next = new ToolRecurrenceEpisode(group.Outcome,
            group.PendingJob ? 0 : equal ? Math.Min(2, previous!.EqualRounds + 1) : 1,
            equal && previous!.Corrected, group.PendingJob);
        _episodes[group.Key] = next;
        _coldKeys.Remove(group.Key);
        if (next.EqualRounds < 2 && !next.Corrected)
            _coldKeys.AddLast(group.Key);
        while (_coldKeys.Count > MaximumColdKeys)
        {
            var evicted = _coldKeys.First!.Value;
            _coldKeys.RemoveFirst();
            _episodes.Remove(evicted);
        }
    }

    internal ToolLoopCheckpoint CaptureCheckpoint(string taskId)
        => new()
        {
            TaskId = taskId,
            Entries = _episodes.Select(pair => new ToolLoopEntry
            {
                ToolName = pair.Key.ToolName.Value, ArgumentsHash = pair.Key.ArgumentsHash,
                OutcomeHash = pair.Value.Outcome, EqualRounds = pair.Value.EqualRounds,
                Corrected = pair.Value.Corrected, PendingJob = pair.Value.PendingJob
            }).ToArray(),
            ColdKeys = _coldKeys.Select(key => new ToolLoopKey(key.ToolName.Value, key.ArgumentsHash)).ToArray(),
            AdjacentHistory = _completedToolCycles.Select(item => new ToolLoopAdjacent(item.Action.Value, item.OutcomeValue)).ToArray(),
            LastBlockedAction = _lastBlockedAction?.Value
        };

    internal ToolLoopDelta CaptureDelta(string taskId, ToolLoopCheckpoint previous)
    {
        var current = CaptureCheckpoint(taskId);
        var reset = previous.TaskId != taskId;
        var old = previous.Entries.ToDictionary(entry => new ToolLoopKey(entry.ToolName, entry.ArgumentsHash));
        var entries = current.Entries.ToDictionary(entry => new ToolLoopKey(entry.ToolName, entry.ArgumentsHash));
        return new ToolLoopDelta
        {
            TaskId = taskId, Reset = reset,
            Upserts = entries.Where(pair => reset || !old.TryGetValue(pair.Key, out var value) || pair.Value != value)
                .Select(pair => pair.Value).ToArray(),
            RemovedKeys = reset ? [] : old.Keys.Where(key => !entries.ContainsKey(key)).ToArray(),
            ColdKeys = current.ColdKeys, AdjacentHistory = current.AdjacentHistory,
            LastBlockedAction = current.LastBlockedAction
        };
    }

    internal static ToolLoopCheckpoint ApplyDelta(ToolLoopCheckpoint previous, ToolLoopDelta delta)
    {
        var entries = delta.Reset ? new Dictionary<ToolLoopKey, ToolLoopEntry>()
            : previous.Entries.ToDictionary(entry => new ToolLoopKey(entry.ToolName, entry.ArgumentsHash));
        foreach (var key in delta.RemovedKeys)
            entries.Remove(key);
        foreach (var entry in delta.Upserts)
            entries[new ToolLoopKey(entry.ToolName, entry.ArgumentsHash)] = entry;
        return new ToolLoopCheckpoint
        {
            TaskId = delta.TaskId, Entries = entries.Values.ToArray(), ColdKeys = delta.ColdKeys,
            AdjacentHistory = delta.AdjacentHistory, LastBlockedAction = delta.LastBlockedAction
        };
    }

    internal void RestoreCheckpoint(ToolLoopCheckpoint checkpoint)
    {
        ResetForNewTurn();
        foreach (var entry in checkpoint.Entries)
            _episodes.Add(new ToolRecurrenceKey(new ToolName(entry.ToolName), entry.ArgumentsHash),
                new ToolRecurrenceEpisode(entry.OutcomeHash, entry.EqualRounds, entry.Corrected, entry.PendingJob));
        foreach (var key in checkpoint.ColdKeys)
            _coldKeys.AddLast(new ToolRecurrenceKey(new ToolName(key.ToolName), key.ArgumentsHash));
        foreach (var entry in checkpoint.AdjacentHistory)
            _completedToolCycles.Add(new CompletedToolCycleIteration(new ToolActionSignature(entry.ActionHash), entry.OutcomeHash));
        _lastBlockedAction = checkpoint.LastBlockedAction is { } blocked ? new ToolActionSignature(blocked) : null;
    }

    public void ObserveCompleted(CompletedToolCycleIteration iteration)
    {
        foreach (var group in iteration.Groups)
            ObserveGroup(group);
        if (!iteration.RecordAdjacent)
            return;
        _completedToolCycles.Add(iteration);
        if (_completedToolCycles.Count > ToolCycleSignatureFactory.MaximumHistory)
            _completedToolCycles.RemoveAt(0);

        if (_lastBlockedAction is { } blocked && iteration.Action != blocked)
            _lastBlockedAction = null;
    }

    private bool HasEqualCycleCopies(int start, int period)
    {
        for (var offset = 0; offset < period; offset++)
        {
            if (_completedToolCycles[start + offset].Action != _completedToolCycles[start + period + offset].Action
                || _completedToolCycles[start + offset].OutcomeValue != _completedToolCycles[start + period + offset].OutcomeValue)
            {
                return false;
            }
        }

        return true;
    }

    public void RecordToolCompletion(int resultCount)
    {
        ToolCallCount = (int)Math.Min(int.MaxValue, (long)ToolCallCount + resultCount);
        ToolIterationCount = (int)Math.Min(int.MaxValue, (long)ToolIterationCount + 1);
    }

    // ── Empty response decisions ──

    /// <summary>
    /// The LLM produced no reply text and no tool calls. Determine what the
    /// actor should do. Expects <paramref name="kind"/> to be
    /// <see cref="LlmResponseKind.ThinkingOnly"/> or
    /// <see cref="LlmResponseKind.Empty"/>.
    /// <para>
    /// Consecutive counters track empty responses in the pre-tool and post-tool
    /// phases independently. They are cleared by
    /// <see cref="ResetEmptyResponseGuards"/> when the model initiates a tool
    /// batch — legitimate thinking-only responses interleaved with tool work do
    /// not accumulate toward the failure threshold, so reasoning models are not
    /// penalised for their normal workflow.
    /// </para>
    /// <para>
    /// <paramref name="truncated"/> is true when the provider reported a
    /// length/token-limit finish reason. Such a response was cut off mid-output,
    /// not refused, so it gets a brevity nudge rather than the "stop thinking"
    /// scold.
    /// </para>
    /// </summary>
    public EmptyResponseAction EvaluateEmptyResponse(
        LlmResponseKind kind,
        bool truncated)
    {
        // Pre-tool: LLM hasn't done any tool work yet
        if (ToolIterationCount == 0)
        {
            _preToolEmptyResponseCount++;
            if (_preToolEmptyResponseCount > MaxPreToolEmptyRetries)
                return new EmptyResponseAction.Fail(
                    EmptyResponseFailureMessage,
                    new InvalidOperationException("LLM produced repeated empty responses before any tool execution."));

            return new EmptyResponseAction.Retry(SelectNudge(kind, truncated, preTool: true));
        }

        // Post-tool: nudge the model to produce its final reply
        _postToolEmptyResponseCount++;
        if (_postToolEmptyResponseCount > MaxPostToolEmptyRetries)
            return new EmptyResponseAction.Fail(
                EmptyResponseFailureMessage,
                new InvalidOperationException("LLM produced repeated empty responses after tool execution."));

        return new EmptyResponseAction.Retry(SelectNudge(kind, truncated, preTool: false));
    }

    private static string SelectNudge(LlmResponseKind kind, bool truncated, bool preTool)
    {
        if (truncated)
            return TruncatedResponseNudge;
        if (kind == LlmResponseKind.ThinkingOnly)
            return ThinkingOnlyNudge;
        return preTool ? PreToolEmptyNudge : PostToolEmptyNudge;
    }
}

internal sealed record ToolActionSignature(string Value);

internal sealed record CompletedToolCycleIteration(
    ToolActionSignature Action,
    string OutcomeValue)
{
    public IReadOnlyList<CompletedToolRecurrenceGroup> Groups { get; init; } = [];
    public bool RecordAdjacent { get; init; } = true;
}

internal readonly record struct ToolRecurrenceKey(ToolName ToolName, string ArgumentsHash);
internal sealed record ToolRecurrenceEpisode(string Outcome, int EqualRounds, bool Corrected, bool PendingJob);
internal sealed record CompletedToolRecurrenceGroup(ToolRecurrenceKey Key, string Outcome, bool PendingJob);
internal sealed record ToolCycleBatchDecision(ToolCycleDecisionKind Kind,
    IReadOnlySet<string> RefusedCallIds, IReadOnlySet<string> ExemptCallIds);

internal sealed record PreparedToolCycleCall(
    ToolCallId CallId,
    ToolName ToolName,
    string ArgumentsHash)
{
    public bool AllowsPendingJob { get; init; }
}

internal sealed record PreparedToolCycleBatch
{
    public PreparedToolCycleBatch(
        ToolActionSignature action,
        IEnumerable<PreparedToolCycleCall> calls)
    {
        Action = action;
        Calls = Array.AsReadOnly(calls.ToArray());
    }

    public ToolActionSignature Action { get; }

    public IReadOnlyList<PreparedToolCycleCall> Calls { get; }
}

internal readonly record struct ToolCycleResult(
    ToolInvocationOutcomeCategory Category,
    string ModelVisibleText)
{
    public bool PendingJob { get; init; }
}

internal enum ToolCycleDecisionKind
{
    Execute,
    Correct,
    Stop
}

internal readonly record struct ToolCycleDecision(
    ToolCycleDecisionKind Kind,
    int Period = 0,
    int Repetitions = 0);

internal static class ToolCycleMessages
{
    public const string Correction =
        "Netclaw stopped this tool call because it would continue a repeated action-and-outcome cycle. "
        + "The same action completed twice without a changed result. This call did not execute.";

    public const string MissingReceipt =
        "Netclaw stopped this task because a completed tool result lacked its required execution receipt. "
        + "The result is partial. Earlier recorded results remain available; unverified effects are unknown.";

    public const string Final =
        "Netclaw stopped this run after you repeated a tool batch that the cycle guard already blocked. "
        + "The result is partial. The last refused operation did not execute. "
        + "Earlier tool results remain available in the session.";
}

internal static class ToolCycleSignatureFactory
{
    internal const int MaximumPeriod = 3;
    // Two complete copies of the longest supported cycle require six entries.
    internal const int MaximumHistory = MaximumPeriod * 2;

    public static PreparedToolCycleBatch Prepare(
        IReadOnlyList<FunctionCallContent> calls,
        IToolExecutor executor)
    {
        var prepared = calls.Select(call =>
        {
            var rejection = executor.ValidateToolCall(call);
            var (_, cleaned) = executor.PrepareToolCall(call);
            // Only valid metadata is irrelevant to execution identity. A repair
            // must differ from a rejected call, even after a cycle correction.
            var arguments = rejection is null ? cleaned.Arguments : call.Arguments;
            return new PreparedToolCycleCall(
                new ToolCallId(call.CallId),
                new ToolName(cleaned.Name),
                HashFields([
                    rejection is null ? "accepted" : "rejected",
                    rejection?.DenyReason ?? string.Empty,
                    HashCanonicalArguments(arguments)]))
            {
                AllowsPendingJob = rejection is null && cleaned.Name == "check_background_job"
                    && (cleaned.Arguments is null || cleaned.Arguments.All(argument =>
                        !argument.Key.Equals("cancel", StringComparison.OrdinalIgnoreCase)
                        || argument.Value is false or JsonElement { ValueKind: JsonValueKind.False }))
            };
        }).ToArray();

        var action = new ToolActionSignature(HashFields(
            OrderedCalls(prepared).SelectMany(static call =>
                new[] { call.ToolName.Value, call.ArgumentsHash })));
        return new PreparedToolCycleBatch(action, prepared);
    }

    public static CompletedToolCycleIteration Complete(
        PreparedToolCycleBatch batch,
        IReadOnlyDictionary<string, ToolCycleResult> results)
    {
        if (results.Count != batch.Calls.Count)
            throw new InvalidOperationException("A completed cycle iteration requires one result for each call.");

        var outcomes = batch.Calls.Select(call =>
        {
            if (!results.TryGetValue(call.CallId.Value, out var result))
                throw new InvalidOperationException("A completed cycle iteration has an unmatched tool result.");

            return new ToolCycleOutcome(
                call.ToolName,
                call.ArgumentsHash,
                result.Category,
                HashFields([result.ModelVisibleText]));
        }).OrderBy(static outcome => outcome.ToolName.Value, StringComparer.Ordinal)
          .ThenBy(static outcome => outcome.ArgumentsHash, StringComparer.Ordinal)
          .ThenBy(static outcome => outcome.Category)
          .ThenBy(static outcome => outcome.ResultHash, StringComparer.Ordinal);

        var outcomeHash = HashFields(outcomes.SelectMany(static outcome => new[]
        {
            outcome.ToolName.Value,
            outcome.ArgumentsHash,
            outcome.Category.ToString(),
            outcome.ResultHash
        }));
        var groups = batch.Calls.GroupBy(static call => new ToolRecurrenceKey(call.ToolName, call.ArgumentsHash))
            .Select(group => new CompletedToolRecurrenceGroup(group.Key,
                HashFields(group.Select(call => results[call.CallId.Value])
                    .Select(result => HashFields([result.Category.ToString(), HashFields([result.ModelVisibleText])]))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                group.All(call => call.AllowsPendingJob && results[call.CallId.Value].PendingJob)))
            .ToArray();
        return new CompletedToolCycleIteration(batch.Action, outcomeHash)
        {
            Groups = groups,
            RecordAdjacent = !groups.Any(static group => group.PendingJob)
        };
    }

    internal static ToolLoopObservation CreateObservation(string callId, ToolInvocationReceipt? receipt,
        string modelVisibleText, bool synthetic)
        => new()
        {
            CallId = callId, Category = receipt is null ? -1 : (int)receipt.Category,
            ResultHash = HashFields([modelVisibleText]), Synthetic = synthetic,
            MissingReceipt = receipt is null, PendingJob = receipt is ToolInvocationReceipt.PendingBackgroundJob
        };

    internal static CompletedToolCycleIteration CompleteEvidence(ToolLoopAdmission admission,
        IReadOnlyList<ToolLoopObservation> observations)
    {
        var paired = observations.ToDictionary(item => item.CallId, StringComparer.Ordinal);
        var actual = admission.Calls.Where(call => !admission.RefusedCallIds.Contains(call.CallId, StringComparer.Ordinal)).ToArray();
        var groups = actual.GroupBy(call => new ToolRecurrenceKey(new ToolName(call.ToolName), call.ArgumentsHash))
            .Select(group => new CompletedToolRecurrenceGroup(group.Key,
                HashFields(group.Select(call => paired[call.CallId])
                    .Select(item => HashFields([((ToolInvocationOutcomeCategory)item.Category).ToString(), item.ResultHash]))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                group.All(call => call.AllowsPendingJob && paired[call.CallId].PendingJob))).ToArray();
        var ordered = actual.OrderBy(call => call.ToolName, StringComparer.Ordinal)
            .ThenBy(call => call.ArgumentsHash, StringComparer.Ordinal)
            .ThenBy(call => paired[call.CallId].Category)
            .ThenBy(call => paired[call.CallId].ResultHash, StringComparer.Ordinal);
        return new CompletedToolCycleIteration(new ToolActionSignature(admission.ActionHash),
            HashFields(ordered.SelectMany(call => new[] { call.ToolName, call.ArgumentsHash,
                ((ToolInvocationOutcomeCategory)paired[call.CallId].Category).ToString(), paired[call.CallId].ResultHash })))
        {
            Groups = groups,
            RecordAdjacent = admission.RefusedCallIds.Count == 0 && !groups.Any(group => group.PendingJob)
        };
    }

    private static IOrderedEnumerable<PreparedToolCycleCall> OrderedCalls(
        IEnumerable<PreparedToolCycleCall> calls)
        => calls.OrderBy(static call => call.ToolName.Value, StringComparer.Ordinal)
            .ThenBy(static call => call.ArgumentsHash, StringComparer.Ordinal);

    private static string HashCanonicalArguments(IDictionary<string, object?>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            if (arguments is null or { Count: 0 })
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                var element = JsonSerializer.SerializeToElement(arguments);
                WriteCanonical(writer, element);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    // This JSON supplies hash input, not a provider payload or a persistence format.
    // Sort nested object properties so dictionary insertion order cannot hide an equal action.
    // Preserve array order, duplicate values, scalar types, and numeric text.
    // Utf8JsonWriter supplies JSON syntax and escapes strings; only property order is custom.
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException("Tool arguments contain an unsupported JSON value.");
        }
    }

    private static string HashFields(IEnumerable<string> fields)
    {
        // Length prefixes keep ["ab", "c"] distinct from ["a", "bc"].
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private readonly record struct ToolCycleOutcome(
        ToolName ToolName,
        string ArgumentsHash,
        ToolInvocationOutcomeCategory Category,
        string ResultHash);
}

// ── Result types ──

/// <summary>Result of <see cref="TurnStateTracker.EvaluateEmptyResponse"/>.</summary>
internal abstract record EmptyResponseAction
{
    /// <summary>Retry the LLM call with the given nudge text.</summary>
    internal sealed record Retry(string NudgeText) : EmptyResponseAction;

    /// <summary>Fail the turn with the given error message and cause.</summary>
    internal sealed record Fail(string ErrorMessage, Exception Cause) : EmptyResponseAction;
}
