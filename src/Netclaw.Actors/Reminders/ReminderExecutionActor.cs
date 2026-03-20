using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Short-lived child actor handling a single reminder execution.
/// Creates a session pipeline, sends reminder instructions, collects output,
/// and reports success/failure to <see cref="ReminderManagerActor"/>.
/// </summary>
internal sealed class ReminderExecutionActor : ReceiveActor
{
    private readonly Guid _executionId;
    private readonly ReminderDefinition _definition;
    private readonly ISessionPipeline _pipeline;
    private readonly ReminderHistoryStore _historyStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;
    private readonly DateTimeOffset _dispatchedAt;

    private readonly StringBuilder _buffer = new();
    private bool _sawTextDelta;
    private bool _completed;
    private bool _notifyAttempted;
    private bool _notifyFailed;
    private string? _notifyFailureDetail;
    private string? _sessionIdValue;
    private HistoryRecord? _pendingHistory;

    private ActorMaterializer? _materializer;
    private MaterializedSession? _session;

    public static Props CreateProps(
        Guid executionId,
        ReminderDefinition definition,
        ISessionPipeline pipeline,
        ReminderConfig config,
        TimeProvider timeProvider,
        ReminderHistoryStore historyStore) =>
        Props.Create(() => new ReminderExecutionActor(executionId, definition, pipeline, config, timeProvider, historyStore));

    public ReminderExecutionActor(
        Guid executionId,
        ReminderDefinition definition,
        ISessionPipeline pipeline,
        ReminderConfig config,
        TimeProvider timeProvider,
        ReminderHistoryStore historyStore)
    {
        _executionId = executionId;
        _definition = definition;
        _pipeline = pipeline;
        _historyStore = historyStore;
        _timeProvider = timeProvider;
        _dispatchedAt = timeProvider.GetUtcNow();
        _log = Context.GetLogger();

        Context.SetReceiveTimeout(TimeSpan.FromSeconds(config.ExecutionTimeoutSeconds));

        Receive<ExecutionOutput>(HandleOutput);
        Receive<ExecutionStarted>(_ => { });
        Receive<ReceiveTimeout>(_ =>
        {
            var elapsed = (int)(_timeProvider.GetUtcNow() - _dispatchedAt).TotalSeconds;
            _log.Warning(
                $"ReminderExecution Timeout: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} dispatched_at={_dispatchedAt} elapsed_s={elapsed}");
            ReportAndStop(false, "Execution timed out");
        });
    }

    protected override void PreStart()
    {
        _log.Info(
            $"ReminderExecution Dispatched: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} schedule_type={_definition.Schedule.Type} dispatched_at={_dispatchedAt}");

        Self.Tell(new ExecutionStarted());
        RunTask(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var sessionId = !string.IsNullOrWhiteSpace(_definition.SessionId)
                ? new SessionId(_definition.SessionId)
                : new SessionId($"reminder/{_definition.Id}/{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}");

            _sessionIdValue = sessionId.Value;
            _log.Info(
                $"ReminderExecution Initialized: execution_id={_executionId} reminder_id={_definition.Id} session_id={sessionId.Value}");

            _materializer = Context.Materializer(namePrefix: "reminder-exec");

            var materialized = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
            {
                ChannelType = Channels.ChannelType.Reminder,
                Filter = OutputFilter.Text
            }, materializer: _materializer);

            var self = Self;

            var inputQueue = Source.Queue<ChannelInput>(8, OverflowStrategy.Backpressure)
                .ToMaterialized(materialized.Input, Keep.Left)
                .Run(_materializer);

            materialized.Output
                .To(Sink.ForEach<SessionOutput>(output => self.Tell(new ExecutionOutput(output))))
                .Run(_materializer);

            _session = materialized;

            var prompt = BuildPrompt(_definition);

            await inputQueue.OfferAsync(new ChannelInput
            {
                SenderId = "reminder-system",
                ChannelId = _definition.ReportToChannel,
                Contents = [new TextContent(prompt)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });

            inputQueue.Complete();
        }
        catch (Exception ex)
        {
            LogFullException(ex, "ReminderExecution InitializationFailed");
            ReportAndStop(false, ex.Message);
        }
    }

    private static string BuildPrompt(ReminderDefinition definition)
    {
        return
            $"{definition.Instructions}\n\n" +
            "Notification instructions:\n" +
            definition.NotifyInstructions;
    }

