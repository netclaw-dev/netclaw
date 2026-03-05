using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Reminders;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Singleton actor that mediates between akka-reminders and session execution.
/// Handles scheduling, cancellation, listing, and reminder-fire delivery.
/// </summary>
public sealed class ReminderManagerActor : ReceiveActor
{
    public const string ShardRegionName = "netclaw-reminders";
    public const string EntityId = "manager";

    private readonly ReminderConfig _config;
    private readonly SessionPipeline _pipeline;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;

    private IReminderClient? _client;

    // Concurrency tracking
    private readonly HashSet<ReminderId> _activeExecutions = new();
    private readonly Queue<ReminderPayload> _deferredQueue = new();

    // Failure tracking: consecutive failures per reminder
    private readonly Dictionary<ReminderId, int> _failureCounts = new();

    public ReminderManagerActor(
        ReminderConfig config,
        SessionPipeline pipeline,
        TimeProvider timeProvider)
    {
        _config = config;
        _pipeline = pipeline;
        _timeProvider = timeProvider;
        _log = Context.GetLogger();

        ReceiveAsync<ScheduleReminderCommand>(HandleScheduleAsync);
        ReceiveAsync<CancelReminderCommand>(HandleCancelAsync);
        ReceiveAsync<ListRemindersCommand>(HandleListAsync);
        ReceiveAsync<ReminderPayload>(HandleReminderFiredAsync);
        Receive<ReminderExecutionCompleted>(HandleExecutionCompleted);
    }

    protected override void PreStart()
    {
        var extension = ReminderClientExtension.Get(Context.System);
        _client = extension.CreateClient(new ReminderEntity(ShardRegionName, EntityId));
        _log.Info("ReminderManagerActor started");
    }

    private async Task HandleScheduleAsync(ScheduleReminderCommand cmd)
    {
        var payload = cmd.Payload;
        var key = new ReminderKey(payload.Id.Value);

        try
        {
            Akka.Reminders.ReminderProtocol.ReminderScheduled result;
            DateTimeOffset? nextFire;

            switch (payload.Schedule.Type)
            {
                case ReminderScheduleType.OneShot:
                    nextFire = payload.Schedule.FireAt!.Value;
                    result = await _client!.ScheduleSingleReminderAsync(key, nextFire.Value, payload);
                    break;

                case ReminderScheduleType.Interval:
                    nextFire = payload.Schedule.FireAt ?? _timeProvider.GetUtcNow().Add(payload.Schedule.Interval!.Value);
                    result = await _client!.ScheduleRecurringReminderAsync(
                        key, nextFire.Value, payload.Schedule.Interval!.Value, payload);
                    break;

                case ReminderScheduleType.Cron:
                    nextFire = CronScheduleHelper.GetNextOccurrence(
                        payload.Schedule.CronExpression!, _timeProvider);
                    if (nextFire is null)
                    {
                        Sender.Tell(new ReminderScheduledResponse(payload.Id, payload.Name, null));
                        return;
                    }
                    result = await _client!.ScheduleSingleReminderAsync(key, nextFire.Value, payload);
                    break;

                default:
                    Sender.Tell(new ReminderScheduledResponse(payload.Id, payload.Name, null));
                    return;
            }

            if (result.ResponseCode == ReminderScheduleResponseCode.Success)
            {
                _log.Info("Scheduled reminder '{0}' ({1}), next fire: {2}",
                    payload.Name, payload.Schedule.Type, nextFire);
            }
            else
            {
                _log.Warning("Failed to schedule reminder '{0}': {1}", payload.Name, result.Message);
            }

            Sender.Tell(new ReminderScheduledResponse(payload.Id, payload.Name, nextFire));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error scheduling reminder '{0}'", payload.Name);
            Sender.Tell(new ReminderScheduledResponse(payload.Id, payload.Name, null));
        }
    }

