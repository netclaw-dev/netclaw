// -----------------------------------------------------------------------
// <copyright file="SkillEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;

namespace Netclaw.Daemon.Skills;

/// <summary>
/// Read endpoint over the daemon's live <see cref="SkillRegistry"/>. This is the
/// authoritative source of what an agent can actually load — file skills PLUS the
/// dynamic MCP prompt skills a filesystem scan can never see. The CLI's
/// <c>skill list</c> is served by this endpoint and requires the daemon; there is
/// no disk fallback.
/// </summary>
public static class SkillEndpointRouteBuilderExtensions
{
    public static void MapSkillEndpoints(this WebApplication app)
    {
        app.MapGet("/api/skills", (SkillRegistry registry, NetclawPaths paths) =>
                (Ok<SkillInventory.Response>)TypedResults.Ok(
                    SkillInventory.From(registry.GetAll(), paths)))
            .WithName("ListSkills")
            .WithSummary("List every skill the daemon has loaded, including dynamic MCP prompt skills.")
            .WithTags("Skills")
            .RequireAuthorization();

        app.MapPost("/api/skills/sync", async Task<IResult> (
                ServerFeedSkillSyncService syncService,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return TypedResults.Ok(await syncService.SyncAsync(cancellationToken));
                }
                catch (InvalidOperationException ex)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Skill sync is unavailable",
                        detail: ex.Message);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Skill sync stopped",
                        detail: "The daemon stopped the active skill sync pass.");
                }
            })
            .WithName("SyncSkills")
            .WithSummary("Run the configured external skill sync pass.")
            .WithTags("Skills")
            .RequireAuthorization();
    }
}
