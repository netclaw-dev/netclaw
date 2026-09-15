// -----------------------------------------------------------------------
// <copyright file="TeamsSdkReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Core.Schema;
using Netclaw.Channels.Teams;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Executes the Teams SDK calls at the daemon transport edge. The SDK context
/// contains the authenticated application client and never enters an actor.
/// </summary>
internal sealed class TeamsSdkReplyClient(
    ITeamsSdkReplyOperations operations,
    ILogger<TeamsSdkReplyClient> logger) : ITeamsReplyClient
{
    public async Task<TeamsDeliveryResult> DeliverAsync(
        TeamsOutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return await ExecuteAsync(async cancellationToken =>
        {
            var activityId = await operations.DeliverAsync(message, cancellationToken);
            return string.IsNullOrWhiteSpace(activityId)
                ? new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: "empty_sdk_result")
                : new TeamsDeliveryResult(
                    string.IsNullOrWhiteSpace(message.UpdateActivityId)
                        ? TeamsDeliveryStatus.Delivered
                        : TeamsDeliveryStatus.Updated,
                    activityId);
        }, cancellationToken);
    }

    public Task<TeamsDeliveryResult> SendTypingAsync(
        TeamsOutboundDestination destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return ExecuteAsync(async cancellationToken =>
        {
            await operations.SendTypingAsync(destination, cancellationToken);
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered);
        }, cancellationToken);
    }

    private async Task<TeamsDeliveryResult> ExecuteAsync(
        Func<CancellationToken, Task<TeamsDeliveryResult>> operation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled, ReasonCode: "cancelled");

        try
        {
            return await operation(cancellationToken);
        }
        catch (TeamsSdkDeliveryException exception)
        {
            logger.LogWarning(
                "Teams SDK outbound delivery failed: stage={Stage}; exception_type={ExceptionType}",
                exception.ReasonCode,
                exception.InnerException?.GetType().Name ?? exception.GetType().Name);
            return MapFailure(exception.InnerException ?? exception, exception.ReasonCode);
        }
        catch (OperationCanceledException)
        {
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled, ReasonCode: "cancelled");
        }
        catch (Exception exception)
        {
            return MapFailure(exception, "sdk_delivery_failed");
        }
    }

    private static TeamsDeliveryResult MapFailure(Exception exception, string failureCode)
    {
        if (exception is HttpRequestException httpRequestException)
        {
            if (httpRequestException.StatusCode == HttpStatusCode.RequestEntityTooLarge
                || httpRequestException.Message.Contains("MessageSizeTooBig", StringComparison.Ordinal))
            {
                return new TeamsDeliveryResult(TeamsDeliveryStatus.RejectedTooLarge, ReasonCode: "output_too_large");
            }

            if (httpRequestException.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new TeamsDeliveryResult(TeamsDeliveryStatus.InvalidDestination, ReasonCode: "sdk_destination_invalid");

            return new TeamsDeliveryResult(
                TeamsDeliveryStatus.Unavailable,
                ReasonCode: failureCode == "sdk_delivery_failed" ? "sdk_unavailable" : failureCode);
        }

        if (exception is UnauthorizedAccessException)
        {
            return new TeamsDeliveryResult(
                TeamsDeliveryStatus.Unavailable,
                ReasonCode: failureCode == "sdk_delivery_failed" ? "sdk_unauthorized" : failureCode);
        }

        return exception.Message.Contains("MessageSizeTooBig", StringComparison.Ordinal)
            ? new TeamsDeliveryResult(TeamsDeliveryStatus.RejectedTooLarge, ReasonCode: "output_too_large")
            : new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: failureCode);
    }
}

/// <summary>
/// Keeps the SDK call surface narrow so the result mapper has direct tests
/// without a live Teams request context.
/// </summary>
internal interface ITeamsSdkReplyOperations
{
    Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken);

    Task SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken);
}

