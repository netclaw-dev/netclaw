// -----------------------------------------------------------------------
// <copyright file="ReminderEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
            .WithTags("Reminders")
            .RequireAuthorization();

        reminders.MapGet("", async ValueTask<Ok<IEnumerable<ReminderSummaryDto>>> (
            IRequiredActor<ReminderManagerActorKey> actor,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var response = await manager.Ask<ReminderListResponse>(
                new ListRemindersCommand(IncludeDisabled: false), TimeSpan.FromSeconds(10), ct);
            var projected = response.Reminders.Select(r => new ReminderSummaryDto(
                Id: r.Id.Value,
                Title: r.Title,
                Enabled: r.Enabled,
                Schedule: ListRemindersTool.DescribeSchedule(r.Schedule),
                NextFire: SetReminderTool.FormatTimestamp(r.NextFire),
                ExpiresAt: r.ExpiresAt is null
                    ? null
                    : SetReminderTool.FormatTimestamp(r.ExpiresAt),
                Audience: r.Audience?.ToWireValue()));
            return TypedResults.Ok(projected);
        })
        .WithName("ListReminders")
        .WithSummary("List all active reminders.");

        reminders.MapPost("", async ValueTask<Results<Ok<ReminderMessageResponse>, BadRequest<ReminderErrorResponse>, ProblemHttpResult>> (
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
                return TypedResults.Problem(
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
                ? TypedResults.BadRequest(new ReminderErrorResponse(result))
                : TypedResults.Ok(new ReminderMessageResponse(result));
        })
        .WithName("CreateReminder")
        .WithSummary("Create a reminder (requires Operator authority).");

        reminders.MapPost("/validate", Results<Ok<ReminderValidationSuccessResponse>, BadRequest<ReminderValidationErrorResponse>> (
            CreateReminderRequest request,
            TimeProvider timeProvider) =>
        {
            var (schedule, error) = ReminderScheduleParser.Parse(
                request.ScheduleType,
                request.Schedule,
                timeProvider);

            if (schedule is null)
                return TypedResults.BadRequest(new ReminderValidationErrorResponse(Valid: false, Error: error));

            return TypedResults.Ok(new ReminderValidationSuccessResponse(
                Valid: true,
                ScheduleType: schedule.Type.ToString(),
                NextFire: schedule.FireAt));
        })
        .WithName("ValidateReminderSchedule")
        .WithSummary("Validate a reminder schedule without persisting it.");

        reminders.MapPost("/import", async ValueTask<Results<Ok<ReminderImportResponse>, BadRequest<ReminderErrorResponse>, JsonHttpResult<ReminderImportErrorResponse>>> (
            ImportReminderRequest request,
            IRequiredActor<ReminderManagerActorKey> actor,
            ClaimsPrincipalMapper mapper,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (request.Definition is null)
                return TypedResults.BadRequest(new ReminderErrorResponse("Reminder definition is required."));

            var authorization = ResolveReminderAuthorizationContext(mapper, httpContext);

            var mode = request.WriteMode?.Trim().ToLowerInvariant() switch
            {
                "replace" => ReminderWriteMode.Replace,
                "upsert" => ReminderWriteMode.Upsert,
                null or "" or "create" or "createonly" => ReminderWriteMode.CreateOnly,
                _ => (ReminderWriteMode?)null
            };

            if (mode is null)
                return TypedResults.BadRequest(new ReminderErrorResponse("Invalid writeMode. Use create, replace, or upsert."));

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

                return TypedResults.Json(
                    new ReminderImportErrorResponse(
                        Error: response.ErrorMessage ?? "Import failed.",
                        Code: response.Error.ToString(),
                        Id: response.Id.Value),
                    statusCode: status);
            }

            return TypedResults.Ok(new ReminderImportResponse(
                Id: response.Id.Value,
                Title: response.Title,
                NextFire: response.NextFire,
                Message: $"Imported reminder '{response.Id.Value}'."));
        })
        .WithName("ImportReminder")
        .WithSummary("Import a reminder definition with the requested write mode.");

        reminders.MapDelete("/{id}", async ValueTask<Results<Ok<ReminderMessageResponse>, NotFound<ReminderErrorResponse>>> (
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
                    ? TypedResults.Ok(new ReminderMessageResponse($"Reminder '{id}' permanently deleted."))
                    : TypedResults.NotFound(new ReminderErrorResponse($"Reminder '{id}' not found."));
            }

            var response = await manager.Ask<ReminderCancelledResponse>(
                new CancelReminderCommand(reminderId),
                TimeSpan.FromSeconds(10), ct);

            return response.Found
                ? TypedResults.Ok(new ReminderMessageResponse($"Reminder '{id}' cancelled (disabled)."))
                : TypedResults.NotFound(new ReminderErrorResponse($"Reminder '{id}' not found."));
        })
        .WithName("DeleteReminder")
        .WithSummary("Cancel a reminder, or permanently delete it with ?permanent=true.");

        reminders.MapPost("/{id}/disable", async ValueTask<Results<Ok<ReminderDisableResponse>, NotFound<ReminderErrorResponse>>> (
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
                ? TypedResults.NotFound(new ReminderErrorResponse(response.ErrorMessage ?? $"Reminder '{id}' not found."))
                : TypedResults.Ok(new ReminderDisableResponse(id, response.Enabled, $"Reminder '{id}' disabled."));
        })
        .WithName("DisableReminder")
        .WithSummary("Disable a reminder without deleting it.");

        reminders.MapPost("/{id}/enable", async ValueTask<Results<Ok<ReminderEnableResponse>, NotFound<ReminderErrorResponse>, BadRequest<ReminderEnableErrorResponse>>> (
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
                return TypedResults.NotFound(new ReminderErrorResponse(response.ErrorMessage ?? $"Reminder '{id}' not found."));
            if (!response.Enabled && !string.IsNullOrWhiteSpace(response.ErrorMessage))
                return TypedResults.BadRequest(new ReminderEnableErrorResponse(response.ErrorMessage, id, Enabled: false));

            return TypedResults.Ok(new ReminderEnableResponse(id, response.Enabled, response.NextFire, $"Reminder '{id}' enabled."));
        })
        .WithName("EnableReminder")
        .WithSummary("Re-enable a previously disabled reminder.");

        reminders.MapGet("/{id}", async ValueTask<Results<Ok<ReminderDetailDto>, NotFound<ReminderErrorResponse>>> (
            string id,
            IRequiredActor<ReminderManagerActorKey> actor,
            CancellationToken ct) =>
        {
            var manager = await actor.GetAsync(ct);
            var response = await manager.Ask<GetReminderResponse>(
                new GetReminderCommand(new ReminderId(id)),
                TimeSpan.FromSeconds(10), ct);

            if (response.Reminder is null)
                return TypedResults.NotFound(new ReminderErrorResponse($"Reminder '{id}' not found."));

            var r = response.Reminder;
            return TypedResults.Ok(new ReminderDetailDto(
                Id: r.Id.Value,
                Title: r.Title,
                Enabled: r.Enabled,
                Schedule: ListRemindersTool.DescribeSchedule(r.Schedule),
                NextFire: SetReminderTool.FormatTimestamp(r.NextFire),
                ExpiresAt: r.ExpiresAt is null
                    ? null
                    : SetReminderTool.FormatTimestamp(r.ExpiresAt),
                Instructions: r.Instructions,
                DeliveryKind: r.Delivery.Kind.ToString().ToLowerInvariant(),
                DeliveryTransport: r.Delivery.Transport,
                DeliveryAddress: r.Delivery.Address,
                DeliveryRequired: r.DeliveryRequired,
                DeliveryInstructions: r.DeliveryInstructions,
                Audience: r.Audience?.ToWireValue()));
        })
        .WithName("GetReminder")
        .WithSummary("Get a single reminder's full definition.");

        reminders.MapGet("/{id}/history", async ValueTask<Results<Ok<IReadOnlyList<HistoryRecord>>, NotFound<ReminderErrorResponse>>> (
            string id,
            int? last,
            ReminderDefinitionStore definitionStore,
            ReminderHistoryStore historyStore,
            CancellationToken ct) =>
        {
            var rid = new ReminderId(id);
            if (!definitionStore.Exists(rid))
                return TypedResults.NotFound(new ReminderErrorResponse($"Reminder '{id}' not found."));

            var maxRecords = Math.Clamp(last ?? 20, 1, 500);
            var records = await historyStore.ReadAsync(rid, maxRecords);
            return TypedResults.Ok(records);
        })
        .WithName("GetReminderHistory")
        .WithSummary("Get recent fire history for a reminder.");

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

