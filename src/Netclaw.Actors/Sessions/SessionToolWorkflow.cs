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
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal sealed record PendingToolInteraction(
    ToolApprovalRequested Request,
    AuthorizationAttemptId AuthorizationAttemptId,
    TurnContext? TurnContext,
    string? TurnContextRestoreFailure) : INoSerializationVerificationNeeded
{
    public bool CanRecoverAfterRestart
        => Request.TurnContext is not null
           && TurnContext is not null
           && TurnContextRestoreFailure is null;

    public static PendingToolInteraction From(ToolApprovalRequested evt)
    {
        var turnContext = ToolApprovalTurnContext.Restore(evt, out var restoreFailure);
        var authorizationAttemptId = ToolApprovalTurnContext.RestoreAuthorizationAttemptId(evt);
        return new PendingToolInteraction(
            evt,
            authorizationAttemptId,
            turnContext,
            restoreFailure);
    }
}

// Internal actor message for approval prompts. The request is the public output
// shape. The session journals every prompt before it shows it.
internal sealed record ToolInteractionRequestDispatch(
    SessionProtocol.ToolInteractionRequest Request) : INoSerializationVerificationNeeded
{
    internal string? ManagedTemporaryDirectory { get; init; }
    internal SubAgentRunId? SourceChildRunId { get; init; }
    internal ToolCallId? OriginalChildCallId { get; init; }
    internal TurnContext? ChildContext { get; init; }
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

// A fresh batch always dispatches. A refused call gets a synthetic correction result in the pipeline.
internal sealed record PreparedFreshBatch(
    List<FunctionCallContent> Calls,
    PreparedToolCycleBatch Cycle,
    ToolCycleBatchDecision Decision,
    SerializableChatMessage AssistantMessage);

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
    // How each closed child prompt ended (see RememberClosedChildCalls). A late click reads it.
    private readonly Dictionary<string, ChildApprovalResolution> _closedChildCalls = new(StringComparer.Ordinal);
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
        var decision = turn.EvaluateBeforeDispatch(cycle);

        // Persist the exact arguments that the executor will interpret.
        var assistant = ChatMessageConverter.FromAiMessage(
            response,
            interpretToolCall: call =>
            {
                var (meta, cleaned) = executor.PrepareToolCall(call);
                return (meta, cleaned.Arguments);
            });
        return new PreparedFreshBatch(calls, cycle, decision, assistant);
    }

    public SessionState ApplyBatchStarted(SessionState state, ToolBatchStarted started)
    {
        state = state.CloseLegacyContinuation(started.ChildContinuation, close: false);
        if (started.ChildContinuation is null && state.FindLastUserMessage() != started.UserMessage)
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

    /// <summary>Add the repeat nudge after the results of the batch that <see cref="StartDispatch"/> started.</summary>
    public void AddRepeatNudgeAfterBatch(string nudge) => _activeBatch.AddNudgeAfterResults(nudge);

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
        CancellationToken cancellationToken,
        IReadOnlySet<string> refusedCallIds)
        => new(turnContext, environment)
        {
            ToolCalls = calls,
            RefusedCallIds = refusedCallIds,
            DefaultTimeout = timeout,
            ReplyTo = replyTo,
            EmitSubAgentOutput = emitSubAgentOutput,
            ApprovalRequests = CreateApprovalRequests(
                emitApprovalRequest,
                new ToolExecutionTimeout(Timeout.InfiniteTimeSpan)),
            BackgroundJobs = backgroundJobs,
            SetWorkingDirectoryAvailable = setWorkingDirectoryAvailable,
            CanDeclareWorkingDirectory = canDeclareWorkingDirectory,
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

    public BatchCompletion CompleteIfReady(SessionState state, TurnStateTracker turn)
        => _activeBatch.CompleteIfReady(state, turn);

    public void ClearCompletedBatch() => _activeBatch.Clear();

    public bool CanParkForRestart()
        => _activeBatch.HasOnlyPendingDurableApprovals(HasRecoverablePending);

    public bool AllowsSnapshot(SessionState state)
        => _calls.Values.All(static call => ChildParkRules.IgnoredBySnapshot(call.Pending.Request))
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

    private int PendingParentCount => _calls.Values.Count(static call => call is PendingToolApproval && call.Pending.Request.SourceChildRunId is null);

    private int ResolvedParentCount => _calls.Values.Count(static call => call is ResolvedToolApproval && call.Pending.Request.SourceChildRunId is null);

    private ApprovalTurnPhase TurnPhase { get; set; }

    private PendingToolInteraction Request(
        ToolApprovalRequested evt,
        bool recovered)
    {
        var pending = PendingToolInteraction.From(evt);
        _calls[evt.CallId] = new PendingToolApproval(pending);

        if (evt.SourceChildRunId is null && pending.TurnContext is not null)
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
        if (pending.Request.SourceChildRunId is null && PendingParentCount == 0 && pending.TurnContext is not null)
        {
            TurnPhase = ApprovalTurnPhase.Running;
        }

        return true;
    }

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

    private ApprovalRedrivePlan BuildRedrivePlan(
        IEnumerable<string> callIds,
        IReadOnlyDictionary<string, FunctionCallContent> originalCalls,
        out string? failure)
    {
        var builder = new ApprovalRedrivePlanBuilder();
        failure = null;

        foreach (var callId in callIds)
        {
            if (TryGetResolved(callId, out var resolved))
            {
                var shellSourceKey = GetShellSourceKey(callId, resolved, originalCalls, out failure);
                if (failure is not null)
                    return builder.Build();

                builder.Add(callId, resolved, shellSourceKey);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Gets the shell source key of an approved shell call from the original call that the re-drive runs again. The
    /// key is null for a refused call and for another tool. The session re-drive and the child resume use this rule.
    /// </summary>
    /// <remarks>
    /// Security boundary: one-time consent for a shell call binds the exact shell source. A missing or different
    /// original call sets <paramref name="failure"/>, and the caller must not seed consent.
    /// </remarks>
    internal static string? GetShellSourceKey(
        string callId,
        ResolvedToolApproval resolved,
        IReadOnlyDictionary<string, FunctionCallContent> originalCalls,
        out string? failure)
    {
        failure = null;
        if (resolved.Answer is ConsentAnswer.Refused
            || !string.Equals(resolved.Pending.Request.ToolName, "shell_execute", StringComparison.Ordinal))
            return null;

        if (!originalCalls.TryGetValue(callId, out var originalCall))
        {
            failure = "original shell call is missing from history";
            return null;
        }
        if (!string.Equals(
                originalCall.Name,
                resolved.Pending.Request.ToolName,
                StringComparison.Ordinal))
        {
            failure = "original shell tool name does not match the recovered approval";
            return null;
        }

        var command = ToolAccessPolicy.ExtractShellCommand(originalCall.Arguments);
        var shellSourceKey = OneTimeApprovalKeys.CreateShellSourceKey(command);
        if (shellSourceKey is null)
            failure = "original shell command is missing from history";
        return shellSourceKey;
    }

    private void ClearCalls()
    {
        foreach (var key in _calls.Where(pair => pair.Value.Pending.Request.SourceChildRunId is null)
                     .Select(pair => pair.Key).ToArray())
            _calls.Remove(key);
    }

    private void ClearTurn() => TurnPhase = ApprovalTurnPhase.None;

    private sealed class ApprovalRedrivePlanBuilder
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _preSeed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RefusalKind> _decisionOverride = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _managedTemporaryDenialDirectories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AuthorizationAttemptId> _authorizationAttemptIds = new(StringComparer.Ordinal);

        public void Add(string callId, ResolvedToolApproval resolved, string? shellSourceKey)
        {
            var entry = ApprovalRedriveEntry.From(resolved, shellSourceKey);
            _authorizationAttemptIds[callId] = entry.AuthorizationAttemptId;
            if (entry.ApprovedConsentKeys is { } keys)
                _preSeed[callId] = keys;
            if (entry.Refusal is { } refusal)
                _decisionOverride[callId] = refusal;
            if (entry.ManagedTemporaryDenialDirectory is { } managedTemporaryDirectory)
                _managedTemporaryDenialDirectories[callId] = managedTemporaryDirectory;
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