internal sealed class TeamsSdkReplyOperations(
    TeamsBotApplication app) : ITeamsSdkReplyOperations
{
    public async Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken)
    {
        var serviceUrl = new Uri(message.Destination.ServiceUrl, UriKind.Absolute);
        var activity = TeamsSdkActivityFactory.CreateMessage(message);

        // The application owns authenticated client credentials. It survives
        // the original HTTP request and sends to the validated destination
        // recovered by the binding actor.
        if (!string.IsNullOrWhiteSpace(message.UpdateActivityId))
        {
            var updated = await ExecuteSdkCallAsync(
                () => app.Api
                    .ForServiceUrl(serviceUrl)
                    .Conversations
                    .UpdateActivityAsync(
                        message.Destination.ConversationId,
                        message.UpdateActivityId,
                        activity,
                        cancellationToken: cancellationToken),
                "sdk_update_failed");
            return updated.Id;
        }

        if (string.IsNullOrWhiteSpace(message.ReplyToActivityId))
        {
            var proactiveSent = await ExecuteSdkCallAsync(
                () => app.SendAsync(
                    message.Destination.ConversationId,
                    activity,
                    serviceUrl,
                    cancellationToken: cancellationToken),
                "sdk_create_failed");
            return proactiveSent?.Id;
        }

        var reply = await ExecuteSdkCallAsync(
            () => app.ReplyAsync(
                message.Destination.ConversationId,
                message.ReplyToActivityId,
                activity,
                serviceUrl,
                cancellationToken: cancellationToken),
            "sdk_reply_failed");
        return reply?.Id;
    }

    public async Task SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken)
    {
        var activity = TeamsSdkActivityFactory.CreateTyping(destination);

        await ExecuteSdkCallAsync(
            () => app.SendActivityAsync(
                destination.ConversationId,
                activity,
                new Uri(destination.ServiceUrl, UriKind.Absolute),
                cancellationToken: cancellationToken),
            "sdk_create_failed");
    }

    private static async Task<T> ExecuteSdkCallAsync<T>(Func<Task<T>> call, string failureCode)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TeamsSdkDeliveryException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TeamsSdkDeliveryException(failureCode, exception);
        }
    }
}

internal static class TeamsSdkActivityFactory
{
    public static MessageActivityInput CreateMessage(TeamsOutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.ApprovalCard is not { } approvalCard)
            return new MessageActivityInput().WithText(message.Text);

        JsonElement payload;
        try
        {
            payload = JsonSerializer.SerializeToElement(TeamsAdaptiveCardPayloadBuilder.Create(approvalCard));
        }
        catch (Exception exception)
        {
            throw new TeamsSdkDeliveryException("approval_payload_build_failed", exception);
        }

        var activity = new MessageActivityInput();
        try
        {
            activity.AddAdaptiveCardAttachment(payload);
        }
        catch (Exception exception)
        {
            throw new TeamsSdkDeliveryException("approval_activity_build_failed", exception);
        }

        try
        {
            _ = activity.ToJson();
        }
        catch (Exception exception)
        {
            throw new TeamsSdkDeliveryException("approval_activity_serialize_failed", exception);
        }

        return activity;
    }

    public static CoreActivityInput CreateTyping(TeamsOutboundDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return new CoreActivityInput("typing")
        {
            ReplyToId = destination.Scope == TeamsConversationScope.Channel
                ? destination.RootActivityId
                : null
        };
    }
}

internal sealed class TeamsSdkDeliveryException(string reasonCode, Exception innerException)
    : Exception(reasonCode, innerException)
{
    public string ReasonCode { get; } = reasonCode;
}

internal static class TeamsAdaptiveCardPayloadBuilder
{
    public static Dictionary<string, object?> Create(TeamsApprovalCard approvalCard)
    {
        ArgumentNullException.ThrowIfNull(approvalCard);

        var body = new List<object?> { CreateHeader(approvalCard) };
        if (!string.IsNullOrWhiteSpace(approvalCard.Banner))
            body.Add(CreateBanner(approvalCard.Banner, approvalCard.Tone));

        if (approvalCard.Fields.Count > 0)
        {
            body.Add(CreateTable(approvalCard.Fields));
        }

        if (!string.IsNullOrWhiteSpace(approvalCard.Summary)
            && !string.Equals(approvalCard.Summary, approvalCard.Banner, StringComparison.Ordinal))
        {
            body.Add(CreateContextSummary(approvalCard.Summary));
        }

        if (!string.IsNullOrWhiteSpace(approvalCard.Footer))
            body.Add(CreateFooter(approvalCard.Footer, approvalCard.Tone));

        var payload = new Dictionary<string, object?>
        {
            ["$schema"] = TeamsApprovalCard.Schema,
            ["type"] = "AdaptiveCard",
            ["version"] = TeamsApprovalCard.Version,
            ["body"] = body,
            ["actions"] = approvalCard.Actions.Select(cardAction => (object?)new Dictionary<string, object?>
            {
                ["type"] = "Action.Execute",
                ["title"] = cardAction.Title,
                ["style"] = ToWireStyle(cardAction.Style),
                ["verb"] = "netclaw-approval",
                ["data"] = new Dictionary<string, object?>
                {
                    ["correlation"] = cardAction.CorrelationId,
                    ["nonce"] = cardAction.Nonce,
                    ["action"] = cardAction.Action
                }
            }).ToArray()
        };
        if (!string.IsNullOrWhiteSpace(approvalCard.Speak))
            payload["speak"] = approvalCard.Speak;

        if (JsonSerializer.SerializeToUtf8Bytes(payload).Length > TeamsApprovalCard.MaxSerializedBytes)
            throw new InvalidOperationException("The Teams approval card exceeds the outbound payload limit.");

        return payload;
    }

