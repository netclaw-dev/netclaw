// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflow.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal sealed record PendingToolInteraction(
    ToolApprovalRequested Request,
    AuthorizationAttemptId AuthorizationAttemptId,
    bool PersistApprovalState,
    TurnContext? TurnContext,
    string? TurnContextRestoreFailure) : INoSerializationVerificationNeeded
{
    public bool CanRecoverAfterRestart
        => PersistApprovalState
           && Request.TurnContext is not null
           && TurnContext is not null
           && TurnContextRestoreFailure is null;

    public static PendingToolInteraction From(
        ToolApprovalRequested evt,
        bool persistApprovalState)
    {
        var turnContext = ToolApprovalTurnContext.Restore(evt, out var restoreFailure);
        var authorizationAttemptId = ToolApprovalTurnContext.RestoreAuthorizationAttemptId(evt);
        return new PendingToolInteraction(
            evt,
            authorizationAttemptId,
            persistApprovalState,
            turnContext,
            restoreFailure);
    }
}

// Internal actor message for approval prompts. The request is the public output
// shape; PersistApprovalState is session routing policy that decides whether the
// prompt becomes durable parent-session approval state.
internal sealed record ToolInteractionRequestDispatch(
    SessionProtocol.ToolInteractionRequest Request,
    bool PersistApprovalState) : INoSerializationVerificationNeeded
{
    internal string? ManagedTemporaryDirectory { get; init; }
}

internal enum ApprovalTurnPhase
{
    None,
    Running,
    Waiting,
    RecoveredWaiting,
    Redriving,
    Abandoning
}

internal sealed record ApprovalRedrivePlan(
    IReadOnlyDictionary<string, IReadOnlyList<string>>? OneTimeApprovalPreSeed,
    IReadOnlyDictionary<string, RefusalKind>? DecisionOverride,
    IReadOnlyDictionary<string, string>? ManagedTemporaryDenialDirectories,
    IReadOnlyDictionary<string, AuthorizationAttemptId>? AuthorizationAttemptIds);

internal abstract record PreparedFreshBatch(ToolCycleDecision Decision)
{
    internal sealed record Stop(ToolCycleDecision Decision) : PreparedFreshBatch(Decision);

    internal sealed record Ready(
        List<FunctionCallContent> Calls,
        PreparedToolCycleBatch Cycle,
        ToolCycleDecision Decision,
        SerializableChatMessage AssistantMessage) : PreparedFreshBatch(Decision);
}

internal abstract record ToolApprovalCallState(PendingToolInteraction Pending);

internal sealed record PendingToolApproval(PendingToolInteraction Pending)
    : ToolApprovalCallState(Pending);

internal sealed record ResolvedToolApproval(
    PendingToolInteraction Pending,
    ConsentAnswer Answer) : ToolApprovalCallState(Pending);

/// <summary>
/// Owns all actor-local approval state for one session.
/// Durable events remain the source of truth after recovery.
/// </summary>
internal sealed partial class SessionToolWorkflow
{
    private readonly Dictionary<string, ToolApprovalCallState> _calls = new(StringComparer.Ordinal);
    private readonly ActiveToolBatchTracker _activeBatch = new();
    private readonly ApprovalChannel _approvalChannel = new();
    private readonly ManagedTemporaryCorrectionState _corrections = new();

    public PreparedFreshBatch PrepareFreshBatch(
        AiChatMessage response,
        List<FunctionCallContent> calls,
        ToolRegistry? registry,
        IToolExecutor executor,
        TurnStateTracker turn)
    {
        turn.ResetEmptyResponseGuards();
        // Canonical names must reach the journal, cycle policy, outputs, and executor.
        registry?.CanonicalizeToolCalls(response, calls);
        var cycle = ToolCycleSignatureFactory.Prepare(calls, executor);
        var decision = turn.EvaluateBeforeDispatch(cycle.Action);
        if (decision.Kind == ToolCycleDecisionKind.Stop)
            return new PreparedFreshBatch.Stop(decision);

        // Persist the exact arguments that the executor will interpret.
        var assistant = ChatMessageConverter.FromAiMessage(
            response,
            interpretToolCall: call =>
            {
                var (meta, cleaned) = executor.PrepareToolCall(call);
                return (meta, cleaned.Arguments);
            });
        return new PreparedFreshBatch.Ready(calls, cycle, decision, assistant);
    }

