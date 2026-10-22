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
/// Owns tool-loop control flow decisions: exact cycle detection,
/// and empty-response retry logic. The actor asks
/// "what should I do?" and the tracker answers based on accumulated state.
/// </summary>
internal sealed class TurnStateTracker
{
    private const int MaxPreToolEmptyRetries = 5;
    private const int MaxPostToolEmptyRetries = 8;

    /// <summary>
    /// The refused repeat of a turn that gets the escalated nudge. The next refused repeat ends the turn
    /// quietly (Aaron, 18 Oct 2026): the loop gets an end, but the person never sees a Netclaw error.
    /// </summary>
    internal const int EscalatedRefusedRepeat = 5;

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
    // One model response that repeats a refused call adds one. Only a new turn and new person input reset it
    // (ResetRefusedRepeatCount). ResetForNewTurn clears the refusals but not the count: a child result in the
    // middle of a turn and an automation input that joins an automation turn call it, and neither may extend
    // the loop. A different completed action does not reset the count.
    private int _refusedRepeatCount;

    public int ToolCallCount { get; private set; }
    public int ToolIterationCount { get; private set; }

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
        _completedToolCycles.Clear();
        _lastBlockedAction = null;
        _episodes.Clear();
    }

    /// <summary>
    /// Start a new refused repeat count. The turn owner calls it for a new turn and for person input that joins
    /// a running turn. See <see cref="EscalatedRefusedRepeat"/>.
    /// </summary>
    public void ResetRefusedRepeatCount() => _refusedRepeatCount = 0;

    /// <summary>
    /// Partial reset for mid-turn buffer drain: clears tool counters
    /// but preserves empty-response state.
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
    /// A repeat of the last corrected action gets the same correction again, marked as repeated, until a
    /// different action completes. The caller then adds the repeat nudge. The turn does not stop.
    /// Synthetic corrections do not enter completed history. New user input resets both intervention states.
    /// </remarks>
    public ToolCycleDecision EvaluateAdjacentBeforeDispatch(ToolActionSignature candidate)
    {
        if (_lastBlockedAction == candidate)
            return new ToolCycleDecision(ToolCycleDecisionKind.Correct, Repeated: true);

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

    /// <summary>
    /// Decide which calls of a batch dispatch. A refused call does not execute. A call that repeats a refused
    /// identity or a refused adjacent action is refused again and also marked as repeated. Other calls in the
    /// batch keep their own decision. A batch with a repeated call is one refused repeat of the turn:
    /// <see cref="ToolCycleBatchDecision.RefusedRepeat"/> tells the turn owner to add the nudge, to add the
    /// escalated nudge, or to end the turn.
    /// </summary>
    public ToolCycleBatchDecision EvaluateBeforeDispatch(PreparedToolCycleBatch batch)
    {
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
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
            {
                foreach (var call in group)
                {
                    refused.Add(call.CallId.Value);
                    repeated.Add(call.CallId.Value);
                }

                continue;
            }

            if (episode.EqualRounds >= 2)
            {
                foreach (var call in group)
                    refused.Add(call.CallId.Value);
            }
        }

        if (exempt.Count == 0)
        {
            var adjacent = EvaluateAdjacentBeforeDispatch(batch.Action);
            if (adjacent.Kind == ToolCycleDecisionKind.Correct)
            {
                foreach (var call in batch.Calls)
                {
                    refused.Add(call.CallId.Value);
                    if (adjacent.Repeated)
                        repeated.Add(call.CallId.Value);
                }
            }
        }

        foreach (var group in batch.Calls.Where(call => refused.Contains(call.CallId.Value))
                     .GroupBy(static call => new ToolRecurrenceKey(call.ToolName, call.ArgumentsHash)))
        {
            if (_episodes.TryGetValue(group.Key, out var episode))
                _episodes[group.Key] = episode with { Corrected = true };
        }

        return new ToolCycleBatchDecision(refused.Count == 0 ? ToolCycleDecisionKind.Execute : ToolCycleDecisionKind.Correct,
            refused, exempt)
        {
            RepeatedCallIds = repeated,
            RefusedRepeat = CountRefusedRepeat(repeated.Count > 0)
        };
    }

    private RefusedRepeatAction CountRefusedRepeat(bool repeats)
    {
        if (!repeats)
            return RefusedRepeatAction.None;

        // EndTurn ends the turn, so the count cannot grow past EscalatedRefusedRepeat + 1 in a live turn.
        _refusedRepeatCount = Math.Min(_refusedRepeatCount + 1, EscalatedRefusedRepeat + 1);
        return _refusedRepeatCount < EscalatedRefusedRepeat ? RefusedRepeatAction.Nudge
            : _refusedRepeatCount == EscalatedRefusedRepeat ? RefusedRepeatAction.EscalatedNudge
            : RefusedRepeatAction.EndTurn;
    }

    private void ObserveGroup(CompletedToolRecurrenceGroup group)
    {
        _episodes.TryGetValue(group.Key, out var previous);
        var equal = previous is not null && previous.Outcome == group.Outcome;
        var next = new ToolRecurrenceEpisode(group.Outcome,
            group.PendingJob ? 0 : equal ? Math.Min(2, previous!.EqualRounds + 1) : 1,
            equal && previous!.Corrected, group.PendingJob);
        _episodes[group.Key] = next;

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

    /// <summary>
    /// Copies the loop guard: the tool iteration and call counts, the completed action and outcome hashes, the last
    /// blocked action, the recurrence episodes, and the refused-repeat count. A parked child keeps this copy, so a
    /// park does not reset the loop guard (durable-child-consent-wait, design D2 and M10). The copy shares no
    /// collection with the tracker. The empty-response counters are not part of the loop guard.
    /// </summary>
    public TurnLoopGuardState ExportLoopGuard()
        => new(
            ToolIterationCount,
            ToolCallCount,
            _completedToolCycles.Select(static cycle => (cycle.Action, cycle.OutcomeValue)).ToArray(),
            _lastBlockedAction,
            new Dictionary<ToolRecurrenceKey, ToolRecurrenceEpisode>(_episodes),
            _refusedRepeatCount);

    /// <summary>
    /// Replaces the loop guard with an exported copy. The tracker then makes the same decisions as the tracker that
    /// exported the copy. The tracker shares no collection with the copy. The empty-response counters do not change.
    /// </summary>
    public void RestoreLoopGuard(TurnLoopGuardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.CompletedCycles);
        ArgumentNullException.ThrowIfNull(state.Episodes);
        if (state.CompletedCycles.Count > ToolCycleSignatureFactory.MaximumHistory)
            throw new ArgumentException(
                $"A loop guard holds at most {ToolCycleSignatureFactory.MaximumHistory} completed cycles.", nameof(state));

        ToolIterationCount = state.ToolIterationCount;
        ToolCallCount = state.ToolCallCount;
        // The history keeps only the cycles that ObserveCompleted recorded as adjacent. The adjacent check reads only
        // the action and the outcome of a stored cycle, never its groups, so the restored entries need no groups.
        _completedToolCycles.Clear();
        _completedToolCycles.AddRange(state.CompletedCycles.Select(static cycle =>
            new CompletedToolCycleIteration(cycle.Action, cycle.OutcomeValue)));
        _lastBlockedAction = state.LastBlockedAction;
        _episodes.Clear();
        foreach (var (key, episode) in state.Episodes)
            _episodes[key] = episode;
        _refusedRepeatCount = state.RefusedRepeatCount;
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

/// <summary>
/// The loop guard of a tool loop, as <see cref="TurnStateTracker.ExportLoopGuard"/> copies it. It holds only counts
/// and hashes, never raw arguments or results. <see cref="CompletedCycles"/> keeps the order of completion, because
/// the adjacent cycle check reads the order.
/// </summary>
internal sealed record TurnLoopGuardState(
    int ToolIterationCount,
    int ToolCallCount,
    IReadOnlyList<(ToolActionSignature Action, string OutcomeValue)> CompletedCycles,
    ToolActionSignature? LastBlockedAction,
    IReadOnlyDictionary<ToolRecurrenceKey, ToolRecurrenceEpisode> Episodes,
    int RefusedRepeatCount);

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
    IReadOnlySet<string> RefusedCallIds, IReadOnlySet<string> ExemptCallIds)
{
    /// <summary>The refused call IDs that repeat an earlier refusal. Each one needs the repeat nudge.</summary>
    public IReadOnlySet<string> RepeatedCallIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What the turn owner does for a refused repeat. <see cref="RefusedRepeatAction.None"/> without one.</summary>
    public RefusedRepeatAction RefusedRepeat { get; init; }
}