    private static Dictionary<string, object?> CreateHeader(TeamsApprovalCard card) => new()
    {
        ["type"] = "ColumnSet",
        ["columns"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "Column",
                ["width"] = "auto",
                ["verticalContentAlignment"] = "Center",
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "Icon",
                        ["name"] = card.IconName,
                        ["size"] = "Large",
                        ["color"] = ToWireTone(card.Tone),
                        ["style"] = "Regular"
                    }
                }
            },
            new Dictionary<string, object?>
            {
                ["type"] = "Column",
                ["width"] = "stretch",
                ["verticalContentAlignment"] = "Center",
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock",
                        ["text"] = card.Title,
                        ["size"] = "Large",
                        ["weight"] = "Bolder",
                        ["wrap"] = true
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock",
                        ["text"] = "NETCLAW SECURITY CONTROL",
                        ["isSubtle"] = true,
                        ["spacing"] = "None",
                        ["wrap"] = true
                    }
                }
            }
        }
    };

    private static Dictionary<string, object?> CreateBanner(string text, TeamsApprovalCardTone tone) => new()
    {
        ["type"] = "Container",
        ["style"] = ToWireTone(tone),
        ["bleed"] = true,
        ["spacing"] = "Medium",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["color"] = ToWireTone(tone),
                ["weight"] = "Bolder",
                ["wrap"] = true
            }
        }
    };

    private static Dictionary<string, object?> CreateTable(IReadOnlyList<TeamsApprovalCardField> fields) => new()
    {
        ["type"] = "Table",
        ["firstRowAsHeader"] = false,
        ["showGridLines"] = true,
        ["gridStyle"] = "Default",
        ["columns"] = new object?[]
        {
            new Dictionary<string, object?> { ["width"] = 1 },
            new Dictionary<string, object?> { ["width"] = 2 }
        },
        ["rows"] = fields.Select(CreateRow).ToArray()
    };

    private static Dictionary<string, object?> CreateRow(TeamsApprovalCardField field) => new()
    {
        ["type"] = "TableRow",
        ["cells"] = new object?[]
        {
            CreateCell(field.Label, subtle: true),
            CreateCell(field.Value, subtle: false)
        }
    };

    private static Dictionary<string, object?> CreateCell(string text, bool subtle) => new()
    {
        ["type"] = "TableCell",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["isSubtle"] = subtle,
                ["weight"] = subtle ? "Default" : "Bolder",
                ["wrap"] = true
            }
        }
    };

    private static Dictionary<string, object?> CreateContextSummary(string summary) => new()
    {
        ["type"] = "Container",
        ["style"] = "Warning",
        ["spacing"] = "Medium",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = summary,
                ["color"] = "Warning",
                ["wrap"] = true
            }
        }
    };

    private static Dictionary<string, object?> CreateFooter(string text, TeamsApprovalCardTone tone) => new()
    {
        ["type"] = "Container",
        ["style"] = ToWireTone(tone),
        ["bleed"] = true,
        ["spacing"] = "Medium",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["horizontalAlignment"] = "Center",
                ["weight"] = "Bolder",
                ["color"] = ToWireTone(tone),
                ["wrap"] = true
            }
        }
    };

    private static string ToWireStyle(TeamsApprovalActionStyle style) => style switch
    {
        TeamsApprovalActionStyle.Default => "default",
        TeamsApprovalActionStyle.Positive => "positive",
        TeamsApprovalActionStyle.Destructive => "destructive",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unsupported Teams approval action style.")
    };

    private static string ToWireTone(TeamsApprovalCardTone tone) => tone switch
    {
        TeamsApprovalCardTone.Default => "Default",
        TeamsApprovalCardTone.Accent => "Accent",
        TeamsApprovalCardTone.Good => "Good",
        TeamsApprovalCardTone.Warning => "Warning",
        TeamsApprovalCardTone.Attention => "Attention",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, "Unsupported Teams approval card tone.")
    };
}
