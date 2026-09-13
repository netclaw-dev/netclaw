// -----------------------------------------------------------------------
// <copyright file="SetReminderTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// LLM tool for scheduling reminders. Supports one-shot, interval, and cron schedules.
/// </summary>
[NetclawTool("set_reminder",
    "Schedule or update a reminder by ID. If a reminder with the given ID already exists it will be updated (upsert). " +
    "Supports relative durations ('30m', '2h'), interval schedules ('every 6h'), and cron ('0 */6 * * *'). " +
    "Cron schedules evaluate in UTC by default; prefix the expression with 'CRON_TZ=<time-zone-id>' " +
    "(e.g. 'CRON_TZ=Europe/Brussels 0 9 * * *') to evaluate it in a specific time zone. " +
    "The time zone id must be an IANA identifier without spaces (e.g. 'Europe/Brussels', 'America/New_York'); " +
    "Windows names like 'Eastern Standard Time' are not supported.",
    Grant = "scheduling")]
[ToolArgumentVariant("DeliveryKind", "current_session", Forbidden = ["DeliveryTransport", "DeliveryAddress"])]
[ToolArgumentVariant("DeliveryKind", "channel", Required = ["DeliveryTransport", "DeliveryAddress"])]
[ToolArgumentVariant("DeliveryKind", "none", Forbidden = ["DeliveryTransport", "DeliveryAddress"])]
public sealed partial class SetReminderTool : NetclawTool<SetReminderTool.Params>
{
    private readonly IActorRef _reminderManager;
    private readonly TimeProvider _timeProvider;
    private readonly SchedulingConfig _schedulingConfig;
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
        [property: Description("Schedule value: relative time, ISO 8601 datetime, interval duration, or cron expression (optional 'CRON_TZ=<IANA-time-zone-id>' prefix for timezone-aware cron, e.g. 'CRON_TZ=Europe/Brussels 0 9 * * *').")]
        string Schedule,
        [property: Description("How to deliver results: 'current_session' (reply here), 'channel' (post to a target), or 'none' (silent execution).")]
        string? DeliveryKind = null,
        [property: Description("Transport for channel delivery (e.g., 'slack' or 'discord'). Required when delivery_kind='channel'.")]
        string? DeliveryTransport = null,
        [property: Description("Target for channel delivery (e.g., '#general', '@user', '<@discordUserId>', channel ID). Required when delivery_kind='channel'.")]
        string? DeliveryAddress = null,
        [property: Description("Fail execution if delivery doesn't succeed. Default true. Set false for audit/cleanup tasks.")]
        bool DeliveryRequired = true,
        [property: Description("Optional guidance for what to include in the delivery to the user. Content guidance only.")]
        string? DeliveryInstructions = null,
        [property: Description("Trust audience for this reminder's execution: 'personal' (all tools including web_search, shell), 'team' (restricted tools), or 'public' (minimal tools). Omit to inherit the creating session/channel audience.")]
        string? Audience = null,
        [property: Description("Optional expiration for recurring reminders. After this duration, the reminder auto-disables. Use relative time like '24h', '7d'. Recommended for task-specific reminders (e.g., CI checks, deploy monitoring).")]
        string? ExpiresIn = null);

    public SetReminderTool(
        IActorRef reminderManager,
        TimeProvider timeProvider,
        SchedulingConfig schedulingConfig,
        IEnumerable<IReminderTargetResolver>? targetResolvers = null)
    {
        _reminderManager = reminderManager;
        _timeProvider = timeProvider;
        _schedulingConfig = schedulingConfig;

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

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!_schedulingConfig.Enabled)
            return "Error: Scheduling is disabled for this deployment.";

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
                    return $"Error: current_session delivery requires a channel with a gateway (Slack, Discord, Tui, SignalR). Current channel type: {context.ChannelType ?? "unknown"}.";

                if (parsedChannelType is not (ChannelType.Slack or ChannelType.Discord or ChannelType.Tui or ChannelType.SignalR))
                    return $"Error: current_session delivery requires a channel with a gateway (Slack, Discord, Tui, SignalR). Current channel type: {parsedChannelType}.";

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
                    var hint = string.Equals(transport, "discord", StringComparison.OrdinalIgnoreCase)
                        ? "Use channel:<channelId>, <#channelId>, dm:<userId>, or <@userId>."
                        : "Use #channel, @user, or a valid channel ID.";
                    return $"Error: Could not resolve delivery_address '{rawDeliveryAddress}': {detail}. {hint}";
                }

                if (string.IsNullOrWhiteSpace(resolution.ResolvedId))
                    return $"Error: Could not resolve delivery_address '{rawDeliveryAddress}': resolver returned an empty canonical target ID.";

                delivery = new ReminderDelivery
                {
                    Kind = DeliveryKind.Channel,
                    Transport = transport,
                    Address = resolution.ResolvedId,
                    Target = BuildDeliveryTarget(transport, resolution)
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

        // Audience is already parsed on the execution context — no wire-string
        // parse, no parse-failure fallback.
        var sourceAudience = context.Audience;
        var effectiveRequestedAudience = audience ?? sourceAudience;

        TrustBoundary? boundary = context.Boundary;

        // When a reminder explicitly downscopes its audience, it must not carry
        // over the creating session's broader boundary. Recompute the boundary
        // from the requested audience instead.
        if (audience is { } explicitAudience && explicitAudience != sourceAudience)
        {
            boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(explicitAudience);
        }
        else if (boundary is null)
        {
            boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(effectiveRequestedAudience);
        }

        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(args.ExpiresIn))
        {
            if (schedule.Type == ReminderScheduleType.OneShot)
                return "Error: expires_in is not applicable to one-shot reminders.";

            var expDuration = ReminderScheduleParser.ParseDuration(args.ExpiresIn);
            if (expDuration is null)
                return $"Error: Cannot parse expires_in '{args.ExpiresIn}'. Use format like '24h', '7d'.";
            expiresAt = now.Add(expDuration.Value);
        }

        var definition = new ReminderDefinition
        {
            Id = id,
            Title = args.Name,
            Schedule = schedule,
            Instructions = args.Prompt,
            Delivery = delivery,
            DeliveryRequired = args.DeliveryRequired,
            DeliveryInstructions = args.DeliveryInstructions,
            // Draft trust context — ReminderManagerActor re-resolves and
            // re-authorizes Audience/Boundary before persisting. Prefer the
            // explicitly requested audience, then the creating session's.
            Audience = effectiveRequestedAudience,
            Boundary = boundary ?? TrustBoundary.Public,
            Enabled = true,
            ExpiresAt = expiresAt,
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

        var nextFireStr = FormatTimestamp(response.NextFire);

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

        var expiresDesc = expiresAt is not null
            ? $" Expires: {FormatTimestamp(expiresAt)}."
            : "";

        return $"Reminder '{args.Name}' scheduled. {scheduleDesc} {deliveryDesc}{expiresDesc} ID: {id.Value}";
    }

    private static string FormatInterval(TimeSpan interval) => interval.TotalHours switch
    {
        >= 24 when interval.TotalHours % 24 == 0 => $"{interval.TotalDays:F0} day(s)",
        >= 1 when interval.TotalMinutes % 60 == 0 => $"{interval.TotalHours:F0}h",
        _ => $"{interval.TotalMinutes:F0}m"
    };

    private static ChannelDeliveryTargetInfo BuildDeliveryTarget(
        string transport,
        ReminderTargetResolution resolution)
    {
        var destinationKind = resolution.Kind == ReminderTargetKind.User
            ? "direct_message"
            : "destination";

        return new ChannelDeliveryTargetInfo(
            transport,
            destinationKind,
            NormalizeDeliveryTargetId(transport, resolution),
            resolution.ResolvedId);
    }

    private static string NormalizeDeliveryTargetId(string transport, ReminderTargetResolution resolution)
    {
        var resolvedId = resolution.ResolvedId
            ?? throw new InvalidOperationException("Cannot build a delivery target from an empty reminder target resolution.");

        if (!string.Equals(transport, "mattermost", StringComparison.OrdinalIgnoreCase))
            return resolvedId;

        return resolution.Kind switch
        {
            ReminderTargetKind.Channel when resolvedId.StartsWith("channel:", StringComparison.OrdinalIgnoreCase)
                => resolvedId[8..],
            ReminderTargetKind.User when resolvedId.StartsWith('@')
                => resolvedId[1..],
            _ => resolvedId
        };
    }

    public static string FormatTimestamp(DateTimeOffset? nextFire)
    {
        if (nextFire is not { } nf)
            return "unknown";

        var local = nf.ToLocalTime();
        var tz = TimeZoneInfo.Local;
        var tzAbbrev = tz.IsDaylightSavingTime(local) ? tz.DaylightName : tz.StandardName;

        return $"{local:dddd, MMMM d 'at' h:mm tt} {tzAbbrev} ({nf:u})";
    }
}
