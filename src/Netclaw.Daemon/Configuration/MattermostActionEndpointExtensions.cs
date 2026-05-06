// -----------------------------------------------------------------------
// <copyright file="MattermostActionEndpointExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;

namespace Netclaw.Daemon.Configuration;

public static class MattermostActionEndpointExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static void MapMattermostActionEndpoint(this WebApplication app)
    {
        app.MapPost("/api/mattermost/actions", async (
            HttpContext httpContext,
            IServiceProvider sp,
            TimeProvider timeProvider,
            ILogger<MattermostChannel> logger,
            CancellationToken ct) =>
        {
            var channel = sp.GetService<MattermostChannel>();
            if (channel is null)
                return Results.NotFound("Mattermost channel is not configured.");

            ActionCallbackPayload? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<ActionCallbackPayload>(
                    httpContext.Request.Body,
                    JsonOptions,
                    ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid JSON payload.");
            }

            if (payload is null
                || string.IsNullOrEmpty(payload.UserId)
                || string.IsNullOrEmpty(payload.PostId)
                || string.IsNullOrEmpty(payload.ChannelId))
            {
                return Results.BadRequest("Missing required fields: user_id, post_id, channel_id.");
            }

            if (payload.Context is null
                || !payload.Context.TryGetValue("call_id", out var callId)
                || !payload.Context.TryGetValue("selected_key", out var selectedKey)
                || string.IsNullOrEmpty(callId)
                || string.IsNullOrEmpty(selectedKey))
            {
                return Results.BadRequest("Missing required context fields: call_id, selected_key.");
            }

            if (!IsValidApprovalKey(selectedKey))
                return Results.BadRequest("Invalid selected_key value.");

            payload.Context.TryGetValue("requester_sender_id", out var requesterSenderId);
            if (string.IsNullOrEmpty(requesterSenderId))
                requesterSenderId = null;

            payload.Context.TryGetValue("root_post_id", out var rootPostId);
            if (string.IsNullOrEmpty(rootPostId))
                return Results.BadRequest("Missing required context field: root_post_id.");

            // Verify HMAC signature to prove we created these buttons
            var signingKey = sp.GetService<MattermostCallbackSigningKey>();
            if (signingKey?.Key is { } key)
            {
                payload.Context.TryGetValue("signature", out var signature);
                if (string.IsNullOrEmpty(signature)
                    || !MattermostCallbackSigner.Verify(key, callId, selectedKey, requesterSenderId ?? string.Empty, rootPostId, signature))
                {
                    logger.LogWarning("Rejected Mattermost action callback with invalid HMAC signature for call {CallId}", callId);
                    return Results.Unauthorized();
                }
            }

            var options = sp.GetRequiredService<MattermostChannelOptions>();
            if (!MattermostAclPolicy.IsAllowedUser(new MattermostUserId(payload.UserId), options))
            {
                logger.LogWarning("Rejected Mattermost action callback from non-allowed user {UserId}", payload.UserId);
                return Results.Json(new ActionCallbackResponse
                {
                    EphemeralText = "You are not authorized to respond to tool approval prompts."
                }, JsonOptions);
            }

            var interaction = new MattermostGatewayInteraction(
                ChannelId: new MattermostChannelId(payload.ChannelId),
                RootPostId: new MattermostRootPostId(rootPostId),
                CallId: callId,
                SelectedKey: selectedKey,
                SenderId: new MattermostUserId(payload.UserId),
                RequesterSenderId: requesterSenderId is not null
                    ? new MattermostUserId(requesterSenderId)
                    : null,
                ReceivedAt: timeProvider.GetUtcNow());

            try
            {
                await channel.GatewayClient.HandleActionCallbackAsync(interaction);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed routing Mattermost action callback for call {CallId}", callId);
                return Results.StatusCode(500);
            }

            var decisionLabel = selectedKey switch
            {
                ApprovalOptionKeys.ApproveOnce => ApprovalOptionKeys.ApproveOnceLabel,
                ApprovalOptionKeys.ApproveSession => ApprovalOptionKeys.ApproveSessionLabel,
                ApprovalOptionKeys.ApproveAlways => ApprovalOptionKeys.ApproveAlwaysLabel,
                ApprovalOptionKeys.Deny => ApprovalOptionKeys.DenyLabel,
                _ => selectedKey
            };

            var response = new ActionCallbackResponse
            {
                EphemeralText = $"You selected: **{decisionLabel}**"
            };

            return Results.Json(response, JsonOptions);
        });
    }

    private sealed class ActionCallbackPayload
    {
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? ChannelId { get; set; }
        public string? PostId { get; set; }
        public string? TriggerId { get; set; }
        public Dictionary<string, string>? Context { get; set; }
    }

    private sealed class ActionCallbackResponse
    {
        public string? EphemeralText { get; set; }
    }

    private static bool IsValidApprovalKey(string key)
        => key is ApprovalOptionKeys.ApproveOnce
            or ApprovalOptionKeys.ApproveSession
            or ApprovalOptionKeys.ApproveAlways
            or ApprovalOptionKeys.Deny;
}

