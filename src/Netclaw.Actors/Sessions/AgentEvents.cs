// -----------------------------------------------------------------------
// <copyright file="AgentEvents.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>The address of one agent of a session. A value object. It never holds an actor reference.</summary>
internal abstract record AgentAddress
{
    private AgentAddress() { }

    /// <summary>The main agent of a session.</summary>
    public sealed record Session(SessionId SessionId) : AgentAddress;

    /// <summary>A child run of a session.</summary>
    public sealed record ChildRun(SessionId SessionId, SubAgentRunId RunId, AgentName Agent) : AgentAddress;
}

/// <summary>The minimal envelope of an agent event.</summary>
internal sealed record AgentEnvelope(AgentAddress Sender, AgentAddress Target, DateTimeOffset SentAt);

/// <summary>
/// The closed family of events between the agents of one session. In-process only. A payload holds IDs, canonical
/// names, enums, and runtime-made paths. It never holds text that an agent chose.
/// </summary>
internal abstract record AgentEvent
{
    private AgentEvent() { }

    public required AgentEnvelope Envelope { get; init; }

    public sealed record ChildApprovalRequested(ToolCallId PromptCallId, ToolName Tool) : AgentEvent;

    public sealed record ChildApprovalResolved(ToolCallId PromptCallId, ChildApprovalResolution Resolution) : AgentEvent;

    public sealed record ChildRunEnded(
        ChildNotice Notice,
        SubAgentRunOutcome Outcome,
        SubAgentOutcomeReason? Reason,
        ChildRunResultFile ResultFile) : AgentEvent;
}

/// <summary>How a child consent request closed.</summary>
internal abstract record ChildApprovalResolution
{
    private ChildApprovalResolution() { }

    /// <summary>The operator allowed the call: one time (<see cref="ConsentAnswer.Once"/>) or with a grant.</summary>
    public sealed record Approved(ConsentAnswer Answer) : ChildApprovalResolution;

    public sealed record Refused(RefusalKind Kind) : ChildApprovalResolution;

    public sealed record Withdrawn(WithdrawReason Reason) : ChildApprovalResolution;

    public static ChildApprovalResolution From(ConsentAnswer answer) => answer switch
    {
        ConsentAnswer.Refused refused => new Refused(refused.Kind),
        ConsentAnswer.Once or ConsentAnswer.Grant => new Approved(answer),
        _ => throw new InvalidOperationException($"Unknown consent answer {answer.GetType().Name}.")
    };
}

/// <summary>
/// Why the session closed an open child consent request with no answer. Public because
/// <see cref="SessionProtocol.ToolInteractionClosed"/> carries it to the channels.
/// </summary>
public enum WithdrawReason
{
    CancelledByParent,
    ChildEnded,
    ChildLost,
    DaemonRestarted,
    PromptUnavailable
}

/// <summary>
/// Why an open consent prompt closed: a person answered it, or the session withdrew it with no answer. Public because
/// <see cref="SessionProtocol.ToolInteractionClosed"/> carries it to the channels.
/// </summary>
public abstract record ToolInteractionCloseReason
{
    private ToolInteractionCloseReason() { }

    /// <summary>A person answered the prompt, maybe on another surface.</summary>
    public sealed record Answered : ToolInteractionCloseReason;

    /// <summary>The session withdrew the prompt with no answer. Nothing ran.</summary>
    public sealed record Withdrawn(WithdrawReason Reason) : ToolInteractionCloseReason;
}

/// <summary>The runtime-made path of the <c>result.md</c> file of a child run.</summary>
internal readonly record struct ChildRunResultFile(string Value);

/// <summary>The fixed facts that identify an approval notice input. See <c>TurnContext.IsChildConsentNotice</c>.</summary>
internal static class ChildConsentNotice
{
    /// <summary>The source kind and the sender of an approval notice.</summary>
    public const string Kind = "child-consent";
}

/// <summary>
/// The owner-stamped builders of agent events. Only the session that owns a run calls them, with its own run record.
/// Security boundary: the sender comes from the run record of the owner, never from the prompt or from a field that
/// the child sets. A fact that names another run or another session is not a fact of this run, so the builder throws
/// and never falls back to the run that the fact names.
/// </summary>
internal static class AgentEventFactory
{
    /// <summary>
    /// Builds the event for a child consent request after <c>ToolApprovalRequested</c> persists. The payload holds no
    /// text of the prompt.
    /// </summary>
    public static AgentEvent.ChildApprovalRequested ApprovalRequested(
        SessionId owner,
        BackgroundChildRun run,
        ToolInteractionRequest prompt,
        DateTimeOffset sentAt)
        => ApprovalRequested(owner, run, prompt.CallId, prompt.ToolName, sentAt);