/// <summary>The response of the turn owner to a refused repeat. See <see cref="TurnStateTracker.EscalatedRefusedRepeat"/>.</summary>
internal enum RefusedRepeatAction
{
    /// <summary>The batch repeats no refused call.</summary>
    None,

    /// <summary>Refuse the repeated calls again and add the repeat nudge after the batch results.</summary>
    Nudge,

    /// <summary>Refuse the repeated calls again and add the escalated nudge: stop, end the turn, tell the person.</summary>
    EscalatedNudge,

    /// <summary>
    /// The model ignored the escalated nudge. Dispatch no call of the batch and make no model request. End the
    /// turn quietly with the text of the model. No error reaches the person.
    /// </summary>
    EndTurn
}

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
    public bool Synthetic { get; init; }
}

internal enum ToolCycleDecisionKind
{
    Execute,
    Correct
}

internal readonly record struct ToolCycleDecision(
    ToolCycleDecisionKind Kind,
    int Period = 0,
    int Repetitions = 0,
    bool Repeated = false);

internal static class ToolCycleMessages
{
    // Every repeat nudge, plain or escalated, parent or child, starts with this text. The batch owner uses it to
    // remove the earlier repeat nudge, so that the history keeps only the latest one.
    internal const string RepeatNudgeStart = "Netclaw refused your repeated call to ";

