// -----------------------------------------------------------------------
// <copyright file="SessionExecutionLifetime.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions.Handlers;

internal sealed record ModelStoppedForRestart(long CallId, Exception? Failure)
    : INoSerializationVerificationNeeded;

internal sealed record ToolPipelineStoppedForRestart(Task WorkTask, Exception? Failure)
    : INoSerializationVerificationNeeded;

/// <summary>
/// Owns the resources for one model call and one tool batch on the session actor thread.
/// Task stop acknowledgements require the exact call ID or task reference.
/// </summary>
internal sealed class SessionExecutionLifetime(ITimerScheduler timers, IActorRef actor)
{
    private readonly ProcessingWatchdog _watchdog = new();
    private CancellationTokenSource? _modelCts;
    private Task? _modelTask;
    private long _modelCallId;
    private bool _modelStopRequested;
    private bool _anyContentStreamed;
    private CancellationTokenSource? _toolCts;
    private Task? _toolTask;
    private bool _toolStopRequested;

    public long CurrentModelCallId => _modelCallId;
    public bool HasLiveModel => _modelCts is not null;
    public bool HasContentStreamed => _anyContentStreamed;
    public bool IsCurrentWatchdog(ProcessingWatchdogExpired message) => _watchdog.IsCurrent(message);
    public bool IsCurrentCompaction(long operationId)
        => _watchdog.IsCurrentOperation(ProcessingWatchdog.Compaction, operationId);

    public (long CallId, CancellationToken Token) BeginModel()
    {
        CancelAndDisposeModel();
        _modelTask = null;
        _modelCts = new CancellationTokenSource();
        _modelCallId++;
        _anyContentStreamed = false;
        return (_modelCallId, _modelCts.Token);
    }

    public bool CanDispatchPreparedModel(long callId)
        => callId == _modelCallId && _modelCts is not null && !_modelStopRequested;

    public (long CallId, CancellationToken Token) ModelDispatch()
    {
        if (!CanDispatchPreparedModel(_modelCallId))
            throw new InvalidOperationException("The current model call cannot dispatch.");

        return (_modelCallId, _modelCts!.Token);
    }

    public void AttachModel(long callId, Task task)
    {
        if (!CanDispatchPreparedModel(callId) || _modelTask is { IsCompleted: false })
            throw new InvalidOperationException("The model task does not match a live call.");

        _modelTask = task;
    }

    public bool AcceptModelResult(long callId)
    {
        if (callId != _modelCallId || _modelStopRequested)
            return false;

        _modelTask = null;
        StopWatchdog();
        CancelAndDisposeModel();
        return true;
    }

    public bool AcceptModelProgress(
        long callId,
        bool substantive,
        TimeSpan prefillTimeout,
        TimeSpan interDeltaTimeout)
    {
        if (callId != _modelCallId || _modelStopRequested)
            return false;

        _anyContentStreamed = _watchdog.OnStreamProgress(
            substantive, _anyContentStreamed, prefillTimeout, interDeltaTimeout, timers);
        return true;
    }

    public void StartModelWatchdog(TimeSpan prefillTimeout, TimeSpan noProgressTimeout)
        => _watchdog.Start(ProcessingWatchdog.LlmCall, prefillTimeout, timers, noProgressTimeout);

    public void StopModelForTimeout()
    {
        StopWatchdog();
        CancelAndDisposeModel();
    }

    public bool RequestModelStop(long callId)
    {
        if (callId != _modelCallId || _modelCts is null || _modelStopRequested)
            return false;

        _modelStopRequested = true;
        StopWatchdog();
        _modelCts.Cancel();
        if (_modelTask is { IsCompleted: false } task)
            _ = ReportModelStopAsync(task, actor, callId);
        else
            actor.Tell(new ModelStoppedForRestart(callId, null));
        return true;
    }

    public bool AcceptModelStop(ModelStoppedForRestart message)
    {
        if (!_modelStopRequested || message.CallId != _modelCallId)
            return false;

        _modelCallId++;
        _modelTask = null;
        CancelAndDisposeModel();
        _modelStopRequested = false;
        return true;
    }

    public CancellationToken BeginTool()
    {
        CancelAndDisposeTool();
        _toolTask = null;
        _toolCts = new CancellationTokenSource();
        return _toolCts.Token;
    }

    public void AttachTool(Task task)
    {
        if (_toolCts is null || _toolTask is { IsCompleted: false })
            throw new InvalidOperationException("The tool task does not match a live batch.");

        _toolTask = task;
    }

    // A synthetic correction can send the batch message without a tool task.
    // The message does not prove that an ordinary pipeline task returned.
    public void OnToolBatchMessage()
    {
        StopWatchdog();
        CancelAndDisposeTool();
        _toolTask = null;
    }

    public void OnToolFailure()
    {
        StopWatchdog();
        CancelAndDisposeTool();
    }

    public bool CanRequestToolStop()
        => !_toolStopRequested && _toolCts is not null && _toolTask is { IsCompleted: false };

    public bool RequestToolStop()
    {
        if (!CanRequestToolStop())
            return false;

        _toolStopRequested = true;
        _toolCts!.Cancel();
        _ = ReportToolPipelineStopAsync(_toolTask!, actor);
        return true;
    }

    public bool AcceptToolStop(ToolPipelineStoppedForRestart message)
        => _toolStopRequested && ReferenceEquals(message.WorkTask, _toolTask);

    public void ClearToolTracking()
    {
        _toolTask = null;
        _toolStopRequested = false;
    }

    public void ReleaseTool() => CancelAndDisposeTool();

    public void ReleaseAll()
    {
        CancelAndDisposeModel();
        CancelAndDisposeTool();
    }

    public long StartCompactionWatchdog(TimeSpan timeout)
    {
        _watchdog.Start(ProcessingWatchdog.Compaction, timeout, timers);
        return _watchdog.CurrentOperationId;
    }

    public void StopWatchdog() => _watchdog.Stop(timers);

    private void CancelAndDisposeModel()
    {
        _modelCts?.Cancel();
        _modelCts?.Dispose();
        _modelCts = null;
    }

    private void CancelAndDisposeTool()
    {
        _toolCts?.Cancel();
        _toolCts?.Dispose();
        _toolCts = null;
    }

    private static async Task ReportModelStopAsync(Task task, IActorRef recipient, long callId)
    {
        try
        {
            await task.ConfigureAwait(false);
            recipient.Tell(new ModelStoppedForRestart(callId, null));
        }
        catch (Exception ex)
        {
            recipient.Tell(new ModelStoppedForRestart(callId, ex));
        }
    }

    private static async Task ReportToolPipelineStopAsync(Task task, IActorRef recipient)
    {
        try
        {
            await task.ConfigureAwait(false);
            recipient.Tell(new ToolPipelineStoppedForRestart(task, null));
        }
        catch (OperationCanceledException)
        {
            recipient.Tell(new ToolPipelineStoppedForRestart(task, null));
        }
        catch (Exception ex)
        {
            recipient.Tell(new ToolPipelineStoppedForRestart(task, ex));
        }
    }
}
