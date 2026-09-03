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

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns tool-loop control flow decisions: tool budgets, exact cycle detection,
/// empty-response retry logic, and force-no-tools state. The actor asks
/// "what should I do?" and the tracker answers based on accumulated state.
/// </summary>
internal sealed class TurnStateTracker
{
    private const int MaxPreToolEmptyRetries = 5;
    private const int MaxPostToolEmptyRetries = 8;
    private const double BudgetNudgeRatio = 0.75;

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

    private readonly List<CompletedToolCycleIteration> _completedToolCycles = [];
    private ToolActionSignature? _lastBlockedAction;

    public int ToolCallCount { get; private set; }
    public int ToolIterationCount { get; private set; }
    public bool ForceNoToolsActive { get; set; }

    private bool _budgetNudgeSent;
    private int _postToolEmptyResponseCount;
    private int _preToolEmptyResponseCount;

    /// <summary>
    /// Reset all per-turn state. Called at the start of each user turn.
    /// </summary>
    public void ResetForNewTurn()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
        _budgetNudgeSent = false;
        _postToolEmptyResponseCount = 0;
        _preToolEmptyResponseCount = 0;
        ForceNoToolsActive = false;
        _completedToolCycles.Clear();
        _lastBlockedAction = null;
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

    public ToolCycleDecision EvaluateBeforeDispatch(ToolActionSignature candidate)
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

    public void ObserveCompleted(CompletedToolCycleIteration iteration)
    {
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
            if (_completedToolCycles[start + offset]
                != _completedToolCycles[start + period + offset])
            {
                return false;
            }
        }

        return true;
    }

    // ── Tool budget decisions ──

    /// <summary>
    /// Record completed tool results and determine what the actor should do next.
    /// Call after tool execution completes with the number of results in the batch.
    /// Enforcement is iteration-based: one completed LLM-to-tools round increments
    /// <see cref="ToolIterationCount"/> by 1 regardless of how many tool calls
    /// were issued in parallel. <see cref="ToolCallCount"/> is retained for
    /// telemetry only.
    /// </summary>
    public ToolBudgetStatus RecordToolCompletion(int resultCount, int maxToolIterationsPerTurn)
    {
        ToolCallCount += resultCount;
        ToolIterationCount++;

        if (ToolIterationCount >= maxToolIterationsPerTurn)
        {
            return new ToolBudgetStatus.Exhausted(
                $"You have reached the tool iteration limit for this turn. "
                + "Do NOT request any more tools. "
                + "Produce a concise final executive summary based only on the information gathered so far. "
                + "Use this format: Summary, Completed, Partial or Unknown, Caveats, Useful Evidence. "
                + "Clearly state that the result is partial when work remains or evidence is incomplete.");
        }

        var budgetThreshold = (int)(maxToolIterationsPerTurn * BudgetNudgeRatio);
        if (ToolIterationCount >= budgetThreshold && !_budgetNudgeSent)
        {
            _budgetNudgeSent = true;
            var remaining = maxToolIterationsPerTurn - ToolIterationCount;
            return new ToolBudgetStatus.NudgeNeeded(
                remaining,
                $"You have used {ToolIterationCount} of {maxToolIterationsPerTurn} tool iterations for this turn. "
                + $"You have approximately {remaining} iterations remaining. "
                + "Start wrapping up your tool usage and prepare to produce your final response.");
        }

        return ToolBudgetStatus.Ok.Instance;
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
    string OutcomeValue);

internal sealed record PreparedToolCycleCall(
    string CallId,
    string ToolName,
    string ArgumentsHash);

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
    string ModelVisibleText);

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
        "Netclaw stopped this tool batch because it would continue a repeated action-and-outcome cycle. "
        + "The same sequence completed twice without a changed result. No requested call executed.";

    public const string Final =
        "Netclaw stopped this run after you repeated a tool batch that the cycle guard already blocked. "
        + "Report completed work, incomplete work, and the last repeated result. "
        + "Do not claim that the blocked operation succeeded.";
}

internal static class ToolCycleSignatureFactory
{
    internal const int MaximumPeriod = 3;
    internal const int MaximumHistory = MaximumPeriod * 2;

    public static PreparedToolCycleBatch Prepare(
        IReadOnlyList<FunctionCallContent> calls,
        IToolExecutor executor)
    {
        var prepared = calls.Select(call =>
        {
            var (_, cleaned) = executor.PrepareToolCall(call);
            return new PreparedToolCycleCall(
                call.CallId,
                cleaned.Name,
                HashCanonicalArguments(cleaned.Arguments));
        }).ToArray();

        var action = new ToolActionSignature(HashFields(
            OrderedCalls(prepared).SelectMany(static call =>
                new[] { call.ToolName, call.ArgumentsHash })));
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
            if (!results.TryGetValue(call.CallId, out var result))
                throw new InvalidOperationException("A completed cycle iteration has an unmatched tool result.");

            return new ToolCycleOutcome(
                call.ToolName,
                call.ArgumentsHash,
                result.Category,
                HashFields([result.ModelVisibleText]));
        }).OrderBy(static outcome => outcome.ToolName, StringComparer.Ordinal)
          .ThenBy(static outcome => outcome.ArgumentsHash, StringComparer.Ordinal)
          .ThenBy(static outcome => outcome.Category)
          .ThenBy(static outcome => outcome.ResultHash, StringComparer.Ordinal);

        var outcomeHash = HashFields(outcomes.SelectMany(static outcome => new[]
        {
            outcome.ToolName,
            outcome.ArgumentsHash,
            outcome.Category.ToString(),
            outcome.ResultHash
        }));
        return new CompletedToolCycleIteration(batch.Action, outcomeHash);
    }

    private static IOrderedEnumerable<PreparedToolCycleCall> OrderedCalls(
        IEnumerable<PreparedToolCycleCall> calls)
        => calls.OrderBy(static call => call.ToolName, StringComparer.Ordinal)
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
        string ToolName,
        string ArgumentsHash,
        ToolInvocationOutcomeCategory Category,
        string ResultHash);
}

// ── Result types ──

/// <summary>Result of <see cref="TurnStateTracker.RecordToolCompletion"/>.</summary>
internal abstract record ToolBudgetStatus
{
    /// <summary>Under budget, continue normally.</summary>
    internal sealed record Ok : ToolBudgetStatus
    {
        public static readonly Ok Instance = new();
    }

    /// <summary>Approaching budget limit — inject a nudge.</summary>
    internal sealed record NudgeNeeded(int Remaining, string NudgeText) : ToolBudgetStatus;

    /// <summary>Budget exhausted — force text-only response.</summary>
    internal sealed record Exhausted(string NudgeText) : ToolBudgetStatus;
}

/// <summary>Result of <see cref="TurnStateTracker.EvaluateEmptyResponse"/>.</summary>
internal abstract record EmptyResponseAction
{
    /// <summary>Retry the LLM call with the given nudge text.</summary>
    internal sealed record Retry(string NudgeText) : EmptyResponseAction;

    /// <summary>Fail the turn with the given error message and cause.</summary>
    internal sealed record Fail(string ErrorMessage, Exception Cause) : EmptyResponseAction;
}