    public static AgentEvent.ChildApprovalRequested ApprovalRequested(
        SessionId owner,
        BackgroundChildRun run,
        ToolCallId promptCallId,
        ToolName tool,
        DateTimeOffset sentAt)
    {
        EnsurePromptOfRun(owner, run, promptCallId);
        return new AgentEvent.ChildApprovalRequested(promptCallId, tool) { Envelope = Envelope(owner, run, sentAt) };
    }

    /// <summary>Builds the event for a child consent request that an answer or a withdrawal closed.</summary>
    public static AgentEvent.ChildApprovalResolved ApprovalResolved(
        SessionId owner,
        BackgroundChildRun run,
        ToolCallId promptCallId,
        ChildApprovalResolution resolution,
        DateTimeOffset sentAt)
    {
        EnsurePromptOfRun(owner, run, promptCallId);
        return new AgentEvent.ChildApprovalResolved(promptCallId, resolution) { Envelope = Envelope(owner, run, sentAt) };
    }

    /// <summary>
    /// The field 14 adapter: reads an admitted child result notice as <see cref="AgentEvent.ChildRunEnded"/>.
    /// The stored row does not change.
    /// </summary>
    public static AgentEvent.ChildRunEnded RunEnded(
        InputAdmitted admitted,
        BackgroundChildRun run,
        ChildRunResultFile resultFile)
    {
        var owner = admitted.SessionId;
        var notice = admitted.ChildNotice
                     ?? throw new InvalidOperationException($"Input {admitted.InputId} is not a child result notice.");
        if (notice.RunId != run.RunId)
            throw new InvalidOperationException(
                $"The notice of run {notice.RunId.Value} is not a fact of run {run.RunId.Value}.");
        EnsureRunOfOwner(owner, run);
        var (outcome, reason) = run.Lifecycle switch
        {
            ChildRunLifecycle.Terminal terminal => (terminal.Result.Result.Outcome, terminal.Result.Result.OutcomeReason),
            ChildRunLifecycle.Reported reported => (reported.Outcome, reported.Reason),
            _ => throw new InvalidOperationException($"Run {run.RunId.Value} has not ended.")
        };
        return new AgentEvent.ChildRunEnded(notice, outcome, reason, resultFile)
        {
            Envelope = Envelope(owner, run, admitted.Timestamp)
        };
    }

    // The runtime makes a child prompt call ID from the run ID of the bridge closure: {runId}/subagent-approval/{n}.
    private static void EnsurePromptOfRun(SessionId owner, BackgroundChildRun run, ToolCallId promptCallId)
    {
        EnsureRunOfOwner(owner, run);
        if (!promptCallId.Value.StartsWith($"{run.RunId.Value}/subagent-approval/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The prompt {promptCallId.Value} is not a consent request of run {run.RunId.Value}.");
    }

    private static void EnsureRunOfOwner(SessionId owner, BackgroundChildRun run)
    {
        if (run.OriginalContext.SessionId != owner)
            throw new InvalidOperationException(
                $"Run {run.RunId.Value} belongs to another session, not to {owner.Value}.");
    }

    private static AgentEnvelope Envelope(SessionId owner, BackgroundChildRun run, DateTimeOffset sentAt)
        => new(new AgentAddress.ChildRun(owner, run.RunId, run.AgentName), new AgentAddress.Session(owner), sentAt);
}

/// <summary>
/// The one deterministic path from typed approval events to model text. The text names agents, runs, tools, and
/// answers only. It never copies a command, a scope, or a path from a prompt.
/// </summary>
internal static class AgentEventRenderer
{
    public const string OpenHeading = "Child consent requests wait for the operator.";
    public const string ClosedHeading = "Child consent requests are closed. None is open in this session.";
    public const string AnswersHeading = "Answers since the last notice:";

    /// <summary>
    /// The last line of each notice. A notice turn is automation, so a call that needs consent is denied with no
    /// prompt. A model that goes on with the operator's work in this turn hits that denial while the operator could
    /// approve the call in their own turn (issue 2653). The eval case notice_turn_stays_short reads this line.
    /// </summary>
    public const string NoticeTurnRule =
        "This notice turn cannot ask for approval. Handle only this notice, tell the operator the next step, and end the turn. Do not start or continue other work in this turn.";

    /// <summary>
    /// Renders the digest of one session. A request with no later resolution is open. With an open request the text
    /// lists the open requests, the answers that no earlier notice gave, and the rules of the pause. With none open,
    /// the text lists each answer.
    /// </summary>
    public static string Render(IReadOnlyList<AgentEvent> events) => Render(events, []);

