// -----------------------------------------------------------------------
// <copyright file="MattermostActionEndpointExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Threading.RateLimiting;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Configuration;

public static class MattermostActionEndpointExtensions
{
    internal const int MaxCallbackBodyBytes = 16 * 1024;
    internal const string CallbackRateLimitPolicy = "mattermost-action-callback";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static void MapMattermostActionEndpoint(this WebApplication app)
    {
        // The interactive callback endpoint is only exposed when the Mattermost
        // channel is enabled AND interactive approvals are configured (a non-empty
        // CallbackUrl). When that is not the case, no inbound HTTP surface is
        // registered for the channel — approvals use the text-reply fallback
        // instead. Authentication on this endpoint is by single-use opaque
        // action token (32 bytes RNG, server-stored in MattermostCallbackActionStore,
        // consumed on first hit, 12h TTL, channel-bound) — see that store for the
        // mint/consume lifecycle. The spec netclaw-gateway-security:
        // "Channel-owned interactive callback endpoint safeguards" describes the
        // forge-rejection property.
        var options = app.Services.GetService<MattermostChannelOptions>();
        if (options is null
            || !options.Enabled
            || string.IsNullOrWhiteSpace(options.CallbackUrl))
        {
            return;
        }

        app.MapPost("/api/mattermost/actions", async ValueTask<Results<BadRequest<string>, StatusCodeHttpResult, JsonHttpResult<ActionCallbackResponse>>> (
            HttpContext httpContext,
            IRequiredActor<MattermostGatewayActorKey> gatewayActor,
            TimeProvider timeProvider,
            MattermostChannelOptions options,
            MattermostCallbackActionStore actionStore,
            ILogger<MattermostChannel> logger,
            CancellationToken ct) =>
        {
            ActionCallbackPayload? payload;
            try
            {
                payload = await ReadPayloadAsync(httpContext.Request, ct);
            }
            catch (JsonException)
            {
                return TypedResults.BadRequest("Invalid JSON payload.");
            }

            if (payload is null)
                return TypedResults.BadRequest("Invalid JSON payload.");

            if (payload.RawBodyLength > MaxCallbackBodyBytes)
                return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

            if (string.IsNullOrEmpty(payload.UserId)
                || string.IsNullOrEmpty(payload.PostId)
                || string.IsNullOrEmpty(payload.ChannelId))
            {
                return TypedResults.BadRequest("Missing required fields: user_id, post_id, channel_id.");
            }

            if (payload.Context is null
                || !payload.Context.TryGetValue("action_token", out var actionToken)
                || string.IsNullOrWhiteSpace(actionToken))
            {
                return TypedResults.BadRequest("Missing required context field: action_token.");
            }

            if (!actionStore.TryGet(actionToken, out var storedAction)
                || storedAction is null)
            {
                logger.LogWarning("Rejected Mattermost action callback with invalid, expired, or replayed action token");
                return TypedResults.Json(new ActionCallbackResponse
                {
                    EphemeralText = "That approval button is no longer valid. Please re-issue the request and try again."
                }, JsonOptions);
            }

            if (!MattermostAclPolicy.IsAllowedUser(new MattermostUserId(payload.UserId), options))
            {
                logger.LogWarning("Rejected Mattermost action callback from non-allowed user {UserId}", payload.UserId);
                return TypedResults.Json(new ActionCallbackResponse
                {
                    EphemeralText = "You are not authorized to respond to tool approval prompts."
                }, JsonOptions);
            }

            if (!string.Equals(payload.ChannelId, storedAction.ChannelId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Rejected Mattermost action callback with mismatched routing data channel={ChannelId}",
                    payload.ChannelId);
                return TypedResults.BadRequest("Callback routing data did not match the issued action.");
            }

            // Forgery defence (#939): the action token is minted before the prompt
            // is posted, so we only know the prompt's actual post_id after
            // PostReplyAsync. The binding registers it via AssociatePromptPostId.
            // Reject any callback whose post_id doesn't match the post the token
            // was minted for — otherwise a token holder could rewrite post_id and
            // redirect the bot's chat.update to an unrelated post in the channel.
            if (storedAction.PromptPostId is { Length: > 0 } expectedPostId
                && !string.Equals(payload.PostId, expectedPostId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Rejected Mattermost action callback with mismatched post_id channel={ChannelId} callId={CallId}",
                    payload.ChannelId,
                    storedAction.CallId);
                return TypedResults.BadRequest("Callback routing data did not match the issued action.");
            }

            // Consume only after routing validation. Otherwise a forged callback
            // can burn the one-time token before the legitimate user clicks it.
            if (!actionStore.TryConsumeIf(
                    actionToken,
                    action => string.Equals(action.ChannelId, payload.ChannelId, StringComparison.Ordinal)
                              && (!(action.PromptPostId is { Length: > 0 } promptPostId)
                                  || string.Equals(payload.PostId, promptPostId, StringComparison.Ordinal)),
                    out storedAction)
                || storedAction is null)
            {
                logger.LogWarning(
                    "Rejected Mattermost action callback with token that could not be consumed after validation channel={ChannelId}",
                    payload.ChannelId);
                return TypedResults.Json(new ActionCallbackResponse
                {
                    EphemeralText = "That approval button is no longer valid. Please re-issue the request and try again."
                }, JsonOptions);
            }

            // Bound the actor-resolution wait so a daemon still mid-startup
            // returns 503 fast instead of letting the HTTP request hang behind
            // an actor system that's not yet ready.
            using var gatewayResolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            gatewayResolveCts.CancelAfter(TimeSpan.FromSeconds(2));

            IActorRef gateway;
            try
            {
                gateway = await gatewayActor.GetAsync(gatewayResolveCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Mattermost callback received before the Mattermost gateway actor was registered.");
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Use the server-stored prompt post ID when available (post-`AssociatePromptPostId`),
            // otherwise fall back to the client-supplied payload.PostId. The forgery
            // gate above guarantees payload.PostId matches storedAction.PromptPostId
            // when the latter is set; the fallback only fires in the narrow window
            // where the prompt was posted but `AssociatePromptPostId` hadn't run yet
            // (impossible in practice — the binding awaits PostReplyAsync before
            // returning — but defensive).
            var promptPostIdForRedraw = storedAction.PromptPostId is { Length: > 0 } trustedPostId
                ? trustedPostId
                : payload.PostId;

            var interaction = new MattermostGatewayInteraction(
                ChannelId: new MattermostChannelId(storedAction.ChannelId),
                RootPostId: new MattermostRootPostId(storedAction.RootPostId),
                CallId: storedAction.CallId,
                SelectedKey: storedAction.SelectedKey,
                SenderId: new MattermostUserId(payload.UserId),
                RequesterSenderId: storedAction.RequesterSenderId is { Length: > 0 } requesterSenderId
                    ? new MattermostUserId(requesterSenderId)
                    : null,
                ReceivedAt: timeProvider.GetUtcNow(),
                // Trusted post ID (see comment above) — survives passivation of the
                // per-session binding so the binding can redraw the prompt on the
                // cold-spawn path. See issue #939.
                PromptPostId: new MattermostPostId(promptPostIdForRedraw));

            try
            {
                using var askCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                askCts.CancelAfter(TimeSpan.FromSeconds(10));
                var reply = await gateway.Ask<ISessionResponse>(interaction, askCts.Token);

                return reply switch
                {
                    CommandAck => TypedResults.Json(new ActionCallbackResponse
                    {
                        EphemeralText = $"You selected: **{ApprovalOptionKeys.LabelFor(storedAction.SelectedKey)}**"
                    }, JsonOptions),
                    CommandNack nack => TypedResults.Json(new ActionCallbackResponse
                    {
                        EphemeralText = MapRejectMessage(nack.Reason)
                    }, JsonOptions),
                    _ => TypedResults.StatusCode(StatusCodes.Status500InternalServerError)
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed routing Mattermost action callback for call {CallId}", storedAction.CallId);
                return TypedResults.StatusCode(500);
            }

        })
        .WithName("MattermostActionCallback")
        .WithSummary("Handle a Mattermost interactive approval button callback.")
        .WithTags("Mattermost")
        .RequireRateLimiting(CallbackRateLimitPolicy).AllowAnonymous();
    }

    private sealed class ActionCallbackPayload
    {
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? ChannelId { get; set; }
        public string? PostId { get; set; }
        public string? TriggerId { get; set; }
        public Dictionary<string, string>? Context { get; set; }
        public int RawBodyLength { get; set; }
    }

    private sealed record ActionCallbackResponse
    {
        public string? EphemeralText { get; init; }
    }

    internal static void AddMattermostActionEndpointRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(CallbackRateLimitPolicy, context =>
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

    private static async Task<ActionCallbackPayload?> ReadPayloadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > 0 and var contentLength && contentLength > MaxCallbackBodyBytes)
        {
            return new ActionCallbackPayload { RawBodyLength = checked((int)contentLength) };
        }

        var buffer = new byte[4096];
        await using var ms = new MemoryStream();
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
            if (ms.Length > MaxCallbackBodyBytes)
                return new ActionCallbackPayload { RawBodyLength = checked((int)ms.Length) };
        }

        if (ms.Length == 0)
            return null;

        var payload = JsonSerializer.Deserialize<ActionCallbackPayload>(ms.ToArray(), JsonOptions);
        if (payload is not null)
            payload.RawBodyLength = checked((int)ms.Length);
        return payload;
    }

    private static string MapRejectMessage(string reason)
        => reason switch
        {
            ApprovalNackReasons.WrongRequester => "Only the requesting user can approve this tool action.",
            ApprovalNackReasons.PromptExpired => "That approval prompt has expired. Please re-issue the request and try again.",
            ApprovalNackReasons.OptionUnavailable => "That approval option is not available for this tool action. Please use one of the options shown on the prompt.",
            SessionIngressGate.RestartInProgressMessage => SessionIngressGate.RestartInProgressMessage,
            _ => "That approval could not be recorded. Please try again."
        };
}
