using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Channels;
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
    private readonly IReadOnlyDictionary<string, IReminderTargetResolver> _resolversByTransport;

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
        [property: Description("How to deliver results: 'current_session' (reply in this conversation), 'channel' (post to a specific target), or 'none' (silent execution). Required unless `delivery.kind` is provided.")]
        string? DeliveryKind = null,
        [property: Description("Transport for channel delivery (e.g., 'slack'). Required when delivery_kind='channel'.")]
        string? DeliveryTransport = null,
        [property: Description("Target for channel delivery (e.g., '#general', '@user', channel ID). Required when delivery_kind='channel'.")]
        string? DeliveryAddress = null,
        [property: Description("Fail execution if delivery doesn't succeed. Default true. Set false for audit/cleanup tasks.")]
        bool DeliveryRequired = true,
        [property: Description("Optional guidance for what to include in the delivery to the user. Content guidance only.")]
        string? DeliveryInstructions = null,
        [property: Description("Trust audience for this reminder's execution: 'personal' (all tools including web_search, shell), 'team' (restricted tools), or 'public' (minimal tools). Omit to inherit the creating session/channel audience.")]
        string? Audience = null);

    public SetReminderTool(
        IActorRef reminderManager,
        TimeProvider timeProvider,
        IEnumerable<IReminderTargetResolver>? targetResolvers = null)
    {
        _reminderManager = reminderManager;
        _timeProvider = timeProvider;

        // Build transport -> resolver dictionary, detecting duplicates
        var resolvers = new Dictionary<string, IReminderTargetResolver>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolver in targetResolvers ?? [])
        {
            if (!resolvers.TryAdd(resolver.Transport, resolver))
                throw new InvalidOperationException(
                    $"Duplicate IReminderTargetResolver registration for transport '{resolver.Transport}'. " +
                    "Each transport may only have one resolver.");
        }
        _resolversByTransport = resolvers;
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
        var rawDeliveryKind = args.DeliveryKind;
        var rawDeliveryTransport = args.DeliveryTransport;
        var rawDeliveryAddress = args.DeliveryAddress;

        if (string.IsNullOrWhiteSpace(rawDeliveryKind))
            return "Error: 'delivery_kind' is required. Use 'current_session', 'channel', or 'none'.";

        // Parse delivery kind
        if (!Enum.TryParse<DeliveryKind>(rawDeliveryKind.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var deliveryKind))
            return $"Error: Invalid delivery_kind '{rawDeliveryKind}'. Use 'current_session', 'channel', or 'none'.";

        // Normalize ID at the tool boundary: lowercase, kebab-case, length cap
        var normalizedId = ReminderIdGenerator.Normalize(args.Id);

        var (schedule, error) = ReminderScheduleParser.Parse(
            args.ScheduleType,
            args.Schedule,
            _timeProvider);

        if (schedule is null)
            return $"Error: {error}";

        var id = new ReminderId(normalizedId);
        var now = _timeProvider.GetUtcNow();

        // Build delivery struct based on kind
        ReminderDelivery delivery;
        switch (deliveryKind)
        {
            case DeliveryKind.CurrentSession:
            {
                // Reject if transport or address supplied
                if (!string.IsNullOrWhiteSpace(rawDeliveryTransport) || !string.IsNullOrWhiteSpace(rawDeliveryAddress))
                    return "Error: delivery_transport and delivery_address are invalid for current_session delivery. Omit them.";

                // Require addressable session context
                if (string.IsNullOrWhiteSpace(context.SessionId))
                    return "Error: current_session delivery requires an active session context.";

                if (!ChannelTypeExtensions.TryFromWireValue(context.ChannelType, out var parsedChannelType))
                    return $"Error: current_session delivery requires a channel with a gateway (Slack, Tui, SignalR). Current channel type: {context.ChannelType ?? "unknown"}.";

                if (parsedChannelType is not (ChannelType.Slack or ChannelType.Tui or ChannelType.SignalR))
                    return $"Error: current_session delivery requires a channel with a gateway (Slack, Tui, SignalR). Current channel type: {parsedChannelType}.";

                delivery = new ReminderDelivery
                {
                    Kind = DeliveryKind.CurrentSession,
                    SessionId = context.SessionId,
                    OriginChannelType = parsedChannelType
                };
                break;
            }

            case DeliveryKind.Channel:
            {
                // Require both transport and address
                if (string.IsNullOrWhiteSpace(rawDeliveryTransport))
                    return "Error: delivery_transport is required for channel delivery (e.g., 'slack').";
                if (string.IsNullOrWhiteSpace(rawDeliveryAddress))
                    return "Error: delivery_address is required for channel delivery (e.g., '#general', '@user').";

                var transport = rawDeliveryTransport.Trim().ToLowerInvariant();

                // Reject session-only transports (SignalR/TUI don't have channel notification tools)
                if (transport == ChannelType.SignalR.ToWireValue() || transport == ChannelType.Tui.ToWireValue())
                    return $"Error: Transport '{transport}' does not support channel delivery. Use current_session instead.";

                // Look up resolver by transport
                if (!_resolversByTransport.TryGetValue(transport, out var resolver))
                {
                    var available = _resolversByTransport.Count > 0
                        ? string.Join(", ", _resolversByTransport.Keys)
                        : "none";
                    return $"Error: Unknown transport '{transport}'. Available transports: [{available}].";
                }

                // Resolve address to canonical ID
                var resolution = await resolver.ResolveAsync(rawDeliveryAddress, ct);
                if (!resolution.Success)
                {
                    var detail = resolution.ErrorMessage ?? "unresolvable target";
                    return $"Error: Could not resolve delivery_address '{rawDeliveryAddress}': {detail}. Use #channel, @user, or a valid channel ID.";
                }

                if (string.IsNullOrWhiteSpace(resolution.ResolvedId))
                    return $"Error: Could not resolve delivery_address '{rawDeliveryAddress}': resolver returned an empty canonical target ID.";

                delivery = new ReminderDelivery
                {
                    Kind = DeliveryKind.Channel,
                    Transport = transport,
                    Address = resolution.ResolvedId
                };
                break;
            }

            case DeliveryKind.None:
            {
                // Reject if transport or address supplied
                if (!string.IsNullOrWhiteSpace(rawDeliveryTransport) || !string.IsNullOrWhiteSpace(rawDeliveryAddress))
                    return "Error: delivery_transport and delivery_address are invalid for none delivery. Omit them.";

                delivery = new ReminderDelivery
                {
                    Kind = DeliveryKind.None
                };
                break;
            }

            default:
                return $"Error: Unhandled delivery kind '{deliveryKind}'.";
        }

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

        string? boundary = null;
        if (!string.IsNullOrWhiteSpace(context.Boundary))
            boundary = context.Boundary.Trim();

        if (string.IsNullOrWhiteSpace(boundary) && sourceAudience is { } resolvedSourceAudience)
            boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(resolvedSourceAudience);

        var definition = new ReminderDefinition
        {
            Id = id.Value,
            Title = args.Name,
            Schedule = schedule,
            Instructions = args.Prompt,
            Delivery = delivery,
            DeliveryRequired = args.DeliveryRequired,
            DeliveryInstructions = args.DeliveryInstructions,
            Audience = audience,
            Boundary = boundary,
            Enabled = true,
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

        var deliveryDesc = deliveryKind switch
        {
            DeliveryKind.CurrentSession => "Delivery: reply to this session.",
            DeliveryKind.Channel => $"Delivery: post to {delivery.Transport} target {delivery.Address}.",
            DeliveryKind.None => "Delivery: none (silent execution).",
            _ => ""
        };

        return $"Reminder '{args.Name}' scheduled. {scheduleDesc} {deliveryDesc} ID: {id.Value}";
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
