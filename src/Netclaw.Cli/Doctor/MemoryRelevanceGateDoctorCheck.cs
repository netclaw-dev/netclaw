// -----------------------------------------------------------------------
// <copyright file="MemoryRelevanceGateDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Embeddings;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Relevance-gate model diagnostics (memory-relevance-gate spec: "Loud degradation without
/// silent fallback" — doctor visibility half of that contract; the other half is the coordinator's
/// rate-limited <c>memory_recall_gate_degraded</c> log). Added as a sibling to
/// <see cref="MemoryEmbeddingDoctorCheck"/> rather than folded into it (design D8: "extending the
/// existing embedding doctor check or adding a sibling relevance-gate doctor check —
/// implementation detail... not a design fork") since the relevance model has no
/// corpus-coverage concept to report, only presence/hash/degraded-mode-reason.
/// </summary>
/// <param name="allowlist">
/// The relevance-model allowlist to verify against — an explicit, required dependency (same
/// seam <see cref="EmbeddingModelProvisioner"/> itself uses for both manifest kinds) rather than
/// always reading the static <see cref="EmbeddingModelProvisioner.RelevanceAllowlist"/>
/// internally, so tests can supply a small allowlist pointed at a local fixture instead of ever
/// reaching the real ~22 MB HuggingFace artifact. Production wiring
/// (<see cref="DoctorRegistrationExtensions"/>) passes
/// <see cref="EmbeddingModelProvisioner.RelevanceAllowlist"/> itself. Tests key their fixture
/// entry under <see cref="EmbeddingModelProvisioner.DefaultRelevanceModelId"/> — the same
/// constant this check looks up — since there is no config knob selecting which relevance model
/// id is active (design D2/D6: one ratified model, not an operator choice).
/// </param>
public sealed class MemoryRelevanceGateDoctorCheck(
    NetclawPaths paths,
    IConfiguration configuration,
    IReadOnlyDictionary<string, RelevanceModelManifestEntry> allowlist) : IDoctorCheck
{
    private const string CheckName = "Memory Relevance Gate";

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var memoryConfig = configuration.GetSection("Memory").Get<MemoryConfig>() ?? new MemoryConfig();

        if (!memoryConfig.Embeddings.Enabled && memoryConfig.Recall.RelevanceGate.Enabled == true)
        {
            return DoctorCheckResult.Warning(
                CheckName,
                "Relevance gate enabled but cannot run because Memory.Embeddings.Enabled is false.",
                "Enable Memory.Embeddings, or remove the relevance-gate override.");
        }

        // "One mental switch" (design D6): the gate follows Memory.Embeddings.Enabled unless
        // explicitly overridden — identical resolution to what SQLiteMemoryRecallCoordinator
        // applies at runtime.
        var gateEnabled = memoryConfig.Recall.RelevanceGate.Enabled ?? memoryConfig.Embeddings.Enabled;
        if (!gateEnabled)
        {
            return DoctorCheckResult.Pass(
                CheckName,
                memoryConfig.Recall.RelevanceGate.Enabled == false
                    ? "Relevance gate disabled (Memory.Recall.RelevanceGate.Enabled is explicitly false)."
                    : "Relevance gate disabled (follows Memory.Embeddings.Enabled, which is false).");
        }

        var modelId = EmbeddingModelProvisioner.DefaultRelevanceModelId;
        var modelDirectory = paths.EmbeddingModelDirectory(modelId);

        try
        {
            using var httpClient = new HttpClient();
            var provisioner = new EmbeddingModelProvisioner(httpClient, new Dictionary<string, EmbeddingModelManifestEntry>());
            var verified = await provisioner.TryLoadVerifiedRelevanceModelAsync(modelId, allowlist, modelDirectory, cancellationToken);
            if (verified is null)
            {
                if (memoryConfig.Embeddings.AutoDownload)
                {
                    return DoctorCheckResult.Warning(
                        CheckName,
                        $"Relevance model '{modelId}' is not yet provisioned. The daemon will download and verify it on next startup.",
                        "Restart the daemon to provision the relevance model.");
                }

                return DoctorCheckResult.Error(
                    CheckName,
                    $"Relevance model '{modelId}' is missing or fails hash verification at {modelDirectory}.",
                    "Memory.Embeddings.AutoDownload is false — provision the model manually, or enable AutoDownload and restart the daemon.");
            }

            return DoctorCheckResult.Pass(
                CheckName,
                $"Relevance gate healthy: model '{modelId}' provisioned (threshold {verified.CalibratedThreshold:F3}).");
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error(
                CheckName,
                $"Unable to inspect relevance model health: {ex.Message}",
                "Verify the models directory is readable.");
        }
    }
}
