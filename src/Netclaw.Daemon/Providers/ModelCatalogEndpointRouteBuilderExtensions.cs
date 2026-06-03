// -----------------------------------------------------------------------
// <copyright file="ModelCatalogEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http.HttpResults;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Providers;

public static class ModelCatalogEndpointRouteBuilderExtensions
{
    public static void MapModelCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/models", async Task<Results<Ok<GetModelCatalogResponse>, ProblemHttpResult>> (
            ModelCatalogService catalog,
            CancellationToken ct) =>
        {
            var result = await catalog.ReadCatalogAsync(ct);
            return result.Success
                ? TypedResults.Ok(result.Catalog!)
                : TypedResults.Problem(result.ErrorMessage, statusCode: result.StatusCode);
        }).RequireAuthorization()
        .WithName("GetModelCatalog")
        .WithSummary("List the models discovered from the configured main provider.")
        .WithTags("Models");

        app.MapGet("/api/model/selection", Ok<GetModelSelectionResponse> (
            ModelCatalogPersistence persistence) =>
        {
            return TypedResults.Ok(persistence.ReadSelection());
        }).RequireAuthorization()
        .WithName("GetModelSelection")
        .WithSummary("Get the model selected for each role (Main, Fallback, Compaction).")
        .WithTags("Models");

        app.MapPut("/api/model/selection", Results<Ok<PutModelSelectionResponse>, BadRequest<PutModelSelectionErrorResponse>> (
            PutModelSelectionRequest request,
            ModelCatalogPersistence persistence) =>
        {
            if (string.IsNullOrWhiteSpace(request.Role))
            {
                return TypedResults.BadRequest(new PutModelSelectionErrorResponse
                {
                    Message = "Role is required.",
                });
            }

            if (string.IsNullOrWhiteSpace(request.Reference?.Provider))
            {
                return TypedResults.BadRequest(new PutModelSelectionErrorResponse
                {
                    Message = "Reference.Provider is required.",
                });
            }

            if (string.IsNullOrWhiteSpace(request.Reference?.ModelId))
            {
                return TypedResults.BadRequest(new PutModelSelectionErrorResponse
                {
                    Message = "Reference.ModelId is required.",
                });
            }

            var result = persistence.Write(request);

            if (!result.Success)
            {
                return TypedResults.BadRequest(new PutModelSelectionErrorResponse
                {
                    Message = result.ErrorMessage ?? "Validation failed.",
                    ValidationErrors = result.ValidationErrors,
                });
            }

            return TypedResults.Ok(new PutModelSelectionResponse
            {
                ConfigPath = result.ConfigPath!,
                RestartRequired = true,
            });
        }).RequireAuthorization()
        .WithName("UpdateModelSelection")
        .WithSummary("Set the model for one role; takes effect after a daemon restart.")
        .WithTags("Models");
    }
}
