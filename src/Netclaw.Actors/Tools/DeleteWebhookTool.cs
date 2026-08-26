// -----------------------------------------------------------------------
// <copyright file="DeleteWebhookTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Webhooks.WebhookRouteProtocol;

namespace Netclaw.Actors.Tools;

[NetclawTool("delete_webhook",
    "Delete an inbound webhook route by name. Use list_webhooks to discover route names.",
    Grant = "webhook_admin")]
public sealed partial class DeleteWebhookTool : NetclawTool<DeleteWebhookTool.Params>
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(10);

    private readonly IActorRef _routeActor;

    public record Params(
        [property: Description("Webhook route name to delete (for example 'github-issues').")]
        string RouteName);

    public DeleteWebhookTool(IActorRef routeActor)
    {
        _routeActor = routeActor;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!WebhookRouteName.TryCreate(args.RouteName, out var routeName, out var routeError))
            return $"Error: {routeError}";

        try
        {
            var response = await _routeActor.Ask<RouteDeleted>(new DeleteRoute(routeName), AskTimeout, ct);
            return response.Found
                ? $"Webhook route '{routeName.Value}' deleted."
                : $"Webhook route '{routeName.Value}' not found.";
        }
        catch (TimeoutException ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
