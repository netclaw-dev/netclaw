// -----------------------------------------------------------------------
// <copyright file="TeamsActivityEndpointExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Schema;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Daemon.Configuration;

internal static class TeamsActivityEndpointExtensions
{
    internal const string ActivityPath = "/api/messages";
    internal const string RateLimitPolicy = "teams-activity";
    internal const string AuthorizationPolicy = "teams-sdk";
    internal const string AuthenticationScheme = "AzureAd";
    internal const int MaxActivityBodyBytes = 64 * 1024;

    public static void AddTeamsIngress(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(nameof(ChannelType.Teams)).Get<TeamsChannelOptions>()
            ?? new TeamsChannelOptions();
        var registration = TeamsIngressRegistration.Evaluate(options);
        if (!registration.CanActivateSdk)
            return;

        RejectConflictingAzureAdConfiguration(builder.Configuration);

        // The native 2.1 host maps the existing Teams configuration.
        builder.Services.AddTeamsBotApplication();
        builder.Services.AddAuthorizationBuilder()
            // The SDK sets AzureAd as the default policy. Restore the upstream
            // scheme-free default so only the Teams endpoint uses AzureAd.
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(AuthorizationPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add(AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
        builder.Services.AddSingleton<TeamsSdkActivityTranslator>();
        builder.Services.AddHttpClient("teams-attachments", client => client.Timeout = Timeout.InfiniteTimeSpan)
            // Standard HTTP logs expose the short-lived URL. The Teams ingress emits safe failure facts.
            .RemoveAllLoggers()
            .AddHttpMessageHandler(() => new TeamsSdkAttachmentDownloader.ResponseStageHandler())
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            });
        builder.Services.AddSingleton<TeamsSdkAttachmentDownloader>();
        builder.Services.AddSingleton<ITeamsAttachmentDownloader>(serviceProvider =>
            serviceProvider.GetRequiredService<TeamsSdkAttachmentDownloader>());
        builder.Services.AddSingleton<ITeamsSdkReplyOperations, TeamsSdkReplyOperations>();
        builder.Services.AddSingleton<ITeamsReplyClient, TeamsSdkReplyClient>();
        builder.Services.AddSingleton<TeamsOutputRenderer>();
        builder.Services.AddSingleton<ITeamsConversationIngressSink, TeamsActorConversationIngressSink>();
        builder.Services.AddSingleton<TeamsIngressActorHost>();
        builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<TeamsIngressActorHost>());
        builder.Services.AddRateLimiter(rateLimitOptions =>
        {
            // Matches the signed Mattermost action budget while keeping a
            // defensive per-source cap independent of Teams platform quotas.
            rateLimitOptions.AddPolicy(RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));
        });
    }

    public static void UseTeamsIngress(this WebApplication app)
    {
        var registration = app.Services.GetRequiredService<TeamsIngressRegistration>();
        if (!registration.CanActivateSdk)
            return;

        var teamsApp = app.Services.GetRequiredService<TeamsBotApplication>();
        var translator = app.Services.GetRequiredService<TeamsSdkActivityTranslator>();
        var attachmentDownloader = app.Services.GetRequiredService<TeamsSdkAttachmentDownloader>();
        var options = app.Services.GetRequiredService<TeamsChannelOptions>();
        var ingress = app.Services.GetRequiredService<TeamsIngressActorHost>();
        var httpContextAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TeamsActivityEndpointExtensions));

        // Use native typed handlers. The adapter translates all SDK types at
        // this HTTP boundary and keeps actors independent of Teams SDK types.
        // CoreActivity omits ReplyToId when it projects to TeamsActivity in
        // SDK 2.1. Preserve this authenticated request value in the copied
        // extension data so the translator can keep reply semantics intact.
        teamsApp.UseMiddleware(new PreserveReplyToActivityIdMiddleware());
        teamsApp.OnAdaptiveCardAction((context, cancellationToken) =>
            HandleAdaptiveCardActionAsync(
                context.Activity,
                ResolveAuthenticatedTenantId(httpContextAccessor),
                cancellationToken));
        teamsApp.OnMessage((context, cancellationToken) =>
            HandleActivityAsync(
                context.Activity,
                ResolveAuthenticatedTenantId(httpContextAccessor),
                cancellationToken));
        teamsApp.OnMessageUpdate((context, cancellationToken) =>
            HandleActivityAsync(
                context.Activity,
                ResolveAuthenticatedTenantId(httpContextAccessor),
                cancellationToken));
        teamsApp.OnMessageDelete((context, cancellationToken) =>
            HandleActivityAsync(
                context.Activity,
                ResolveAuthenticatedTenantId(httpContextAccessor),
                cancellationToken));
        teamsApp.OnConversationUpdate((context, cancellationToken) =>
            HandleActivityAsync(
                context.Activity,
                ResolveAuthenticatedTenantId(httpContextAccessor),
                cancellationToken));

        async Task<InvokeResponse> HandleAdaptiveCardActionAsync(
            InvokeActivity activity,
            string? authenticatedTenantId,
            CancellationToken cancellationToken)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventReceived("activity");
            var resolvedTenantId = ResolveTenantId(activity, authenticatedTenantId);
            var result = translator.Translate(activity, resolvedTenantId);
            if (result.Disposition != TeamsTranslationDisposition.Accepted || result.ApprovalAction is not { } approvalAction)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered(result.ReasonCode);
                RecordRejectedAttachmentDiagnostic(logger, translator, activity, resolvedTenantId, result);
                RecordTranslationTelemetry(result);
                return CreateAdaptiveCardActionResponse(
                    new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected));
            }

            RecordTranslationTelemetry(result);
            var approvalResult = await ingress.SubmitApprovalAsync(approvalAction, cancellationToken);
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(
                $"approval_action_{approvalResult.Disposition.ToString().ToLowerInvariant()}");
            logger.LogInformation(
                "Teams approval action processed: disposition={Disposition}; terminal_card={HasTerminalCard}",
                approvalResult.Disposition,
                approvalResult.TerminalCard is not null);
            return CreateAdaptiveCardActionResponse(approvalResult);
        }

        async Task HandleActivityAsync(
            TeamsActivity activity,
            string? authenticatedTenantId,
            CancellationToken cancellationToken)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventReceived("activity");
            var result = translator.Translate(
                activity,
                ResolveTenantId(activity, authenticatedTenantId));

            if (result.Disposition == TeamsTranslationDisposition.Accepted)
            {
                RecordTranslationTelemetry(result);
                if (result.ApprovalAction is { } approvalAction)
                {
                    var approvalResult = await ingress.SubmitApprovalAsync(approvalAction, cancellationToken);
                    ChannelTelemetry.For(ChannelType.Teams).RecordExtra(
                        $"approval_action_{approvalResult.Disposition.ToString().ToLowerInvariant()}");
                    logger.LogInformation(
                        "Teams approval action processed: disposition={Disposition}; terminal_card={HasTerminalCard}",
                        approvalResult.Disposition,
                        approvalResult.TerminalCard is not null);
                }
                else
                {
                    if (options.AllowAttachments && activity is MessageActivity message)
                        attachmentDownloader.Capture(message, result.Activity!);

                    var routeResult = await ingress.SubmitAsync(result.Activity!, cancellationToken);
                    if (routeResult.Disposition != TeamsIngressRouteDisposition.Routed)
                        ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped($"ingress_{routeResult.Disposition.ToString().ToLowerInvariant()}");
                }
            }
            else
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered(result.ReasonCode);
                RecordRejectedAttachmentDiagnostic(logger, translator, activity, ResolveTenantId(activity, authenticatedTenantId), result);
                RecordTranslationTelemetry(result);
            }
        }
    }

    private sealed class PreserveReplyToActivityIdMiddleware : ITurnMiddleware
    {
        public Task OnTurnAsync(
            BotApplication botApplication,
            CoreActivity activity,
            NextTurn nextTurn,
            CancellationToken cancellationToken = default)
        {
            activity.Properties.Remove(TeamsSdkActivityTranslator.PreservedReplyToActivityIdProperty);
            if (!string.IsNullOrWhiteSpace(activity.ReplyToId))
            {
                activity.Properties[TeamsSdkActivityTranslator.PreservedReplyToActivityIdProperty] = activity.ReplyToId;
            }

            return nextTurn(cancellationToken);
        }
    }

    private static InvokeResponse CreateAdaptiveCardActionResponse(TeamsApprovalActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var message = result.Disposition switch
        {
            TeamsApprovalActionDisposition.Accepted => "Approval resolved.",
            TeamsApprovalActionDisposition.Expired => "This approval has expired.",
            TeamsApprovalActionDisposition.AlreadyProcessed => "This approval was already processed.",
            TeamsApprovalActionDisposition.Unavailable => "This approval is no longer available.",
            _ => "This approval is unavailable."
        };
        var approvalCard = result.TerminalCard ?? TeamsApprovalCardRenderer.CreateTerminal(message);
        var payload = TeamsAdaptiveCardPayloadBuilder.Create(approvalCard);
        // An Action.Execute invoke can replace its source card. The native
        // response shape disables the actionable prompt after resolution.
        return AdaptiveCardResponse.CreateCardResponse(payload);
    }

    internal static void RecordTranslationTelemetry(TeamsTranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var telemetryCode = result.ReasonCode switch
        {
            "plain_text_accepted" => "plain_text_accepted",
            "teams_text_rendering_wrapper_ignored" => "teams_text_rendering_wrapper_ignored",
            "teams_attachment_received" => "attachment_received",
            "graph_backed_attachment_unsupported" => "attachment_graph_backed_rejected",
            "unsupported_attachment_shape" => "attachment_shape_rejected",
            "attachment_malformed_rejected" => "attachment_malformed_rejected",
            _ => null
        };
        if (telemetryCode is not null)
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(telemetryCode);
    }

    private static void RecordRejectedAttachmentDiagnostic(
        ILogger logger,
        TeamsSdkActivityTranslator translator,
        TeamsActivity activity,
        string? authenticatedTenantId,
        TeamsTranslationResult result)
    {
        var diagnostic = translator.DescribeRejectedAttachment(activity, authenticatedTenantId, result);
        if (diagnostic is null)
            return;

        foreach (var attachment in diagnostic.Attachments)
        {
            logger.LogWarning(
                "Teams attachment diagnostic: scope={Scope}; tenant_match={TenantMatch}; team_match={TeamMatch}; channel_match={ChannelMatch}; sender_match={SenderMatch}; mentioned={Mentioned}; root_activity_valid={RootActivityValid}; audience_valid={AudienceValid}; policy_reason={PolicyReason}; attachment_count={AttachmentCount}; attachment_index={AttachmentIndex}; attachment_content_type={AttachmentContentType}; attachment_content_kind={AttachmentContentKind}; attachment_content_url_exists={AttachmentContentUrlExists}; attachment_name_exists={AttachmentNameExists}; attachment_thumbnail_exists={AttachmentThumbnailExists}; classifier_disposition={ClassifierDisposition}; resolved_inbound_attachment_kind={ResolvedInboundAttachmentKind}; mention_count={MentionCount}; reply_to_id_exists={ReplyToIdExists}",
                diagnostic.Scope,
                diagnostic.TenantMatch,
                diagnostic.TeamMatch,
                diagnostic.ChannelMatch,
                diagnostic.SenderMatch,
                diagnostic.Mentioned,
                diagnostic.RootActivityValid,
                diagnostic.AudienceValid,
                diagnostic.PolicyReason,
                diagnostic.AttachmentCount,
                attachment.Index,
                attachment.ContentType,
                attachment.ContentKind,
                attachment.HasContentUrl,
                attachment.HasName,
                attachment.HasThumbnail,
                attachment.ClassifierDisposition,
                attachment.ResolvedInboundAttachmentKind,
                diagnostic.MentionCount,
                diagnostic.ReplyToIdExists);
        }
    }

    /// <summary>
    /// Resolves the tenant asserted by the Teams JWT when it supplies one, with
    /// the platform conversation tenant as the authenticated-activity fallback.
    /// Bot Framework service JWTs do not carry a tenant claim for all valid
    /// Teams deliveries. This runs only after the native Teams host has
    /// authenticated the request; the translator then requires the resolved
    /// tenant to match the operator-configured tenant and rejects a conflicting
    /// conversation tenant.
    /// </summary>
    internal static string? ResolveTenantId(TeamsActivity activity, string? sdkTenantId)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return string.IsNullOrWhiteSpace(sdkTenantId)
            ? activity.Conversation?.TenantId
            : sdkTenantId;
    }

    public static void MapTeamsActivityEndpoint(this WebApplication app)
    {
        var registration = app.Services.GetRequiredService<TeamsIngressRegistration>();
        if (!registration.CanActivateSdk)
            return;

        // Route-group conventions let the native host own POST parsing and
        // dispatch while this boundary retains the authenticated route and the
        // existing per-source rate budget. The body guard runs before routing.
        app.MapGroup(string.Empty)
            .RequireAuthorization(AuthorizationPolicy)
            .RequireRateLimiting(RateLimitPolicy)
            .UseTeamsBotApplication(ActivityPath.TrimStart('/'));
    }

    private static string? ResolveAuthenticatedTenantId(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);

        var user = httpContextAccessor.HttpContext?.User;
        return user?.FindFirst("tid")?.Value
               ?? user?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
    }

    private static void RejectConflictingAzureAdConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.IsNullOrWhiteSpace(configuration["AzureAd:ClientId"]))
        {
            throw new InvalidOperationException(
                "Teams configuration cannot activate when AzureAd:ClientId is set. " +
                "Remove the conflicting AzureAd configuration before enabling Teams.");
        }
    }
}
