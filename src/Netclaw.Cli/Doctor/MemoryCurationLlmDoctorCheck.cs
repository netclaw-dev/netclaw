// -----------------------------------------------------------------------
// <copyright file="MemoryCurationLlmDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Verifies the memory-curation LLM tier is actually producing decisions.
///
/// A misconfigured curation model fails SILENTLY at runtime: the actor logs a
/// warning and falls back to deterministic rules, memory quality decays over
/// weeks, and nothing user-visible breaks. The July 2026 audit found the tier
/// had been dead for its entire production life (0 successful decisions, 3
/// timeouts, 3 empty responses from a reasoning model whose hidden thinking
/// exhausted the output-token cap) and nobody knew. This check scans recent
/// daemon logs for the curation LLM outcome markers so a dead or degraded
/// tier surfaces in `netclaw doctor` instead of in corpus decay.
/// </summary>
public sealed class MemoryCurationLlmDoctorCheck(NetclawPaths paths, TimeProvider timeProvider) : IDoctorCheck
{
    private const string CheckName = "Memory Curation LLM";
    private const int LogWindowDays = 14;
    private const double FailureRateWarningThreshold = 0.2;

    private const string SuccessMarker = "curation_llm_decision";
    private static readonly string[] FailureMarkers =
    [
        "curation_llm_no_decision",
        "curation_llm_timeout",
        "curation_llm_error"
    ];

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(paths.LogsDirectory))
            {
                return DoctorCheckResult.Pass(
                    CheckName,
                    "No daemon logs found; nothing to inspect.");
            }

            var cutoff = timeProvider.GetUtcNow().AddDays(-LogWindowDays);
            var successes = 0;
            var failures = 0;

            foreach (var file in Directory.EnumerateFiles(paths.LogsDirectory, "daemon-*.log"))
            {
                if (!TryParseLogDate(file, out var day) || day < cutoff)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                using var reader = new StreamReader(file);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    // Failure markers first: none of them contains the success
                    // marker as a substring, but the explicit order keeps the
                    // classification obvious and robust to future marker names.
                    if (FailureMarkers.Any(line.Contains))
                        failures++;
                    else if (line.Contains(SuccessMarker))
                        successes++;
                }
            }

            var total = successes + failures;
            if (total == 0)
            {
                return DoctorCheckResult.Pass(
                    CheckName,
                    $"No curation LLM activity in the last {LogWindowDays} days (ambiguous dedup band not reached).");
            }

            if (successes == 0)
            {
                return DoctorCheckResult.Warning(
                    CheckName,
                    $"Curation LLM tier is failing: 0 successful decisions vs {failures} failures in the last {LogWindowDays} days.",
                    "The curation model (ModelRole.Compaction, falling back to Main) is returning empty/garbled output or timing out. " +
                    "If it is a reasoning model, hidden thinking may exhaust the output-token cap. " +
                    "Check daemon logs for curation_llm_no_decision/curation_llm_timeout and consider assigning a small non-reasoning model to the Compaction role.");
            }

            var failureRate = (double)failures / total;
            if (failureRate >= FailureRateWarningThreshold)
            {
                return DoctorCheckResult.Warning(
                    CheckName,
                    $"Curation LLM failure rate {failureRate:P0} ({failures}/{total}) in the last {LogWindowDays} days.",
                    "Check daemon logs for curation_llm_timeout/curation_llm_no_decision patterns and the Compaction-role model configuration.");
            }

            return DoctorCheckResult.Pass(
                CheckName,
                $"Curation LLM healthy: {successes} decisions, {failures} failures in the last {LogWindowDays} days.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error(
                CheckName,
                $"Unable to inspect curation LLM health: {ex.Message}",
                "Verify ~/.netclaw/logs is readable.");
        }
    }

    private static bool TryParseLogDate(string path, out DateTimeOffset day)
    {
        day = default;
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > "daemon-".Length &&
               DateTimeOffset.TryParseExact(
                   name["daemon-".Length..],
                   "yyyy-MM-dd",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal,
                   out day);
    }
}