    public SessionState ApplyBatchStarted(SessionState state, ToolBatchStarted started)
    {
        if (state.FindLastUserMessage() != started.UserMessage)
            state = state with { History = state.History.Add(started.UserMessage) };
        if (!state.History.Contains(started.AssistantMessage))
            state = state with { History = state.History.Add(started.AssistantMessage) };

        _activeBatch.Start(
            started.AssistantMessage,
            ParkedToolBatchHistory.FindToolResultsFor(state.History, started.AssistantMessage));
        return state;
    }

    public void StartDispatch(
        IReadOnlyList<FunctionCallContent> calls,
        PreparedToolCycleBatch? cycle,
        IToolExecutor? executor,
        bool recordCompletedCycle)
    {
        if (recordCompletedCycle && cycle is null && executor is not null)
            cycle = ToolCycleSignatureFactory.Prepare(calls, executor);
        _activeBatch.Start(calls, cycle);
    }

    public SessionToolBatch CreateBatch(
        TurnContext turnContext,
        SessionToolRunEnvironment environment,
        IReadOnlyList<FunctionCallContent> calls,
        ToolExecutionTimeout timeout,
        IActorRef replyTo,
        Action<SubAgentOutput> emitSubAgentOutput,
        Action<ToolInteractionRequestDispatch> emitApprovalRequest,
        BackgroundJobDispatch backgroundJobs,
        bool setWorkingDirectoryAvailable,
        Func<string, ToolInvocationContext, bool>? canDeclareWorkingDirectory,
        ApprovalRedrivePlan? approvalPlan,
        CancellationToken cancellationToken)
        => new(turnContext, environment)
        {
            ToolCalls = calls,
            DefaultTimeout = timeout,
            ReplyTo = replyTo,
            EmitSubAgentOutput = emitSubAgentOutput,
            ApprovalRequests = CreateApprovalRequests(
                emitApprovalRequest,
                new ToolExecutionTimeout(Timeout.InfiniteTimeSpan)),
            BackgroundJobs = backgroundJobs,
            SetWorkingDirectoryAvailable = setWorkingDirectoryAvailable,
            CanDeclareWorkingDirectory = canDeclareWorkingDirectory,
            StreamResults = true,
            OneTimeApprovalPreSeed = approvalPlan?.OneTimeApprovalPreSeed
                ?? new Dictionary<string, IReadOnlyList<string>>(),
            DecisionOverrides = approvalPlan?.DecisionOverride
                ?? new Dictionary<string, RefusalKind>(),
            ManagedTemporaryDenialDirectories = approvalPlan?.ManagedTemporaryDenialDirectories
                ?? new Dictionary<string, string>(),
            AuthorizationAttemptIds = approvalPlan?.AuthorizationAttemptIds
                ?? new Dictionary<string, AuthorizationAttemptId>(),
            ManagedTemporaryCorrections = CorrectionSnapshot(),
            CancellationToken = cancellationToken
        };

    private ToolApprovalRequests CreateApprovalRequests(
        Action<ToolInteractionRequestDispatch> emit,
        ToolExecutionTimeout timeout)
        => new(_approvalChannel, emit, timeout);

    private ManagedTemporaryCorrectionDispatch CorrectionSnapshot() => _corrections.Snapshot();

    public void ApplyCorrection(ManagedTemporaryCorrectionChange? change) => _corrections.Apply(change);

    public void MarkExecutionTaskCompleted() => _activeBatch.MarkExecutionTaskCompleted();

    public void DiscardPendingMedia() => _activeBatch.DiscardPendingMedia();

    public SessionState ApplyRecordedResult(SessionState state, SerializableChatMessage result)
        => _activeBatch.ApplyRecordedResult(state, result);

    public ToolCallId RecordOutcome(ToolCallResult result) => _activeBatch.RecordOutcome(result);

    public BatchCompletion CompleteIfReady(SessionState state, TurnStateTracker turn, int limit)
        => _activeBatch.CompleteIfReady(state, turn, limit);

    public void ClearCompletedBatch() => _activeBatch.Clear();

    public bool CanParkForRestart()
        => _activeBatch.HasOnlyPendingDurableApprovals(HasRecoverablePending);