/// <summary>Summary projection of a reminder returned by the list endpoint.</summary>
internal sealed record ReminderSummaryDto(
    string Id,
    string Title,
    bool Enabled,
    string Schedule,
    string NextFire,
    string? ExpiresAt,
    string? Audience);

/// <summary>Full reminder projection returned by <c>GET /api/reminders/{id}</c>.</summary>
internal sealed record ReminderDetailDto(
    string Id,
    string Title,
    bool Enabled,
    string Schedule,
    string NextFire,
    string? ExpiresAt,
    string Instructions,
    string DeliveryKind,
    string? DeliveryTransport,
    string? DeliveryAddress,
    bool DeliveryRequired,
    string? DeliveryInstructions,
    string? Audience);

/// <summary>Acknowledgement carrying a human-readable message.</summary>
internal sealed record ReminderMessageResponse(string Message);

/// <summary>Error payload returned when a reminder request fails.</summary>
internal sealed record ReminderErrorResponse(string Error);

/// <summary>Successful schedule validation result.</summary>
internal sealed record ReminderValidationSuccessResponse(bool Valid, string ScheduleType, DateTimeOffset? NextFire);

/// <summary>Failed schedule validation result.</summary>
internal sealed record ReminderValidationErrorResponse(bool Valid, string? Error);

/// <summary>Successful reminder import acknowledgement.</summary>
internal sealed record ReminderImportResponse(string Id, string Title, DateTimeOffset? NextFire, string Message);

/// <summary>Failure detail for a rejected reminder import.</summary>
internal sealed record ReminderImportErrorResponse(string Error, string Code, string Id);

/// <summary>State of a reminder after a disable request.</summary>
internal sealed record ReminderDisableResponse(string Id, bool Enabled, string Message);

/// <summary>State of a reminder after an enable request.</summary>
internal sealed record ReminderEnableResponse(string Id, bool Enabled, DateTimeOffset? NextFire, string Message);

/// <summary>Failure detail for a rejected enable request.</summary>
internal sealed record ReminderEnableErrorResponse(string? Error, string Id, bool Enabled);
