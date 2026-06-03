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
        app.MapGet("/api/models", async Task<Results<Ok<ModelCatalogWire.GetCatalogResponse>, ProblemHttpResult>> (
            ModelCatalogService catalog,
            CancellationToken ct) =>
        {
            var result = await catalog.ReadCatalogAsync(ct);
            return result.Success
                ? TypedResults.Ok(result.Catalog!)
                : TypedResults.Problem(result.ErrorMessage, statusCode: result.StatusCode);
        }).RequireAuthorization();

        app.MapGet("/api/model/selection", (ModelCatalogPersistence persistence) =>
        {
            return TypedResults.Ok(persistence.ReadSelection());
        }).RequireAuthorization();

        app.MapPut("/api/model/selection", Results<Ok<ModelCatalogWire.PutSelectionResponse>, BadRequest<ModelCatalogWire.PutSelectionErrorResponse>> (
            ModelCatalogWire.PutSelectionRequest request,
            ModelCatalogPersistence persistence) =>
        {
            if (string.IsNullOrWhiteSpace(request.Role))
            {
                return TypedResults.BadRequest(new ModelCatalogWire.PutSelectionErrorResponse
                {
                    Message = "Role is required.",
                });
            }

            if (string.IsNullOrWhiteSpace(request.Reference?.Provider))
            {
                return TypedResults.BadRequest(new ModelCatalogWire.PutSelectionErrorResponse
                {
                    Message = "Reference.Provider is required.",
                });
            }

            if (string.IsNullOrWhiteSpace(request.Reference?.ModelId))
            {
                return TypedResults.BadRequest(new ModelCatalogWire.PutSelectionErrorResponse
                {
                    Message = "Reference.ModelId is required.",
                });
            }

            var result = persistence.Write(request);

            if (!result.Success)
            {
                return TypedResults.BadRequest(new ModelCatalogWire.PutSelectionErrorResponse
                {
                    Message = result.ErrorMessage ?? "Validation failed.",
                    ValidationErrors = result.ValidationErrors,
                });
            }

            return TypedResults.Ok(new ModelCatalogWire.PutSelectionResponse
            {
                ConfigPath = result.ConfigPath!,
                RestartRequired = true,
            });
        }).RequireAuthorization();
    }
}