    public bool AllowsSnapshot(SessionState state)
        => PendingCount == 0
           && ResolvedCount == 0
           && ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, null) is null;

    public void ClearApprovalTurn()
    {
        _corrections.Clear();
        ClearTurn();
    }

    public void StartNewTurn()
    {
        _corrections.Clear();
        StartTurn();
    }

    public void ClearFailedTurn()
    {
        ClearCalls();
        ClearApprovalTurn();
    }

    public void CompleteTurn()
    {
        ClearCalls();
        ClearApprovalTurn();
        ClearCompletedBatch();
    }

    public void ClearNormalBatch()
    {
        ClearCalls();
        ClearCompletedBatch();
        MarkRunningAfterRedrive();
    }

    private int PendingCount => _calls.Values.Count(static call => call is PendingToolApproval);

    private int ResolvedCount => _calls.Values.Count(static call => call is ResolvedToolApproval);

    private ApprovalTurnPhase TurnPhase { get; set; }

    private PendingToolInteraction Request(
        ToolApprovalRequested evt,
        bool persistApprovalState,
        bool recovered)
    {
        var pending = PendingToolInteraction.From(evt, persistApprovalState);
        _calls[evt.CallId] = new PendingToolApproval(pending);

        if (persistApprovalState && pending.TurnContext is not null)
        {
            TurnPhase = recovered
                ? ApprovalTurnPhase.RecoveredWaiting
                : ApprovalTurnPhase.Waiting;
        }

        return pending;
    }

    private bool TryGetPending(string callId, out PendingToolInteraction pending)
    {
        if (_calls.TryGetValue(callId, out var call)
            && call is PendingToolApproval pendingCall)
        {
            pending = pendingCall.Pending;
            return true;
        }

        pending = null!;
        return false;
    }

    private bool TryGetResolved(string callId, out ResolvedToolApproval resolved)
    {
        if (_calls.TryGetValue(callId, out var call)
            && call is ResolvedToolApproval resolvedCall)
        {
            resolved = resolvedCall;
            return true;
        }

        resolved = null!;
        return false;
    }

    private bool HasPending(string callId)
        => _calls.TryGetValue(callId, out var call) && call is PendingToolApproval;

    private bool HasRecoverablePending(string callId)
        => TryGetPending(callId, out var pending)
           && pending.CanRecoverAfterRestart;

    private bool HasResolved(string callId)
        => _calls.TryGetValue(callId, out var call) && call is ResolvedToolApproval;

    private bool RemovePending(string callId)
        => _calls.TryGetValue(callId, out var call)
           && call is PendingToolApproval
           && _calls.Remove(callId);

    private bool Resolve(
        string callId,
        ConsentAnswer answer,
        out PendingToolInteraction pending)
    {
        if (!TryGetPending(callId, out pending))
            return false;

        _calls[callId] = new ResolvedToolApproval(pending, answer);
        if (PendingCount == 0 && pending.TurnContext is not null)
        {
            TurnPhase = ApprovalTurnPhase.Running;
        }

        return true;
    }

    private PendingToolInteraction? FindLatestPending(
        Func<PendingToolInteraction, bool> predicate)
        => _calls.Values
            .OfType<PendingToolApproval>()
            .Select(static call => call.Pending)
            .Where(predicate)
            .OrderBy(static pending => pending.Request.RequestedAtMs)
            .LastOrDefault();

    public void StartTurn() => TurnPhase = ApprovalTurnPhase.Running;

    private bool MarkAbandoning()
    {
        if (TurnPhase is not ApprovalTurnPhase.Waiting
            and not ApprovalTurnPhase.RecoveredWaiting)
        {
            return false;
        }

        TurnPhase = ApprovalTurnPhase.Abandoning;
        return true;
    }

    private bool MarkRedriving(PendingToolInteraction pending)
    {
        if (TurnPhase is not ApprovalTurnPhase.Running
            || pending.TurnContext is null)
        {
            return false;
        }

        TurnPhase = ApprovalTurnPhase.Redriving;
        return true;
    }

    private void MarkRunningAfterRedrive()
    {
        if (TurnPhase is not ApprovalTurnPhase.Redriving)
            return;

        TurnPhase = ApprovalTurnPhase.Running;
    }

    private ApprovalRedrivePlan BuildRedrivePlan(IEnumerable<string> callIds)
    {
        var builder = new ApprovalRedrivePlanBuilder();

        foreach (var callId in callIds)
        {
            if (TryGetResolved(callId, out var resolved))
                builder.Add(callId, resolved);
        }

        return builder.Build();
    }

    private void ClearCalls() => _calls.Clear();

    private void ClearTurn() => TurnPhase = ApprovalTurnPhase.None;

    private sealed class ApprovalRedrivePlanBuilder
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _preSeed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RefusalKind> _decisionOverride = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _managedTemporaryDenialDirectories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AuthorizationAttemptId> _authorizationAttemptIds = new(StringComparer.Ordinal);

        public void Add(string callId, ResolvedToolApproval resolved)
        {
            var request = resolved.Pending.Request;
            _authorizationAttemptIds[callId] = resolved.Pending.AuthorizationAttemptId;

            if (resolved.Answer is not ConsentAnswer.Refused refusal)
            {
                // The re-drive seeds these keys as a one-time consent for the
                // tool name of the re-driven call.
                _preSeed[callId] = OneTimeApprovalKeys.Create(
                    request.Patterns,
                    request.Candidates,
                    request.Cwd);
                return;
            }

            _decisionOverride[callId] = refusal.Kind;
            if (refusal.Kind == RefusalKind.Denied
                && request.ManagedTemporaryDirectory is { Length: > 0 } managedTemporaryDirectory)
            {
                _managedTemporaryDenialDirectories[callId] = managedTemporaryDirectory;
            }
        }

        public ApprovalRedrivePlan Build()
            => new(
                _preSeed.Count == 0 ? null : _preSeed,
                _decisionOverride.Count == 0 ? null : _decisionOverride,
                _managedTemporaryDenialDirectories.Count == 0 ? null : _managedTemporaryDenialDirectories,
                _authorizationAttemptIds.Count == 0 ? null : _authorizationAttemptIds);
    }
}

