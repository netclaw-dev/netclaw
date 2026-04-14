using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for scheduling reminders. Supports one-shot, interval, and cron schedules.
/// </summary>
[NetclawTool("set_reminder",
    "Schedule or update a reminder by ID. If a reminder with the given ID already exists it will be updated (upsert). " +
    "Supports relative durations ('30m', '2h'), interval schedules ('every 6h'), and cron ('0 */6 * * *').",
    Grant = "scheduling")]
public sealed partial class SetReminderTool : NetclawTool<SetReminderTool.Params>
{
    private readonly IActorRef _reminderManager;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderConfig _config;
    private readonly IReminderTargetResolver? _targetResolver;

    public record Params(
        [property: Description("Stable identifier for this reminder (kebab-case slug, e.g. 'daily-standup'). If a reminder with this ID exists it will be updated.")]
        string Id,
        [property: Description("A short human-readable title for this reminder.")]
        string Name,
        [property: Description("Execution instructions for this reminder.")]
        string Prompt,
        [property: Description("Schedule type: 'once', 'interval', or 'cron'.")]
        string ScheduleType,
        [property: Description("Schedule value: relative time, ISO 8601 datetime, interval duration, or cron expression.")]
        string Schedule,
        [property: Description("Optional notification target. Accepts raw channel/user IDs, '#channel-name', or '@username'. Omit for current-session targeting.")]
        string? ReportToChannel = null,
        [property: Description("Optional notify instructions describing how Netclaw should report results.")]
        string? NotifyInstructions = null,
        [property: Description("Notification policy: 'required' (default, fail if no notification sent) or 'conditional' (OK to skip notification if nothing actionable).")]
        string? NotifyPolicy = null,
        [property: Description("Trust audience for this reminder's execution: 'personal' (all tools including web_search, shell), 'team' (restricted tools), or 'public' (minimal tools). Omit to inherit the creating session/channel audience.")]
        string? Audience = null);