    public const string Correction =
        "Netclaw stopped this tool call because it would continue a repeated action-and-outcome cycle. "
        + "The same action completed twice without a changed result. This call did not execute.";

    // Model-facing. A refused call repeats, so the turn continues with this nudge after the batch results.
    // The text names the skill page for long commands. A person sees it only when the model writes about it.
    public static string RepeatNudge(IEnumerable<string> toolNames)
        => $"{RepeatNudgeStart}{FormatToolNames(toolNames)} again. The call did not execute. "
            + "Netclaw refuses the same call each time that you send it. Do not send it again. Select a different action:\n"
            + "- For a slow shell command, run it as a background job: set `_background: true` on `shell_execute`, "
            + "then use `check_background_job`.\n"
            + "- Change the command or its arguments.\n"
            + "- Divide the work into smaller steps.\n"
            + "- Ask the person how to continue.\n"
            + "For instructions about long-running commands, call "
            + "skill_read_resource('netclaw-operations', 'references/long-running-commands.md').";

    // Model-facing, for a child run. A child has no background job manager and can lack the skill tools,
    // so this text sends a long command back to the parent instead.
    public static string ChildRepeatNudge(IEnumerable<string> toolNames)
        => $"{RepeatNudgeStart}{FormatToolNames(toolNames)} again. The call did not execute. "
            + "Netclaw refuses the same call each time that you send it. Do not send it again. Select a different action:\n"
            + "- Change the command or its arguments.\n"
            + "- Divide the work into smaller steps.\n"
            + "- Stop and give your result to the parent. Name the command that did not finish, "
            + "so that the parent can run it as a background job.";

