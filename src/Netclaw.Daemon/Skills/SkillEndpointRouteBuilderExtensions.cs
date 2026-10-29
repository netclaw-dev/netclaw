// -----------------------------------------------------------------------
// <copyright file="SkillEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Akka.Actor;
using Akka.Hosting;
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

        app.MapGet("/api/skills/{name}", async Task<IResult> (
            string name, SkillRegistry registry, NetclawPaths paths, CancellationToken cancellationToken) =>
            {
                var skill = registry.GetByName(name);

                if (skill is null)
                {
                    // Carries a body on purpose. The CLI distinguishes "unknown name"
                    // from "daemon predates this route" by the presence of a body,
                    // because both are otherwise a bare 404.
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Skill not found",
                        detail: $"The daemon has no skill named '{name}'.");
                }

                string? content = null;
                if (skill.Source is FileSkillSource file)
                {
                    try
                    {
                        content = await File.ReadAllTextAsync(file.FilePath, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return TypedResults.Problem(
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "Skill content unavailable",
                            detail: "The daemon cannot read the registered skill file. Run skill sync and retry.");
                    }
                }

                return TypedResults.Ok(SkillInventory.DetailView(skill, paths, content));
            })
            .WithName("GetSkill")
            .WithSummary("Get one skill's metadata and content from the daemon's live registry.")
            .WithTags("Skills")
            .RequireAuthorization();

        app.MapPost("/api/skills/sync", async Task<IResult> (
                IRequiredActor<ServerFeedSkillSyncActorKey> syncActor,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var response = await syncActor.ActorRef.Ask<SkillSyncResult.Response>(
                        ServerFeedSkillSyncActor.Run.Instance,
                        cancellationToken);
                    return TypedResults.Ok(response);
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