    private void HandleOutput(ExecutionOutput wrapper)
    {
        switch (wrapper.Output)
        {
            case TextDeltaOutput delta:
                _buffer.Append(delta.Delta);
                _sawTextDelta = true;
                break;

            case TextOutput text:
                if (!_sawTextDelta)
                    _buffer.Append(text.Text);
                break;

            case ToolResultOutput toolResult:
                TrackNotificationResult(toolResult);
                break;

            case BufferFlush:
                // Reminder accumulates full text for final result -- no mid-turn flush needed.
                break;

            case TurnCompleted:
                var result = _buffer.ToString().Trim();
                var notifyFailureMessage = BuildNotifyFailureMessage();
                var success = notifyFailureMessage is null;
                _log.Info(
                    $"ReminderExecution Completed: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success={success} output_length={result.Length} notify_attempted={_notifyAttempted} notify_failed={_notifyFailed} dispatched_at={_dispatchedAt} completed_at={_timeProvider.GetUtcNow()}");

                if (string.IsNullOrWhiteSpace(result))
                    result = $"(Reminder '{_definition.Title}' executed but produced no output)";

                ReportAndStop(success, notifyFailureMessage);
                break;

            case ErrorOutput err:
                var completedAt = _timeProvider.GetUtcNow();
                var failedMsg = $"ReminderExecution Failed: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success=false error_type={err.Category} error_message={err.Message} dispatched_at={_dispatchedAt} completed_at={completedAt}";
                if (err.Cause is not null)
                    _log.Error(err.Cause, "{0}\n{1}", failedMsg, err.Cause.ToString());
                else
                    _log.Warning("{0}", failedMsg);
                ReportAndStop(false, err.Message);
                break;
        }
    }

    private void ReportAndStop(bool success, string? errorMessage = null)
    {
        if (_completed)
            return;

        _completed = true;

        var durationMs = (long)(_timeProvider.GetUtcNow() - _dispatchedAt).TotalMilliseconds;
        _pendingHistory = new HistoryRecord(
            FiredAt: _dispatchedAt,
            Success: success,
            DurationMs: durationMs,
            SessionId: _sessionIdValue ?? $"reminder/{_definition.Id}/unknown",
            ErrorMessage: errorMessage);

        if (!success)
        {
            _log.Warning(
                $"ReminderExecution ReportFailed: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success=false error_message={errorMessage}");
        }

        Context.Parent.Tell(new ReminderExecutionCompleted(
            _executionId,
            new ReminderId(_definition.Id),
            success,
            errorMessage));
        Context.Stop(Self);
    }

    private void TrackNotificationResult(ToolResultOutput toolResult)
    {
        if (!string.Equals(toolResult.ToolName, "send_slack_message", StringComparison.Ordinal))
            return;

        _notifyAttempted = true;

        var result = toolResult.Result?.Trim() ?? string.Empty;
        if (result.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            _notifyFailed = true;
            _notifyFailureDetail = result;
            _log.Warning(
                "ReminderExecution NotifyFailed: execution_id={0} reminder_id={1} tool={2} call_id={3} detail={4}",
                _executionId,
                _definition.Id,
                toolResult.ToolName,
                toolResult.CallId,
                result);
            return;
        }

        _notifyFailed = false;
        _notifyFailureDetail = null;
        _log.Info(
            "ReminderExecution NotifySucceeded: execution_id={0} reminder_id={1} tool={2} call_id={3}",
            _executionId,
            _definition.Id,
            toolResult.ToolName,
            toolResult.CallId);
    }

    private string? BuildNotifyFailureMessage()
    {
        if (string.IsNullOrWhiteSpace(_definition.NotifyInstructions))
            return null;

        if (!_notifyAttempted)
            return "Notification instructions were provided but no notification tool was invoked.";

        if (_notifyFailed)
            return _notifyFailureDetail ?? "Notification tool returned an unspecified error.";

        return null;
    }

    protected override void PostStop()
    {
        if (_pendingHistory is not null)
        {
            try
            {
                _historyStore.AppendAsync(new ReminderId(_definition.Id), _pendingHistory)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to write execution history for reminder '{0}'", _definition.Id);
            }
        }

        try
        {
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _materializer?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to dispose reminder execution resources for '{0}'", _definition.Title);
        }
    }

    private void LogFullException(Exception ex, string phase)
    {
        var completedAt = _timeProvider.GetUtcNow();
        var msg = $"{phase}: execution_id={_executionId} reminder_id={_definition.Id} title={_definition.Title} success=false dispatched_at={_dispatchedAt} completed_at={completedAt}\n{ex}";
        _log.Error(ex, "{0}", msg);
    }

    private sealed record ExecutionStarted;
    private sealed record ExecutionOutput(SessionOutput Output);
}