    // Model-facing. Refused repeat 5 of the turn. The model must stop and tell the person. Another refused
    // repeat ends the turn quietly with the text of the model.
    public static string EscalatedRepeatNudge(IEnumerable<string> toolNames)
        => $"{RepeatNudgeStart}{FormatToolNames(toolNames)} again. The call did not execute. "
            + $"You repeated refused calls {TurnStateTracker.EscalatedRefusedRepeat} times in this turn. "
            + "Stop trying this action. End your turn now. "
            + "Tell the person what blocked you, and name the action that did not finish. "
            + "If you send a refused call again, Netclaw ends the turn. No call in that response runs.";

    // Model-facing, for a child run. Refused repeat 5 of the run. Another refused repeat ends the run with
    // the last text of the child.
    public static string EscalatedChildRepeatNudge(IEnumerable<string> toolNames)
        => $"{RepeatNudgeStart}{FormatToolNames(toolNames)} again. The call did not execute. "
            + $"You repeated refused calls {TurnStateTracker.EscalatedRefusedRepeat} times in this run. "
            + "Stop trying this action. Stop now and give your result to the parent. "
            + "Name the action that did not finish. "
            + "If you send a refused call again, Netclaw ends this run with your last text. No call in that response runs.";

    /// <summary>
    /// The nudge for the refused repeat of <paramref name="decision"/>, or null when the batch needs none. It
    /// names only the repeated tools. <see cref="RefusedRepeatAction.EndTurn"/> has no nudge: the turn ends.
    /// </summary>
    public static string? ForRefusedRepeat(
        ToolCycleBatchDecision decision, IEnumerable<FunctionCallContent> toolCalls, bool childRun)
    {
        var names = toolCalls.Where(call => decision.RepeatedCallIds.Contains(call.CallId))
            .Select(static call => call.Name);
        return decision.RefusedRepeat switch
        {
            RefusedRepeatAction.Nudge => childRun ? ChildRepeatNudge(names) : RepeatNudge(names),
            RefusedRepeatAction.EscalatedNudge => childRun ? EscalatedChildRepeatNudge(names) : EscalatedRepeatNudge(names),
            _ => null
        };
    }

    private static string FormatToolNames(IEnumerable<string> toolNames)
        => string.Join(", ", toolNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(static name => $"`{name}`"));
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
            var (meta, cleaned) = executor.PrepareToolCall(call);
            // Only valid metadata is irrelevant to execution identity. A repair
            // must differ from a rejected call, even after a cycle correction.
            var arguments = rejection is null ? cleaned.Arguments : call.Arguments;
            return new PreparedToolCycleCall(
                new ToolCallId(call.CallId),
                new ToolName(cleaned.Name),
                HashFields([
                    rejection is null ? "accepted" : "rejected",
                    rejection?.DenyReason ?? string.Empty,
                    HashCanonicalArguments(arguments),
                    // A valid background request changes how a shell call runs. The repeat nudge offers it,
                    // so a background run of a refused command must not get the same refusal. Every other
                    // tool runs a background request in the foreground, so the field must not give it a
                    // new identity: that would permit two more real runs of a refused call.
                    rejection is null && meta is { Background: true } && cleaned.Name == ShellTool.ToolName
                        ? "background" : "foreground"]))
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
        var groups = batch.Calls.Where(call => !results[call.CallId.Value].Synthetic).GroupBy(static call => new ToolRecurrenceKey(call.ToolName, call.ArgumentsHash))
            .Select(group => new CompletedToolRecurrenceGroup(group.Key,
                HashFields(group.Select(call => results[call.CallId.Value])
                    .Select(result => HashFields([result.Category.ToString(), HashFields([result.ModelVisibleText])]))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                group.All(call => call.AllowsPendingJob && results[call.CallId.Value].PendingJob)))
            .ToArray();
        return new CompletedToolCycleIteration(batch.Action, outcomeHash)
        {
            Groups = groups,
            RecordAdjacent = !results.Values.Any(result => result.Synthetic) && !groups.Any(static group => group.PendingJob)
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
