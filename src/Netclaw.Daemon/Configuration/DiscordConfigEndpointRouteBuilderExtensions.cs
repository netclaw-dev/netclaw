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
            return Results.Ok(ToResponse(persistence.Read()));
        }).RequireAuthorization();

        app.MapPut("/api/config/discord", (PutDiscordConfigRequest request, DiscordConfigPersistence persistence) =>
        {
            var result = persistence.Write(ToPersistenceRequest(request));
            return Results.Ok(ToResponse(result));
        }).RequireAuthorization();
    }

    private static GetDiscordConfigResponse ToResponse(DiscordConfigWire.GetResponse state)
        => new()
        {
            Enabled = state.Enabled,
            BotTokenIsSet = state.BotTokenIsSet,
            DefaultChannelId = state.DefaultChannelId,
            AllowDirectMessages = state.AllowDirectMessages,
            MentionOnly = state.MentionOnly,
            MentionRequiredInDm = state.MentionRequiredInDm,
            AllowedChannelIds = state.AllowedChannelIds,
            AllowedUserIds = state.AllowedUserIds,
            ChannelAudiences = state.ChannelAudiences,
        };

    private static DiscordConfigWire.PutRequest ToPersistenceRequest(PutDiscordConfigRequest request)
        => new()
        {
            Enabled = request.Enabled,
            BotToken = request.BotToken,
            DefaultChannelId = request.DefaultChannelId,
            AllowDirectMessages = request.AllowDirectMessages,
            MentionOnly = request.MentionOnly,
            MentionRequiredInDm = request.MentionRequiredInDm,
            AllowedChannelIds = request.AllowedChannelIds,
            AllowedUserIds = request.AllowedUserIds,
            ChannelAudiences = request.ChannelAudiences,
        };

    private static PutDiscordConfigResponse ToResponse(DiscordConfigWire.PutResponse result)
        => new()
        {
            ConfigPath = result.ConfigPath,
            SecretsPath = result.SecretsPath,
            RestartRequired = result.RestartRequired,
        };
}
