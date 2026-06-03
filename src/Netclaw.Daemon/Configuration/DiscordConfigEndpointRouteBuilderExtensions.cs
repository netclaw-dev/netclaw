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
        }).RequireAuthorization();

        app.MapPut("/api/config/discord", (PutDiscordConfigRequest request, DiscordConfigPersistence persistence) =>
        {
            var response = persistence.Write(request);
            return Results.Ok(response);
        }).RequireAuthorization();
    }
}
