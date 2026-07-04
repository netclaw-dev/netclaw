// -----------------------------------------------------------------------
// <copyright file="MemoryEmbeddingDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Embeddings;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Embedding coverage diagnostics (memory-core-redesign spec: "Embedding coverage
/// diagnostics"). Reports model provisioning state and corpus coverage so a degraded or
/// partially-embedded corpus surfaces in <c>netclaw doctor</c> instead of only in a daemon log
/// line (design D2/D3, spec "Loud degradation without silent fallback"). Mirrors
/// <see cref="MemoryCheckpointHealthDoctorCheck"/>'s pattern of constructing its own
/// <see cref="SQLiteMemoryStore"/> directly against the same on-disk database rather than
/// sharing the daemon process's DI-resolved instance.
/// </summary>
/// <param name="allowlist">
/// The embedding model allowlist to verify against — an explicit, required dependency (same
/// seam <see cref="EmbeddingModelProvisioner"/> itself uses) rather than always reading the
/// static <see cref="EmbeddingModelProvisioner.Allowlist"/> internally, so tests can supply a
/// small allowlist pointed at a local fixture instead of ever reaching the real ~100-300 MB
/// HuggingFace artifacts. Production wiring (<see cref="DoctorRegistrationExtensions"/>) passes
/// <see cref="EmbeddingModelProvisioner.Allowlist"/> itself.
/// </param>
public sealed class MemoryEmbeddingDoctorCheck(
    NetclawPaths paths,
    IConfiguration configuration,
    IReadOnlyDictionary<string, EmbeddingModelManifestEntry> allowlist) : IDoctorCheck
{
    private const string CheckName = "Memory Embeddings";

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var memoryConfig = configuration.GetSection("Memory").Get<MemoryConfig>() ?? new MemoryConfig();

        if (!memoryConfig.Embeddings.Enabled)
        {
            return DoctorCheckResult.Pass(
                CheckName,
                "Embeddings disabled (Memory.Embeddings.Enabled is false).");
        }

        var modelId = memoryConfig.Embeddings.ModelId;
        var modelDirectory = paths.EmbeddingModelDirectory(modelId);

        try
        {
            var provisioner = new EmbeddingModelProvisioner(new HttpClient(), allowlist);
            var verified = await provisioner.TryLoadVerifiedAsync(modelId, modelDirectory, cancellationToken);
            if (verified is null)
            {
                return DoctorCheckResult.Error(
                    CheckName,
                    $"Embedding model '{modelId}' is missing or fails hash verification at {modelDirectory}.",
                    memoryConfig.Embeddings.AutoDownload
                        ? "Restart the daemon to re-provision, or run `netclaw memory backfill-embeddings`."
                        : "Memory.Embeddings.AutoDownload is false — provision the model manually, or enable AutoDownload and restart the daemon.");
            }

            var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
            await store.InitializeAsync(cancellationToken);
            var coverage = await store.GetEmbeddingCoverageAsync(modelId, cancellationToken);

            if (coverage.OtherModelCount > 0)
            {
                return DoctorCheckResult.Warning(
                    CheckName,
                    $"Embeddings exist under another model id in addition to '{modelId}' ({coverage.OtherModelCount} items) — " +
                    "similarity thresholds are calibrated per model.",
                    "Run `netclaw memory backfill-embeddings --force` to re-embed the full corpus under the active model.");
            }

            var missing = coverage.TotalRecallableDocuments - coverage.EmbeddedCurrentHashCount;
            if (missing > 0)
            {
                return DoctorCheckResult.Warning(
                    CheckName,
                    $"{missing} of {coverage.TotalRecallableDocuments} recallable documents lack a current-model embedding.",
                    "The daemon's gap-repair sweep heals this at next startup, or run `netclaw memory backfill-embeddings` now.");
            }

            return DoctorCheckResult.Pass(
                CheckName,
                $"Embeddings healthy: {coverage.EmbeddedCurrentHashCount}/{coverage.TotalRecallableDocuments} documents embedded under '{modelId}'.");
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error(
                CheckName,
                $"Unable to inspect embedding health: {ex.Message}",
                "Verify the models directory and SQLite memory database are readable.");
        }
    }
}