    private async Task HandleCancelAsync(CancelReminderCommand cmd)
    {
        var key = new ReminderKey(cmd.Id.Value);
        try
        {
            var result = await _client!.CancelReminderAsync(key);
            var found = result.ResponseCode == ReminderCancelResponseCode.Success;
            _failureCounts.Remove(cmd.Id);
            _log.Info("Cancel reminder '{0}': {1}", cmd.Id.Value, found ? "found and cancelled" : "not found");
            Sender.Tell(new ReminderCancelledResponse(cmd.Id, found));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error cancelling reminder '{0}'", cmd.Id.Value);
            Sender.Tell(new ReminderCancelledResponse(cmd.Id, false));
        }
    }

    private async Task HandleListAsync(ListRemindersCommand _)
    {
        try
        {
            var result = await _client!.ListRemindersAsync();
            var infos = new List<ReminderInfo>();

            if (result.ResponseCode == FetchRemindersResponseCode.Success)
            {
                foreach (var scheduled in result.Reminders)
                {
                    if (scheduled.Message is ReminderPayload payload)
                    {
                        infos.Add(new ReminderInfo(
                            payload.Id,
                            payload.Name,
                            payload.Prompt,
                            payload.Schedule,
                            scheduled.When,
                            payload.ReportToChannel));
                    }
                }
            }

            Sender.Tell(new ReminderListResponse(infos));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error listing reminders");
            Sender.Tell(new ReminderListResponse([]));
        }
    }

    private async Task HandleReminderFiredAsync(ReminderPayload payload)
    {
        _log.Info("Reminder fired: '{0}' (id={1})", payload.Name, payload.Id.Value);

        // For cron schedules, reschedule the next occurrence immediately
        if (payload.Schedule.Type == ReminderScheduleType.Cron)
        {
            var nextFire = CronScheduleHelper.GetNextOccurrence(
                payload.Schedule.CronExpression!, _timeProvider);
            if (nextFire is not null)
            {
                var key = new ReminderKey(payload.Id.Value);
                await _client!.ScheduleSingleReminderAsync(key, nextFire.Value, payload);
                _log.Info("Rescheduled cron reminder '{0}', next fire: {1}", payload.Name, nextFire);
            }
        }

        // Check concurrency limit
        if (_activeExecutions.Count >= _config.MaxConcurrentExecutions)
        {
            _log.Info("Concurrency limit reached ({0}), deferring reminder '{1}'",
                _config.MaxConcurrentExecutions, payload.Name);
            _deferredQueue.Enqueue(payload);
            return;
        }

        StartExecution(payload);
    }

    private void StartExecution(ReminderPayload payload)
    {
        _activeExecutions.Add(payload.Id);

        var executionActor = Context.ActorOf(
            ReminderExecutionActor.CreateProps(payload, _pipeline, _config, _timeProvider),
            $"exec-{payload.Id.Value}-{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}");

        _log.Info("Started execution actor for reminder '{0}': {1}", payload.Name, executionActor.Path);
    }

    private void HandleExecutionCompleted(ReminderExecutionCompleted completed)
    {
        _activeExecutions.Remove(completed.Id);

        if (completed.Success)
        {
            _failureCounts.Remove(completed.Id);
            _log.Info("Reminder '{0}' execution completed successfully", completed.Id.Value);
        }
        else
        {
            var count = _failureCounts.GetValueOrDefault(completed.Id) + 1;
            _failureCounts[completed.Id] = count;

            _log.Warning("Reminder '{0}' execution failed ({1}/{2}): {3}",
                completed.Id.Value, count, _config.FailurePauseThreshold, completed.ErrorMessage);

            if (count >= _config.FailurePauseThreshold)
            {
                _log.Warning("Reminder '{0}' hit failure threshold ({1}), auto-cancelling",
                    completed.Id.Value, _config.FailurePauseThreshold);
                Self.Tell(new CancelReminderCommand(completed.Id));
                _failureCounts.Remove(completed.Id);
            }
        }

        // Process deferred queue
        if (_deferredQueue.Count > 0 && _activeExecutions.Count < _config.MaxConcurrentExecutions)
        {
            var next = _deferredQueue.Dequeue();
            StartExecution(next);
        }
    }
}
