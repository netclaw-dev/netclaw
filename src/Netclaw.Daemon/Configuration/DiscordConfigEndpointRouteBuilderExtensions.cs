// -----------------------------------------------------------------------
// <copyright file="DiscordConfigEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

public static class DiscordConfigEndpointRouteBuilderExtensions
{
    public static void MapDiscordConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config/discord", (DiscordConfigPersistence persistence) =>
        {
            return Results.Ok(persistence.Read());
        }).RequireAuthorization()
        .WithName("GetDiscordConfig")
        .WithSummary("Get the current Discord connector configuration (bot token excluded).")
        .WithTags("Discord");

        app.MapPut("/api/config/discord", (PutDiscordConfigRequest request, DiscordConfigPersistence persistence) =>
        {
            var response = persistence.Write(request);
            return Results.Ok(response);
        }).RequireAuthorization()
        .WithName("UpdateDiscordConfig")
        .WithSummary("Update the Discord connector configuration; takes effect after a daemon restart.")
        .WithTags("Discord");
    }
}
