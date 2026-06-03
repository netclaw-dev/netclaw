// -----------------------------------------------------------------------
// <copyright file="ModelCatalogService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Providers;

public sealed class ModelCatalogService(
    ConfiguredModelProviderState providerState,
    IProviderProbe probe)
{
    public async Task<ModelCatalogResult> ReadCatalogAsync(CancellationToken ct)
    {
        var providerName = providerState.Models.Main.Provider;
        if (string.IsNullOrWhiteSpace(providerName))
            return ModelCatalogResult.Failure(500, "Models.Main.Provider is not configured.");

        if (!providerState.Providers.TryGetValue(providerName, out var provider))
        {
            var configured = providerState.Providers.Count == 0
                ? "(none)"
                : string.Join(", ", providerState.Providers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            return ModelCatalogResult.Failure(500,
                $"Provider '{providerName}' referenced by Models.Main.Provider was not found. Configured providers: {configured}.");
        }

        ProviderProbeResult result;
        try
        {
            result = await probe.ProbeAsync(provider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ModelCatalogResult.Failure(502,
                $"Model discovery failed for provider '{providerName}': {ex.Message}");
        }

        if (!result.Success)
        {
            return ModelCatalogResult.Failure(502,
                $"Model discovery failed for provider '{providerName}': {result.ErrorMessage ?? "Provider probe failed."}");
        }

        return ModelCatalogResult.Ok(new GetModelCatalogResponse
        {
            Models = result.Models.Select(model => ToWire(providerName, model)).ToArray(),
            Warning = string.IsNullOrWhiteSpace(result.ErrorMessage) ? null : result.ErrorMessage,
        });
    }

    private static ModelCatalogEntry ToWire(string providerName, DiscoveredModel model)
        => new()
        {
            Provider = providerName,
            ModelId = model.ModelId.Value,
            DisplayName = model.ModelId.Value,
            ContextWindow = model.ContextWindowTokens,
            InputModalities = ToWireModalities(model.InputModalities),
            OutputModalities = ToWireModalities(model.OutputModalities),
        };

    private static string[] ToWireModalities(ModelModality? modalities)
    {
        if (modalities is null or ModelModality.None)
            return [];

        return Enum.GetValues<ModelModality>()
            .Where(modality => modality is not ModelModality.None && modalities.Value.HasFlag(modality))
            .Select(static modality => modality.ToString())
            .ToArray();
    }
}

public sealed record ModelCatalogResult(
    bool Success,
    GetModelCatalogResponse? Catalog,
    int StatusCode,
    string? ErrorMessage)
{
    public static ModelCatalogResult Ok(GetModelCatalogResponse catalog)
        => new(true, catalog, 200, null);

    public static ModelCatalogResult Failure(int statusCode, string message)
        => new(false, null, statusCode, message);
}
