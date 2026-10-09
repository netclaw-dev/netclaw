// -----------------------------------------------------------------------
// <copyright file="LlmSessionActor.ChildApprovals.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Sessions;

public sealed partial class LlmSessionActor
{
    private sealed record ChildApprovalRequested(SubAgentRunId RunId, ChildRunDispatch Dispatch,
        ToolInteractionRequestDispatch Prompt) : INoSerializationVerificationNeeded;
    private sealed record ChildApprovalWaitFinished(SubAgentRunId RunId, AuthorizationAttemptId Attempt,
        ConsentAnswer Answer) : INoSerializationVerificationNeeded;

    private sealed class ChildApprovalBridge(IParentConsentBridge inner, IActorRef owner, SubAgentRunId runId)
        : IParentConsentBridge
    {
        public async Task<ConsentStep> RequestConsentAsync(ParentApprovalRequest request, CancellationToken ct)
        {
            var step = await inner.RequestConsentAsync(request, ct).ConfigureAwait(false);
            owner.Tell(new ChildApprovalWaitFinished(runId, request.AuthorizationAttemptId, step.Answer));
            return step;
        }
    }

    private RunSubAgent WithChildApprovalOwner(BackgroundChildRun run, RunSubAgent execution, ChildRuntime runtime)
    {
        if (execution.Scope.Authority.InteractiveApproval is InteractiveApprovalCapability.Unavailable)
            return execution;
        if (!TurnContext.TryFromRecord(run.OriginalContext, out var context, out var reason) || context is null)
            throw new InvalidDataException($"The child approval context is invalid: {reason}");
        var owner = Self;
        var bridge = new ParentSessionApprovalBridge(_approvalChannel,
            prompt => owner.Tell(new ChildApprovalRequested(run.RunId, runtime.Dispatch, prompt)),
            new ToolExecutionTimeout(Timeout.InfiniteTimeSpan), _sessionId, run.RunId.Value,
            context.RequesterSenderId, context.RequesterPrincipal, context.HasAdoptedContext,
            context.HasThirdPartyAdoptedContext, context.AdoptedSpeakerIds);
        return execution with
        {
            Scope = execution.Scope with
            {
                Authority = execution.Scope.Authority with
                {
                    InteractiveApproval = new InteractiveApprovalCapability.Available(new ChildApprovalBridge(bridge, owner, run.RunId))
                }
            }
        };
    }

    private void HandleChildApprovalRequested(ChildApprovalRequested message)
    {
        if (!_backgroundChildRuntimes.TryGetValue(message.RunId, out var runtime)
            || !ReferenceEquals(runtime.Dispatch, message.Dispatch)
            || !_state.ChildRuns.TryGetValue(message.RunId, out var run)
            || run.Terminal is not null || run.DispatchClosedAtMs is not null)
        {
            RefuseChildApprovalWait(message.Prompt.Request.CallId);
            return;
        }
        if (!TurnContext.TryFromRecord(run.OriginalContext, out var original, out var reason) || original is null)
            throw new InvalidDataException($"The child approval context is invalid: {reason}");
        var evt = CreateToolApprovalRequest(message.Prompt, original) with
        {
            SourceChildRunId = run.RunId, OriginalChildCallId = message.Prompt.OriginalChildCallId
        };
        _ = _state.ApplyChildApproval(evt);
        try
        {
            _ = runtime.Dispatch.Enter(() =>
            {
                Persist(evt, committed =>
                {
                    ApplyToolApprovalRequested(committed);
                    EmitOutput(message.Prompt.Request);
                });
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            RefuseChildApprovalWait(message.Prompt.Request.CallId);
        }
    }

    private void RefuseChildApprovalWait(ToolCallId callId)
    {
        if (_approvalChannel.TryClaim(callId, out var wait))
            wait.Complete(ConsentAnswer.Denied);
    }

    private async Task<bool> TryHandleChildApprovalResponseAsync(ToolInteractionResponse message)
    {
        var run = _state.ChildRuns.Values.SingleOrDefault(candidate =>
            candidate.Approvals.Any(approval => approval.Request.CallId == message.CallId.Value));
        if (run is null)
            return false;
        var approval = run.Approvals.Single(item => item.Request.CallId == message.CallId.Value);
        if (approval.Resolution is not null || run.Terminal is not null || run.DispatchClosedAtMs is not null
            || !_backgroundChildRuntimes.TryGetValue(run.RunId, out var runtime))
        {
            EmitExpiredPromptNotice();
            TryReplyNack(ApprovalNackReasons.PromptExpired);
            return true;
        }
        var pending = PendingToolInteraction.From(approval.Request, persistApprovalState: false);
        var authorization = await AuthorizeApprovalResponseAsync(pending, message, persistApprovalGrant: false);
        if (authorization.Answer is not { } answer)
        {
            TryReplyNack(authorization.NackReason ?? ApprovalNackReasons.WrongRequester);
            return true;
        }
        if (!_approvalChannel.TryClaim(message.CallId, out var wait))
        {
            EmitExpiredPromptNotice();
            TryReplyNack(ApprovalNackReasons.PromptExpired);
            return true;
        }
        try
        {
            // A claimed prompt alone cannot grant after the run closes dispatch admission.
            await runtime.Dispatch.Enter(() => PersistApprovalGrantIfNeededAsync(pending, answer, runtime.Lifetime.Token));
            PersistChildApprovalResolution(run.RunId, approval.Request, answer, () =>
            {
                wait.Complete(answer);
                TryReplyAck();
            });
        }
        catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
        {
            wait.Complete(ConsentAnswer.Denied);
            _log.Error(error, "Child approval decision failed runId={RunId} callId={CallId}", run.RunId.Value, message.CallId.Value);
            EmitExpiredPromptNotice();
            TryReplyNack(ApprovalNackReasons.PersistFailed);
        }
        return true;
    }

    private void PersistChildApprovalResolution(SubAgentRunId runId, ToolApprovalRequested request,
        ConsentAnswer answer, Action afterCommit)
    {
        var evt = new ToolApprovalResolved
        {
            SessionId = _sessionId, SourceChildRunId = runId, CallId = request.CallId,
            AuthorizationAttemptId = request.AuthorizationAttemptId,
            Decision = ConsentAnswerCodec.ToJournalText(answer), ResolvedAtMs = NowMs()
        };
        _ = _state.ApplyChildApproval(evt);
        Persist(evt, committed =>
        {
            ApplyToolApprovalResolved(committed);
            afterCommit();
        });
    }

    private void HandleChildApprovalWaitFinished(ChildApprovalWaitFinished message)
    {
        if (!_state.ChildRuns.TryGetValue(message.RunId, out var run))
            return;
        var request = run.Approvals.SingleOrDefault(approval =>
            approval.Request.AuthorizationAttemptId == message.Attempt.Value && approval.Resolution is null)?.Request;
        if (request is not null)
            PersistChildApprovalResolution(run.RunId, request, message.Answer, () => { });
    }

    private void ExpireChildApprovals(SubAgentRunId runId, Action afterCommit)
    {
        var pending = _state.ChildRuns[runId].Approvals.FirstOrDefault(static approval => approval.Resolution is null);
        if (pending is null)
        {
            afterCommit();
            return;
        }
        PersistChildApprovalResolution(runId, pending.Request, ConsentAnswer.Denied, () =>
        {
            RefuseChildApprovalWait(new ToolCallId(pending.Request.CallId));
            EmitExpiredPromptNotice();
            ExpireChildApprovals(runId, afterCommit);
        });
    }
}
