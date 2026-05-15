// -----------------------------------------------------------------------
// <copyright file="ReminderEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Tools;

namespace Netclaw.Daemon.Reminders;

public static class ReminderEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapReminderEndpoints(this IEndpointRouteBuilder app)
    {
        var reminders = app.MapGroup("/api/reminders")
            .RequireAuthorization();

        reminders.MapGet("", async (
            IRequiredActor<ReminderManagerActorKey> actor,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var response = await manager.Ask<ReminderListResponse>(
                new ListRemindersCommand(IncludeDisabled: false), TimeSpan.FromSeconds(10), ct);
            var projected = response.Reminders.Select(r => new
            {
                id = r.Id.Value,
                title = r.Title,
                enabled = r.Enabled,
                schedule = ListRemindersTool.DescribeSchedule(r.Schedule),
                nextFire = SetReminderTool.FormatTimestamp(r.NextFire),
                expiresAt = r.ExpiresAt is null
                    ? null
                    : SetReminderTool.FormatTimestamp(r.ExpiresAt),
                audience = r.Audience?.ToWireValue(),
            });
            return Results.Ok(projected);
        });

        reminders.MapPost("", async (
            CreateReminderRequest request,
            IRequiredActor<ReminderManagerActorKey> actor,
            IServiceProvider serviceProvider,
            ClaimsPrincipalMapper mapper,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var authorization = ResolveReminderAuthorizationContext(mapper, httpContext);

            // Creating a reminder requires Operator authority — ResolveReminderAuthorizationContext
            // returns null for a non-Operator caller. Reject here: the tool-execution context's
            // audience is now required and non-nullable, so a null authorization would otherwise
            // be silently defaulted, smuggling the request past the actor's authority check.
            if (authorization?.SourceAudience is not { } reminderSourceAudience)
                return Results.Problem(
                    detail: "Creating a reminder requires Operator authority.",
                    statusCode: StatusCodes.Status403Forbidden);

            var effectiveId = !string.IsNullOrWhiteSpace(request.Id)
                ? request.Id
                : ReminderIdGenerator.Generate(request.Name).Value;

            var deliveryKind = request.Delivery?.Kind ?? request.DeliveryKind;
            var deliveryTransport = request.Delivery?.Transport ?? request.DeliveryTransport;
            var deliveryAddress = request.Delivery?.Address ?? request.DeliveryAddress;

            var reminderResolvers = serviceProvider.GetServices<IReminderTargetResolver>();
            var restSchedulingConfig = serviceProvider.GetRequiredService<SchedulingConfig>();
            var tool = new SetReminderTool(manager, timeProvider, restSchedulingConfig, reminderResolvers);
            var toolContext = new ToolExecutionContext(sessionId: null, sessionDirectory: null)
            {
                Audience = reminderSourceAudience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromChannelType("manual", reminderSourceAudience),
            };
            toolContext.ChannelType = "manual";
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?>
                {
                    ["Id"] = effectiveId,
                    ["Name"] = request.Name,
                    ["Prompt"] = request.Prompt,
                    ["ScheduleType"] = request.ScheduleType,
                    ["Schedule"] = request.Schedule,
                    ["DeliveryKind"] = deliveryKind,
                    ["DeliveryTransport"] = deliveryTransport,
                    ["DeliveryAddress"] = deliveryAddress,
                    ["DeliveryRequired"] = request.DeliveryRequired,
                    ["DeliveryInstructions"] = request.DeliveryInstructions,
                    ["Audience"] = request.Audience,
                    ["ExpiresIn"] = request.ExpiresIn
                }, toolContext, ct);

            return result.StartsWith("Error", StringComparison.Ordinal)
                ? Results.BadRequest(new { error = result })
                : Results.Ok(new { message = result });
        });

        reminders.MapPost("/validate", (
            CreateReminderRequest request,
            TimeProvider timeProvider) =>
        {
            var (schedule, error) = ReminderScheduleParser.Parse(
                request.ScheduleType,
                request.Schedule,
                timeProvider);

            if (schedule is null)
                return Results.BadRequest(new { valid = false, error });

            return Results.Ok(new { valid = true, scheduleType = schedule.Type.ToString(), nextFire = schedule.FireAt });
        });

        reminders.MapPost("/import", async (
            ImportReminderRequest request,
            IRequiredActor<ReminderManagerActorKey> actor,
            ClaimsPrincipalMapper mapper,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (request.Definition is null)
                return Results.BadRequest(new { error = "Reminder definition is required." });

            var authorization = ResolveReminderAuthorizationContext(mapper, httpContext);

            var mode = request.WriteMode?.Trim().ToLowerInvariant() switch
            {
                "replace" => ReminderWriteMode.Replace,
                "upsert" => ReminderWriteMode.Upsert,
                null or "" or "create" or "createonly" => ReminderWriteMode.CreateOnly,
                _ => (ReminderWriteMode?)null
            };

            if (mode is null)
                return Results.BadRequest(new { error = "Invalid writeMode. Use create, replace, or upsert." });

            var manager = await actor.GetAsync(ct);
            var response = await manager.Ask<ReminderSavedResponse>(
                new SaveReminderCommand(request.Definition, mode.Value, authorization),
                TimeSpan.FromSeconds(10),
                ct);

            if (!response.Success)
            {
                var status = response.Error is ReminderSaveError.Conflict
                    ? StatusCodes.Status409Conflict
                    : response.Error is ReminderSaveError.NotFound
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status400BadRequest;

                return Results.Json(new
                {
                    error = response.ErrorMessage ?? "Import failed.",
                    code = response.Error.ToString(),
                    id = response.Id.Value
                }, statusCode: status);
            }

            return Results.Ok(new
            {
                id = response.Id.Value,
                title = response.Title,
                nextFire = response.NextFire,
                message = $"Imported reminder '{response.Id.Value}'."
            });
        });

        reminders.MapDelete("/{id}", async (
            string id,
            bool? permanent,
            IRequiredActor<ReminderManagerActorKey> actor,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var reminderId = new ReminderId(id);

            if (permanent == true)
            {
                var deleted = await manager.Ask<ReminderDeletedResponse>(
                    new DeleteReminderCommand(reminderId),
                    TimeSpan.FromSeconds(10), ct);

                return deleted.Found
                    ? Results.Ok(new { message = $"Reminder '{id}' permanently deleted." })
                    : Results.NotFound(new { error = $"Reminder '{id}' not found." });
            }

            var response = await manager.Ask<ReminderCancelledResponse>(
                new CancelReminderCommand(reminderId),
                TimeSpan.FromSeconds(10), ct);

            return response.Found
                ? Results.Ok(new { message = $"Reminder '{id}' cancelled (disabled)." })
                : Results.NotFound(new { error = $"Reminder '{id}' not found." });
        });

        reminders.MapPost("/{id}/disable", async (
            string id,
            IRequiredActor<ReminderManagerActorKey> actor,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var response = await manager.Ask<ReminderStateResponse>(
                new DisableReminderCommand(new ReminderId(id)),
                TimeSpan.FromSeconds(10),
                ct);

            return !response.Found
                ? Results.NotFound(new { error = response.ErrorMessage ?? $"Reminder '{id}' not found." })
                : Results.Ok(new { id = id, enabled = response.Enabled, message = $"Reminder '{id}' disabled." });
        });

        reminders.MapPost("/{id}/enable", async (
            string id,
            IRequiredActor<ReminderManagerActorKey> actor,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var response = await manager.Ask<ReminderStateResponse>(
                new EnableReminderCommand(new ReminderId(id)),
                TimeSpan.FromSeconds(10),
                ct);

            if (!response.Found)
                return Results.NotFound(new { error = response.ErrorMessage ?? $"Reminder '{id}' not found." });
            if (!response.Enabled && !string.IsNullOrWhiteSpace(response.ErrorMessage))
                return Results.BadRequest(new { error = response.ErrorMessage, id, enabled = false });

            return Results.Ok(new { id, enabled = response.Enabled, nextFire = response.NextFire, message = $"Reminder '{id}' enabled." });
        });

        reminders.MapGet("/{id}", async (
            string id,
            IRequiredActor<ReminderManagerActorKey> actor,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var response = await manager.Ask<GetReminderResponse>(
                new GetReminderCommand(new ReminderId(id)),
                TimeSpan.FromSeconds(10), ct);

            if (response.Reminder is null)
                return Results.NotFound(new { error = $"Reminder '{id}' not found." });

            var r = response.Reminder;
            return Results.Ok(new
            {
                id = r.Id.Value,
                title = r.Title,
                enabled = r.Enabled,
                schedule = ListRemindersTool.DescribeSchedule(r.Schedule),
                nextFire = SetReminderTool.FormatTimestamp(r.NextFire),
                expiresAt = r.ExpiresAt is null
                    ? null
                    : SetReminderTool.FormatTimestamp(r.ExpiresAt),
                instructions = r.Instructions,
                deliveryKind = r.Delivery.Kind.ToString().ToLowerInvariant(),
                deliveryTransport = r.Delivery.Transport,
                deliveryAddress = r.Delivery.Address,
                deliveryRequired = r.DeliveryRequired,
                deliveryInstructions = r.DeliveryInstructions,
                audience = r.Audience?.ToWireValue(),
            });
        });

        reminders.MapGet("/{id}/history", async (
            string id,
            int? last,
            ReminderDefinitionStore definitionStore,
            ReminderHistoryStore historyStore,
            CancellationToken ct) =>
        {
            var rid = new ReminderId(id);
            if (!definitionStore.Exists(rid))
                return Results.NotFound(new { error = $"Reminder '{id}' not found." });

            var maxRecords = Math.Clamp(last ?? 20, 1, 500);
            var records = await historyStore.ReadAsync(rid, maxRecords);
            return Results.Ok(records);
        });

        return app;
    }

    private static ReminderAudienceAuthorizationContext? ResolveReminderAuthorizationContext(ClaimsPrincipalMapper mapper, HttpContext httpContext)
    {
        var identity = mapper.Map(httpContext.User);
        if (identity.Principal is not PrincipalClassification.Operator)
            return null;

        return new ReminderAudienceAuthorizationContext(
            TrustAudience.Personal,
            $"{identity.Principal}/{identity.Transport}");
    }
}

/// <summary>
/// REST API request body for creating a reminder.
/// </summary>
internal sealed record CreateReminderRequest
{
    public string? Id { get; init; }
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required string ScheduleType { get; init; }
    public required string Schedule { get; init; }
    public string? DeliveryKind { get; init; }
    public string? DeliveryTransport { get; init; }
    public string? DeliveryAddress { get; init; }
    public bool DeliveryRequired { get; init; } = true;
    public string? DeliveryInstructions { get; init; }
    public ReminderDeliveryRequest? Delivery { get; init; }
    public string? Audience { get; init; }
    public string? ExpiresIn { get; init; }
}

internal sealed record ReminderDeliveryRequest
{
    public string? Kind { get; init; }
    public string? Transport { get; init; }
    public string? Address { get; init; }
}

internal sealed record ImportReminderRequest
{
    public required ReminderDefinition Definition { get; init; }
    public string? WriteMode { get; init; }
}