internal static class ToolApprovalTurnContext
{
    public static AuthorizationAttemptId RestoreAuthorizationAttemptId(ToolApprovalRequested evt)
        => AuthorizationAttemptId.TryParse(evt.AuthorizationAttemptId, out var restoredAttemptId)
            ? restoredAttemptId
            : AuthorizationAttemptId.New();

    public static TurnContext? Restore(ToolApprovalRequested evt, out string? failure)
    {
        if (evt.TurnContext is not null)
        {
            if (TurnContext.TryFromRecord(evt.TurnContext, out var context, out failure))
                return context;

            return null;
        }

        return RestoreLegacy(evt, out failure);
    }

    private static TurnContext? RestoreLegacy(ToolApprovalRequested evt, out string? failure)
    {
        failure = null;

        if (!Enum.IsDefined(evt.Audience))
        {
            failure = $"legacy approval event has invalid trust audience '{(int)evt.Audience}'";
            return null;
        }

        if (!ChannelTypeExtensions.TryFromWireValue(evt.ChannelType, out var channelType))
        {
            failure = "legacy approval event is missing channel type";
            return null;
        }

        var requesterPrincipal = evt.RequesterPrincipal ?? PrincipalClassification.UntrustedExternal;
        if (requesterPrincipal is not PrincipalClassification.VerifiedAutomation
            && evt.RequesterSenderId is null)
        {
            failure = "legacy approval event is missing requester sender";
            return null;
        }

        return new TurnContext
        {
            SessionId = evt.SessionId,
            TurnId = new TurnId($"recovered-approval/{evt.CallId}"),
            Audience = evt.Audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundary(evt.Boundary, evt.ChannelType, evt.Audience),
            ChannelType = channelType,
            RequesterSenderId = evt.RequesterSenderId,
            RequesterPrincipal = requesterPrincipal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Unknown)
            {
                SourceKind = new SourceKind(channelType.ToWireValue())
            },
            HasAdoptedContext = evt.HasThirdPartyAdoptedContext || evt.AdoptedSpeakerIds.Count > 0,
            HasThirdPartyAdoptedContext = evt.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = evt.AdoptedSpeakerIds,
            SupportsInteractiveApproval = evt.SupportsInteractiveApproval ?? channelType.SupportsInteractiveApproval()
        };
    }
}
