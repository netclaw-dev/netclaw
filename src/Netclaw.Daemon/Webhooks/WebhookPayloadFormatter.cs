// -----------------------------------------------------------------------
// <copyright file="WebhookPayloadFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;

namespace Netclaw.Daemon.Webhooks;

public static class WebhookPayloadFormatter
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true
    };

    public static string Format(WebhookInvocation invocation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("A verified webhook delivery was received.");
        builder.AppendLine();
        builder.AppendLine($"Route: {invocation.Route.Name}");

        if (!string.IsNullOrWhiteSpace(invocation.EventType))
            builder.AppendLine($"Event: {invocation.EventType}");

        if (!string.IsNullOrWhiteSpace(invocation.DeliveryId))
            builder.AppendLine($"Delivery ID: {invocation.DeliveryId}");

        builder.AppendLine($"Received At (UTC): {invocation.ReceivedAt:u}");
        builder.AppendLine();
        builder.AppendLine("Payload JSON:");
        builder.AppendLine("```json");
        builder.AppendLine(PrettyPrintJson(invocation.PayloadJson));
        builder.AppendLine("```");
        return builder.ToString().TrimEnd();
    }

    private static string PrettyPrintJson(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(doc.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }
}