    /// <summary>
    /// Renders the digest, and names in the closing notice the reminder fires that waited for the pause. An open
    /// notice ignores <paramref name="heldFires"/>, because those fires still wait.
    /// </summary>
    public static string Render(IReadOnlyList<AgentEvent> events, IReadOnlyList<ReminderFireKey> heldFires)
    {
        var resolved = events.OfType<AgentEvent.ChildApprovalResolved>()
            .Select(static resolution => resolution.PromptCallId)
            .ToHashSet();
        var open = events.OfType<AgentEvent.ChildApprovalRequested>()
            .Where(request => !resolved.Contains(request.PromptCallId))
            .ToArray();
        var text = new StringBuilder();
        if (open.Length > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{OpenHeading} {open.Length} {(open.Length == 1 ? "is" : "are")} open.");
            foreach (var request in open)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {Sender(request)} tool={request.Tool.Value} open_since={Clock(request.Envelope.SentAt)}");
            }

            var answers = events.OfType<AgentEvent.ChildApprovalResolved>().ToArray();
            if (answers.Length > 0)
            {
                text.AppendLine(AnswersHeading);
                foreach (var resolution in answers)
                    text.AppendLine(CultureInfo.InvariantCulture, $"- {Sender(resolution)} answer={Answer(resolution.Resolution)}");
            }

            text.AppendLine("The operator answers these prompts in the channel. You cannot answer them.");
            text.AppendLine("The children are not stalled. They wait for the operator, with no time limit.");
            text.AppendLine("Until all requests close, spawn_agent is refused and automated turns wait.");
            text.AppendLine("check_agent_run can read the status of a run of this session, or cancel it.");
            text.AppendLine("Ask the operator to answer the prompts, or to cancel a child.");
            text.AppendLine(NoticeTurnRule);
            return text.ToString();
        }

        text.AppendLine(ClosedHeading);
        foreach (var resolution in events.OfType<AgentEvent.ChildApprovalResolved>())
            text.AppendLine(CultureInfo.InvariantCulture, $"- {Sender(resolution)} answer={Answer(resolution.Resolution)}");
        if (heldFires.Count > 0)
        {
            text.AppendLine("Held reminder fires now run: " + string.Join(", ", heldFires.Select(static fire =>
                $"reminder={fire.DefinitionId.Value} fired_at={Clock(DateTimeOffset.FromUnixTimeMilliseconds(fire.FireTimeMs))}")));
        }

        text.AppendLine("spawn_agent is available again.");
        text.AppendLine(NoticeTurnRule);
        return text.ToString();
    }

    private static string Sender(AgentEvent evt) => evt.Envelope.Sender is AgentAddress.ChildRun child
        ? $"agent={child.Agent.Value} run={child.RunId.Value}"
        : throw new InvalidOperationException("A child approval event must come from a child run.");

    private static string Clock(DateTimeOffset at)
        => at.UtcDateTime.ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Answer(ChildApprovalResolution resolution) => resolution switch
    {
        ChildApprovalResolution.Approved { Answer: ConsentAnswer.Once } => "approved_once",
        ChildApprovalResolution.Approved { Answer: ConsentAnswer.Grant grant }
            => $"approved_{grant.Scope.ToString().ToLowerInvariant()}",
        ChildApprovalResolution.Refused refused => $"refused reason={Refusal(refused.Kind)}",
        ChildApprovalResolution.Withdrawn withdrawn => $"withdrawn reason={Withdrawal(withdrawn.Reason)}",
        _ => throw new InvalidOperationException($"Unknown child approval resolution {resolution.GetType().Name}.")
    };

    private static string Refusal(RefusalKind kind) => kind switch
    {
        RefusalKind.Denied => "denied",
        RefusalKind.TimedOut => "timed_out",
        RefusalKind.PromptUnavailable => "prompt_unavailable",
        _ => throw new InvalidOperationException($"Unknown refusal kind {kind}.")
    };

    private static string Withdrawal(WithdrawReason reason) => reason switch
    {
        WithdrawReason.CancelledByParent => "cancelled_by_parent",
        WithdrawReason.ChildEnded => "child_ended",
        WithdrawReason.ChildLost => "child_lost",
        WithdrawReason.DaemonRestarted => "daemon_restarted",
        WithdrawReason.PromptUnavailable => "prompt_unavailable",
        _ => throw new InvalidOperationException($"Unknown withdraw reason {reason}.")
    };
}