    public SetReminderTool(
        IActorRef reminderManager,
        TimeProvider timeProvider,
        ReminderConfig config,
        IReminderTargetResolver? targetResolver = null)
    {
        _reminderManager = reminderManager;
        _timeProvider = timeProvider;
        _config = config;
        _targetResolver = targetResolver;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Id))
            return "Error: 'id' is required.";
        if (string.IsNullOrWhiteSpace(args.Name))
            return "Error: 'name' is required.";
        if (string.IsNullOrWhiteSpace(args.Prompt))
            return "Error: 'prompt' is required.";
        if (string.IsNullOrWhiteSpace(args.Schedule))
            return "Error: 'schedule' is required.";

        // Normalize ID at the tool boundary: lowercase, kebab-case, length cap
        var normalizedId = ReminderIdGenerator.Normalize(args.Id);

        var (schedule, error) = ReminderScheduleParser.Parse(
            args.ScheduleType,
            args.Schedule,
            _timeProvider,
            _config);

        if (schedule is null)
            return $"Error: {error}";

        var id = new ReminderId(normalizedId);
        var now = _timeProvider.GetUtcNow();

        string? sessionId = null;
        string? reportToChannel = args.ReportToChannel;
        string? reportToThreadTs = null;
        var resolvedTargetKind = ReminderTargetKind.Unknown;

        if (!string.IsNullOrWhiteSpace(reportToChannel))
        {
            if (_targetResolver is null)
                return $"Error: No notification channel transport is configured; cannot target '{reportToChannel}'.";

            var resolution = await _targetResolver.ResolveAsync(reportToChannel, ct);
            if (!resolution.Success)
            {
                var detail = resolution.ErrorMessage ?? "unresolvable target";
                return $"Error: Could not resolve reportToChannel '{reportToChannel}': {detail}. Use #channel, @user, or a valid channel ID.";
            }

            if (string.IsNullOrWhiteSpace(resolution.ResolvedId))
                return $"Error: Could not resolve reportToChannel '{reportToChannel}': resolver returned an empty canonical target ID.";

            reportToChannel = resolution.ResolvedId;
            resolvedTargetKind = resolution.Kind;
        }
        else if (context.SessionId is not null)
        {
            sessionId = context.SessionId;

            var parts = context.SessionId.Split('/');
            if (parts.Length >= 2)
            {
                reportToChannel = parts[0];
                reportToThreadTs = parts[1];
                resolvedTargetKind = ReminderTargetKind.Channel;
            }
        }

        var notifyInstructions = args.NotifyInstructions;
        if (string.IsNullOrWhiteSpace(notifyInstructions))
        {
            notifyInstructions = reportToChannel is null
                ? "Reply in the originating session thread with a concise result."
                : resolvedTargetKind switch
                {
                    ReminderTargetKind.User => $"Send a direct message to user {reportToChannel} with your findings, or lack thereof.",
                    ReminderTargetKind.Channel => $"Post the result to channel {reportToChannel}.",
                    _ => $"Send the result to target {reportToChannel}."
                };
        }

        var notifyPolicy = Enum.TryParse<NotificationPolicy>(args.NotifyPolicy, ignoreCase: true, out var parsed)
            ? parsed
            : NotificationPolicy.Required;

        TrustAudience? audience = null;
        if (!string.IsNullOrWhiteSpace(args.Audience))
        {
            if (!SecurityPolicyDefaults.TryParseAudience(args.Audience, out var parsedAudience))
                return $"Error: Invalid audience '{args.Audience}'. Use 'personal', 'team', or 'public'.";
            audience = parsedAudience;
        }

        TrustAudience? sourceAudience = null;
        if (!string.IsNullOrWhiteSpace(context.Audience))
        {
            if (!SecurityPolicyDefaults.TryParseAudience(context.Audience, out var parsedSourceAudience))
                return $"Error: Invalid source audience '{context.Audience}' in tool execution context.";

            sourceAudience = parsedSourceAudience;
        }

        var definition = new ReminderDefinition
        {
            Id = id.Value,
            Title = args.Name,
            Schedule = schedule,
            Instructions = args.Prompt,
            NotifyInstructions = notifyInstructions,
            NotifyPolicy = notifyPolicy,
            Audience = audience,
            Enabled = true,
            SessionId = sessionId,
            ReportToChannel = reportToChannel,
            ReportToThreadTs = reportToThreadTs,
            CreatedBy = "llm-tool",
            CreatedAt = now,
            UpdatedAt = now
        };

        var response = await _reminderManager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                ReminderWriteMode.Upsert,
                new ReminderAudienceAuthorizationContext(sourceAudience, context.SessionId ?? context.ChannelType)),
            TimeSpan.FromSeconds(10),
            ct);

        if (!response.Success)
        {
            var message = response.ErrorMessage ?? "unknown error";
            return response.Error == ReminderSaveError.Validation
                ? $"Error: {message}"
                : $"Failed to schedule reminder '{args.Name}': {message}";
        }

        var nextFireStr = FormatNextFire(response.NextFire);

        var scheduleDesc = schedule.Type switch
        {
            ReminderScheduleType.OneShot => $"Runs once. Next execution: {nextFireStr}.",
            ReminderScheduleType.Interval => $"Runs every {FormatInterval(schedule.Interval!.Value)}. Next execution: {nextFireStr}.",
            ReminderScheduleType.Cron => $"Runs {CronScheduleHelper.Describe(schedule.CronExpression!)}. Next execution: {nextFireStr}.",
            _ => $"Next execution: {nextFireStr}."
        };

        return $"Reminder '{args.Name}' scheduled. {scheduleDesc} ID: {id.Value}";
    }

    private static string FormatInterval(TimeSpan interval) => interval.TotalHours switch
    {
        >= 24 when interval.TotalHours % 24 == 0 => $"{interval.TotalDays:F0} day(s)",
        >= 1 when interval.TotalMinutes % 60 == 0 => $"{interval.TotalHours:F0}h",
        _ => $"{interval.TotalMinutes:F0}m"
    };

    public static string FormatNextFire(DateTimeOffset? nextFire)
    {
        if (nextFire is not { } nf)
            return "unknown";

        var local = nf.ToLocalTime();
        var tz = TimeZoneInfo.Local;
        var tzAbbrev = tz.IsDaylightSavingTime(local) ? tz.DaylightName : tz.StandardName;

        return $"{local:dddd, MMMM d 'at' h:mm tt} {tzAbbrev} ({nf:u})";
    }
}
